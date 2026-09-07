using System.Collections.Frozen;
using Archie.Contracts;

namespace Archie.Core;

public sealed class GraphIndexes
{
    public GraphIndexes(GraphSnapshot snapshot)
    {
        Nodes = snapshot.Nodes.ToFrozenDictionary(node => node.Id, StringComparer.Ordinal);
        Edges = snapshot.Edges.ToFrozenDictionary(edge => edge.Id, StringComparer.Ordinal);
        Evidence = snapshot.Evidence.ToFrozenDictionary(item => item.Id, StringComparer.Ordinal);
        Outgoing = BuildAdjacency(snapshot.Nodes, snapshot.Edges, edge => edge.From);
        Incoming = BuildAdjacency(snapshot.Nodes, snapshot.Edges, edge => edge.To);
        Downstream = BuildLogicalAdjacency(snapshot.Nodes, snapshot.Edges, downstream: true);
        Upstream = BuildLogicalAdjacency(snapshot.Nodes, snapshot.Edges, downstream: false);
        EvidenceSubjects = BuildEvidenceSubjects(snapshot);
        EvidenceByPath = snapshot.Evidence
            .GroupBy(item => item.Path, StringComparer.Ordinal)
            .ToFrozenDictionary(
                group => group.Key,
                group => (IReadOnlyList<Evidence>)group.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
    }

    public FrozenDictionary<string, GraphNode> Nodes { get; }
    public FrozenDictionary<string, GraphEdge> Edges { get; }
    public FrozenDictionary<string, Evidence> Evidence { get; }
    public FrozenDictionary<string, IReadOnlyList<GraphEdge>> Outgoing { get; }
    public FrozenDictionary<string, IReadOnlyList<GraphEdge>> Incoming { get; }
    public FrozenDictionary<string, IReadOnlyList<GraphEdge>> Downstream { get; }
    public FrozenDictionary<string, IReadOnlyList<GraphEdge>> Upstream { get; }
    public FrozenDictionary<string, IReadOnlyList<string>> EvidenceSubjects { get; }
    public FrozenDictionary<string, IReadOnlyList<Evidence>> EvidenceByPath { get; }

    public static string LogicalFrom(GraphEdge edge) => edge.Kind == EdgeKind.Subscribes ? edge.To : edge.From;

    public static string LogicalTo(GraphEdge edge) => edge.Kind == EdgeKind.Subscribes ? edge.From : edge.To;

    private static FrozenDictionary<string, IReadOnlyList<GraphEdge>> BuildAdjacency(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        Func<GraphEdge, string> keySelector)
    {
        var grouped = edges
            .GroupBy(keySelector, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GraphEdge>)group.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        return nodes.ToFrozenDictionary(
            node => node.Id,
            node => grouped.GetValueOrDefault(node.Id, Array.Empty<GraphEdge>()),
            StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, IReadOnlyList<GraphEdge>> BuildLogicalAdjacency(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        bool downstream) =>
        BuildAdjacency(nodes, edges, edge => downstream ? LogicalFrom(edge) : LogicalTo(edge));

    private static FrozenDictionary<string, IReadOnlyList<string>> BuildEvidenceSubjects(GraphSnapshot snapshot)
    {
        var subjects = snapshot.Evidence.ToDictionary(item => item.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var node in snapshot.Nodes)
            foreach (var evidenceId in node.EvidenceIds) subjects[evidenceId].Add(node.Id);
        foreach (var edge in snapshot.Edges)
            foreach (var evidenceId in edge.EvidenceIds) subjects[evidenceId].Add(edge.Id);
        return subjects.ToFrozenDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }
}
