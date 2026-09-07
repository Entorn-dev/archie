using System.Text.Json;
using Archie.Contracts;
using Xunit;

namespace Archie.Contracts.Tests;

public sealed class DeterministicJsonTests
{
    [Fact]
    public void EquivalentSnapshotsSerializeToIdenticalUtf8()
    {
        var snapshot = ReadFixture();
        using var firstProperties = JsonDocument.Parse("{\"z\":1,\"nested\":{\"b\":2,\"a\":1}}");
        using var secondProperties = JsonDocument.Parse("{\"nested\":{\"a\":1,\"b\":2},\"z\":1}");
        var firstNode = snapshot.Nodes[0] with
        {
            Properties = new Dictionary<string, JsonElement> { ["details"] = firstProperties.RootElement.Clone() }
        };
        var secondNode = snapshot.Nodes[0] with
        {
            Properties = new Dictionary<string, JsonElement> { ["details"] = secondProperties.RootElement.Clone() }
        };
        var reordered = snapshot with
        {
            Nodes = [secondNode, .. snapshot.Nodes.Skip(1).Reverse()],
            Edges = snapshot.Edges.Reverse().ToArray(),
            Evidence = snapshot.Evidence.Reverse().ToArray()
        };
        snapshot = snapshot with { Nodes = [firstNode, .. snapshot.Nodes.Skip(1)] };

        Assert.Equal(ContractJson.WriteGraphSnapshot(snapshot), ContractJson.WriteGraphSnapshot(reordered));
    }

    [Fact]
    public void UnknownRequiredContractFieldIsRejectedDuringDeserialization()
    {
        var json = File.ReadAllText(FixturePath()).Replace(
            "\"schemaVersion\": \"architecture/v1\"",
            "\"schemaVersion\": \"architecture/v1\", \"unexpected\": true",
            StringComparison.Ordinal);

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Throws<JsonException>(() => ContractJson.ReadGraphSnapshot(stream));
    }

    [Fact]
    public void EquivalentObservationBundlesSerializeToIdenticalUtf8()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "observations", "book-retail.authored.json"));
        var bundle = ContractJson.ReadObservationBundle(stream);
        var reordered = bundle with { Observations = bundle.Observations.Reverse().ToArray() };

        Assert.Equal(ContractJson.WriteObservationBundle(bundle), ContractJson.WriteObservationBundle(reordered));
        Assert.Equal(ContractJson.WriteObservationBundle(bundle), ScannerContractJson.WriteObservationBundle(bundle));
    }

    [Fact]
    public void EquivalentSourceContextSerializesToIdenticalUtf8()
    {
        var repository = new RepositoryRevision("repo", null, "revision", false, new string('a', 64));
        var first = new SourceOwnership("scanner:b", "1", "src/B.cs", "candidate:b", "module:b",
            SourceOwnershipKind.Project, Confidence.Confirmed, Resolution.Resolved, Resolution.Resolved, "rule:b");
        var second = new SourceOwnership("scanner:a", "1", "src/A.cs", "candidate:a", "module:a",
            SourceOwnershipKind.Project, Confidence.Confirmed, Resolution.Resolved, Resolution.Resolved, "rule:a");
        var snapshot = new SourceContextSnapshot("source-context/v1", repository, new string('b', 64),
            [new("scanner:b", "1"), new("scanner:a", "1")], new string('c', 64), new string('d', 64), [first, second]);
        var reordered = snapshot with { Scanners = snapshot.Scanners.Reverse().ToArray(), Ownership = snapshot.Ownership.Reverse().ToArray() };

        Assert.Equal(ContractJson.WriteSourceContext(snapshot), ContractJson.WriteSourceContext(reordered));
    }

    [Fact]
    public void InstalledScannerReceiptSerializationIsDeterministicallyOrdered()
    {
        var first = new InstalledScannerPackage("scanner:b", "2", new string('b', 64), ScannerPackageOrigin.LocalUnsigned,
            ScannerTrustState.LocalUnsigned, null, null, DateTimeOffset.UnixEpoch, 2);
        var second = new InstalledScannerPackage("scanner:a", "1", new string('a', 64), ScannerPackageOrigin.LocalUnsigned,
            ScannerTrustState.LocalUnsigned, null, null, DateTimeOffset.UnixEpoch, 1);
        var receipt = new InstalledScannerReceipt("installed-scanners/v1", [first, second],
            [new(first.Id, first.Version, first.Sha256), new(second.Id, second.Version, second.Sha256)]);
        var reordered = receipt with { Packages = receipt.Packages.Reverse().ToArray(), Active = receipt.Active.Reverse().ToArray() };

        Assert.Equal(ContractJson.WriteInstalledScanners(receipt), ContractJson.WriteInstalledScanners(reordered));
    }

    [Fact]
    public void ScannerCatalogSerializationIsDeterministicallyOrdered()
    {
        var first = CatalogRelease("scanner:b", "2.0.0", ["source-ownership", "observations"]);
        var second = CatalogRelease("scanner:a", "1.0.0", ["observations", "source-ownership"]);
        var catalog = new ScannerCatalog("scanner-catalog/v1", "catalog-v1", DateTimeOffset.UnixEpoch, [first, second]);
        var reordered = catalog with
        {
            Releases = [second with { Capabilities = second.Capabilities.Reverse().ToArray() },
                first with { Capabilities = first.Capabilities.Reverse().ToArray() }]
        };

        Assert.Equal(ContractJson.WriteScannerCatalog(catalog), ContractJson.WriteScannerCatalog(reordered));
    }

    private static GraphSnapshot ReadFixture()
    {
        using var stream = File.OpenRead(FixturePath());
        return ContractJson.ReadGraphSnapshot(stream);
    }

    private static string FixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "graphs", "book-retail-minimal.json");

    private static ScannerCatalogRelease CatalogRelease(string id, string version, IReadOnlyList<string> capabilities) => new(
        id, "Test scanner", version, "stable", "https://github.com/archie-dev/test-scanner", $"v{version}",
        "linux", "x64", "[1.0.0,2.0.0)", "scanner/v1",
        new Uri($"https://github.com/archie-dev/test-scanner/releases/download/v{version}/test-scanner.tar.gz"),
        1024, 2048, 4, new string('a', 64), "key-v1", Convert.ToBase64String(new byte[64]), capabilities,
        new ScannerPermissions(true, false, false), "MIT",
        new Uri($"https://github.com/archie-dev/test-scanner/releases/tag/v{version}"), false);
}
