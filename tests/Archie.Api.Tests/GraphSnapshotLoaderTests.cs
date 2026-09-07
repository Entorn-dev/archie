using Archie.Api;
using Archie.Contracts;
using System.Text.Json;
using Xunit;

namespace Archie.Api.Tests;

public sealed class GraphSnapshotLoaderTests
{
    [Fact]
    public void SchemaInvalidArtifactFailsBeforeIndexesAreBuilt()
    {
        var artifact = File.ReadAllText(FixturePath()).Replace(
            "\"schemaVersion\": \"architecture/v1\"",
            "\"schemaVersion\": \"architecture/v2\"",
            StringComparison.Ordinal);
        var path = WriteTemporaryArtifact(artifact);

        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => GraphSnapshotLoader.Load(path));
            Assert.Contains("does not conform to architecture/v1", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ArtifactAboveNodeLimitFailsRatherThanBeingTruncated()
    {
        var limits = new GraphRuntimeLimits(MaxSnapshotNodes: 2);

        var exception = Assert.Throws<InvalidDataException>(() => GraphSnapshotLoader.Load(FixturePath(), limits));

        Assert.Contains("nodes 14/2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("was not loaded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactAboveDiagnosticLimitFailsRatherThanBeingTruncated()
    {
        using var stream = File.OpenRead(FixturePath());
        var graph = ContractJson.ReadGraphSnapshot(stream) with
        {
            Diagnostics =
            [
                new("diagnostic:first", "FIRST", "warning", "First warning.", null),
                new("diagnostic:second", "SECOND", "warning", "Second warning.", null)
            ]
        };
        var path = Path.Combine(Path.GetTempPath(), $"archie-diagnostic-limit-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(graph, ContractJson.Options));
        try
        {
            var limits = new GraphRuntimeLimits(MaxSnapshotDiagnostics: 1);

            var exception = Assert.Throws<InvalidDataException>(() => GraphSnapshotLoader.Load(path, limits));

            Assert.Contains("diagnostics 2/1", exception.Message, StringComparison.Ordinal);
            Assert.Contains("was not loaded", exception.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    private static string WriteTemporaryArtifact(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"archie-invalid-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static string FixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "graphs", "book-retail-minimal.json");
}
