using Archie.Contracts;

namespace Archie.Core;

public sealed class ArtifactValidationException(IReadOnlyList<string> errors)
    : Exception($"The artifact is invalid:{Environment.NewLine}{string.Join(Environment.NewLine, errors.Select(error => $"- {error}"))}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class ObservationValidator
{
    public static void Validate(ObservationBundle bundle)
    {
        var errors = new List<string>();
        if (bundle.SchemaVersion != "observations/v1") errors.Add($"Unsupported schemaVersion '{bundle.SchemaVersion}'.");
        if (bundle.Source == ObservationSource.Scanner && bundle.Scanners.Count == 0)
            errors.Add("Scanner-produced bundles must identify at least one scanner.");
        if (bundle.Source == ObservationSource.Authored && bundle.Scanners.Count != 0)
            errors.Add("Authored bundles must not claim scanner identities.");
        ValidateUnique(bundle.Observations.Select(item => item.Id), "observation", errors);
        ValidateUnique(bundle.Observations.Select(item => item.Evidence.Id), "evidence", errors);

        foreach (var observation in bundle.Observations)
        {
            var evidence = observation.Evidence;
            if (evidence.ClaimId != observation.Id) errors.Add($"Evidence '{evidence.Id}' must claim observation '{observation.Id}'.");
            if (Path.IsPathRooted(evidence.Path) || evidence.Path.Split('/').Contains("..", StringComparer.Ordinal))
                errors.Add($"Evidence '{evidence.Id}' path must be repository-relative.");
            if (bundle.Source == ObservationSource.Scanner &&
                (evidence.ScannerId is null || evidence.ScannerVersion is null ||
                 !bundle.Scanners.Any(scanner => scanner.Id == evidence.ScannerId && scanner.Version == evidence.ScannerVersion)))
                errors.Add($"Evidence '{evidence.Id}' does not identify a declared scanner and version.");
            if (bundle.Source == ObservationSource.Authored && (evidence.ScannerId is not null || evidence.ScannerVersion is not null))
                errors.Add($"Authored evidence '{evidence.Id}' must not claim scanner metadata.");
        }

        if (errors.Count > 0) throw new ArtifactValidationException(errors);
    }

    private static void ValidateUnique(IEnumerable<string> ids, string kind, ICollection<string> errors)
    {
        var values = ids.ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            errors.Add($"Duplicate {kind} IDs are not allowed.");
    }
}

public static class OverlayValidator
{
    public static void Validate(ArchitectureOverlay overlay)
    {
        var errors = new List<string>();
        if (overlay.SchemaVersion != "overlay/v1") errors.Add($"Unsupported schemaVersion '{overlay.SchemaVersion}'.");
        if (string.IsNullOrWhiteSpace(overlay.DefaultOwner)) errors.Add("defaultOwner is required.");
        if (overlay.Entries.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != overlay.Entries.Count)
            errors.Add("Duplicate overlay entry IDs are not allowed.");

        foreach (var entry in overlay.Entries)
        {
            var governance = entry.Governance;
            if (string.IsNullOrWhiteSpace(governance.Rationale)) errors.Add($"Overlay entry '{entry.Id}' requires a rationale.");
            var interval = governance.ReviewIntervalDays ?? 90;
            if (interval is < 30 or > 365) errors.Add($"Overlay entry '{entry.Id}' reviewIntervalDays must be 30-365.");
            if (governance.LastReviewedOn < governance.CreatedOn) errors.Add($"Overlay entry '{entry.Id}' cannot be reviewed before creation.");
            if (entry is ManualRelationshipOverlay { Confidence: Confidence.Confirmed } && string.IsNullOrWhiteSpace(governance.Reviewer))
                errors.Add($"Confirmed manual relationship '{entry.Id}' requires a reviewer.");
            if (entry is ManualNodeOverlay node && InvalidPath(node.EvidencePath)) errors.Add($"Overlay entry '{entry.Id}' evidencePath must be repository-relative.");
            if (entry is ManualRelationshipOverlay relationship && InvalidPath(relationship.EvidencePath)) errors.Add($"Overlay entry '{entry.Id}' evidencePath must be repository-relative.");
            if (entry is AnnotationOverlay annotation && InvalidPath(annotation.EvidencePath)) errors.Add($"Overlay entry '{entry.Id}' evidencePath must be repository-relative.");
        }

        if (errors.Count > 0) throw new ArtifactValidationException(errors);
    }

    private static bool InvalidPath(string path) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Split('/').Contains("..", StringComparer.Ordinal);
}
