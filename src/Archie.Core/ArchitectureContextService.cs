using System.Collections.Frozen;
using System.Text;
using Archie.Contracts;

namespace Archie.Core;

public enum DependencyDirection { Upstream, Downstream, Both }

public sealed record ArchitectureSubject(string Id, string SubjectType);

public sealed record ContextLookupResult(
    string SnapshotId,
    RepositoryRevision Repository,
    string Match,
    string? Path,
    int? Line,
    string? SubjectId,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<GraphNode> Containers,
    IReadOnlyList<GraphEdge> Incoming,
    IReadOnlyList<GraphEdge> Outgoing,
    IReadOnlyList<SourceOwnership> Ownership,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record DependencyResult(
    string SnapshotId,
    RepositoryRevision Repository,
    string RootId,
    DependencyDirection Direction,
    int Depth,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges);

public sealed record DetailedGraphPath(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<Evidence> Evidence);

public sealed record FlowTraceResult(
    string SnapshotId,
    RepositoryRevision Repository,
    IReadOnlyList<DetailedGraphPath> Paths,
    string Markdown);

public sealed record ChangedSource(string Path, int? StartLine = null, int? EndLine = null);

public sealed record ImpactItem(
    string Path,
    ArchitectureSubject Subject,
    string Reason,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<SourceOwnership> Ownership);

public sealed record ChangeImpactResult(
    string SnapshotId,
    RepositoryRevision Repository,
    string Assessment,
    int DownstreamDepth,
    IReadOnlyList<ImpactItem> Direct,
    IReadOnlyList<ImpactItem> Owner,
    IReadOnlyList<ImpactItem> Downstream,
    IReadOnlyList<string> Unmatched,
    string Markdown);

public sealed record ContextRuntimeLimits(
    int MaxChangedFiles = 100,
    int MaxDependencyDepth = 12,
    int MaxNodes = 500,
    int MaxEdges = 1_500,
    int MaxMarkdownBytes = 262_144,
    int MaxResponseBytes = 1_048_576,
    int MaxSummaryBytes = 16_384,
    int MaxEvidenceBytes = 65_536);

public sealed partial class ArchitectureContextService
{
    private readonly GraphSnapshot snapshot;
    private readonly GraphIndexes indexes;
    private readonly SourceContextSnapshot sourceContext;
    private readonly GraphQueryService queryService;
    private readonly ContextRuntimeLimits limits;
    private readonly FrozenDictionary<string, IReadOnlyList<SourceOwnership>> ownershipByPath;
    private readonly RepositoryRevision repository;

    public ArchitectureContextService(
        GraphSnapshot snapshot,
        SourceContextSnapshot sourceContext,
        ContextRuntimeLimits? limits = null,
        PathRuntimeLimits? pathLimits = null)
    {
        this.snapshot = snapshot;
        indexes = new(snapshot);
        this.sourceContext = sourceContext;
        this.limits = limits ?? new();
        queryService = new(snapshot, indexes, pathLimits);
        repository = snapshot.Composition.Inputs.Single().Repository;
        ownershipByPath = sourceContext.Ownership.GroupBy(item => item.Path, StringComparer.Ordinal)
            .ToFrozenDictionary(
                group => group.Key,
                group => (IReadOnlyList<SourceOwnership>)group.OrderBy(item => item.OwnerId, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
    }

    public ContextLookupResult GetContext(string? path, int? line, string? subjectId)
    {
        if ((path is null) == (subjectId is null))
            throw new ArgumentException("Provide exactly one of path or subjectId.");
        if (subjectId is not null && line is not null)
            throw new ArgumentException("line can be used only with path.");
        if (line is <= 0) throw new ArgumentOutOfRangeException(nameof(line), "line must be positive.");

        IReadOnlyList<Evidence> evidence;
        IReadOnlyList<SourceOwnership> ownership;
        var subjectIds = new HashSet<string>(StringComparer.Ordinal);
        if (path is not null)
        {
            SourceContextValidator.ValidatePath(path);
            evidence = indexes.EvidenceByPath.GetValueOrDefault(path, [])
                .Where(item => line is null || item.Range is { } range && line >= range.StartLine && line <= range.EndLine)
                .ToArray();
            ownership = ownershipByPath.GetValueOrDefault(path, []);
            foreach (var item in evidence)
                foreach (var id in indexes.EvidenceSubjects[item.Id]) subjectIds.Add(id);
            foreach (var item in ownership) subjectIds.Add(item.OwnerId);
        }
        else
        {
            if (!indexes.Nodes.ContainsKey(subjectId!) && !indexes.Edges.ContainsKey(subjectId!))
                throw new KeyNotFoundException(subjectId);
            subjectIds.Add(subjectId!);
            var evidenceIds = indexes.Nodes.TryGetValue(subjectId!, out var node)
                ? node.EvidenceIds
                : indexes.Edges[subjectId!].EvidenceIds;
            evidence = evidenceIds.Select(id => indexes.Evidence[id]).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            ownership = [];
        }

        var nodes = subjectIds.Where(indexes.Nodes.ContainsKey).Select(id => indexes.Nodes[id]).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var edges = subjectIds.Where(indexes.Edges.ContainsKey).Select(id => indexes.Edges[id]).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var relatedNodes = nodes.Select(item => item.Id)
            .Concat(edges.SelectMany(item => new[] { item.From, item.To })).Distinct(StringComparer.Ordinal).ToArray();
        var incoming = relatedNodes.SelectMany(id => indexes.Incoming[id]).DistinctBy(item => item.Id).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var outgoing = relatedNodes.SelectMany(id => indexes.Outgoing[id]).DistinctBy(item => item.Id).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var containers = Containers(relatedNodes).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var diagnosticSubjects = subjectIds.Concat(relatedNodes).ToHashSet(StringComparer.Ordinal);
        var diagnostics = snapshot.Diagnostics.Where(item => item.SubjectId is not null && diagnosticSubjects.Contains(item.SubjectId))
            .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var match = subjectIds.Count == 0 ? "no-match" : subjectIds.Count == 1 ? "matched" : "ambiguous";
        return new(snapshot.Id, repository, match, path, line, subjectId, nodes, edges, evidence, containers, incoming, outgoing, ownership, diagnostics);
    }

    public DependencyResult GetDependencies(
        string nodeId,
        DependencyDirection direction,
        int depth,
        IReadOnlySet<NodeKind>? nodeKinds = null,
        IReadOnlySet<EdgeKind>? edgeKinds = null)
    {
        if (!indexes.Nodes.ContainsKey(nodeId)) throw new KeyNotFoundException(nodeId);
        ValidateDepth(depth);
        nodeKinds ??= new HashSet<NodeKind>();
        edgeKinds ??= new HashSet<EdgeKind>();
        var included = new HashSet<string>(StringComparer.Ordinal) { nodeId };
        var edges = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((nodeId, 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Depth == depth) continue;
            foreach (var edge in Adjacent(current.Id, direction).Where(item => edgeKinds.Count == 0 || edgeKinds.Contains(item.Kind)))
            {
                var next = Next(current.Id, edge);
                edges[edge.Id] = edge;
                if (included.Add(next)) queue.Enqueue((next, current.Depth + 1));
                if (included.Count > limits.MaxNodes || edges.Count > limits.MaxEdges) throw DependencyLimit();
            }
        }
        var nodes = included.Select(id => indexes.Nodes[id])
            .Where(item => item.Id == nodeId || nodeKinds.Count == 0 || nodeKinds.Contains(item.Kind))
            .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var nodeIds = nodes.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var selectedEdges = edges.Values.Where(item => nodeIds.Contains(item.From) && nodeIds.Contains(item.To))
            .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        return new(snapshot.Id, repository, nodeId, direction, depth, nodes, selectedEdges);
    }

    public FlowTraceResult TraceFlow(
        string from,
        string to,
        PathDirection direction,
        int maxDepth,
        IReadOnlySet<EdgeKind>? edgeKinds = null)
    {
        ValidateDepth(maxDepth);
        var paths = queryService.FindPaths(new(from, to, direction, maxDepth, edgeKinds ?? new HashSet<EdgeKind>()))
            .Select(path => new DetailedGraphPath(
                path.NodeIds.Select(id => indexes.Nodes[id]).ToArray(),
                path.EdgeIds.Select(id => indexes.Edges[id]).ToArray(),
                path.EdgeIds.SelectMany(id => indexes.Edges[id].EvidenceIds).Distinct(StringComparer.Ordinal)
                    .Select(id => indexes.Evidence[id]).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray()))
            .ToArray();
        var markdown = RenderFlow(from, to, paths);
        EnsureMarkdownLimit(markdown);
        return new(snapshot.Id, repository, paths, markdown);
    }

    public ChangeImpactResult AssessChangeImpact(IReadOnlyList<ChangedSource> changes, int downstreamDepth)
    {
        if (changes.Count == 0 || changes.Count > limits.MaxChangedFiles)
            throw new ArgumentOutOfRangeException(nameof(changes), $"Provide 1-{limits.MaxChangedFiles} changed files.");
        ValidateDepth(downstreamDepth);

        var direct = new List<ImpactItem>();
        var owner = new List<ImpactItem>();
        var downstream = new List<ImpactItem>();
        var unmatched = new List<string>();
        var seeds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            SourceContextValidator.ValidatePath(change.Path);
            if ((change.StartLine is null) != (change.EndLine is null) ||
                change.StartLine is <= 0 || change.EndLine is <= 0 || change.StartLine > change.EndLine)
                throw new ArgumentException($"Changed line range for '{change.Path}' is invalid.");
            var evidence = indexes.EvidenceByPath.GetValueOrDefault(change.Path, [])
                .Where(item => Intersects(item.Range, change.StartLine, change.EndLine)).ToArray();
            var ownership = ownershipByPath.GetValueOrDefault(change.Path, []);
            foreach (var item in evidence)
                foreach (var subjectId in indexes.EvidenceSubjects[item.Id])
                {
                    direct.Add(new(change.Path, Subject(subjectId), "Changed location directly intersects graph evidence.", [item], []));
                    AddTraversalSeeds(subjectId, seeds);
                }

            foreach (var item in ownership)
            {
                owner.Add(new(change.Path, Subject(item.OwnerId),
                    "The scanner established source ownership; internal reachability to this boundary's relationships is unknown.", [], [item]));
                var boundary = Descendants(item.OwnerId);
                foreach (var id in boundary) seeds.Add(id);
                foreach (var edge in boundary.SelectMany(id => indexes.Outgoing[id].Concat(indexes.Incoming[id]))
                             .Where(edge => edge.Kind != EdgeKind.Contains).DistinctBy(edge => edge.Id))
                    owner.Add(new(change.Path, Subject(edge.Id),
                        "This relationship touches the owning architecture boundary; internal code reachability is unknown.", [], [item]));
            }
            if (evidence.Length == 0 && ownership.Count == 0) unmatched.Add(change.Path);
        }

        var seen = new HashSet<string>(seeds, StringComparer.Ordinal);
        var queue = new Queue<(string Id, int Depth)>(seeds.Select(id => (id, 0)));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Depth == downstreamDepth) continue;
            foreach (var edge in indexes.Downstream[current.Id])
            {
                var next = GraphIndexes.LogicalTo(edge);
                downstream.Add(new("", Subject(edge.Id), "Known logical downstream architecture relationship.",
                    edge.EvidenceIds.Select(id => indexes.Evidence[id]).ToArray(), []));
                if (seen.Add(next))
                {
                    downstream.Add(new("", Subject(next), "Potentially affected through bounded downstream architecture traversal.", [], []));
                    queue.Enqueue((next, current.Depth + 1));
                }
                if (seen.Count > limits.MaxNodes || downstream.Count > limits.MaxEdges * 2) throw DependencyLimit();
            }
        }

        var directResult = Unique(direct);
        var ownerResult = Unique(owner).Where(item => directResult.All(directItem =>
            directItem.Path != item.Path || directItem.Subject.Id != item.Subject.Id)).ToArray();
        var downstreamResult = Unique(downstream)
            .Where(item => directResult.All(directItem => directItem.Subject.Id != item.Subject.Id) && ownerResult.All(ownerItem => ownerItem.Subject.Id != item.Subject.Id))
            .ToArray();
        var markdown = RenderImpact(directResult, ownerResult, downstreamResult, unmatched);
        EnsureMarkdownLimit(markdown);
        return new(snapshot.Id, repository, "potential static impact", downstreamDepth,
            directResult, ownerResult, downstreamResult, unmatched.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), markdown);
    }

    private IEnumerable<GraphNode> Containers(IEnumerable<string> nodeIds)
    {
        var seen = nodeIds.ToHashSet(StringComparer.Ordinal);
        var queue = new Queue<string>(seen);
        while (queue.Count > 0)
            foreach (var edge in indexes.Incoming[queue.Dequeue()].Where(item => item.Kind == EdgeKind.Contains))
                if (seen.Add(edge.From)) queue.Enqueue(edge.From);
        return seen.Except(nodeIds, StringComparer.Ordinal).Select(id => indexes.Nodes[id]);
    }

    private HashSet<string> Descendants(string ownerId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { ownerId };
        var queue = new Queue<string>();
        queue.Enqueue(ownerId);
        while (queue.Count > 0)
            foreach (var edge in indexes.Outgoing[queue.Dequeue()].Where(item => item.Kind == EdgeKind.Contains))
                if (seen.Add(edge.To)) queue.Enqueue(edge.To);
        return seen;
    }

    private IEnumerable<GraphEdge> Adjacent(string id, DependencyDirection direction) => direction switch
    {
        DependencyDirection.Downstream => indexes.Downstream[id],
        DependencyDirection.Upstream => indexes.Upstream[id],
        _ => indexes.Downstream[id].Concat(indexes.Upstream[id]).DistinctBy(item => item.Id)
    };

    private static string Next(string current, GraphEdge edge) =>
        GraphIndexes.LogicalFrom(edge) == current ? GraphIndexes.LogicalTo(edge) : GraphIndexes.LogicalFrom(edge);

    private void AddTraversalSeeds(string subjectId, ISet<string> seeds)
    {
        if (indexes.Nodes.ContainsKey(subjectId)) seeds.Add(subjectId);
        else
        {
            var edge = indexes.Edges[subjectId];
            seeds.Add(GraphIndexes.LogicalFrom(edge));
            seeds.Add(GraphIndexes.LogicalTo(edge));
        }
    }

    private ArchitectureSubject Subject(string id) => new(id, indexes.Nodes.ContainsKey(id) ? "node" : "edge");

    private static bool Intersects(SourceRange? range, int? startLine, int? endLine) =>
        startLine is null || range is not null && range.StartLine <= endLine && range.EndLine >= startLine;

    private static ImpactItem[] Unique(IEnumerable<ImpactItem> items) => items
        .GroupBy(item => $"{item.Path}\0{item.Subject.Id}", StringComparer.Ordinal).Select(group => group.First())
        .OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Subject.Id, StringComparer.Ordinal).ToArray();

    private string RenderFlow(string from, string to, IReadOnlyList<DetailedGraphPath> paths)
    {
        var title = $"# {Escape(indexes.Nodes[from].Name)} → {Escape(indexes.Nodes[to].Name)}";
        var builder = new StringBuilder().AppendLine(title).AppendLine()
            .AppendLine($"Snapshot `{Code(snapshot.Id)}` · revision `{Code(repository.Revision)}`").AppendLine();
        if (paths.Count == 0) builder.AppendLine("No path was found within the requested bounds.");
        for (var pathIndex = 0; pathIndex < paths.Count; pathIndex++)
        {
            builder.AppendLine($"## Path {pathIndex + 1}");
            var path = paths[pathIndex];
            for (var index = 0; index < path.Edges.Count; index++)
            {
                var edge = path.Edges[index];
                var wording = edge.Kind is EdgeKind.Publishes or EdgeKind.Subscribes ? "asynchronously" : "via";
                builder.AppendLine($"{index + 1}. **{Escape(path.Nodes[index].Name)}** {wording} `{edge.Kind.ToString().ToLowerInvariant()}` → **{Escape(path.Nodes[index + 1].Name)}**");
                foreach (var evidenceId in edge.EvidenceIds)
                {
                    var evidence = indexes.Evidence[evidenceId];
                    builder.AppendLine($"   - `{Code(evidence.Path)}`{Range(evidence.Range)} · {evidence.Confidence.ToString().ToLowerInvariant()}");
                }
                if (edge.Resolution != Resolution.Resolved) builder.AppendLine($"   - Resolution: **{edge.Resolution.ToString().ToLowerInvariant()}**");
            }
            builder.AppendLine();
        }
        builder.AppendLine("_Generated architecture context. Verify important decisions against the cited source evidence._");
        return builder.ToString();
    }

    private string RenderImpact(
        IReadOnlyList<ImpactItem> direct,
        IReadOnlyList<ImpactItem> owner,
        IReadOnlyList<ImpactItem> downstream,
        IReadOnlyList<string> unmatched)
    {
        var builder = new StringBuilder().AppendLine("# Potential static change impact").AppendLine()
            .AppendLine($"Snapshot `{Code(snapshot.Id)}` · revision `{Code(repository.Revision)}`").AppendLine();
        AppendImpact(builder, "Direct evidence", direct);
        AppendImpact(builder, "Owner-level possibilities", owner);
        AppendImpact(builder, "Downstream architecture effects", downstream);
        builder.AppendLine("## Unmatched files");
        if (unmatched.Count == 0) builder.AppendLine("- None");
        else foreach (var path in unmatched.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) builder.AppendLine($"- `{Code(path)}`");
        builder.AppendLine().AppendLine("_Potential static impact only; this is not proof of runtime use, breakage, deployment state, or internal code reachability._");
        return builder.ToString();
    }

    private static void AppendImpact(StringBuilder builder, string heading, IReadOnlyList<ImpactItem> items)
    {
        builder.AppendLine($"## {heading}");
        if (items.Count == 0) builder.AppendLine("- None");
        else foreach (var item in items) builder.AppendLine($"- `{Code(item.Subject.Id)}` — {Escape(item.Reason)}");
        builder.AppendLine();
    }

    private static string Range(SourceRange? range) => range is null ? string.Empty : $":{range.StartLine}-{range.EndLine}";
    private static string Code(string value) => value.Replace("`", "'", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    private static string Escape(string value) => Code(value).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private void ValidateDepth(int depth)
    {
        if (depth <= 0 || depth > limits.MaxDependencyDepth)
            throw new ArgumentOutOfRangeException(nameof(depth), $"depth must be 1-{limits.MaxDependencyDepth}.");
    }

    private void EnsureMarkdownLimit(string markdown)
    {
        if (Encoding.UTF8.GetByteCount(markdown) > limits.MaxMarkdownBytes)
            throw new GraphQueryLimitException("MARKDOWN_LIMIT_EXCEEDED", $"Generated Markdown exceeds {limits.MaxMarkdownBytes} bytes; no partial document was returned.");
    }

    private GraphQueryLimitException DependencyLimit() => new(
        "QUERY_LIMIT_EXCEEDED",
        $"The context query exceeded {limits.MaxNodes} nodes or {limits.MaxEdges} edges; no partial result was returned.");
}
