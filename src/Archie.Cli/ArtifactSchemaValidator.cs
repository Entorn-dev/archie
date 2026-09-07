using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Archie.Cli;

internal static class ArtifactSchemaValidator
{
    public static string Validate(ReadOnlySpan<byte> artifact)
    {
        var instance = JsonNode.Parse(artifact) ?? throw new JsonException("The artifact is empty.");
        var schemaVersion = instance["schemaVersion"]?.GetValue<string>()
            ?? throw new InvalidDataException("schemaVersion is required.");
        var schemaFile = schemaVersion switch
        {
            "architecture/v1" => "graph-snapshot.schema.json",
            "observations/v1" => "observation-bundle.schema.json",
            "composition-manifest/v1" => "composition-manifest.schema.json",
            "source-context/v1" => "source-context.schema.json",
            "overlay/v1" => "overlay.schema.json",
            "scanner-manifest/v1" => "scanner-manifest.schema.json",
            "signed-scanner-catalog/v1" => "signed-scanner-catalog.schema.json",
            "scanner-catalog/v1" => "scanner-catalog.schema.json",
            "scanner-package/v1" => "scanner-package.schema.json",
            "installed-scanners/v1" => "installed-scanners.schema.json",
            _ => throw new InvalidDataException($"Unsupported schemaVersion '{schemaVersion}'.")
        };
        var directory = Path.Combine(AppContext.BaseDirectory, "schemas", "v1");
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
        foreach (var file in Directory.EnumerateFiles(directory, "*.schema.json"))
            options.SchemaRegistry.Register(JsonSchema.FromText(File.ReadAllText(file)));
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(directory, schemaFile)));
        var result = schema.Evaluate(instance, options);
        if (!result.IsValid)
        {
            var errors = result.Details
                .Where(detail => detail.Errors is not null)
                .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"))
                .Distinct(StringComparer.Ordinal);
            throw new InvalidDataException($"Artifact does not conform to {schemaVersion}:{Environment.NewLine}{string.Join(Environment.NewLine, errors.Select(error => $"- {error}"))}");
        }
        return schemaVersion;
    }
}
