using Archie.Contracts;

namespace Archie.Core;

public sealed class GraphValidationException(IReadOnlyList<string> errors)
    : Exception($"The canonical graph artifact is invalid:{Environment.NewLine}{string.Join(Environment.NewLine, errors.Select(error => $"- {error}"))}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class GraphValidator
{
    public static void Validate(GraphSnapshot snapshot)
    {
        var errors = new List<string>();
        if (snapshot.SchemaVersion != "architecture/v1")
        {
            errors.Add($"Unsupported schemaVersion '{snapshot.SchemaVersion}'.");
        }

        ValidateUniqueAndSorted(snapshot.Nodes.Select(node => node.Id), "node", errors);
        ValidateUniqueAndSorted(snapshot.Edges.Select(edge => edge.Id), "edge", errors);
        ValidateUniqueAndSorted(snapshot.Evidence.Select(evidence => evidence.Id), "evidence", errors);

        var nodeIds = snapshot.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var evidenceIds = snapshot.Evidence.Select(evidence => evidence.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var edge in snapshot.Edges)
        {
            if (!nodeIds.Contains(edge.From)) errors.Add($"Edge '{edge.Id}' has missing from endpoint '{edge.From}'.");
            if (!nodeIds.Contains(edge.To)) errors.Add($"Edge '{edge.Id}' has missing to endpoint '{edge.To}'.");
            if (edge.EvidenceIds.Count == 0) errors.Add($"Edge '{edge.Id}' has no evidence.");
            ValidateEvidenceReferences(edge.Id, edge.EvidenceIds, evidenceIds, errors);
            var actualProvenance = snapshot.Evidence
                .Where(evidence => edge.EvidenceIds.Contains(evidence.Id, StringComparer.Ordinal))
                .Select(evidence => evidence.Provenance)
                .Distinct()
                .OrderBy(value => value.ToString(), StringComparer.Ordinal);
            if (!edge.Provenance.OrderBy(value => value.ToString(), StringComparer.Ordinal).SequenceEqual(actualProvenance))
                errors.Add($"Edge '{edge.Id}' provenance does not match its evidence.");
        }

        foreach (var node in snapshot.Nodes)
        {
            ValidateEvidenceReferences(node.Id, node.EvidenceIds, evidenceIds, errors);
        }

        foreach (var evidence in snapshot.Evidence)
        {
            if (Path.IsPathRooted(evidence.Path) || evidence.Path.Split('/').Contains("..", StringComparer.Ordinal))
            {
                errors.Add($"Evidence '{evidence.Id}' path must be repository-relative.");
            }
            if (evidence.Provenance == EvidenceProvenance.Manual && (evidence.ScannerId is not null || evidence.ScannerVersion is not null))
                errors.Add($"Manual evidence '{evidence.Id}' must not claim scanner metadata.");
            if ((evidence.ScannerId is null) != (evidence.ScannerVersion is null))
                errors.Add($"Evidence '{evidence.Id}' must provide both scannerId and scannerVersion or neither.");
            if (evidence.Range is { } range &&
                (range.EndLine < range.StartLine || range.EndLine == range.StartLine && range.EndColumn < range.StartColumn))
                errors.Add($"Evidence '{evidence.Id}' range ends before it starts.");
        }

        if (errors.Count > 0) throw new GraphValidationException(errors);
    }

    private static void ValidateUniqueAndSorted(IEnumerable<string> ids, string kind, ICollection<string> errors)
    {
        var values = ids.ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            errors.Add($"Duplicate {kind} IDs are not allowed.");
        }

        if (!values.SequenceEqual(values.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            errors.Add($"{char.ToUpperInvariant(kind[0]) + kind[1..]}s must be ordered by ID.");
        }
    }

    private static void ValidateEvidenceReferences(
        string subjectId,
        IEnumerable<string> references,
        IReadOnlySet<string> evidenceIds,
        ICollection<string> errors)
    {
        foreach (var evidenceId in references)
        {
            if (!evidenceIds.Contains(evidenceId))
            {
                errors.Add($"'{subjectId}' references missing evidence '{evidenceId}'.");
            }
        }
    }
}
