using System.Text.Json;
using Archie.Contracts;
using Archie.Core;
using Xunit;

namespace Archie.Core.Tests;

public sealed class ReconciliationTests
{
    [Fact]
    public void BookRetailReconciliationIsByteIdenticalAndPreservesSliceThreeSemantics()
    {
        var bundle = ReadBundle();
        var overlay = ReadOverlay();
        var reconciler = new Reconciler();

        var first = reconciler.Reconcile(bundle, overlay, new DateOnly(2026, 9, 1)).Snapshot;
        var second = reconciler.Reconcile(bundle with { Observations = bundle.Observations.Reverse().ToArray() }, overlay, new DateOnly(2026, 9, 1)).Snapshot;

        Assert.Equal(ContractJson.WriteGraphSnapshot(first), ContractJson.WriteGraphSnapshot(second));
        Assert.Equal(14, first.Nodes.Count);
        Assert.Equal(13, first.Edges.Count);
        Assert.Contains(first.Edges, edge => edge.Id == "edge:storefront-calls-checkout");
        Assert.Contains(first.Edges, edge => edge.Id == "edge:notifications-subscribes-dispatch");
        Assert.Equal([EvidenceProvenance.Deterministic, EvidenceProvenance.Manual], first.Edges.Single(edge => edge.Id == "edge:checkout-calls-payment").Provenance);
        Assert.Equal([EvidenceProvenance.Manual], first.Edges.Single(edge => edge.Id == "edge:storefront-calls-catalogue").Provenance);
    }

    [Fact]
    public void ExplicitIdWinsAndUniqueStrongSignalMergesEvidence()
    {
        var explicitCandidate = Candidate("first", "deployable:checkout", "Checkout", signals: new Dictionary<string, string> { ["deployment"] = "checkout" });
        var matchingCandidate = Candidate("second", null, "Checkout renamed", signals: new Dictionary<string, string> { ["deployment"] = "checkout" });
        var bundle = Bundle(Entity("observation:one", explicitCandidate), Entity("observation:two", matchingCandidate));

        var result = new Reconciler().Reconcile(bundle, null, new DateOnly(2026, 9, 1));

        var node = Assert.Single(result.Snapshot.Nodes);
        Assert.Equal("deployable:checkout", node.Id);
        Assert.Equal(2, node.EvidenceIds.Count);
        Assert.Contains(result.MergeDecisions, decision => decision.Rationale == "unique strong identity signal");
    }

    [Fact]
    public void WeakNameMatchRemainsSeparateAndAmbiguous()
    {
        var bundle = Bundle(
            Entity("observation:one", Candidate("first", null, "Payments")),
            Entity("observation:two", Candidate("second", null, "Payments")));

        var result = new Reconciler().Reconcile(bundle, null, new DateOnly(2026, 9, 1));

        Assert.Equal(2, result.Snapshot.Nodes.Count);
        Assert.All(result.Snapshot.Nodes, node => Assert.Equal(Resolution.Ambiguous, node.Resolution));
        Assert.Equal(2, result.Diagnostics.Count(diagnostic => diagnostic.Code == "IDENTITY_AMBIGUOUS"));
    }

