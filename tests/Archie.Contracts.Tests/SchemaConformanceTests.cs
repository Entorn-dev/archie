using System.Text.Json;
using System.Text.Json.Nodes;
using Archie.Contracts;
using Json.Schema;
using Xunit;

namespace Archie.Contracts.Tests;

public sealed class SchemaConformanceTests
{
    [Fact]
    public void MinimalBookRetailFixtureConformsToVersionedSchema()
    {
        var schemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas", "v1");
        var common = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, "common.schema.json")));
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, "graph-snapshot.schema.json")));
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
        options.SchemaRegistry.Register(common);
        var fixture = JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "graphs", "book-retail-minimal.json")))!;

        var result = schema.Evaluate(fixture, options);

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void UnknownCanonicalFieldIsRejected()
    {
        var schemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas", "v1");
        var common = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, "common.schema.json")));
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, "graph-snapshot.schema.json")));
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        options.SchemaRegistry.Register(common);
        var fixture = JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "graphs", "book-retail-minimal.json")))!.AsObject();
        fixture["unexpected"] = true;

        Assert.False(schema.Evaluate(fixture, options).IsValid);
    }

    [Fact]
    public void PlaceOrderSavedViewConformsToVersionedSchema()
    {
        var schemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas", "v1");
        var common = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, "common.schema.json")));
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, "saved-view.schema.json")));
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        options.SchemaRegistry.Register(common);
        var fixture = JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "views", "place-order.json")))!;

        Assert.True(schema.Evaluate(fixture, options).IsValid);
    }

    [Theory]
    [InlineData("observation-bundle.schema.json", "fixtures/observations/book-retail.authored.json")]
    [InlineData("overlay.schema.json", "fixtures/overlays/book-retail.overlay.json")]
    [InlineData("scanner-manifest.schema.json", "fixtures/scanners/fake-book-retail/scanner.json")]
    [InlineData("scanner-manifest.schema.json", "fixtures/scanners/dotnet/scanner.json")]
    [InlineData("scanner-manifest.schema.json", "fixtures/scanners/php/scanner.json")]
    [InlineData("protocol-message.schema.json", "fixtures/protocol/ready.json")]
    [InlineData("scanner-package.schema.json", "fixtures/scanner-packages/fake/PACKAGE.json")]
    [InlineData("installed-scanners.schema.json", "fixtures/scanner-packages/installed.json")]
    public void ContractInputsConformToVersionedSchemas(string schemaFile, string fixturePath)
    {
        var schemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas", "v1");
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
        foreach (var file in Directory.EnumerateFiles(schemaDirectory, "*.schema.json"))
            options.SchemaRegistry.Register(JsonSchema.FromText(File.ReadAllText(file)));
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, schemaFile)));
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fixturePath)))!;

        var result = schema.Evaluate(fixture, options);

        Assert.True(result.IsValid, result.ToString());
    }

    [Theory]
    [InlineData("scanner-package.schema.json", "fixtures/scanner-packages/fake/PACKAGE.json")]
    [InlineData("installed-scanners.schema.json", "fixtures/scanner-packages/installed.json")]
    public void DistributionSchemasRejectUnknownFields(string schemaFile, string fixturePath)
    {
        var schemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas", "v1");
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
        foreach (var file in Directory.EnumerateFiles(schemaDirectory, "*.schema.json"))
            options.SchemaRegistry.Register(JsonSchema.FromText(File.ReadAllText(file)));
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, schemaFile)));
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fixturePath)))!.AsObject();
        fixture["unexpected"] = true;

        Assert.False(schema.Evaluate(fixture, options).IsValid);
    }

    [Fact]
    public void CatalogSchemasAcceptCanonicalDocumentsAndRejectUnknownReleaseFields()
    {
        var schemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas", "v1");
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
        foreach (var file in Directory.EnumerateFiles(schemaDirectory, "*.schema.json"))
            options.SchemaRegistry.Register(JsonSchema.FromText(File.ReadAllText(file)));
        var release = CatalogRelease();
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new ScannerCatalog("scanner-catalog/v1", "catalog-v1", DateTimeOffset.UnixEpoch, [release]),
            ContractJson.Options);
        var catalogNode = JsonNode.Parse(payload)!;
        var envelopeNode = JsonSerializer.SerializeToNode(
            new SignedScannerCatalog("signed-scanner-catalog/v1", "key-v1", Convert.ToBase64String(payload), Convert.ToBase64String(new byte[64])),
            ContractJson.Options)!;

        var catalogResult = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, "scanner-catalog.schema.json")))
            .Evaluate(catalogNode, options);
        var envelopeResult = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, "signed-scanner-catalog.schema.json")))
            .Evaluate(envelopeNode, options);

        Assert.True(catalogResult.IsValid, catalogResult.ToString());
        Assert.True(envelopeResult.IsValid, envelopeResult.ToString());
        catalogNode["releases"]![0]!["unexpected"] = true;
        Assert.False(JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, "scanner-catalog.schema.json")))
            .Evaluate(catalogNode, options).IsValid);
    }

    [Fact]
    public void ProtocolConformanceFixturesHaveExpectedValidity()
    {
        var (schema, options) = ProtocolSchema();
        var protocolDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures", "protocol");

        foreach (var fixturePath in Directory.EnumerateFiles(Path.Combine(protocolDirectory, "valid"), "*.json"))
        {
            var fixture = JsonNode.Parse(File.ReadAllText(fixturePath))!;
            var result = schema.Evaluate(fixture, options);
            Assert.True(result.IsValid, $"Expected {Path.GetFileName(fixturePath)} to be valid: {result}");
        }

        foreach (var fixturePath in Directory.EnumerateFiles(Path.Combine(protocolDirectory, "invalid"), "*.json"))
        {
            var fixture = JsonNode.Parse(File.ReadAllText(fixturePath))!;
            Assert.False(schema.Evaluate(fixture, options).IsValid,
                $"Expected {Path.GetFileName(fixturePath)} to be invalid.");
        }
    }

    [Fact]
    public async Task NonDotNetWorkerEmitsConformantBindingCompatibleProtocol()
    {
        var protocolDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures", "protocol");
        var start = new System.Diagnostics.ProcessStartInfo("node", [Path.Combine(protocolDirectory, "non-dotnet-worker.mjs")])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the non-.NET fixture worker.");

        var lines = new List<string> { (await process.StandardOutput.ReadLineAsync())! };
        var request = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(protocolDirectory, "valid", "scan-request.json")))!;
        await process.StandardInput.WriteLineAsync(request.ToJsonString());
        process.StandardInput.Close();
        while (await process.StandardOutput.ReadLineAsync() is { } line) lines.Add(line);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        Assert.Equal(6, lines.Count);
        var (schema, options) = ProtocolSchema();
        var messages = new List<ProtocolMessage>();
        foreach (var line in lines)
        {
            var fixture = JsonNode.Parse(line)!;
            var result = schema.Evaluate(fixture, options);
            Assert.True(result.IsValid, result.ToString());
            messages.Add(System.Text.Json.JsonSerializer.Deserialize<ProtocolMessage>(line, ScannerContractJson.Options)!);
        }

        Assert.IsType<ReadyMessage>(messages[0]);
        Assert.Equal(2, messages.OfType<ObservationMessage>().Count());
        Assert.Single(messages.OfType<SourceOwnershipMessage>());
        Assert.Single(messages.OfType<DiagnosticMessage>());
        Assert.Equal(2, Assert.Single(messages.OfType<CompletedMessage>()).Summary.ObservationCount);
    }

    private static (JsonSchema Schema, EvaluationOptions Options) ProtocolSchema()
    {
        var schemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas", "v1");
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
        foreach (var file in Directory.EnumerateFiles(schemaDirectory, "*.schema.json"))
            options.SchemaRegistry.Register(JsonSchema.FromText(File.ReadAllText(file)));
        return (JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, "protocol-message.schema.json"))), options);
    }

    private static ScannerCatalogRelease CatalogRelease() => new(
        "scanner:test", "Test scanner", "1.0.0", "stable", "https://github.com/archie-dev/test-scanner", "v1.0.0",
        "linux", "x64", "[1.0.0,2.0.0)", "scanner/v1",
        new Uri("https://github.com/archie-dev/test-scanner/releases/download/v1.0.0/test-scanner.tar.gz"),
        1024, 2048, 4, new string('a', 64), "key-v1", Convert.ToBase64String(new byte[64]),
        ["observations", "source-ownership"], new ScannerPermissions(true, false, false), "MIT",
        new Uri("https://github.com/archie-dev/test-scanner/releases/tag/v1.0.0"), false);
}
