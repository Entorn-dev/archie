using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archie.Api;
using Archie.Contracts;
using Archie.Core;
using Archie.Runner;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Archie.Api.Tests;

public sealed class GraphEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public GraphEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GraphContainsEvidenceBackedBookRetailRelationship()
    {
        var graph = await _client.GetFromJsonAsync<GraphResponse>("/api/v1/graph");

        var relationship = Assert.Single(graph!.Edges, edge => edge.Id == GraphEndpoints.RelationshipId);
        var evidence = Assert.Single(relationship.Evidence);

        Assert.Equal("architecture/v1", graph.SchemaVersion);
        Assert.Equal("graph:archie:reconciled", graph.SnapshotId);
        Assert.Equal("publishes", relationship.Kind);
        Assert.Equal("deployable:checkout-service", relationship.From);
        Assert.Equal("channel:order-submitted", relationship.To);
        Assert.Equal("confirmed", relationship.Confidence);
        Assert.Empty(relationship.Properties);
        Assert.Equal("tests/fixtures/observations/book-retail.authored.json", evidence.Path);
        Assert.Equal("authored", evidence.Provenance);
    }

    [Fact]
    public async Task ManualAndMixedProvenanceRemainStructurallyVisible()
    {
        var manual = await _client.GetFromJsonAsync<GraphEdgeResponse>("/api/v1/edges/edge%3Astorefront-calls-catalogue");
        var mixed = await _client.GetFromJsonAsync<GraphEdgeResponse>("/api/v1/edges/edge%3Acheckout-calls-payment");

        Assert.Equal(["manual"], manual!.Provenance);
        Assert.Equal("role:architecture-maintainer", Assert.Single(manual.Evidence).Owner);
        Assert.Equal(["authored", "manual"], mixed!.Provenance);
        Assert.Contains(mixed.Evidence, evidence => evidence.ReviewStatus == "current");
    }

    [Fact]
    public async Task UnknownEdgeReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/edges/not-an-edge");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NodeDetailsComeFromImmutableAdjacencyIndexes()
    {
        var details = await _client.GetFromJsonAsync<NodeDetailsResponse>(
            "/api/v1/nodes/channel%3Aorder-submitted");

        Assert.Equal("order.submitted", details!.Node.Name);
        Assert.Equal(
            ["edge:checkout-publishes-order-submitted", "edge:ordering-subscribes-order-submitted"],
            details.IncomingEdgeIds);
        Assert.Empty(details.OutgoingEdgeIds);
    }

    [Fact]
    public async Task OverBudgetGraphReturnsExplicitErrorWithoutPartialData()
    {
        var response = await _client.GetAsync("/api/v1/graph?nodeLimit=2&edgeLimit=2");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("QUERY_LIMIT_EXCEEDED", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("nodes", out _));
    }

    [Fact]
    public async Task QueryAboveHardLimitIsRejected()
    {
        var response = await _client.GetAsync("/api/v1/graph?nodeLimit=501");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("QUERY_LIMIT_INVALID", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GraphEndpointFiltersByEnvironment()
    {
        var graph = await _client.GetFromJsonAsync<GraphResponse>("/api/v1/graph?environment=legacy");

        Assert.Equal(2, graph!.Nodes.Count);
        Assert.All(graph.Nodes, node => Assert.Equal("legacy", node.Environment));
        Assert.Single(graph.Edges, edge => edge.Id == "edge:legacy-subscribes-fulfilment");
    }

    [Fact]
    public async Task PathEndpointReturnsSavedCurrentToLegacyJourney()
    {
        var paths = await _client.GetFromJsonAsync<PathResponse[]>(
            "/api/v1/paths?from=deployable%3Astorefront&to=deployable%3Anotification-service&direction=downstream&maxDepth=8&edgeKind=calls,publishes,subscribes");

        var path = Assert.Single(paths!);
        Assert.Contains("deployable:legacy-fulfilment", path.NodeIds);
        Assert.Equal(7, path.EdgeIds.Count);
    }

    [Fact]
    public async Task PathDepthLimitReturnsActionableDiagnosticWithoutPartialPaths()
    {
        var response = await _client.GetAsync(
            "/api/v1/paths?from=deployable%3Astorefront&to=deployable%3Anotification-service&maxDepth=2");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PATH_LIMIT_EXCEEDED", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("paths", out _));
    }

    [Fact]
    public async Task SavedViewReferencesCanonicalJourneyEndpoints()
    {
        var view = await _client.GetFromJsonAsync<JsonElement>("/api/v1/views/place-order");
        var pathQuery = view.GetProperty("pathQuery");

        Assert.Equal("downstream", pathQuery.GetProperty("direction").GetString());
        Assert.Equal("deployable:storefront", pathQuery.GetProperty("from").GetString());
        Assert.Equal("deployable:notification-service", pathQuery.GetProperty("to").GetString());
        Assert.Equal(["calls", "publishes", "subscribes"], pathQuery.GetProperty("edgeKinds").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task SavedViewsAreListedAsBoundedDeterministicSummaries()
    {
        var views = await _client.GetFromJsonAsync<SavedViewSummaryResponse[]>("/api/v1/views");

        var view = Assert.Single(views!);
        Assert.Equal("place-order", view.Id);
        Assert.Equal("Place an order", view.Name);
        Assert.DoesNotContain("pathQuery", await _client.GetStringAsync("/api/v1/views"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticsEndpointReturnsTheBoundedSnapshotInDeterministicOrder()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "graphs", "book-retail-minimal.json");
        await using var stream = File.OpenRead(fixture);
        var graph = ContractJson.ReadGraphSnapshot(stream) with
        {
            Diagnostics =
            [
                new("diagnostic:scanner-coverage:laravel", "SCANNER_COVERAGE_MISSING", "warning",
                    "Laravel was detected but has no installed scanner. Install with archie scanner add archie.php.", "stack:laravel"),
                new("diagnostic:scanner-coverage:dotnet", "SCANNER_COVERAGE_INSTALLED", "info",
                    ".NET scanner coverage is installed.", "stack:dotnet")
            ]
        };
        var path = Path.Combine(Path.GetTempPath(), $"archie-api-diagnostics-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(graph, ContractJson.Options));
        try
        {
            await using var factory = _factory.WithWebHostBuilder(builder => builder.UseSetting("AIP_GRAPH_PATH", path));
            using var client = factory.CreateClient();

            var diagnostics = Assert.IsType<DiagnosticResponse[]>(
                await client.GetFromJsonAsync<DiagnosticResponse[]>("/api/v1/diagnostics"));

            Assert.Equal(
                ["diagnostic:scanner-coverage:dotnet", "diagnostic:scanner-coverage:laravel"],
                diagnostics.Select(diagnostic => diagnostic.Id));
            var missing = diagnostics[1];
            Assert.Equal("SCANNER_COVERAGE_MISSING", missing.Code);
            Assert.Equal("warning", missing.Severity);
            Assert.Equal("stack:laravel", missing.SubjectId);
            Assert.Contains("archie scanner add archie.php", missing.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("PLACE-ORDER")]
    [InlineData("not-a-view")]
    [InlineData("..%2Fplace-order")]
    public async Task SavedViewLookupRequiresAnExactParsedId(string viewId)
    {
        var response = await _client.GetAsync($"/api/v1/views/{viewId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SavedViewListingUsesParsedIdsRatherThanFileNames()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"archie-views-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var originalPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "views", "place-order.json");
            var original = JsonSerializer.Deserialize<SavedView>(await File.ReadAllBytesAsync(originalPath), ContractJson.Options)!;
            await File.WriteAllBytesAsync(Path.Combine(directory, "z-last.json"), JsonSerializer.SerializeToUtf8Bytes(original, ContractJson.Options));
            await File.WriteAllBytesAsync(Path.Combine(directory, "a-first.json"), JsonSerializer.SerializeToUtf8Bytes(original with { Id = "account-opening", Name = "Account opening" }, ContractJson.Options));
            await using var factory = _factory.WithWebHostBuilder(builder => builder.UseSetting("AIP_VIEW_PATH", directory));
            using var client = factory.CreateClient();

            var views = await client.GetFromJsonAsync<SavedViewSummaryResponse[]>("/api/v1/views");

            Assert.Equal(["account-opening", "place-order"], views!.Select(view => view.Id));
            var response = await client.GetAsync("/api/v1/views/z-last");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ApiNeverSurfacesRedactedWorkerStringsFromPersistedGraph()
    {
        const string secret = "api-secret-value";
        using var configuration = JsonDocument.Parse("{}");
        var manifest = new ScannerManifest(
            "scanner-manifest/v1", "test.scanner", "1.0.0", "unused", [], ["**/*"], [],
            configuration.RootElement.Clone(), new(true, false, false));
        var scanner = new DiscoveredScanner(manifest, Path.Combine(Path.GetTempPath(), "scanner.json"));
        var evidence = new Evidence(
            $"evidence:token={secret}", $"observation:token={secret}", EvidenceProvenance.Deterministic,
            "worker-controlled", "worker-controlled", $"safe context password={secret}",
            $"src/password={secret}/fixture.cs", null, Confidence.Confirmed,
            new Dictionary<string, JsonElement>
            {
                [$"apiKey={secret}"] = JsonSerializer.SerializeToElement(secret),
                ["safeEvidence"] = JsonSerializer.SerializeToElement("safe-api-evidence")
            });
        var candidate = new EntityCandidate(
            $"deployable:credential={secret}", NodeKind.Deployable, null, "Safe API node", Resolution.Resolved,
            new Dictionary<string, string> { [$"token={secret}"] = secret },
            new Dictionary<string, JsonElement>());
        var target = new EntityCandidate(
            "deployable:safe-target", NodeKind.Deployable, "deployable:safe-target", "Safe API target", Resolution.Resolved,
            new Dictionary<string, string>(), new Dictionary<string, JsonElement>());
        var redactor = new ObservationRedactor();
        var observation = redactor.Redact(new RelationshipObservation(
            $"observation:token={secret}", evidence, EdgeKind.Calls, candidate, target,
            new Dictionary<string, JsonElement> { ["label"] = JsonSerializer.SerializeToElement("safe API call") }), scanner, Path.GetTempPath());
        var diagnostic = redactor.RedactDiagnostic(new Diagnostic(
            $"diagnostic:password={secret}", $"TOKEN={secret}", "warning", $"Useful warning password={secret}", $"subject:token={secret}"));
        var bundle = new ObservationBundle(
            "observations/v1", ObservationSource.Scanner, new string('a', 64),
            new("test-repository", null, "abc123", false, new string('b', 64)),
            [new("test.scanner", "1.0.0")], [observation.Observation], [diagnostic.Diagnostic],
            [.. observation.Redactions, .. diagnostic.Redactions]);
        var graph = new Reconciler().Reconcile(bundle, null, new DateOnly(2026, 9, 1)).Snapshot;
        var path = Path.Combine(Path.GetTempPath(), $"archie-api-redaction-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(path, ContractJson.WriteGraphSnapshot(graph));
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseSetting("AIP_GRAPH_PATH", path));
            using var client = factory.CreateClient();

            var graphResponse = await client.GetStringAsync("/api/v1/graph");
            var diagnosticsResponse = await client.GetStringAsync("/api/v1/diagnostics");

            Assert.DoesNotContain(secret, graphResponse + diagnosticsResponse, StringComparison.Ordinal);
            Assert.Contains("safe context", graphResponse, StringComparison.Ordinal);
            Assert.Contains("safe API call", graphResponse, StringComparison.Ordinal);
            Assert.Contains("Useful warning", diagnosticsResponse, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task GraphAndEdgeEvidenceNeverBuildSourceUrlsFromLocalRemotes()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "graphs", "book-retail-minimal.json");
        await using var stream = File.OpenRead(fixture);
        var graph = ContractJson.ReadGraphSnapshot(stream);
        var localRemote = Path.Combine(Path.GetTempPath(), $"archie-local-origin-{Guid.NewGuid():N}");
        var input = Assert.Single(graph.Composition.Inputs);
        graph = graph with
        {
            Composition = graph.Composition with
            {
                Inputs = [input with { Repository = input.Repository with { RemoteUrl = localRemote } }]
            }
        };
        var path = Path.Combine(Path.GetTempPath(), $"archie-api-local-origin-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(path, ContractJson.WriteGraphSnapshot(graph));
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseSetting("AIP_GRAPH_PATH", path));
            using var client = factory.CreateClient();

            var graphText = await client.GetStringAsync("/api/v1/graph");
            var graphResponse = JsonSerializer.Deserialize<GraphResponse>(graphText, ContractJson.Options)!;
            var edge = graphResponse.Edges.First(item => item.Evidence.Count > 0);
            var edgeText = await client.GetStringAsync($"/api/v1/edges/{Uri.EscapeDataString(edge.Id)}");
            var edgeResponse = JsonSerializer.Deserialize<GraphEdgeResponse>(edgeText, ContractJson.Options)!;

            Assert.DoesNotContain(localRemote, graphText + edgeText, StringComparison.Ordinal);
            Assert.DoesNotContain("file:", graphText + edgeText, StringComparison.OrdinalIgnoreCase);
            Assert.All(graphResponse.Edges.SelectMany(item => item.Evidence), evidence => Assert.Null(evidence.SourceUrl));
            Assert.All(edgeResponse.Evidence, evidence => Assert.Null(evidence.SourceUrl));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task GraphAndEdgeEndpointsExposeOnlyValidatedScalarRelationshipProperties()
    {
        const string sentinel = "SYNTHETIC_NESTED_API_SECRET";
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "graphs", "book-retail-minimal.json");
        await using var stream = File.OpenRead(fixture);
        var graph = ContractJson.ReadGraphSnapshot(stream);
        IReadOnlyDictionary<string, JsonElement>[] invalidProperties =
        [
            new Dictionary<string, JsonElement> { ["provider"] = JsonSerializer.SerializeToElement(new { nested = new { token = sentinel } }) },
            new Dictionary<string, JsonElement> { ["resourceKind"] = JsonSerializer.SerializeToElement(new[] { sentinel }) },
            new Dictionary<string, JsonElement> { ["host"] = JsonSerializer.SerializeToElement("Bad Host") },
            new Dictionary<string, JsonElement> { ["scheme"] = JsonSerializer.SerializeToElement("HTTPS") },
            new Dictionary<string, JsonElement> { ["contextType"] = JsonSerializer.SerializeToElement(new { value = sentinel }) },
            new Dictionary<string, JsonElement> { ["configurationKey"] = JsonSerializer.SerializeToElement($"Payment:Api_Key:{sentinel}") },
            new Dictionary<string, JsonElement> { ["configurationKey"] = JsonSerializer.SerializeToElement("Payment:Auth") },
            new Dictionary<string, JsonElement> { ["configurationKey"] = JsonSerializer.SerializeToElement("payment_auth") },
            new Dictionary<string, JsonElement> { ["port"] = JsonSerializer.SerializeToElement(new { value = 443, token = sentinel }) },
            new Dictionary<string, JsonElement> { ["provider"] = JsonSerializer.SerializeToElement("HTTP") }
        ];
        IReadOnlyDictionary<string, JsonElement>[] safeProperties =
        [
            new Dictionary<string, JsonElement>
            {
                ["provider"] = JsonSerializer.SerializeToElement("http"),
                ["resourceKind"] = JsonSerializer.SerializeToElement("external-service"),
                ["host"] = JsonSerializer.SerializeToElement("payment.example"),
                ["scheme"] = JsonSerializer.SerializeToElement("https"),
                ["contextType"] = JsonSerializer.SerializeToElement("OrdersDbContext"),
                ["configurationKey"] = JsonSerializer.SerializeToElement("ConnectionStrings:Orders"),
                ["port"] = JsonSerializer.SerializeToElement(8443)
            },
            new Dictionary<string, JsonElement> { ["configurationKey"] = JsonSerializer.SerializeToElement("Payment:Author") },
            new Dictionary<string, JsonElement> { ["configurationKey"] = JsonSerializer.SerializeToElement("Payment:Authority") }
        ];
        var edges = graph.Edges.Select((edge, index) => index < invalidProperties.Length
            ? edge with { Properties = invalidProperties[index] }
            : index - invalidProperties.Length < safeProperties.Length
                ? edge with { Properties = safeProperties[index - invalidProperties.Length] }
                : edge).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"archie-api-properties-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(path, ContractJson.WriteGraphSnapshot(graph with { Edges = edges }));
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseSetting("AIP_GRAPH_PATH", path));
            using var client = factory.CreateClient();

            var graphText = await client.GetStringAsync("/api/v1/graph");
            var response = JsonSerializer.Deserialize<GraphResponse>(graphText, ContractJson.Options)!;

            Assert.DoesNotContain(sentinel, graphText, StringComparison.Ordinal);
            for (var index = 0; index < invalidProperties.Length; index++)
            {
                var graphEdge = response.Edges.Single(item => item.Id == edges[index].Id);
                Assert.Empty(graphEdge.Properties);
                var edgeText = await client.GetStringAsync($"/api/v1/edges/{Uri.EscapeDataString(edges[index].Id)}");
                Assert.DoesNotContain(sentinel, edgeText, StringComparison.Ordinal);
                Assert.Empty(JsonSerializer.Deserialize<GraphEdgeResponse>(edgeText, ContractJson.Options)!.Properties);
            }
            var safe = response.Edges.Single(item => item.Id == edges[invalidProperties.Length].Id).Properties;
            Assert.Equal(7, safe.Count);
            Assert.Equal("ConnectionStrings:Orders", safe["configurationKey"].GetString());
            Assert.Equal(8443, safe["port"].GetInt32());
            foreach (var benign in new[] { "Payment:Author", "Payment:Authority" })
            {
                var index = invalidProperties.Length + Array.FindIndex(safeProperties,
                    properties => properties["configurationKey"].GetString() == benign);
                var graphEdge = response.Edges.Single(item => item.Id == edges[index].Id);
                Assert.Equal(benign, graphEdge.Properties["configurationKey"].GetString());
                var edgeText = await client.GetStringAsync($"/api/v1/edges/{Uri.EscapeDataString(edges[index].Id)}");
                Assert.Equal(benign, JsonSerializer.Deserialize<GraphEdgeResponse>(edgeText, ContractJson.Options)!
                    .Properties["configurationKey"].GetString());
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task GraphAndEdgeEndpointsRejectUnicodeCompatibilityConfigurationKeys()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "graphs", "book-retail-minimal.json");
        await using var stream = File.OpenRead(fixture);
        var graph = ContractJson.ReadGraphSnapshot(stream);
        var unsafeKeys = new[] { "Payment:ａｕｔｈ", "Payment:ᴬᵁᵀᴴ", "Payment:a\u0301uth" };
        var edges = graph.Edges.Select((edge, index) => index < unsafeKeys.Length
            ? edge with
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["configurationKey"] = JsonSerializer.SerializeToElement(unsafeKeys[index])
                }
            }
            : edge).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"archie-api-unicode-properties-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(path, ContractJson.WriteGraphSnapshot(graph with { Edges = edges }));
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseSetting("AIP_GRAPH_PATH", path));
            using var client = factory.CreateClient();
            var graphText = await client.GetStringAsync("/api/v1/graph");
            var response = JsonSerializer.Deserialize<GraphResponse>(graphText, ContractJson.Options)!;

            foreach (var (edge, index) in edges.Take(unsafeKeys.Length).Select((edge, index) => (edge, index)))
            {
                Assert.DoesNotContain(unsafeKeys[index], graphText, StringComparison.Ordinal);
                Assert.Empty(response.Edges.Single(item => item.Id == edge.Id).Properties);
                var edgeText = await client.GetStringAsync($"/api/v1/edges/{Uri.EscapeDataString(edge.Id)}");
                Assert.DoesNotContain(unsafeKeys[index], edgeText, StringComparison.Ordinal);
                Assert.Empty(JsonSerializer.Deserialize<GraphEdgeResponse>(edgeText, ContractJson.Options)!.Properties);
            }
        }
        finally { File.Delete(path); }
    }
}