    [Fact]
    public void ProjectCompositionIsOrderedRepositoryScopedAndMergesOnlyStrongIdentity()
    {
        var firstLocal = Candidate("service", null, "Worker");
        var secondLocal = Candidate("service", null, "Worker");
        var firstChannel = Candidate("orders", "message-channel:orders", "Orders", signals: new Dictionary<string, string> { ["channel"] = "orders" });
        var secondChannel = Candidate("orders", null, "Orders renamed", signals: new Dictionary<string, string> { ["channel"] = "orders" });
        var firstMissing = Candidate("missing", null, "Missing API", Resolution.Unresolved);
        var secondMissing = Candidate("missing", null, "Missing API", Resolution.Unresolved);
        var first = Bundle(
            Entity("observation:channel", firstChannel),
            Entity("observation:service", firstLocal),
            new RelationshipObservation("observation:missing", Evidence("observation:missing"), EdgeKind.Calls, firstLocal, firstMissing, new Dictionary<string, JsonElement>())) with
        {
            Repository = new("repo-a", "https://github.com/example/a", "aaaaaaaa", false, new string('a', 64))
        };
        var second = Bundle(
            Entity("observation:channel", secondChannel),
            Entity("observation:service", secondLocal),
            new RelationshipObservation("observation:missing", Evidence("observation:missing"), EdgeKind.Calls, secondLocal, secondMissing, new Dictionary<string, JsonElement>())) with
        {
            Repository = new("repo-b", "https://github.com/example/b", "bbbbbbbb", false, new string('b', 64))
        };
        var reconciler = new Reconciler();

        var forward = reconciler.ReconcileProject("project:test", [first, second], null, new DateOnly(2026, 9, 3)).Snapshot;
        var reverse = reconciler.ReconcileProject("project:test", [second, first], null, new DateOnly(2026, 9, 3)).Snapshot;

        Assert.Equal(ContractJson.WriteGraphSnapshot(forward), ContractJson.WriteGraphSnapshot(reverse));
        Assert.Equal("multi-repository", forward.Composition.Policy);
        Assert.Equal(["repo-a", "repo-b"], forward.Composition.Inputs.Select(input => input.Repository.RepositoryId));
        Assert.Single(forward.Nodes, node => node.Id == "message-channel:orders" && node.EvidenceIds.Count == 2);
        Assert.Equal(2, forward.Nodes.Count(node => node.Name == "Worker" && node.Resolution == Resolution.Ambiguous));
        Assert.Equal(2, forward.Nodes.Count(node => node.Name == "Missing API" && node.Resolution == Resolution.Unresolved));
        Assert.Equal(2, forward.Edges.Count);
        Assert.Equal(6, forward.Evidence.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["repo-a", "repo-b"], forward.Evidence
            .Select(item => item.Properties["repositoryId"].GetString()!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ProjectCompositionRejectsDuplicateRepositories()
    {
        var bundle = Bundle(Entity("observation:one", Candidate("first", null, "Payments")));

        var exception = Assert.Throws<ArtifactValidationException>(() =>
            new Reconciler().ReconcileProject("project:test", [bundle, bundle], null, new DateOnly(2026, 9, 3)));

        Assert.Contains("same repository", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialProjectCompositionScopesSingleSuccessfulRepositoryEvidence()
    {
        var bundle = Bundle(Entity("observation:one", Candidate("first", null, "Payments"))) with
        {
            Repository = new("repo-a", "https://github.com/example/a", "aaaaaaaa", false, new string('a', 64))
        };

        var result = new Reconciler().ReconcileProject("project:test", [bundle], null, new DateOnly(2026, 9, 3));

        var evidence = Assert.Single(result.Snapshot.Evidence);
        Assert.StartsWith("repo-", evidence.Id, StringComparison.Ordinal);
        Assert.Equal("repo-a", evidence.Properties["repositoryId"].GetString());
        Assert.Equal("aaaaaaaa", evidence.Properties["revision"].GetString());
    }

    [Fact]
    public void ProjectCompositionRejectsOverlaysUntilScopedBindingsAreDefined()
    {
        var bundle = Bundle(Entity("observation:one", Candidate("first", null, "Payments")));
        var overlay = new ArchitectureOverlay("overlay/v1", "project:test", []);

        var exception = Assert.Throws<ArtifactValidationException>(() =>
            new Reconciler().ReconcileProject("project:test", [bundle], overlay, new DateOnly(2026, 9, 3)));

        Assert.Contains("repository-scoped candidate bindings", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedRelationshipProducesVisiblePlaceholder()
    {
        var from = Candidate("caller", "deployable:caller", "Caller");
        var target = Candidate("payments-key", null, "{Payments:BaseUrl}", Resolution.Unresolved);
        var relationship = new RelationshipObservation(
            "edge:caller-payments", Evidence("edge:caller-payments"), EdgeKind.Calls, from, target,
            new Dictionary<string, JsonElement> { ["label"] = JsonSerializer.SerializeToElement("calls unresolved payment target") });

        var result = new Reconciler().Reconcile(Bundle(relationship), null, new DateOnly(2026, 9, 1));

        var placeholder = Assert.Single(result.Snapshot.Nodes, node => node.Resolution == Resolution.Unresolved);
        Assert.StartsWith("unresolved:", placeholder.Id, StringComparison.Ordinal);
        Assert.Equal(placeholder.Id, Assert.Single(result.Snapshot.Edges).To);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TARGET_UNRESOLVED");
    }

    [Fact]
    public void ApprovedBindingResolvesPlaceholderDeterministically()
    {
        var from = Candidate("caller", "deployable:caller", "Caller");
        var target = Candidate("payments-key", null, "{Payments:BaseUrl}", Resolution.Unresolved);
        var relationship = new RelationshipObservation(
            "edge:caller-payments", Evidence("edge:caller-payments"), EdgeKind.Calls, from, target, new Dictionary<string, JsonElement>());
        var governance = new OverlayEntryGovernance(null, "role:reviewer", "Bind the approved payment endpoint.", new DateOnly(2026, 9, 1), null, null, null);
        var overlay = new ArchitectureOverlay("overlay/v1", "team:checkout", [new BindingOverlay("binding:payments", governance, "payments-key", "external:payments")]);

        var result = new Reconciler().Reconcile(Bundle(relationship), overlay, new DateOnly(2026, 9, 1));

        Assert.Contains(result.Snapshot.Nodes, node => node.Id == "external:payments" && node.Resolution == Resolution.Resolved);
        Assert.Equal("external:payments", Assert.Single(result.Snapshot.Edges).To);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "TARGET_UNRESOLVED");
    }

    [Fact]
    public void ExpiredSuppressionStopsHidingGeneratedFinding()
    {
        var overlay = ReadOverlay();
        var expired = new SuppressionOverlay(
            "suppression:checkout-publish",
            new(null, null, "Temporary review suppression.", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 90, null),
            "edge:checkout-publishes-order-submitted");
        overlay = overlay with { Entries = [.. overlay.Entries, expired] };

        var result = new Reconciler().Reconcile(ReadBundle(), overlay, new DateOnly(2026, 9, 1));

        Assert.Contains(result.Snapshot.Edges, edge => edge.Id == expired.TargetId);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "OVERLAY_SUPPRESSION_EXPIRED");
    }

    [Fact]
    public void ConfirmedManualRelationshipRequiresReviewer()
    {
        var overlay = ReadOverlay();
        var entry = Assert.IsType<ManualRelationshipOverlay>(overlay.Entries[0]);
        overlay = overlay with { Entries = [entry with { Governance = entry.Governance with { Reviewer = null } }] };

        var exception = Assert.Throws<ArtifactValidationException>(() => OverlayValidator.Validate(overlay));

        Assert.Contains("requires a reviewer", exception.Message, StringComparison.Ordinal);
    }

    private static ObservationBundle Bundle(params Observation[] observations) => new(
        "observations/v1", ObservationSource.Authored, new string('a', 64),
        new("repo", null, "revision", false, new string('b', 64)), [], observations, [], []);

    private static EntityObservation Entity(string id, EntityCandidate candidate) => new(id, Evidence(id), candidate);

    private static Evidence Evidence(string claimId) => new(
        $"evidence:{claimId}", claimId, EvidenceProvenance.Deterministic, null, null,
        "authored test observation", "tests/fixture.json", null, Confidence.Confirmed,
        new Dictionary<string, JsonElement> { ["observationSource"] = JsonSerializer.SerializeToElement("authored") });

    private static EntityCandidate Candidate(
        string key,
        string? explicitId,
        string name,
        Resolution resolution = Resolution.Resolved,
        IReadOnlyDictionary<string, string>? signals = null) =>
        new(key, NodeKind.Deployable, explicitId, name, resolution, signals ?? new Dictionary<string, string>(), new Dictionary<string, JsonElement>());

    private static ObservationBundle ReadBundle()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "observations", "book-retail.authored.json"));
        return ContractJson.ReadObservationBundle(stream);
    }

    private static ArchitectureOverlay ReadOverlay()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "overlays", "book-retail.overlay.json"));
        return ContractJson.ReadOverlay(stream);
    }
}
