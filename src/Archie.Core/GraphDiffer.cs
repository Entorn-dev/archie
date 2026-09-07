using System.Text.Json;
using System.Text.Json.Nodes;
using Archie.Contracts;

namespace Archie.Core;

public sealed record GraphSubjectChange<T>(string Id, T Before, T After);

public sealed record ScannerVersionChange(
    string Id,
    IReadOnlyList<string> BeforeVersions,
    IReadOnlyList<string> AfterVersions);

public sealed record GraphDiff(
    string BeforeSnapshotId,
    string AfterSnapshotId,
    bool CompositionChanged,
    IReadOnlyList<GraphNode> AddedNodes,
    IReadOnlyList<GraphNode> RemovedNodes,
    IReadOnlyList<GraphSubjectChange<GraphNode>> ChangedNodes,
    IReadOnlyList<GraphEdge> AddedEdges,
    IReadOnlyList<GraphEdge> RemovedEdges,
    IReadOnlyList<GraphSubjectChange<GraphEdge>> ChangedEdges,
    IReadOnlyList<Evidence> AddedEvidence,
    IReadOnlyList<Evidence> RemovedEvidence,
    IReadOnlyList<GraphSubjectChange<Evidence>> ChangedEvidence,
    IReadOnlyList<string> AddedUnresolved,
    IReadOnlyList<string> Resolved,
    IReadOnlyList<ScannerVersionChange> ScannerVersionChanges)
{
    public bool HasChanges => CompositionChanged || AddedNodes.Count > 0 || RemovedNodes.Count > 0 ||
        ChangedNodes.Count > 0 || AddedEdges.Count > 0 || RemovedEdges.Count > 0 ||
        ChangedEdges.Count > 0 || AddedEvidence.Count > 0 || RemovedEvidence.Count > 0 ||
        ChangedEvidence.Count > 0;
}

public sealed class GraphDiffer
{
    public GraphDiff Compare(GraphSnapshot before, GraphSnapshot after)
    {
        GraphValidator.Validate(before);
        GraphValidator.Validate(after);
        before = Normalize(before);
        after = Normalize(after);

        var nodes = CompareById(before.Nodes, after.Nodes, item => item.Id);
        var edges = CompareById(before.Edges, after.Edges, item => item.Id);
        var evidence = CompareById(before.Evidence, after.Evidence, item => item.Id);
        var beforeResolutions = Resolutions(before);
        var afterResolutions = Resolutions(after);

        return new(
            before.Id,
            after.Id,
            !Equivalent(before.Composition, after.Composition) || before.OverlayDigest != after.OverlayDigest ||
                before.GovernanceDate != after.GovernanceDate,
            nodes.Added,
            nodes.Removed,
            nodes.Changed,
            edges.Added,
            edges.Removed,
            edges.Changed,
            evidence.Added,
            evidence.Removed,
            evidence.Changed,
            afterResolutions
                .Where(pair => pair.Value != Resolution.Resolved &&
                    (!beforeResolutions.TryGetValue(pair.Key, out var previous) || previous == Resolution.Resolved))
                .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray(),
            beforeResolutions
                .Where(pair => pair.Value != Resolution.Resolved &&
                    afterResolutions.TryGetValue(pair.Key, out var current) && current == Resolution.Resolved)
                .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray(),
            ScannerChanges(before.Evidence, after.Evidence));
    }

    private static GraphSnapshot Normalize(GraphSnapshot snapshot)
    {
        using var stream = new MemoryStream(ContractJson.WriteGraphSnapshot(snapshot), writable: false);
        return ContractJson.ReadGraphSnapshot(stream);
    }

    private static (IReadOnlyList<T> Added, IReadOnlyList<T> Removed, IReadOnlyList<GraphSubjectChange<T>> Changed)
        CompareById<T>(IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, string> id)
    {
        var beforeById = before.ToDictionary(id, StringComparer.Ordinal);
        var afterById = after.ToDictionary(id, StringComparer.Ordinal);
        var added = after.Where(item => !beforeById.ContainsKey(id(item))).OrderBy(id, StringComparer.Ordinal).ToArray();
        var removed = before.Where(item => !afterById.ContainsKey(id(item))).OrderBy(id, StringComparer.Ordinal).ToArray();
        var changed = beforeById.Keys.Intersect(afterById.Keys, StringComparer.Ordinal)
            .Where(key => !Equivalent(beforeById[key], afterById[key]))
            .Order(StringComparer.Ordinal)
            .Select(key => new GraphSubjectChange<T>(key, beforeById[key], afterById[key]))
            .ToArray();
        return (added, removed, changed);
    }

    private static bool Equivalent<T>(T left, T right) => JsonNode.DeepEquals(
        JsonSerializer.SerializeToNode(left, ContractJson.Options),
        JsonSerializer.SerializeToNode(right, ContractJson.Options));

    private static Dictionary<string, Resolution> Resolutions(GraphSnapshot snapshot) => snapshot.Nodes
        .Select(item => (item.Id, item.Resolution))
        .Concat(snapshot.Edges.Select(item => (item.Id, item.Resolution)))
        .ToDictionary(item => item.Id, item => item.Resolution, StringComparer.Ordinal);

    private static IReadOnlyList<ScannerVersionChange> ScannerChanges(
        IReadOnlyList<Evidence> before,
        IReadOnlyList<Evidence> after)
    {
        var beforeVersions = Versions(before);
        var afterVersions = Versions(after);
        return beforeVersions.Keys.Union(afterVersions.Keys, StringComparer.Ordinal)
            .Where(id => !beforeVersions.GetValueOrDefault(id, []).SequenceEqual(afterVersions.GetValueOrDefault(id, []), StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(id => new ScannerVersionChange(id, beforeVersions.GetValueOrDefault(id, []), afterVersions.GetValueOrDefault(id, [])))
            .ToArray();
    }

    private static Dictionary<string, IReadOnlyList<string>> Versions(IEnumerable<Evidence> evidence) => evidence
        .Where(item => item.ScannerId is not null && item.ScannerVersion is not null)
        .GroupBy(item => item.ScannerId!, StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<string>)group.Select(item => item.ScannerVersion!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
}
