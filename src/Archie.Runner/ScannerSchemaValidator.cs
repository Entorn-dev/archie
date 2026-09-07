using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Archie.Runner;

internal static class ScannerSchemaValidator
{
    public static void Validate(ReadOnlySpan<byte> bytes, string schemaFile, string contractName, string source)
    {
        var instance = JsonNode.Parse(bytes) ?? throw new JsonException($"{contractName} is empty: {source}");
        var directory = Path.Combine(AppContext.BaseDirectory, "schemas", "v1");
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
        foreach (var file in Directory.EnumerateFiles(directory, "*.schema.json"))
            options.SchemaRegistry.Register(JsonSchema.FromText(File.ReadAllText(file)));
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(directory, schemaFile)));
        var result = schema.Evaluate(instance, options);
        if (!result.IsValid) throw new InvalidDataException($"{contractName} does not conform to its schema: {source}");
    }
}
