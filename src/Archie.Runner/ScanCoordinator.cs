using System.Security.Cryptography;
using System.Text.Json;
using Archie.Core;

namespace Archie.Runner;

public sealed record ScanCoordinatorResult(
    bool Succeeded,
    ObservationBundle? Bundle,
    IReadOnlyList<SourceOwnershipClaim> SourceOwnership,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed class ScanCoordinator
{
    private readonly ScannerProcess scannerProcess = new();

    public static string ConfigurationDigest(
        IReadOnlyList<DiscoveredScanner> scanners,
        ScannerRuntimeLimits limits) =>
        ConfigurationDigest(scanners, limits, TechnologyStackDetector.RuleVersion);

    internal static string ConfigurationDigest(
        IReadOnlyList<DiscoveredScanner> scanners,
        ScannerRuntimeLimits limits,
        string recommendationRuleVersion)
    {
        var scannerInputs = scanners
            .Select(item => new { item.Manifest.Id, item.Manifest.Version, item.PackageSha256 })
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var digestInput = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocolVersion = "scanner/v1",
            recommendationRuleVersion,
            scanners = scannerInputs,
            handshakeTimeoutMilliseconds = limits.HandshakeTimeout.TotalMilliseconds,
            executionTimeoutMilliseconds = limits.ExecutionTimeout.TotalMilliseconds,
            cancellationGracePeriodMilliseconds = limits.CancellationGracePeriod.TotalMilliseconds,
            limits.MaxProtocolMessageBytes,
            limits.MaxStandardOutputBytes,
            limits.MaxStandardErrorBytes,
            limits.MaxObservations,
            limits.MaxSourceOwnership
        }, ScannerContractJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(digestInput));
    }

    public async Task<ScanCoordinatorResult> ScanAsync(
        string repositoryRoot,
        RepositoryRevision repository,
        IReadOnlyList<DiscoveredScanner> scanners,
        IReadOnlyList<Diagnostic> coverageDiagnostics,
        ScannerRuntimeLimits limits,
        CancellationToken cancellationToken)
    {
        if (scanners.Count == 0)
        {
            var diagnostic = new Diagnostic(
                "diagnostic:scan:no-scanners",
                "SCANNER_NOT_FOUND",
                "error",
                "No applicable scanners were found. Run 'archie scanner recommend <repository>' for next steps.",
                null);
            return new(false, null, [], [.. coverageDiagnostics, diagnostic]);
        }

        var observations = new List<Observation>();
        var sourceOwnership = new List<SourceOwnershipClaim>();
        var diagnostics = new List<Diagnostic>(coverageDiagnostics);
        var redactions = new List<RedactionEvent>();
        using var configurationDocument = JsonDocument.Parse("{}");
        var context = new ScanContext(repository, repositoryRoot, configurationDocument.RootElement.Clone());
        foreach (var scanner in scanners)
        {
            ScannerProcessResult result;
            try
            {
                result = await scannerProcess.RunAsync(scanner, context, limits, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or System.ComponentModel.Win32Exception or ScannerContainmentUnavailableException)
            {
                var message = ObservationRedactor.SanitizeText(exception.Message);
                var code = exception is ScannerContainmentUnavailableException ? "SCANNER_CONTAINMENT_UNAVAILABLE" : "SCANNER_PROCESS_FAILED";
                var diagnostic = new Diagnostic($"diagnostic:{scanner.Manifest.Id}:start", code, "error", message, scanner.Manifest.Id);
                return new(false, null, [], [.. diagnostics, ObservationRedactor.SanitizeDiagnostic(diagnostic)]);
            }
            diagnostics.AddRange(result.Diagnostics);
            if (!result.Succeeded || result.Diagnostics.Any(item => item.Severity == "error"))
                return new(false, null, [], diagnostics);
            observations.AddRange(result.Observations);
            sourceOwnership.AddRange(result.SourceOwnership);
            redactions.AddRange(result.Redactions);
        }

        var identities = scanners
            .Select(item => new ScannerIdentity(item.Manifest.Id, item.Manifest.Version))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var bundle = new ObservationBundle(
            "observations/v1",
            ObservationSource.Scanner,
            ConfigurationDigest(scanners, limits),
            repository,
            identities,
            observations,
            diagnostics,
            redactions.DistinctBy(item => item.Id).ToArray());
        ObservationValidator.Validate(bundle);
        return new(true, bundle, sourceOwnership, diagnostics);
    }
}
