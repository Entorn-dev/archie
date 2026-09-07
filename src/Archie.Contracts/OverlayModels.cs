using System.Text.Json;
using System.Text.Json.Serialization;

namespace Archie.Contracts;

public sealed record OverlayEntryGovernance(
    string? Owner,
    string? Reviewer,
    string Rationale,
    DateOnly CreatedOn,
    DateOnly? LastReviewedOn,
    int? ReviewIntervalDays,
    DateOnly? ExpiresOn);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ManualNodeOverlay), "manual-node")]
[JsonDerivedType(typeof(ManualRelationshipOverlay), "manual-relationship")]
[JsonDerivedType(typeof(BindingOverlay), "binding")]
[JsonDerivedType(typeof(MergeOverlay), "merge")]
[JsonDerivedType(typeof(SuppressionOverlay), "suppression")]
[JsonDerivedType(typeof(AnnotationOverlay), "annotation")]
public abstract record OverlayEntry(string Id, OverlayEntryGovernance Governance);

public sealed record ManualNodeOverlay(
    string Id,
    OverlayEntryGovernance Governance,
    GraphNode Node,
    string EvidencePath,
    SourceRange? Range) : OverlayEntry(Id, Governance);

public sealed record ManualRelationshipOverlay(
    string Id,
    OverlayEntryGovernance Governance,
    EdgeKind Relationship,
    string From,
    string To,
    Confidence Confidence,
    Resolution Resolution,
    IReadOnlyDictionary<string, JsonElement> Properties,
    string EvidencePath,
    SourceRange? Range) : OverlayEntry(Id, Governance);

public sealed record BindingOverlay(
    string Id,
    OverlayEntryGovernance Governance,
    string CandidateKey,
    string TargetId) : OverlayEntry(Id, Governance);

public sealed record MergeOverlay(
    string Id,
    OverlayEntryGovernance Governance,
    IReadOnlyList<string> CandidateKeys,
    string TargetId) : OverlayEntry(Id, Governance);

public sealed record SuppressionOverlay(
    string Id,
    OverlayEntryGovernance Governance,
    string TargetId) : OverlayEntry(Id, Governance);

public sealed record AnnotationOverlay(
    string Id,
    OverlayEntryGovernance Governance,
    string TargetId,
    IReadOnlyList<string> Aliases,
    IReadOnlyDictionary<string, JsonElement> Properties,
    string EvidencePath,
    SourceRange? Range) : OverlayEntry(Id, Governance);

public sealed record ArchitectureOverlay(
    string SchemaVersion,
    string DefaultOwner,
    IReadOnlyList<OverlayEntry> Entries);
