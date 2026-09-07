using System.Text;
using System.Text.Json;
using Archie.Contracts;

namespace Archie.Core;

public enum CompactContextDetail
{
    Summary,
    Evidence
}

public sealed partial class ArchitectureContextService
{
    private const int SummarySourcePreviewItems = 1;

    public string RenderCompact(ContextLookupResult result, CompactContextDetail detail, int maxItems)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, 1);
        var builder = new StringBuilder();
        var target = result.Path is not null
            ? result.Line is null ? $"`{Code(result.Path)}`" : $"`{Code(result.Path)}:{result.Line}`"
            : SubjectLabel(result.SubjectId!);
        builder.AppendLine($"# Context for {target}").AppendLine();
        builder.AppendLine(result.Match switch
        {
            "no-match" => $"No architecture context matched {target}.",
            "ambiguous" => $"{target} matches {result.Nodes.Count + result.Edges.Count} architecture subjects; preserve the ambiguity.",
            _ when result.Path is not null && result.Ownership.Count > 0 => $"{target} belongs to {JoinOwners(result.Ownership)}.",
            _ => $"Matched {target}."
        }).AppendLine();

        var subjects = result.Nodes.Select(node => CompactNode(node, detail))
            .Concat(result.Edges.Select(edge => CompactRelationship(edge, detail)))
            .ToArray();
        AppendSection(builder, "Subjects", subjects, maxItems);
        AppendSection(builder, "Containers", result.Containers.Select(node => CompactNode(node, detail)), maxItems);
        AppendSection(builder, "Incoming", result.Incoming.Select(edge => CompactRelationship(edge, detail)), maxItems);
        AppendSection(builder, "Outgoing", result.Outgoing.Select(edge => CompactRelationship(edge, detail)), maxItems);
        AppendSection(builder, "Ownership", result.Ownership.Select(OwnershipItem), maxItems);
        if (detail == CompactContextDetail.Summary)
            AppendSourcePreview(builder, result.Nodes.SelectMany(node => node.EvidenceIds)
                .Concat(result.Edges.SelectMany(edge => edge.EvidenceIds))
                .Concat(result.Incoming.SelectMany(edge => edge.EvidenceIds))
                .Concat(result.Outgoing.SelectMany(edge => edge.EvidenceIds))
                .Distinct(StringComparer.Ordinal).Select(id => SourceItem(indexes.Evidence[id]))
                .Concat(result.Ownership.Select(item => $"`{Code(item.Path)}`")));
        else
            AppendSection(builder, "Evidence", result.Evidence.Select(EvidenceItem), maxItems);
        AppendSection(builder, "Diagnostics", result.Diagnostics.Select(DiagnosticItem), maxItems);

        builder.AppendLine(detail == CompactContextDetail.Summary
            ? "Expand: `detail: evidence` or `detail: full`."
            : "Expand: `detail: full`.");
        builder.AppendLine($"Snapshot `{Code(result.SnapshotId)}` @ `{Code(result.Repository.Revision)}`");
        return EnsureCompactLimit(builder.ToString(), detail);

        string OwnershipItem(SourceOwnership ownership) =>
            $"`{Code(ownership.Path)}` → {SubjectLabel(ownership.OwnerId)} · {EnumName(ownership.OwnershipKind)} · {EnumName(ownership.Resolution)}" +
            (detail == CompactContextDetail.Evidence
                ? $"\n  - {EnumName(ownership.Confidence)} · claim {EnumName(ownership.ClaimResolution)} · rule `{Code(ownership.DerivationRule)}` · scanner `{Code(ownership.ScannerId)}@{Code(ownership.ScannerVersion)}`"
                : string.Empty);

        string EvidenceItem(Evidence evidence) =>
            $"`{Citation(evidence)}`" + (detail == CompactContextDetail.Evidence
                ? $"\n  - Evidence `{Code(evidence.Id)}` · {EnumName(evidence.Provenance)} · {EnumName(evidence.Confidence)} · method `{Code(evidence.ExtractionMethod)}`"
                : string.Empty);

        string DiagnosticItem(Diagnostic diagnostic) =>
            $"{Code(diagnostic.Severity)} `{Code(diagnostic.Code)}` — {Escape(diagnostic.Message)}" +
            (diagnostic.SubjectId is null ? string.Empty : $" · [`{Code(diagnostic.SubjectId)}`]");
    }

    public string RenderCompact(DependencyResult result, CompactContextDetail detail, int maxItems)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, 1);
        var root = indexes.Nodes[result.RootId];
        var builder = new StringBuilder()
            .AppendLine($"# Dependencies for {SubjectLabel(root.Id)}").AppendLine();
        builder.AppendLine(result.Edges.Count == 0
            ? $"No {EnumName(result.Direction)} dependencies were found within depth {result.Depth}."
            : $"Found {result.Edges.Count} {EnumName(result.Direction)} relationships across {Math.Max(0, result.Nodes.Count - 1)} related subjects within depth {result.Depth}.")
            .AppendLine();
        AppendSection(builder, "Relationships", result.Edges.Select(edge => CompactRelationship(edge, detail)), maxItems);
        if (detail == CompactContextDetail.Summary)
            AppendSourcePreview(builder, result.Edges.SelectMany(edge => edge.EvidenceIds)
                .Distinct(StringComparer.Ordinal).Select(id => SourceItem(indexes.Evidence[id])));
        builder.AppendLine(detail == CompactContextDetail.Summary
            ? "Expand: `detail: evidence` or `detail: full`."
            : "Expand: `detail: full`.");
        builder.AppendLine($"Snapshot `{Code(result.SnapshotId)}` @ `{Code(result.Repository.Revision)}`");
        return EnsureCompactLimit(builder.ToString(), detail);
    }

    public string RenderCompact(FlowTraceResult result, CompactContextDetail detail, int maxItems)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, 1);
        var shown = Math.Min(maxItems, result.Paths.Count);
        var builder = new StringBuilder().AppendLine($"# Flow trace ({shown}/{result.Paths.Count} paths)").AppendLine();
        if (result.Paths.Count == 0)
        {
            builder.AppendLine("No path was found within the requested bounds.").AppendLine();
        }
        else
        {
            for (var pathIndex = 0; pathIndex < shown; pathIndex++)
            {
                var path = result.Paths[pathIndex];
                builder.AppendLine($"### Path {pathIndex + 1} ({path.Edges.Count} {Plural(path.Edges.Count, "transition", "transitions")})");
                for (var edgeIndex = 0; edgeIndex < path.Edges.Count; edgeIndex++)
                {
                    var edge = path.Edges[edgeIndex];
                    var transition = edge.Kind switch
                    {
                        EdgeKind.Publishes or EdgeKind.Subscribes => $"asynchronously {EnumName(edge.Kind)}",
                        EdgeKind.Calls => $"synchronously {EnumName(edge.Kind)}",
                        _ => EnumName(edge.Kind)
                    };
                    var from = detail == CompactContextDetail.Summary
                        ? $"`{Code(path.Nodes[edgeIndex].Id)}`"
                        : SubjectLabel(path.Nodes[edgeIndex].Id);
                    var to = detail == CompactContextDetail.Summary
                        ? $"`{Code(path.Nodes[edgeIndex + 1].Id)}`"
                        : SubjectLabel(path.Nodes[edgeIndex + 1].Id);
                    builder.AppendLine($"{edgeIndex + 1}. {from} —{transition}→ {to}" +
                        (edge.Resolution == Resolution.Resolved ? string.Empty : $" · {EnumName(edge.Resolution)}") +
                        (detail == CompactContextDetail.Summary ? string.Empty : CompactCitations(edge.EvidenceIds)));
                    if (detail == CompactContextDetail.Evidence)
                        foreach (var evidenceLine in CompactEvidenceLines(edge.EvidenceIds).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                            builder.AppendLine($" {evidenceLine}");
                }
                builder.AppendLine();
            }
        }
        if (detail == CompactContextDetail.Summary)
            AppendSourcePreview(builder, result.Paths.Take(maxItems).SelectMany(path => path.Edges)
                .SelectMany(edge => edge.EvidenceIds).Distinct(StringComparer.Ordinal)
                .Select(id => SourceItem(indexes.Evidence[id])));
        builder.AppendLine(detail == CompactContextDetail.Summary
            ? "Expand: `detail: evidence` or `detail: full`."
            : "Expand: `detail: full`.");
        builder.AppendLine($"Snapshot `{Code(result.SnapshotId)}` @ `{Code(result.Repository.Revision)}`");
        return EnsureCompactLimit(builder.ToString(), detail);
    }

    public string RenderCompact(ChangeImpactResult result, CompactContextDetail detail, int maxItems)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, 1);
        var builder = new StringBuilder().AppendLine("# Potential static change impact").AppendLine()
            .AppendLine($"Potential static impact: {result.Direct.Count} direct, {result.Owner.Count} owner-level, {result.Downstream.Count} downstream, and {result.Unmatched.Count} unmatched within downstream depth {result.DownstreamDepth}.")
            .AppendLine("Internal code reachability remains unknown; this is not proof of runtime use, breakage, or deployment state.")
            .AppendLine();
        AppendImpactSection(builder, "Direct evidence", result.Direct, detail, maxItems);
        AppendImpactSection(builder, "Owner-level possibilities", result.Owner, detail, maxItems);
        AppendImpactSection(builder, "Downstream architecture effects", result.Downstream, detail, maxItems);
        AppendCountedSection(builder, "Unmatched files", result.Unmatched.Select(path => $"`{Code(path)}`"), maxItems);
        if (detail == CompactContextDetail.Summary)
            AppendSourcePreview(builder, result.Direct.Concat(result.Owner).Concat(result.Downstream)
                .SelectMany(item => item.Evidence.Select(SourceItem)
                    .Concat(item.Ownership.Select(ownership => $"`{Code(ownership.Path)}`"))));
        builder.AppendLine(detail == CompactContextDetail.Summary
            ? "Expand: `detail: evidence` or `detail: full`."
            : "Expand: `detail: full`.");
        builder.AppendLine($"Snapshot `{Code(result.SnapshotId)}` @ `{Code(result.Repository.Revision)}`");
        return EnsureCompactLimit(builder.ToString(), detail);
    }

    private void AppendImpactSection(
        StringBuilder builder,
        string heading,
        IEnumerable<ImpactItem> source,
        CompactContextDetail detail,
        int maxItems) =>
        AppendCountedSection(builder, heading, source.Select(item => CompactImpactItem(item, detail)), maxItems);

    private string CompactImpactItem(ImpactItem item, CompactContextDetail detail)
    {
        if (detail == CompactContextDetail.Summary) return CompactSubject(item.Subject.Id);
        var subject = indexes.Nodes.ContainsKey(item.Subject.Id) ? SubjectLabel(item.Subject.Id) : ImpactRelationship(item.Subject.Id);
        var path = item.Path.Length == 0 ? string.Empty : $" · changed `{Code(item.Path)}`";
        var evidence = item.Evidence
            .DistinctBy(value => value.Id).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
        var ownership = item.Ownership
            .DistinctBy(value => $"{value.Path}\0{value.OwnerId}\0{value.DerivationRule}")
            .OrderBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.OwnerId, StringComparer.Ordinal).ToArray();
        var evidenceLines = CompactEvidenceLines(evidence);
        var expanded = detail == CompactContextDetail.Evidence
            ? (evidenceLines.Length == 0 ? string.Empty : $"\n{evidenceLines}") + string.Concat(ownership.Select(value =>
                $"\n  - Ownership `{Code(value.Path)}` → {SubjectLabel(value.OwnerId)} · {EnumName(value.OwnershipKind)} · {EnumName(value.Confidence)} · claim {EnumName(value.ClaimResolution)} · {EnumName(value.Resolution)} · rule `{Code(value.DerivationRule)}` · scanner `{Code(value.ScannerId)}@{Code(value.ScannerVersion)}`"))
            : string.Empty;
        return $"{subject} — {Escape(item.Reason)}{path}{CompactCitations(evidence)}{expanded}";
    }

    private string ImpactRelationship(string edgeId)
    {
        var edge = indexes.Edges[edgeId];
        var from = indexes.Nodes[GraphIndexes.LogicalFrom(edge)];
        var to = indexes.Nodes[GraphIndexes.LogicalTo(edge)];
        return $"{Escape(from.Name)} [`{Code(from.Id)}`] —{EnumName(edge.Kind)}→ {Escape(to.Name)} [`{Code(to.Id)}`] [`{Code(edge.Id)}`]" +
            (edge.Resolution == Resolution.Resolved ? string.Empty : $" · {EnumName(edge.Resolution)}");
    }

    private string CompactNode(GraphNode node, CompactContextDetail detail) =>
        (detail == CompactContextDetail.Summary ? $"`{Code(node.Id)}`" : $"{Escape(node.Name)} [`{Code(node.Id)}`]") +
        $" · {EnumName(node.Kind)}" +
        (node.Resolution == Resolution.Resolved ? string.Empty : $" · {EnumName(node.Resolution)}") +
        (detail == CompactContextDetail.Summary ? string.Empty : CompactCitations(node.EvidenceIds)) +
        CompactEvidenceLines(node.EvidenceIds, detail);

    private string CompactRelationship(GraphEdge edge, CompactContextDetail detail)
    {
        var from = indexes.Nodes[GraphIndexes.LogicalFrom(edge)];
        var to = indexes.Nodes[GraphIndexes.LogicalTo(edge)];
        var fromLabel = detail == CompactContextDetail.Summary ? $"`{Code(from.Id)}`" : $"{Escape(from.Name)} [`{Code(from.Id)}`]";
        var toLabel = detail == CompactContextDetail.Summary ? $"`{Code(to.Id)}`" : $"{Escape(to.Name)} [`{Code(to.Id)}`]";
        return $"{fromLabel} —{EnumName(edge.Kind)}→ {toLabel}" +
            (edge.Resolution == Resolution.Resolved ? string.Empty : $" · {EnumName(edge.Resolution)}") +
            (detail == CompactContextDetail.Summary ? string.Empty : CompactCitations(edge.EvidenceIds)) +
            CompactEvidenceLines(edge.EvidenceIds, detail);
    }

    private string CompactSubject(string id)
    {
        if (indexes.Nodes.ContainsKey(id)) return $"`{Code(id)}`";
        var edge = indexes.Edges[id];
        return $"`{Code(GraphIndexes.LogicalFrom(edge))}` —{EnumName(edge.Kind)}→ `{Code(GraphIndexes.LogicalTo(edge))}` [`{Code(edge.Id)}`]" +
            (edge.Resolution == Resolution.Resolved ? string.Empty : $" · {EnumName(edge.Resolution)}");
    }

    private string CompactCitations(IEnumerable<string> evidenceIds) =>
        CompactCitations(evidenceIds.Select(id => indexes.Evidence[id]));

    private static string CompactCitations(IEnumerable<Evidence> evidence)
    {
        var citations = evidence.Select(Citation).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return citations.Length == 0 ? string.Empty : $" — {string.Join(", ", citations.Select(item => $"`{item}`"))}";
    }

    private string CompactEvidenceLines(IEnumerable<string> evidenceIds, CompactContextDetail detail)
    {
        if (detail != CompactContextDetail.Evidence) return string.Empty;
        var lines = CompactEvidenceLines(evidenceIds);
        return lines.Length == 0 ? string.Empty : $"\n{lines}";
    }

    private string CompactEvidenceLines(IEnumerable<string> evidenceIds) =>
        CompactEvidenceLines(evidenceIds.Select(id => indexes.Evidence[id]));

    private static string CompactEvidenceLines(IEnumerable<Evidence> evidence) =>
        string.Join('\n', evidence.DistinctBy(value => value.Id).OrderBy(value => value.Id, StringComparer.Ordinal).Select(value =>
        {
            return $"  - Evidence `{Code(value.Id)}` · {EnumName(value.Provenance)} · {EnumName(value.Confidence)} · method `{Code(value.ExtractionMethod)}`";
        }));

    private string SubjectLabel(string id) => indexes.Nodes.TryGetValue(id, out var node)
        ? $"{Escape(node.Name)} [`{Code(node.Id)}`]"
        : $"[`{Code(id)}`]";

    private string JoinOwners(IEnumerable<SourceOwnership> ownership) => string.Join(
        ", ", ownership.Select(item => SubjectLabel(item.OwnerId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private static void AppendSection(StringBuilder builder, string heading, IEnumerable<string> source, int maxItems)
    {
        var items = source.Distinct(StringComparer.Ordinal).ToArray();
        if (items.Length == 0) return;
        var shown = Math.Min(maxItems, items.Length);
        builder.AppendLine($"## {heading} ({shown}/{items.Length})");
        foreach (var item in items.Take(shown)) builder.AppendLine($"- {item}");
        builder.AppendLine();
    }

    private static void AppendCountedSection(StringBuilder builder, string heading, IEnumerable<string> source, int maxItems)
    {
        var items = source.Distinct(StringComparer.Ordinal).ToArray();
        var shown = Math.Min(maxItems, items.Length);
        builder.AppendLine($"## {heading} ({shown}/{items.Length})");
        if (items.Length == 0) builder.AppendLine("None.");
        else foreach (var item in items.Take(shown)) builder.AppendLine($"- {item}");
        builder.AppendLine();
    }

    private static void AppendSourcePreview(StringBuilder builder, IEnumerable<string> source)
    {
        var items = source.Distinct(StringComparer.Ordinal).ToArray();
        if (items.Length > 0) builder.AppendLine($"Sources ({SummarySourcePreviewItems}/{items.Length}): {items[0]}").AppendLine();
    }

    private string EnsureCompactLimit(string markdown, CompactContextDetail detail)
    {
        var limit = detail == CompactContextDetail.Summary ? limits.MaxSummaryBytes : limits.MaxEvidenceBytes;
        if (Encoding.UTF8.GetByteCount(markdown) > limit)
            throw new GraphQueryLimitException("COMPACT_RESPONSE_LIMIT_EXCEEDED",
                $"Compact {EnumName(detail)} response exceeds {limit} bytes; lower maxItems or request detail 'full'.");
        return markdown;
    }

    private static string Citation(Evidence evidence) => evidence.Range is null
        ? Code(evidence.Path)
        : $"{Code(evidence.Path)}:{evidence.Range.StartLine}-{evidence.Range.EndLine}";

    private static string SourceItem(Evidence evidence) => $"`{Citation(evidence)}`";

    private static string Plural(int count, string singular, string plural) => count == 1 ? singular : plural;

    private static string EnumName<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
}
