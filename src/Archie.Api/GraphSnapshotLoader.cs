using System.Text.Json;
using System.Text.Json.Nodes;
using Archie.Contracts;
using Archie.Core;
using Json.Schema;

namespace Archie.Api;

public sealed record GraphRuntimeLimits(
    long MaxSnapshotBytes = 268_435_456,
    int MaxSnapshotNodes = 100_000,
    int MaxSnapshotEdges = 500_000,
    int MaxSnapshotEvidence = 1_000_000,
    int MaxSnapshotDiagnostics = 100_000,
    int DefaultSubgraphNodes = 300,
    int MaxSubgraphNodes = 500,
    int DefaultSubgraphEdges = 900,
    int MaxSubgraphEdges = 1_500,
    int DefaultPathDepth = 8,
    int MaxPathDepth = 12,
    int MaxReturnedPaths = 50,
    int MaxVisitedNodes = 10_000,
    TimeSpan? TraversalTimeout = null)
{
    public PathRuntimeLimits PathLimits => new(
        DefaultPathDepth,
        MaxPathDepth,
        MaxReturnedPaths,
        MaxVisitedNodes,
        TraversalTimeout ?? TimeSpan.FromSeconds(2));
}

public sealed record LoadedGraph(GraphSnapshot Snapshot, GraphIndexes Indexes, GraphRuntimeLimits Limits);

public static class GraphSnapshotLoader
{
    public static LoadedGraph Load(string? configuredPath, GraphRuntimeLimits? limits = null)
    {
        limits ??= new GraphRuntimeLimits();
        var artifactPath = configuredPath ?? Path.Combine(AppContext.BaseDirectory, "fixtures", "graphs", "book-retail-minimal.json");
        var schemaDirectory = Path.Combine(AppContext.BaseDirectory, "schemas", "v1");

        var file = new FileInfo(artifactPath);
        if (!file.Exists) throw new FileNotFoundException("The canonical graph artifact does not exist.", artifactPath);
        if (file.Length > limits.MaxSnapshotBytes)
        {
            throw new InvalidDataException($"The canonical graph artifact is {file.Length} bytes; the limit is {limits.MaxSnapshotBytes} bytes.");
        }

        var bytes = File.ReadAllBytes(artifactPath);
        ValidateSchema(bytes, schemaDirectory);

        GraphSnapshot snapshot;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            snapshot = ContractJson.ReadGraphSnapshot(stream);
        }

        ValidateArtifactLimits(snapshot, limits);
        GraphValidator.Validate(snapshot);
        return new LoadedGraph(snapshot, new GraphIndexes(snapshot), limits);
    }

    public static void ValidateSchema(ReadOnlySpan<byte> artifact, string schemaDirectory)
    {
        var commonSchema = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, "common.schema.json")));
        var graphSchema = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, "graph-snapshot.schema.json")));
        var options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        };
        options.SchemaRegistry.Register(commonSchema);

        var instance = JsonNode.Parse(artifact) ?? throw new JsonException("The canonical graph artifact is empty.");
        var results = graphSchema.Evaluate(instance, options);
        if (!results.IsValid)
        {
            throw new InvalidDataException($"The canonical graph artifact does not conform to architecture/v1: {JsonSerializer.Serialize(results)}");
        }
    }

    private static void ValidateArtifactLimits(GraphSnapshot snapshot, GraphRuntimeLimits limits)
    {
        var errors = new List<string>();
        if (snapshot.Nodes.Count > limits.MaxSnapshotNodes) errors.Add($"nodes {snapshot.Nodes.Count}/{limits.MaxSnapshotNodes}");
        if (snapshot.Edges.Count > limits.MaxSnapshotEdges) errors.Add($"edges {snapshot.Edges.Count}/{limits.MaxSnapshotEdges}");
        if (snapshot.Evidence.Count > limits.MaxSnapshotEvidence) errors.Add($"evidence {snapshot.Evidence.Count}/{limits.MaxSnapshotEvidence}");
        if (snapshot.Diagnostics.Count > limits.MaxSnapshotDiagnostics) errors.Add($"diagnostics {snapshot.Diagnostics.Count}/{limits.MaxSnapshotDiagnostics}");
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"The canonical graph artifact exceeds startup limits ({string.Join(", ", errors)}). It was not loaded.");
        }
    }
}
