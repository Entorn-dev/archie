using Archie.Contracts;
using Archie.Core;
using Xunit;

namespace Archie.Core.Tests;

public sealed class GraphOperationsTests
{
    [Fact]
    public void IndexesExposeDeterministicallyOrderedAdjacency()
    {
        var snapshot = ReadFixture();
        var indexes = new GraphIndexes(snapshot);

        Assert.Equal(14, indexes.Nodes.Count);
        Assert.Equal(13, indexes.Edges.Count);
        Assert.Equal(
            ["edge:checkout-publishes-order-submitted", "edge:ordering-subscribes-order-submitted"],
            indexes.Incoming["channel:order-submitted"].Select(edge => edge.Id));
    }

    [Fact]
    public void GraphQueryFiltersByEnvironmentWithoutLeavingDanglingEdges()
    {
        var snapshot = ReadFixture();
        var service = new GraphQueryService(snapshot, new GraphIndexes(snapshot));

        var result = service.Query(new GraphQuery(
            null, 2, new HashSet<NodeKind>(), new HashSet<EdgeKind>(), new HashSet<Confidence>(),
            new HashSet<Resolution>(), "legacy", 300, 900));

        Assert.Equal(
            ["channel:fulfilment-requested", "deployable:legacy-fulfilment"],
            result.Nodes.Select(node => node.Id));
        Assert.Single(result.Edges, edge => edge.Id == "edge:legacy-subscribes-fulfilment");
    }

    [Fact]
    public void RootedGraphQueryHonorsDepthAndResultBudget()
    {
        var snapshot = ReadFixture();
        var service = new GraphQueryService(snapshot, new GraphIndexes(snapshot));

        var result = service.Query(new GraphQuery(
            "deployable:checkout-service", 1, new HashSet<NodeKind>(), new HashSet<EdgeKind>(),
            new HashSet<Confidence>(), new HashSet<Resolution>(), null, 4, 3));

        Assert.Equal(4, result.Nodes.Count);
        Assert.Equal(3, result.Edges.Count);
        Assert.Contains(result.Nodes, node => node.Id == "deployable:storefront");
        Assert.Contains(result.Nodes, node => node.Id == "channel:order-submitted");
    }

    [Fact]
    public void PlaceOrderPathCrossesCurrentAndLegacyUsingLogicalSubscriptionDirection()
    {
        var snapshot = ReadFixture();
        var service = new GraphQueryService(snapshot, new GraphIndexes(snapshot));

        var paths = service.FindPaths(new PathQuery(
            "deployable:storefront", "deployable:notification-service", PathDirection.Downstream, 8,
            new HashSet<EdgeKind> { EdgeKind.Calls, EdgeKind.Publishes, EdgeKind.Subscribes }));

        var path = Assert.Single(paths);
        Assert.Equal(7, path.EdgeIds.Count);
        Assert.Contains("deployable:legacy-fulfilment", path.NodeIds);
        Assert.Equal("deployable:notification-service", path.NodeIds[^1]);
    }

