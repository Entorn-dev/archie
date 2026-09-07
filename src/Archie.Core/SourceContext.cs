using System.Security.Cryptography;
using Archie.Contracts;

namespace Archie.Core;

public sealed record CandidateResolution(string CandidateKey, string SubjectId, Resolution Resolution);

public static class SourceContextBuilder
{
    public static SourceContextSnapshot Build(
        ObservationBundle bundle,
        GraphSnapshot graph,
        ReadOnlySpan<byte> graphBytes,
        IReadOnlyList<CandidateResolution> candidateResolutions,
        IReadOnlyList<SourceOwnershipClaim> claims)
    {
        var resolutions = candidateResolutions.ToDictionary(item => item.CandidateKey, StringComparer.Ordinal);
        var scanners = bundle.Scanners.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var ownership = new List<SourceOwnership>();
        foreach (var claim in claims)
        {
            ValidateClaim(claim);
            if (!scanners.TryGetValue(claim.ScannerId, out var scanner) || scanner.Version != claim.ScannerVersion)
                throw new ArtifactValidationException([$"Source ownership for '{claim.Path}' names scanner '{claim.ScannerId}' version '{claim.ScannerVersion}' outside the observation bundle."]);
            if (!resolutions.TryGetValue(claim.OwnerCandidateKey, out var resolved) &&
                !TryResolvePhpComposerProjectOwner(claim, resolutions, out resolved))
                throw new ArtifactValidationException([$"Source ownership for '{claim.Path}' references unknown candidate '{claim.OwnerCandidateKey}'."]);
            ownership.Add(new(
                claim.ScannerId, claim.ScannerVersion, claim.Path, claim.OwnerCandidateKey, resolved.SubjectId,
                claim.OwnershipKind, claim.Confidence, claim.Resolution,
                MostUncertain(claim.Resolution, resolved.Resolution), claim.DerivationRule));
        }

        var observationDigest = Digest(ContractJson.WriteObservationBundle(bundle));
        var graphInput = graph.Composition.Inputs.SingleOrDefault(input => input.Repository.RepositoryId == bundle.Repository.RepositoryId);
        if (graphInput is null || graphInput.ObservationBundleDigest != observationDigest)
            throw new ArtifactValidationException(["The graph is not composed from the supplied observation bundle."]);

        var snapshot = new SourceContextSnapshot(
            "source-context/v1", bundle.Repository, bundle.ScanConfigurationDigest, bundle.Scanners,
            observationDigest, Digest(graphBytes), ownership
                .DistinctBy(item => (item.ScannerId, item.Path, item.OwnerCandidateKey))
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.OwnerId, StringComparer.Ordinal)
                .ThenBy(item => item.ScannerId, StringComparer.Ordinal)
                .ThenBy(item => item.OwnerCandidateKey, StringComparer.Ordinal)
                .ToArray());
        SourceContextValidator.Validate(snapshot, graph, graphBytes);
        return snapshot;
    }

    private static bool TryResolvePhpComposerProjectOwner(
        SourceOwnershipClaim claim,
        IReadOnlyDictionary<string, CandidateResolution> resolutions,
        out CandidateResolution resolved)
    {
        const string composerProjectPrefix = "php:composer-project:";
        resolved = null!;
        if (claim.ScannerId != "archie.php" || claim.ScannerVersion != "2.0.0" ||
            claim.OwnershipKind != SourceOwnershipKind.Project ||
            !claim.OwnerCandidateKey.StartsWith(composerProjectPrefix, StringComparison.Ordinal)) return false;
        var laravelCandidateKey = $"php:laravel-app:{claim.OwnerCandidateKey[composerProjectPrefix.Length..]}";
        return resolutions.TryGetValue(laravelCandidateKey, out resolved!);
    }

    private static void ValidateClaim(SourceOwnershipClaim claim)
    {
        if (string.IsNullOrWhiteSpace(claim.ScannerId) || string.IsNullOrWhiteSpace(claim.ScannerVersion) ||
            string.IsNullOrWhiteSpace(claim.OwnerCandidateKey) || string.IsNullOrWhiteSpace(claim.DerivationRule))
            throw new ArtifactValidationException(["Source ownership fields must not be empty."]);
        SourceContextValidator.ValidatePath(claim.Path);
    }

    private static Resolution MostUncertain(Resolution left, Resolution right) =>
        left == Resolution.Unresolved || right == Resolution.Unresolved ? Resolution.Unresolved :
        left == Resolution.Ambiguous || right == Resolution.Ambiguous ? Resolution.Ambiguous : Resolution.Resolved;

    internal static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public static class SourceContextValidator
{
    public const int MaxOwnership = 200_000;

    public static void Validate(SourceContextSnapshot snapshot, GraphSnapshot graph, ReadOnlySpan<byte> graphBytes)
    {
        var errors = new List<string>();
        if (snapshot.SchemaVersion != "source-context/v1") errors.Add($"Unsupported schemaVersion '{snapshot.SchemaVersion}'.");
        if (graph.Composition.Inputs.Count != 1)
            errors.Add("Source context requires one single-repository graph composition input.");
        var graphInput = graph.Composition.Inputs.FirstOrDefault();
        if (graphInput is not null && snapshot.Repository != graphInput.Repository)
            errors.Add("Repository metadata does not match the canonical graph.");
        if (snapshot.GraphDigest != SourceContextBuilder.Digest(graphBytes)) errors.Add("Graph digest does not match the canonical graph bytes.");
        if (graphInput is not null && snapshot.ObservationBundleDigest != graphInput.ObservationBundleDigest)
            errors.Add("Observation-bundle digest does not match the canonical graph composition.");
        if (snapshot.Ownership.Count > MaxOwnership) errors.Add($"Ownership count {snapshot.Ownership.Count} exceeds {MaxOwnership}.");

        var nodeIds = graph.Nodes.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var scannerVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var scanner in snapshot.Scanners)
            if (!scannerVersions.TryAdd(scanner.Id, scanner.Version)) errors.Add($"Duplicate source-context scanner '{scanner.Id}'.");
        var keys = new HashSet<(string ScannerId, string Path, string Candidate)>();
        foreach (var item in snapshot.Ownership)
        {
            try { ValidatePath(item.Path); }
            catch (ArtifactValidationException exception) { errors.AddRange(exception.Errors); }
            if (!nodeIds.Contains(item.OwnerId)) errors.Add($"Source ownership for '{item.Path}' references missing owner '{item.OwnerId}'.");
            if (!scannerVersions.TryGetValue(item.ScannerId, out var version) || version != item.ScannerVersion)
                errors.Add($"Source ownership for '{item.Path}' has an unknown scanner identity.");
            if (!keys.Add((item.ScannerId, item.Path, item.OwnerCandidateKey)))
                errors.Add($"Duplicate source ownership for '{item.Path}' and '{item.OwnerCandidateKey}'.");
        }

        var ordered = snapshot.Ownership.OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.OwnerId, StringComparer.Ordinal).ThenBy(item => item.ScannerId, StringComparer.Ordinal)
            .ThenBy(item => item.OwnerCandidateKey, StringComparer.Ordinal);
        if (!snapshot.Ownership.SequenceEqual(ordered)) errors.Add("Source ownership must be deterministically ordered.");
        if (errors.Count > 0) throw new ArtifactValidationException(errors);
    }

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || Path.IsPathRooted(path) || path.Contains('\\') ||
            path.StartsWith("/", StringComparison.Ordinal) || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArtifactValidationException([$"Source path '{path}' must be a normalized repository-relative path."]);
    }
}
