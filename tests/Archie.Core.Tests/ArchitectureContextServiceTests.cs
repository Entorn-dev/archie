using System.Security.Cryptography;
using System.Text;
using Archie.Contracts;
using Archie.Core;
using Xunit;

namespace Archie.Core.Tests;

public sealed class ArchitectureContextServiceTests
{
    [Fact]
    public void ContextUsesExactEvidenceLinesAndSnapshotScopedOwnershipWithoutGuessing()
    {
        var service = Service();

        var direct = service.GetContext("tests/fixtures/observations/book-retail.authored.json", 179, null);
        var owned = service.GetContext("Checkout/Other.cs", null, null);
        var unknown = service.GetContext("Checkout/Unknown.cs", null, null);

        Assert.Equal("ambiguous", direct.Match);
        Assert.Contains(direct.Edges, edge => edge.Id == "edge:checkout-calls-payment");
        Assert.Equal("matched", owned.Match);
        Assert.Single(owned.Ownership, item => item.OwnerId == "deployable:checkout-service");
        Assert.Equal("no-match", unknown.Match);
    }

    [Fact]
    public void DependenciesAndFlowPreserveLogicalSubscriptionDirectionAndEvidence()
    {
        var service = Service();

        var dependencies = service.GetDependencies(
            "deployable:checkout-service", DependencyDirection.Downstream, 2,
            new HashSet<NodeKind>(), new HashSet<EdgeKind>());
        var flow = service.TraceFlow(
            "deployable:checkout-service", "deployable:notification-service", PathDirection.Downstream, 8);

        Assert.Contains(dependencies.Nodes, node => node.Id == "deployable:ordering-service");
        Assert.Contains(dependencies.Edges, edge => edge.Id == "edge:ordering-subscribes-order-submitted");
        var path = Assert.Single(flow.Paths);
        Assert.Equal("deployable:notification-service", path.Nodes[^1].Id);
        Assert.NotEmpty(path.Evidence);
        Assert.Contains("Generated architecture context", flow.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeImpactKeepsDirectOwnerDownstreamAndUnmatchedCategoriesSeparate()
    {
        var service = Service();

        var result = service.AssessChangeImpact(
            [
                new("tests/fixtures/observations/book-retail.authored.json", 179, 179),
                new("Checkout/Other.cs"),
                new("unknown.txt")
            ],
            2);

        Assert.Contains(result.Direct, item => item.Subject.Id == "edge:checkout-calls-payment");
        Assert.Contains(result.Owner, item => item.Subject.Id == "deployable:checkout-service");
        Assert.Contains(result.Downstream, item => item.Subject.Id == "deployable:ordering-service");
        Assert.Equal(["unknown.txt"], result.Unmatched);
        Assert.Equal("potential static impact", result.Assessment);
        Assert.Contains("internal code reachability", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactContextSummaryIsBoundedCountedAndDeterministic()
    {
        var service = Service();
        var result = service.GetContext("tests/fixtures/observations/book-retail.authored.json", 179, null);

        var first = service.RenderCompact(result, CompactContextDetail.Summary, 1);
        var second = service.RenderCompact(result, CompactContextDetail.Summary, 1);

        Assert.Equal(first, second);
        Assert.Contains("matches 7 architecture subjects; preserve the ambiguity", first, StringComparison.Ordinal);
        Assert.Contains("## Subjects (1/7)", first, StringComparison.Ordinal);
        Assert.Contains("## Incoming (1/4)", first, StringComparison.Ordinal);
        Assert.Contains("## Outgoing (1/4)", first, StringComparison.Ordinal);
        Assert.Contains("Sources (1/", first, StringComparison.Ordinal);
        Assert.Contains("detail: evidence", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Evidence `evidence:", first, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactContextEvidenceExpandsExactVerification()
    {
        var service = Service();
        var result = service.GetContext("Checkout/Other.cs", null, null);

        var markdown = service.RenderCompact(result, CompactContextDetail.Evidence, 6);

        Assert.Contains("belongs to Checkout Service [`deployable:checkout-service`]", markdown, StringComparison.Ordinal);
        Assert.Contains("rule `msbuild:compile-item-ownership`", markdown, StringComparison.Ordinal);
        Assert.Contains("scanner `archie.dotnet@1.3.0`", markdown, StringComparison.Ordinal);
        Assert.Contains("confirmed · claim resolved", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("detail: evidence", markdown, StringComparison.Ordinal);
        Assert.Contains("detail: full", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactContextRejectsTheWholeResponseAboveItsUtf8Limit()
    {
        var service = Service(new(MaxSummaryBytes: 64));
        var result = service.GetContext("Checkout/Other.cs", null, null);

        var exception = Assert.Throws<GraphQueryLimitException>(() =>
            service.RenderCompact(result, CompactContextDetail.Summary, 6));

        Assert.Equal("COMPACT_RESPONSE_LIMIT_EXCEEDED", exception.Code);
        Assert.Contains("lower maxItems or request detail 'full'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactDependenciesAreCountedDeterministicAndUseLogicalDirection()
    {
        var service = Service();
        var result = service.GetDependencies(
            "deployable:checkout-service", DependencyDirection.Downstream, 2,
            new HashSet<NodeKind>(), new HashSet<EdgeKind>());

        var first = service.RenderCompact(result, CompactContextDetail.Summary, 1);
        var second = service.RenderCompact(result, CompactContextDetail.Summary, 1);

        Assert.Equal(first, second);
        Assert.Contains($"## Relationships (1/{result.Edges.Count})", first, StringComparison.Ordinal);
        Assert.Contains("Sources (1/", first, StringComparison.Ordinal);
        Assert.Contains("`channel:order-submitted` —subscribes→ `deployable:ordering-service`",
            service.RenderCompact(result, CompactContextDetail.Summary, 50), StringComparison.Ordinal);
        Assert.Contains("detail: evidence", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Evidence `evidence:", first, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactDependencyEvidenceExpandsExactVerification()
    {
        var service = Service();
        var result = service.GetDependencies(
            "deployable:checkout-service", DependencyDirection.Downstream, 2,
            new HashSet<NodeKind>(), new HashSet<EdgeKind>());

        var markdown = service.RenderCompact(result, CompactContextDetail.Evidence, 50);

        Assert.Contains("Evidence `evidence:ordering-subscribes-order-submitted`", markdown, StringComparison.Ordinal);
        Assert.Contains("method `", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("detail: evidence", markdown, StringComparison.Ordinal);
        Assert.Contains("detail: full", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactDependenciesStateWhenNoRelationshipsMatch()
    {
        var service = Service();
        var result = service.GetDependencies(
            "deployable:checkout-service", DependencyDirection.Downstream, 1,
            new HashSet<NodeKind>(), new HashSet<EdgeKind> { EdgeKind.Contains });

        var markdown = service.RenderCompact(result, CompactContextDetail.Summary, 12);

        Assert.Contains("No downstream dependencies were found within depth 1.", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Relationships", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactFlowLimitsWholePathsAndPreservesOrderedAsyncTransitions()
    {
        var service = Service();
        var result = service.TraceFlow(
            "deployable:checkout-service", "deployable:notification-service", PathDirection.Downstream, 8);
        var path = Assert.Single(result.Paths);
        var twoPaths = result with { Paths = [path, path] };

        var first = service.RenderCompact(twoPaths, CompactContextDetail.Summary, 1);
        var second = service.RenderCompact(twoPaths, CompactContextDetail.Summary, 1);

        Assert.Equal(first, second);
        Assert.StartsWith("# Flow trace (1/2 paths)", first, StringComparison.Ordinal);
        Assert.Contains($"### Path 1 ({path.Edges.Count} transitions)", first, StringComparison.Ordinal);
        Assert.DoesNotContain("### Path 2", first, StringComparison.Ordinal);
        Assert.Contains("Sources (1/", first, StringComparison.Ordinal);
        Assert.Contains("`deployable:checkout-service` —asynchronously publishes→ `channel:order-submitted`",
            first, StringComparison.Ordinal);
        Assert.Contains("`channel:order-submitted` —asynchronously subscribes→ `deployable:ordering-service`",
            first, StringComparison.Ordinal);
        var synchronous = service.TraceFlow(
            "deployable:checkout-service", "external:payment-provider", PathDirection.Downstream, 1);
        Assert.Contains("`deployable:checkout-service` —synchronously calls→ `external:payment-provider`",
            service.RenderCompact(synchronous, CompactContextDetail.Summary, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void CompactFlowEvidenceExpandsExactSupport()
    {
        var service = Service();
        var result = service.TraceFlow(
            "deployable:checkout-service", "deployable:notification-service", PathDirection.Downstream, 8);

        var markdown = service.RenderCompact(result, CompactContextDetail.Evidence, 3);

        Assert.Contains("Evidence `evidence:checkout-publishes-order-submitted`", markdown, StringComparison.Ordinal);
        Assert.Contains("deterministic · confirmed · method", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("detail: evidence", markdown, StringComparison.Ordinal);
        Assert.Contains("detail: full", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactFlowDistinguishesNoPathFromHiddenPaths()
    {
        var service = Service();
        var result = service.TraceFlow(
            "deployable:notification-service", "deployable:checkout-service", PathDirection.Downstream, 8);

        var markdown = service.RenderCompact(result, CompactContextDetail.Summary, 3);

        Assert.Empty(result.Paths);
        Assert.Contains("No path was found within the requested bounds.", markdown, StringComparison.Ordinal);
        Assert.StartsWith("# Flow trace (0/0 paths)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactImpactKeepsQualifiedCategoriesIndependentAndCounted()
    {
        var service = Service();
        var result = service.AssessChangeImpact(
            [
                new("tests/fixtures/observations/book-retail.authored.json", 179, 179),
                new("Checkout/Other.cs"),
                new("unknown.txt")
            ],
            2);

        var first = service.RenderCompact(result, CompactContextDetail.Summary, 1);
        var second = service.RenderCompact(result, CompactContextDetail.Summary, 1);

        Assert.Equal(first, second);
        Assert.Contains($"Potential static impact: {result.Direct.Count} direct, {result.Owner.Count} owner-level, {result.Downstream.Count} downstream, and {result.Unmatched.Count} unmatched", first, StringComparison.Ordinal);
        Assert.Contains($"## Direct evidence (1/{result.Direct.Count})", first, StringComparison.Ordinal);
        Assert.Contains($"## Owner-level possibilities (1/{result.Owner.Count})", first, StringComparison.Ordinal);
        Assert.Contains($"## Downstream architecture effects (1/{result.Downstream.Count})", first, StringComparison.Ordinal);
        Assert.Contains("## Unmatched files (1/1)", first, StringComparison.Ordinal);
        Assert.Contains("Sources (1/", first, StringComparison.Ordinal);
        Assert.Contains("Internal code reachability remains unknown", first, StringComparison.Ordinal);
        Assert.Contains("not proof of runtime use, breakage, or deployment state", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Evidence `evidence:", first, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactImpactEvidenceExpandsDirectAndOwnershipSupport()
    {
        var service = Service();
        var result = service.AssessChangeImpact(
            [
                new("tests/fixtures/observations/book-retail.authored.json", 179, 179),
                new("Checkout/Other.cs")
            ],
            2);

        var markdown = service.RenderCompact(result, CompactContextDetail.Evidence, 25);

        Assert.Contains("Evidence `evidence:checkout-calls-payment`", markdown, StringComparison.Ordinal);
        Assert.Contains("Ownership `Checkout/Other.cs` → Checkout Service", markdown, StringComparison.Ordinal);
        Assert.Contains("rule `msbuild:compile-item-ownership`", markdown, StringComparison.Ordinal);
        Assert.Contains("scanner `archie.dotnet@1.3.0`", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("detail: evidence", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactRenderersFailAtomicallyAtSummaryAndEvidenceByteLimits()
    {
        var service = Service(new(MaxSummaryBytes: 64, MaxEvidenceBytes: 64));
        var context = service.GetContext("Checkout/Other.cs", null, null);
        var dependencies = service.GetDependencies(
            "deployable:checkout-service", DependencyDirection.Downstream, 2,
            new HashSet<NodeKind>(), new HashSet<EdgeKind>());
        var flow = service.TraceFlow(
            "deployable:checkout-service", "deployable:notification-service", PathDirection.Downstream, 8);
        var impact = service.AssessChangeImpact([new("Checkout/Other.cs")], 2);

        foreach (var detail in new[] { CompactContextDetail.Summary, CompactContextDetail.Evidence })
        {
            Assert.Equal("COMPACT_RESPONSE_LIMIT_EXCEEDED", Assert.Throws<GraphQueryLimitException>(() => service.RenderCompact(context, detail, 6)).Code);
            Assert.Equal("COMPACT_RESPONSE_LIMIT_EXCEEDED", Assert.Throws<GraphQueryLimitException>(() => service.RenderCompact(dependencies, detail, 12)).Code);
            Assert.Equal("COMPACT_RESPONSE_LIMIT_EXCEEDED", Assert.Throws<GraphQueryLimitException>(() => service.RenderCompact(flow, detail, 3)).Code);
            Assert.Equal("COMPACT_RESPONSE_LIMIT_EXCEEDED", Assert.Throws<GraphQueryLimitException>(() => service.RenderCompact(impact, detail, 8)).Code);
        }
    }

    [Fact]
    public void CompactImpactEscapesHostileMetadataAndCountsUtf8Bytes()
    {
        var service = Service();
        var context = service.GetContext(null, null, "deployable:checkout-service");
        var hostileNode = context.Nodes[0] with { Name = "name\n## forged `tick` *star* _under_ \\slash" };
        var hostileContext = context with { Nodes = [hostileNode] };
        var original = service.AssessChangeImpact(
            [new("tests/fixtures/observations/book-retail.authored.json", 179, 179)], 1);
        var evidence = original.Direct[0].Evidence[0] with { ExtractionMethod = "méthod\n## forged `code` *emphasis* _italic_ \\slash" };
        var hostile = original with
        {
            Direct = [original.Direct[0] with { Evidence = [evidence] }],
            Unmatched = [new string('é', 100)]
        };

        var markdown = service.RenderCompact(hostile, CompactContextDetail.Evidence, 8);
        var contextMarkdown = service.RenderCompact(hostileContext, CompactContextDetail.Evidence, 6);

        Assert.DoesNotContain("\n## forged", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\n## forged", contextMarkdown, StringComparison.Ordinal);
        Assert.Contains("name ## forged 'tick' \\*star\\* \\_under\\_ \\\\slash", contextMarkdown, StringComparison.Ordinal);
        Assert.Contains("method `méthod ## forged 'code' *emphasis* _italic_ \\slash`", markdown, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(markdown) > markdown.Length);
        var limited = Service(new(MaxEvidenceBytes: markdown.Length));
        Assert.Throws<GraphQueryLimitException>(() => limited.RenderCompact(hostile, CompactContextDetail.Evidence, 8));
    }

    [Fact]
    public void UnsafePathsAndPartialLineRangesAreRejected()
    {
        var service = Service();

        Assert.Throws<ArtifactValidationException>(() => service.GetContext("../secret", null, null));
        Assert.Throws<ArgumentException>(() => service.AssessChangeImpact([new("Checkout/Other.cs", 10, null)], 1));
    }

    [Fact]
    public void SourceContextRejectsStaleGraphDigest()
    {
        var graphBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "graphs", "book-retail-minimal.json"));
        using var stream = new MemoryStream(graphBytes, writable: false);
        var graph = ContractJson.ReadGraphSnapshot(stream);
        var source = new SourceContextSnapshot(
            "source-context/v1", graph.Composition.Inputs.Single().Repository, new string('c', 64), [],
            graph.Composition.Inputs.Single().ObservationBundleDigest, new string('0', 64), []);

        var exception = Assert.Throws<ArtifactValidationException>(() => SourceContextValidator.Validate(source, graph, graphBytes));

        Assert.Contains("Graph digest does not match", exception.Message, StringComparison.Ordinal);
    }

    private static ArchitectureContextService Service(ContextRuntimeLimits? limits = null)
    {
        var graphBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "graphs", "book-retail-minimal.json"));
        using var stream = new MemoryStream(graphBytes, writable: false);
        var graph = ContractJson.ReadGraphSnapshot(stream);
        var repository = graph.Composition.Inputs.Single().Repository;
        var source = new SourceContextSnapshot(
            "source-context/v1", repository, new string('c', 64), [new("archie.dotnet", "1.3.0")],
            graph.Composition.Inputs.Single().ObservationBundleDigest,
            Convert.ToHexStringLower(SHA256.HashData(graphBytes)),
            [new("archie.dotnet", "1.3.0", "Checkout/Other.cs", "dotnet:service:Checkout/Checkout.csproj",
                "deployable:checkout-service", SourceOwnershipKind.Project, Confidence.Confirmed, Resolution.Resolved, Resolution.Resolved,
                "msbuild:compile-item-ownership")]);
        SourceContextValidator.Validate(source, graph, graphBytes);
        return new(graph, source, limits);
    }
}