    [Fact]
    public void PathDepthLimitReturnsDiagnosticInsteadOfPartialPaths()
    {
        var snapshot = ReadFixture();
        var service = new GraphQueryService(snapshot, new GraphIndexes(snapshot));

        var exception = Assert.Throws<GraphQueryLimitException>(() => service.FindPaths(new PathQuery(
            "deployable:storefront", "deployable:notification-service", PathDirection.Downstream, 2,
            new HashSet<EdgeKind>())));

        Assert.Equal("PATH_LIMIT_EXCEEDED", exception.Code);
        Assert.Contains("depth limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingEdgeEndpointIsRejectedRatherThanDropped()
    {
        var snapshot = ReadFixture();
        var invalidEdge = snapshot.Edges[0] with { To = "deployable:missing" };
        var invalid = snapshot with { Edges = [invalidEdge, .. snapshot.Edges.Skip(1)] };

        var exception = Assert.Throws<GraphValidationException>(() => GraphValidator.Validate(invalid));
        Assert.Contains("missing to endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedEdgeWithoutEvidenceIsRejected()
    {
        var snapshot = ReadFixture();
        var invalidEdge = snapshot.Edges[0] with { EvidenceIds = [] };
        var invalid = snapshot with { Edges = [invalidEdge, .. snapshot.Edges.Skip(1)] };

        var exception = Assert.Throws<GraphValidationException>(() => GraphValidator.Validate(invalid));
        Assert.Contains("has no evidence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiffReportsSemanticSubjectsUncertaintyAndScannerVersions()
    {
        var before = ReadFixture();
        var changedNode = before.Nodes[0] with { Name = $"{before.Nodes[0].Name} renamed" };
        var addedNode = new GraphNode(
            "unresolved:new-target", NodeKind.ExternalService, "New target", Resolution.Unresolved,
            [], new Dictionary<string, System.Text.Json.JsonElement>(), []);
        var changedEdge = before.Edges[0] with { Confidence = Confidence.Inferred };
        var changedEvidence = before.Evidence[0] with
        {
            ScannerId = "archie.dotnet",
            ScannerVersion = "2.0.0",
            ExtractionMethod = "updated semantic extraction"
        };
        var beforeEvidence = before.Evidence[0] with { ScannerId = "archie.dotnet", ScannerVersion = "1.0.0" };
        before = before with
        {
            Evidence = [beforeEvidence, .. before.Evidence.Skip(1)]
        };
        var after = before with
        {
            Composition = before.Composition with
            {
                Inputs = before.Composition.Inputs.Select(input => input with { ObservationBundleDigest = new string('f', 64) }).ToArray()
            },
            Nodes = before.Nodes.Select(node => node.Id == changedNode.Id ? changedNode : node)
                .Append(addedNode).OrderBy(node => node.Id, StringComparer.Ordinal).ToArray(),
            Edges = before.Edges.Select(edge => edge.Id == changedEdge.Id ? changedEdge : edge).ToArray(),
            Evidence = before.Evidence.Select(item => item.Id == changedEvidence.Id ? changedEvidence : item).ToArray()
        };

        var diff = new GraphDiffer().Compare(before, after);

        Assert.True(diff.HasChanges);
        Assert.True(diff.CompositionChanged);
        Assert.Single(diff.AddedNodes, node => node.Id == addedNode.Id);
        Assert.Single(diff.ChangedNodes, change => change.Id == changedNode.Id);
        Assert.Single(diff.ChangedEdges, change => change.Id == changedEdge.Id);
        Assert.Single(diff.ChangedEvidence, change => change.Id == changedEvidence.Id);
        Assert.Equal([addedNode.Id], diff.AddedUnresolved);
        var scanner = Assert.Single(diff.ScannerVersionChanges);
        Assert.Equal("archie.dotnet", scanner.Id);
        Assert.Equal(["1.0.0"], scanner.BeforeVersions);
        Assert.Equal(["2.0.0"], scanner.AfterVersions);
    }

    [Fact]
    public void DiffIgnoresCanonicalCollectionOrderingNoise()
    {
        var before = ReadFixture();
        var reordered = before with
        {
            Nodes = before.Nodes.Select(node => node with
            {
                Aliases = node.Aliases.Reverse().ToArray(),
                EvidenceIds = node.EvidenceIds.Reverse().ToArray()
            }).ToArray(),
            Edges = before.Edges.Select(edge => edge with
            {
                Provenance = edge.Provenance.Reverse().ToArray(),
                EvidenceIds = edge.EvidenceIds.Reverse().ToArray()
            }).ToArray()
        };

        var diff = new GraphDiffer().Compare(before, reordered);

        Assert.False(diff.HasChanges);
    }

    private static GraphSnapshot ReadFixture()
    {
        using var stream = File.OpenRead(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "graphs", "book-retail-minimal.json"));
        return ContractJson.ReadGraphSnapshot(stream);
    }
}
