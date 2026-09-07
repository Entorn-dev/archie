using System.Security.Cryptography;
using System.Text.Json;
using Archie.Cli;
using Archie.Contracts;
using Archie.Core;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Archie.Cli.Tests;

public sealed class McpServerHostTests
{
    [Fact]
    public void AllToolsAdvertiseTheirProgressiveDetailLimits()
    {
        var tools = McpServerHost.CreateTools();

        Assert.Equal([
            "archie_get_context", "archie_get_dependencies", "archie_trace_flow", "archie_assess_change_impact"
        ], tools.Select(tool => tool.Name).ToArray());
        var context = tools.Single(tool => tool.Name == "archie_get_context");
        var properties = context.InputSchema.GetProperty("properties");
        Assert.Equal(["summary", "evidence", "full"], properties.GetProperty("detail").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal("summary", properties.GetProperty("detail").GetProperty("default").GetString());
        Assert.Equal(6, properties.GetProperty("maxItems").GetProperty("default").GetInt32());
        Assert.Equal(25, properties.GetProperty("maxItems").GetProperty("maximum").GetInt32());
        Assert.Contains("compact summary by default", context.Description, StringComparison.Ordinal);
        var dependencies = tools.Single(tool => tool.Name == "archie_get_dependencies");
        var dependencyProperties = dependencies.InputSchema.GetProperty("properties");
        Assert.Equal(["summary", "evidence", "full"], dependencyProperties.GetProperty("detail").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal("summary", dependencyProperties.GetProperty("detail").GetProperty("default").GetString());
        Assert.Equal(12, dependencyProperties.GetProperty("maxItems").GetProperty("default").GetInt32());
        Assert.Equal(50, dependencyProperties.GetProperty("maxItems").GetProperty("maximum").GetInt32());
        Assert.Contains("compact summary by default", dependencies.Description, StringComparison.Ordinal);
        var flow = tools.Single(tool => tool.Name == "archie_trace_flow");
        var flowProperties = flow.InputSchema.GetProperty("properties");
        Assert.Equal(["summary", "evidence", "full"], flowProperties.GetProperty("detail").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal("summary", flowProperties.GetProperty("detail").GetProperty("default").GetString());
        Assert.Equal(3, flowProperties.GetProperty("maxItems").GetProperty("default").GetInt32());
        Assert.Equal(10, flowProperties.GetProperty("maxItems").GetProperty("maximum").GetInt32());
        Assert.Contains("compact summary by default", flow.Description, StringComparison.Ordinal);
        var impact = tools.Single(tool => tool.Name == "archie_assess_change_impact");
        var impactProperties = impact.InputSchema.GetProperty("properties");
        Assert.Equal(["summary", "evidence", "full"], impactProperties.GetProperty("detail").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal("summary", impactProperties.GetProperty("detail").GetProperty("default").GetString());
        Assert.Equal(8, impactProperties.GetProperty("maxItems").GetProperty("default").GetInt32());
        Assert.Equal(25, impactProperties.GetProperty("maxItems").GetProperty("maximum").GetInt32());
        Assert.Contains("compact summary by default", impact.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextDefaultAndEvidenceReturnOneBlockWithoutStructuredContent()
    {
        var service = Service();

        var summary = Call(service, new Dictionary<string, JsonElement>
        {
            ["path"] = Json("tests/fixtures/observations/book-retail.authored.json"),
            ["line"] = Json(179)
        });
        var evidence = Call(service, new Dictionary<string, JsonElement>
        {
            ["path"] = Json("Checkout/Other.cs"),
            ["detail"] = Json("evidence")
        });

        Assert.False(summary.IsError);
        Assert.Null(summary.StructuredContent);
        var summaryText = Assert.IsType<TextContentBlock>(Assert.Single(summary.Content)).Text;
        Assert.StartsWith("# Context for", summaryText, StringComparison.Ordinal);
        Assert.Contains("## Subjects (6/7)", summaryText, StringComparison.Ordinal);
        Assert.False(evidence.IsError);
        Assert.Null(evidence.StructuredContent);
        var evidenceText = Assert.IsType<TextContentBlock>(Assert.Single(evidence.Content)).Text;
        Assert.Contains("rule `msbuild:compile-item-ownership`", evidenceText, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextFullPreservesTheExistingContentAndStructuredShape()
    {
        var service = Service();
        var arguments = new Dictionary<string, JsonElement>
        {
            ["path"] = Json("tests/fixtures/observations/book-retail.authored.json"),
            ["line"] = Json(179),
            ["detail"] = Json("full"),
            ["maxItems"] = Json(1)
        };
        var expected = service.GetContext(
            "tests/fixtures/observations/book-retail.authored.json", 179, null);
        var expectedJson = JsonSerializer.Serialize(expected, ContractJson.Options);

        var result = Call(service, arguments);

        Assert.False(result.IsError);
        Assert.Equal(expectedJson, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.NotNull(result.StructuredContent);
        Assert.Equal(expectedJson, result.StructuredContent.Value.GetRawText());
    }

    [Theory]
    [InlineData("unknown", null)]
    [InlineData("summary", 0)]
    [InlineData("summary", 26)]
    public void ContextRejectsInvalidPresentationArguments(string detail, int? maxItems)
    {
        var arguments = new Dictionary<string, JsonElement>
        {
            ["path"] = Json("Checkout/Other.cs"),
            ["detail"] = Json(detail)
        };
        if (maxItems is not null) arguments["maxItems"] = Json(maxItems.Value);

        var result = Call(Service(), arguments);

        Assert.True(result.IsError);
        var error = result.StructuredContent!.Value;
        Assert.Equal("INVALID_ARGUMENT", error.GetProperty("error").GetString());
        Assert.Single(result.Content);
    }

    [Fact]
    public void DependencyDefaultAndEvidenceReturnOneBlockWithoutStructuredContent()
    {
        var service = Service();
        var summary = Call(service, "archie_get_dependencies", new Dictionary<string, JsonElement>
        {
            ["nodeId"] = Json("deployable:checkout-service"),
            ["direction"] = Json("downstream"),
            ["depth"] = Json(2),
            ["maxItems"] = Json(1)
        });
        var evidence = Call(service, "archie_get_dependencies", new Dictionary<string, JsonElement>
        {
            ["nodeId"] = Json("deployable:checkout-service"),
            ["direction"] = Json("downstream"),
            ["depth"] = Json(2),
            ["detail"] = Json("evidence")
        });

        Assert.False(summary.IsError);
        Assert.Null(summary.StructuredContent);
        var summaryText = Assert.IsType<TextContentBlock>(Assert.Single(summary.Content)).Text;
        Assert.StartsWith("# Dependencies for", summaryText, StringComparison.Ordinal);
        Assert.Contains("## Relationships (1/", summaryText, StringComparison.Ordinal);
        Assert.False(evidence.IsError);
        Assert.Null(evidence.StructuredContent);
        Assert.Contains("Evidence `evidence:", Assert.IsType<TextContentBlock>(Assert.Single(evidence.Content)).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyFullPreservesTheExistingContentAndStructuredShape()
    {
        var service = Service();
        var arguments = new Dictionary<string, JsonElement>
        {
            ["nodeId"] = Json("deployable:checkout-service"),
            ["direction"] = Json("downstream"),
            ["depth"] = Json(2),
            ["detail"] = Json("full"),
            ["maxItems"] = Json(1)
        };
        var expected = service.GetDependencies(
            "deployable:checkout-service", DependencyDirection.Downstream, 2,
            new HashSet<NodeKind>(), new HashSet<EdgeKind>());
        var expectedJson = JsonSerializer.Serialize(expected, ContractJson.Options);

        var result = Call(service, "archie_get_dependencies", arguments);

        Assert.False(result.IsError);
        Assert.Equal(expectedJson, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.NotNull(result.StructuredContent);
        Assert.Equal(expectedJson, result.StructuredContent.Value.GetRawText());
    }

    [Fact]
    public void DependencyRejectsMaxItemsAboveItsLimit()
    {
        var result = Call(Service(), "archie_get_dependencies", new Dictionary<string, JsonElement>
        {
            ["nodeId"] = Json("deployable:checkout-service"),
            ["maxItems"] = Json(51)
        });

        Assert.True(result.IsError);
        Assert.Equal("INVALID_ARGUMENT", result.StructuredContent!.Value.GetProperty("error").GetString());
    }

    [Fact]
    public void DependencyPresentationLimitDoesNotBypassTheExistingQueryLimit()
    {
        var result = Call(Service(new(MaxEdges: 1)), "archie_get_dependencies", new Dictionary<string, JsonElement>
        {
            ["nodeId"] = Json("deployable:checkout-service"),
            ["direction"] = Json("downstream"),
            ["depth"] = Json(2),
            ["maxItems"] = Json(1)
        });

        Assert.True(result.IsError);
        Assert.Equal("QUERY_LIMIT_EXCEEDED", result.StructuredContent!.Value.GetProperty("error").GetString());
    }

    [Fact]
    public void FlowDefaultAndEvidenceReturnOneBlockWithoutStructuredContent()
    {
        var service = Service();
        var summary = Call(service, "archie_trace_flow", FlowArguments());
        var evidenceArguments = FlowArguments();
        evidenceArguments["detail"] = Json("evidence");
        var evidence = Call(service, "archie_trace_flow", evidenceArguments);

        Assert.False(summary.IsError);
        Assert.Null(summary.StructuredContent);
        var summaryText = Assert.IsType<TextContentBlock>(Assert.Single(summary.Content)).Text;
        Assert.StartsWith("# Flow trace", summaryText, StringComparison.Ordinal);
        Assert.StartsWith("# Flow trace (1/1 paths)", summaryText, StringComparison.Ordinal);
        Assert.False(evidence.IsError);
        Assert.Null(evidence.StructuredContent);
        Assert.Contains("Evidence `evidence:", Assert.IsType<TextContentBlock>(Assert.Single(evidence.Content)).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FlowFullPreservesTheExistingMarkdownAndStructuredShape()
    {
        var service = Service();
        var arguments = FlowArguments();
        arguments["detail"] = Json("full");
        arguments["maxItems"] = Json(1);
        var expected = service.TraceFlow(
            "deployable:checkout-service", "deployable:notification-service", PathDirection.Downstream, 8);
        var expectedJson = JsonSerializer.Serialize(expected, ContractJson.Options);

        var result = Call(service, "archie_trace_flow", arguments);

        Assert.False(result.IsError);
        Assert.Equal(expected.Markdown, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.NotNull(result.StructuredContent);
        Assert.Equal(expectedJson, result.StructuredContent.Value.GetRawText());
    }

    [Fact]
    public void FlowRejectsMaxItemsAboveItsLimit()
    {
        var arguments = FlowArguments();
        arguments["maxItems"] = Json(11);

        var result = Call(Service(), "archie_trace_flow", arguments);

        Assert.True(result.IsError);
        Assert.Equal("INVALID_ARGUMENT", result.StructuredContent!.Value.GetProperty("error").GetString());
    }

    [Fact]
    public void ImpactDefaultAndEvidenceReturnOneBlockWithoutStructuredContent()
    {
        var service = Service();
        var summary = Call(service, "archie_assess_change_impact", ImpactArguments());
        var evidenceArguments = ImpactArguments();
        evidenceArguments["detail"] = Json("evidence");
        var evidence = Call(service, "archie_assess_change_impact", evidenceArguments);

        Assert.False(summary.IsError);
        Assert.Null(summary.StructuredContent);
        var summaryText = Assert.IsType<TextContentBlock>(Assert.Single(summary.Content)).Text;
        Assert.StartsWith("# Potential static change impact", summaryText, StringComparison.Ordinal);
        Assert.Contains("## Direct evidence (", summaryText, StringComparison.Ordinal);
        Assert.Contains("## Owner-level possibilities (0/0)", summaryText, StringComparison.Ordinal);
        Assert.Contains("## Downstream architecture effects (", summaryText, StringComparison.Ordinal);
        Assert.Contains("## Unmatched files (1/1)", summaryText, StringComparison.Ordinal);
        Assert.False(evidence.IsError);
        Assert.Null(evidence.StructuredContent);
        Assert.Contains("Evidence `evidence:", Assert.IsType<TextContentBlock>(Assert.Single(evidence.Content)).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImpactFullPreservesTheExistingMarkdownAndStructuredShape()
    {
        var service = Service();
        var arguments = ImpactArguments();
        arguments["detail"] = Json("full");
        arguments["maxItems"] = Json(1);
        var expected = service.AssessChangeImpact(
            [
                new("tests/fixtures/observations/book-retail.authored.json", 179, 179),
                new("unknown.txt")
            ],
            2);
        var expectedJson = JsonSerializer.Serialize(expected, ContractJson.Options);

        var result = Call(service, "archie_assess_change_impact", arguments);

        Assert.False(result.IsError);
        Assert.Equal(expected.Markdown, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.NotNull(result.StructuredContent);
        Assert.Equal(expectedJson, result.StructuredContent.Value.GetRawText());
    }

    [Theory]
    [InlineData("unknown", null)]
    [InlineData("summary", 0)]
    [InlineData("summary", 26)]
    public void ImpactRejectsInvalidPresentationArguments(string detail, int? maxItems)
    {
        var arguments = ImpactArguments();
        arguments["detail"] = Json(detail);
        if (maxItems is not null) arguments["maxItems"] = Json(maxItems.Value);

        var result = Call(Service(), "archie_assess_change_impact", arguments);

        Assert.True(result.IsError);
        Assert.Equal("INVALID_ARGUMENT", result.StructuredContent!.Value.GetProperty("error").GetString());
    }

    [Fact]
    public void ImpactPresentationLimitDoesNotBypassTheExistingQueryLimit()
    {
        var arguments = ImpactArguments();
        arguments["maxItems"] = Json(1);

        var result = Call(Service(new(MaxEdges: 1)), "archie_assess_change_impact", arguments);

        Assert.True(result.IsError);
        Assert.Equal("QUERY_LIMIT_EXCEEDED", result.StructuredContent!.Value.GetProperty("error").GetString());
    }

    private static CallToolResult Call(ArchitectureContextService service, Dictionary<string, JsonElement> arguments) =>
        Call(service, "archie_get_context", arguments);

    private static CallToolResult Call(
        ArchitectureContextService service,
        string name,
        Dictionary<string, JsonElement> arguments) =>
        McpServerHost.Call(service, new() { Name = name, Arguments = arguments }, CancellationToken.None);

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static Dictionary<string, JsonElement> FlowArguments() => new()
    {
        ["from"] = Json("deployable:checkout-service"),
        ["to"] = Json("deployable:notification-service")
    };

    private static Dictionary<string, JsonElement> ImpactArguments() => new()
    {
        ["changes"] = Json(new object[]
        {
            new { path = "tests/fixtures/observations/book-retail.authored.json", startLine = 179, endLine = 179 },
            new { path = "unknown.txt" }
        }),
        ["downstreamDepth"] = Json(2)
    };

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
                "deployable:checkout-service", SourceOwnershipKind.Project, Confidence.Confirmed, Resolution.Resolved,
                Resolution.Resolved, "msbuild:compile-item-ownership")]);
        SourceContextValidator.Validate(source, graph, graphBytes);
        return new(graph, source, limits);
    }
}
