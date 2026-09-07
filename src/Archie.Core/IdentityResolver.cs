using System.Security.Cryptography;
using System.Text;
using Archie.Contracts;

namespace Archie.Core;

internal sealed record ResolvedIdentity(string Id, Resolution Resolution, string Rationale);

internal sealed class IdentityResolver(
    IReadOnlyDictionary<string, string> repositoryIdsByCandidateKey,
    IReadOnlyDictionary<string, string> overlayTargets,
    IReadOnlyDictionary<string, IReadOnlySet<string>> explicitStrongTargets,
    IReadOnlySet<string> weaklyAmbiguousKeys,
    bool reconcileStrongSignalsAcrossRepositories)
{
    private static readonly IReadOnlySet<string> StrongSignalNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "architecture-id", "deployment", "resource-id", "channel"
    };

    public ResolvedIdentity Resolve(EntityCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.ExplicitId))
            return new(candidate.ExplicitId, candidate.Resolution, "explicit ID");
        if (overlayTargets.TryGetValue(candidate.Key, out var overlayTarget))
            return new(overlayTarget, Resolution.Resolved, "governed overlay binding");

        var targets = candidate.IdentitySignals
            .Where(signal => StrongSignalNames.Contains(signal.Key))
            .Select(signal => $"{signal.Key}={signal.Value}")
            .Where(explicitStrongTargets.ContainsKey)
            .SelectMany(signal => explicitStrongTargets[signal])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (targets.Length == 1) return new(targets[0], Resolution.Resolved, "unique strong identity signal");
        if (targets.Length > 1) return new(DeriveId(candidate, $"ambiguous:{candidate.Key}", RepositoryId(candidate.Key)), Resolution.Ambiguous, "conflicting strong identity signals");
        if (candidate.Resolution == Resolution.Unresolved)
            return new(DeriveId(candidate, $"placeholder:{candidate.Key}", RepositoryId(candidate.Key)), Resolution.Unresolved, "unresolved candidate placeholder");
        if (weaklyAmbiguousKeys.Contains(candidate.Key))
            return new(DeriveId(candidate, $"ambiguous:{candidate.Key}", RepositoryId(candidate.Key)), Resolution.Ambiguous, "weak name-only match left separate");

        var strongSignal = candidate.IdentitySignals
            .Where(signal => StrongSignalNames.Contains(signal.Key))
            .OrderBy(signal => signal.Key, StringComparer.Ordinal)
            .ThenBy(signal => signal.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        var stableKey = string.IsNullOrEmpty(strongSignal.Key)
            ? candidate.Key
            : $"{strongSignal.Key}={strongSignal.Value}";
        var identityScope = reconcileStrongSignalsAcrossRepositories && !string.IsNullOrEmpty(strongSignal.Key)
            ? "cross-repository"
            : RepositoryId(candidate.Key);
        return new(DeriveId(candidate, stableKey, identityScope), candidate.Resolution, string.IsNullOrEmpty(strongSignal.Key) ? "repository-local candidate key" : "strong identity signal");
    }

    private string RepositoryId(string candidateKey) =>
        repositoryIdsByCandidateKey.TryGetValue(candidateKey, out var repositoryId)
            ? repositoryId
            : throw new ArtifactValidationException([$"Candidate key '{candidateKey}' has no repository scope."]);

    private static string DeriveId(EntityCandidate candidate, string stableKey, string identityScope)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{identityScope}\n{candidate.Kind}\n{stableKey}"))).ToLowerInvariant()[..12];
        var prefix = candidate.Resolution == Resolution.Unresolved ? "unresolved" : ContractName(candidate.Kind);
        return $"{prefix}:{Slug(candidate.Name)}:{digest}";
    }

    private static string Slug(string value)
    {
        var characters = value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        return string.Join('-', new string(characters).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string ContractName(NodeKind kind) =>
        System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(kind.ToString());
}
