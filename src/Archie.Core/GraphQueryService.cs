using Archie.Contracts;

namespace Archie.Core;

public sealed record GraphQuery(
    string? Root,
    int Depth,
    IReadOnlySet<NodeKind> NodeKinds,
    IReadOnlySet<EdgeKind> EdgeKinds,
    IReadOnlySet<Confidence> Confidence,
    IReadOnlySet<Resolution> Resolution,
    string? Environment,
    int NodeLimit,
    int EdgeLimit);

public sealed record PathQuery(
    string From,
    string? To,
    PathDirection Direction,
    int MaxDepth,
    IReadOnlySet<EdgeKind> EdgeKinds);

public sealed record GraphSubgraph(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);

public sealed record GraphPath(IReadOnlyList<string> NodeIds, IReadOnlyList<string> EdgeIds);

public sealed record PathRuntimeLimits(
    int DefaultDepth = 8,
    int MaxDepth = 12,
    int MaxReturnedPaths = 50,
    int MaxVisitedNodes = 10_000,
    TimeSpan? TraversalTimeout = null)
{
    public TimeSpan EffectiveTraversalTimeout => TraversalTimeout ?? TimeSpan.FromSeconds(2);
}

public sealed class GraphQueryLimitException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class GraphQueryService(GraphSnapshot snapshot, GraphIndexes indexes, PathRuntimeLimits? pathLimits = null)
{
    private readonly PathRuntimeLimits _pathLimits = pathLimits ?? new PathRuntimeLimits();

    public GraphSubgraph Query(GraphQuery query)
    {
        var included = snapshot.Nodes
            .Where(node => query.NodeKinds.Count == 0 || query.NodeKinds.Contains(node.Kind))
            .Where(node => query.Resolution.Count == 0 || query.Resolution.Contains(node.Resolution))
            .Where(node => query.Environment is null || PropertyEquals(node, "environment", query.Environment))
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (query.Root is not null)
        {
            if (!indexes.Nodes.ContainsKey(query.Root)) throw new KeyNotFoundException(query.Root);
            var reachable = Neighborhood(query.Root, query.Depth);
            included.IntersectWith(reachable);
        }

        var edges = snapshot.Edges
            .Where(edge => included.Contains(edge.From) && included.Contains(edge.To))
            .Where(edge => query.EdgeKinds.Count == 0 || query.EdgeKinds.Contains(edge.Kind))
            .Where(edge => query.Confidence.Count == 0 || query.Confidence.Contains(edge.Confidence))
            .Where(edge => query.Resolution.Count == 0 || query.Resolution.Contains(edge.Resolution))
            .ToArray();
        var nodes = snapshot.Nodes.Where(node => included.Contains(node.Id)).ToArray();

        if (nodes.Length > query.NodeLimit || edges.Length > query.EdgeLimit)
        {
            throw new GraphQueryLimitException(
                "QUERY_LIMIT_EXCEEDED",
                $"The query requires {nodes.Length} nodes and {edges.Length} edges, exceeding the requested budget of {query.NodeLimit} nodes and {query.EdgeLimit} edges. Narrow the query or raise the budget; no partial graph was returned.");
        }

        return new GraphSubgraph(nodes, edges);
    }

    public IReadOnlyList<GraphPath> FindPaths(PathQuery query)
    {
        if (!indexes.Nodes.ContainsKey(query.From)) throw new KeyNotFoundException(query.From);
        if (query.To is not null && !indexes.Nodes.ContainsKey(query.To)) throw new KeyNotFoundException(query.To);
        if (query.MaxDepth <= 0 || query.MaxDepth > _pathLimits.MaxDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(query), $"maxDepth must be 1-{_pathLimits.MaxDepth}.");
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<GraphPath>();
        var queue = new Queue<(string Node, string[] Nodes, string[] Edges)>();
        queue.Enqueue((query.From, [query.From], []));
        var visited = 0;
        var hitDepthLimit = false;

        while (queue.Count > 0)
        {
            if (++visited > _pathLimits.MaxVisitedNodes)
                throw Limit("visited-node", _pathLimits.MaxVisitedNodes);
            if (started.Elapsed > _pathLimits.EffectiveTraversalTimeout)
                throw Limit("time", (int)_pathLimits.EffectiveTraversalTimeout.TotalMilliseconds);

            var current = queue.Dequeue();
            if (current.Edges.Length == query.MaxDepth)
            {
                if (Adjacent(current.Node, query).Any(edge => !current.Nodes.Contains(Next(edge, query.Direction), StringComparer.Ordinal)))
                    hitDepthLimit = true;
                continue;
            }

            foreach (var edge in Adjacent(current.Node, query))
            {
                var next = Next(edge, query.Direction);
                if (current.Nodes.Contains(next, StringComparer.Ordinal)) continue;
                var nodes = current.Nodes.Append(next).ToArray();
                var edges = current.Edges.Append(edge.Id).ToArray();
                if (query.To is null || StringComparer.Ordinal.Equals(next, query.To))
                {
                    results.Add(new GraphPath(nodes, edges));
                    if (results.Count > _pathLimits.MaxReturnedPaths)
                        throw Limit("returned-path", _pathLimits.MaxReturnedPaths);
                    if (query.To is not null) continue;
                }
                queue.Enqueue((next, nodes, edges));
            }
        }

        if (query.To is not null && results.Count == 0 && hitDepthLimit)
            throw Limit("depth", query.MaxDepth);

        return results
            .OrderBy(path => path.EdgeIds.Count)
            .ThenBy(path => string.Join('\0', path.EdgeIds), StringComparer.Ordinal)
            .ToArray();
    }

    private HashSet<string> Neighborhood(string root, int depth)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { root };
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Depth == depth) continue;
            foreach (var edge in indexes.Downstream[current.Id].Concat(indexes.Upstream[current.Id]))
            {
                var other = StringComparer.Ordinal.Equals(GraphIndexes.LogicalFrom(edge), current.Id)
                    ? GraphIndexes.LogicalTo(edge)
                    : GraphIndexes.LogicalFrom(edge);
                if (seen.Add(other)) queue.Enqueue((other, current.Depth + 1));
            }
        }
        return seen;
    }

    private IEnumerable<GraphEdge> Adjacent(string node, PathQuery query) =>
        (query.Direction == PathDirection.Downstream ? indexes.Downstream[node] : indexes.Upstream[node])
        .Where(edge => query.EdgeKinds.Count == 0 || query.EdgeKinds.Contains(edge.Kind));

    private static string Next(GraphEdge edge, PathDirection direction) => direction == PathDirection.Downstream
        ? GraphIndexes.LogicalTo(edge)
        : GraphIndexes.LogicalFrom(edge);

    private static bool PropertyEquals(GraphNode node, string name, string expected) =>
        node.Properties.TryGetValue(name, out var value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.String &&
        StringComparer.OrdinalIgnoreCase.Equals(value.GetString(), expected);

    private static GraphQueryLimitException Limit(string bound, int value) => new(
        "PATH_LIMIT_EXCEEDED",
        $"Path traversal reached its {bound} limit ({value}); no partial paths were returned. Narrow the relationship kinds or endpoints.");
}
