using System.Text.Json;
using Archie.Contracts;
using Archie.Core;
using Xunit;

namespace Archie.Core.Tests;

public sealed class SourceContextTests
{
    [Fact]
    public void PhpTwoComposerProjectOwnershipResolvesToLaravelCandidate()
    {
        var (bundle, graph, graphBytes, resolutions) = ReconcilePhpLaravel();

        var source = SourceContextBuilder.Build(bundle, graph, graphBytes, resolutions,
            [Ownership("php:composer-project:laravel-storefront/composer.json")]);

        var ownership = Assert.Single(source.Ownership);
        Assert.Equal("php:composer-project:laravel-storefront/composer.json", ownership.OwnerCandidateKey);
        Assert.Equal("deployable:laravel-storefront", ownership.OwnerId);
    }

    [Fact]
    public void UnknownOwnershipCandidateStillFailsClosed()
    {
        var (bundle, graph, graphBytes, resolutions) = ReconcilePhpLaravel();

        var exception = Assert.Throws<ArtifactValidationException>(() =>
            SourceContextBuilder.Build(bundle, graph, graphBytes, resolutions,
                [Ownership("php:composer-project:unknown/composer.json")]));

        Assert.Contains("references unknown candidate 'php:composer-project:unknown/composer.json'", exception.Message,
            StringComparison.Ordinal);
    }

    private static (ObservationBundle Bundle, GraphSnapshot Graph, byte[] GraphBytes, IReadOnlyList<CandidateResolution> Resolutions)
        ReconcilePhpLaravel()
    {
        var observation = new EntityObservation(
            "observation:laravel",
            new Evidence(
                "evidence:laravel", "observation:laravel", EvidenceProvenance.Deterministic,
                "archie.php", "2.0.0", "laravel:application", "laravel-storefront/composer.json", null,
                Confidence.Confirmed, new Dictionary<string, JsonElement>()),
            new EntityCandidate(
                "php:laravel-app:laravel-storefront/composer.json", NodeKind.Deployable,
                "deployable:laravel-storefront", "Laravel Storefront", Resolution.Resolved,
                new Dictionary<string, string>(), new Dictionary<string, JsonElement>()));
        var bundle = new ObservationBundle(
            "observations/v1", ObservationSource.Scanner, new string('a', 64),
            new("repo", null, "revision", false, new string('b', 64)),
            [new("archie.php", "2.0.0")], [observation], [], []);
        var result = new Reconciler().Reconcile(bundle, null, new DateOnly(2026, 9, 7));
        var graphBytes = ContractJson.WriteGraphSnapshot(result.Snapshot);
        return (bundle, result.Snapshot, graphBytes, result.CandidateResolutions);
    }

    private static SourceOwnershipClaim Ownership(string candidateKey) => new(
        "archie.php", "2.0.0", "laravel-storefront/app/Http/Controllers/CheckoutController.php",
        candidateKey, SourceOwnershipKind.Project, Confidence.Confirmed, Resolution.Resolved,
        "composer:project-root");
}
