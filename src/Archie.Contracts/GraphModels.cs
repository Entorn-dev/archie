using System.Text.Json;
using Entorn.Scanner.Contracts;

namespace Archie.Contracts;

public enum PathDirection { Upstream, Downstream }

public sealed record GraphCompositionInput(
    RepositoryRevision Repository,
    string ObservationBundleDigest);

public sealed record GraphComposition(
    string Policy,
    IReadOnlyList<GraphCompositionInput> Inputs);

public sealed record GraphNode(
    string Id,
    NodeKind Kind,
    string Name,
    Resolution Resolution,
    IReadOnlyList<string> Aliases,
    IReadOnlyDictionary<string, JsonElement> Properties,
    IReadOnlyList<string> EvidenceIds);

public sealed record GraphEdge(
    string Id,
    EdgeKind Kind,
    string From,
    string To,
    Confidence Confidence,
    Resolution Resolution,
    IReadOnlyList<EvidenceProvenance> Provenance,
    IReadOnlyDictionary<string, JsonElement> Properties,
    IReadOnlyList<string> EvidenceIds);

public sealed record MergeDecision(string Id, string Outcome, IReadOnlyList<string> CandidateIds, string Rationale);

public sealed record GraphSnapshot(
    string SchemaVersion,
    string Id,
    GraphComposition Composition,
    string? OverlayDigest,
    DateOnly GovernanceDate,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<MergeDecision> MergeDecisions,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<RedactionEvent> Redactions);

public sealed record SavedPathQuery(
    string From,
    string? To,
    PathDirection Direction,
    int MaxDepth,
    IReadOnlyList<EdgeKind> EdgeKinds);

public sealed record SavedView(
    string SchemaVersion,
    string Id,
    string Name,
    string Description,
    string LayoutDirection,
    string? SelectedId,
    SavedPathQuery PathQuery);
