using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Archie.Cli;
using Archie.Contracts;
using Archie.Runner;
using Xunit;

namespace Archie.Cli.Tests;

public sealed class ScannerCatalogClientTests
{
    [Fact]
    public async Task VerifiedCatalogReplacesCacheAndInvalidSignaturePreservesIt()
    {
        using var temporary = new TemporaryDirectory();
        using var fixture = await CatalogFixture.CreateAsync(temporary.Path);
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var responseBytes = fixture.EnvelopeBytes;
        var handler = new DelegateHandler(_ => Bytes(responseBytes));
        using var client = new ScannerCatalogClient(store, handler, fixture.CatalogUrl, fixture.Verifier);

        var catalog = await client.RefreshAsync(CancellationToken.None);
        var cached = await File.ReadAllBytesAsync(client.CachePath);
        var tampered = JsonNode.Parse(responseBytes)!.AsObject();
        var payload = Convert.FromBase64String(tampered["payload"]!.GetValue<string>());
        payload[^1] ^= 1;
        tampered["payload"] = Convert.ToBase64String(payload);
        responseBytes = JsonSerializer.SerializeToUtf8Bytes(tampered, ContractJson.Options);

        var fallback = await client.RefreshOrCachedAsync(CancellationToken.None);

        Assert.Equal("catalog-test-v1", catalog.CatalogVersion);
        Assert.True(fallback.UsedCachedCatalog);
        Assert.Contains("signature is invalid", fallback.RefreshFailure, StringComparison.Ordinal);
        Assert.Equal(cached, await File.ReadAllBytesAsync(client.CachePath));
        Assert.Equal(
            ContractJson.WriteScannerCatalog(catalog),
            ContractJson.WriteScannerCatalog((await client.ReadCachedAsync(CancellationToken.None))!));
    }

    [Fact]
    public async Task RefreshFailureUsesOnlyTheLastVerifiedCachedCatalog()
    {
        using var temporary = new TemporaryDirectory();
        using var fixture = await CatalogFixture.CreateAsync(temporary.Path);
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        byte[]? responseBytes = fixture.EnvelopeBytes;
        var handler = new DelegateHandler(_ => responseBytes is null
            ? throw new HttpRequestException("catalog unavailable")
            : Bytes(responseBytes));
        using var client = new ScannerCatalogClient(store, handler, fixture.CatalogUrl, fixture.Verifier);
        await client.RefreshAsync(CancellationToken.None);
        var cachedBytes = await File.ReadAllBytesAsync(client.CachePath);
        responseBytes = null;

        var loaded = await client.RefreshOrCachedAsync(CancellationToken.None);

        Assert.True(loaded.UsedCachedCatalog);
        Assert.Equal("catalog-test-v1", loaded.Catalog.CatalogVersion);
        Assert.Contains("catalog unavailable", loaded.RefreshFailure, StringComparison.Ordinal);
        Assert.Equal(File.GetLastWriteTimeUtc(client.CachePath), loaded.RetrievedAt);
        Assert.Equal(cachedBytes, await File.ReadAllBytesAsync(client.CachePath));
    }

    [Fact]
    public async Task RefreshFailureWithoutVerifiedCacheIsActionable()
    {
        using var temporary = new TemporaryDirectory();
        using var fixture = await CatalogFixture.CreateAsync(temporary.Path);
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        using var client = new ScannerCatalogClient(store,
            new DelegateHandler(_ => throw new HttpRequestException("catalog unavailable")),
            fixture.CatalogUrl, fixture.Verifier);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RefreshOrCachedAsync(CancellationToken.None));

        Assert.Contains("no verified cached catalog", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(client.CachePath));
    }

    [Fact]
    public async Task GitHubRedirectDownloadAndSignedPackageInstallActivateVerifiedReceipt()
    {
        using var temporary = new TemporaryDirectory();
        using var fixture = await CatalogFixture.CreateAsync(temporary.Path);
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var redirected = new Uri("https://release-assets.githubusercontent.com/asset?token=ephemeral");
        var handler = new DelegateHandler(request => request.RequestUri switch
        {
            var uri when uri == fixture.CatalogUrl => Bytes(fixture.EnvelopeBytes),
            var uri when uri == fixture.Release.AssetUrl => Redirect(redirected),
            var uri when uri == redirected => Bytes(fixture.ArchiveBytes),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var client = new ScannerCatalogClient(store, handler, fixture.CatalogUrl, fixture.Verifier);
        var catalog = await client.RefreshAsync(CancellationToken.None);
        var archive = await client.DownloadAsync(fixture.Release, CancellationToken.None);
        try
        {
            var installed = await new ScannerPackageInstaller(store, fixture.Verifier)
                .InstallCatalogAsync(fixture.Release, catalog.CatalogVersion, archive, CancellationToken.None);
            var receipt = await store.ReadAsync(CancellationToken.None);

            Assert.Equal(ScannerPackageOrigin.Catalog, installed.Origin);
            Assert.Equal(ScannerTrustState.VerifiedFirstParty, installed.Trust);
            Assert.Equal("catalog-test-v1", installed.CatalogVersion);
            Assert.Equal(CatalogFixture.KeyId, installed.VerifiedKeyId);
            Assert.Equal(installed, Assert.Single(receipt.Packages));
            Assert.Equal(installed.Sha256, Assert.Single(receipt.Active).Sha256);
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    [Theory]
    [InlineData("https://example.com/org/scanner.tar.gz")]
    [InlineData("https://github.com/org/repo/releases/download/v1/scanner.tar.gz?token=secret")]
    [InlineData("https://user:password@github.com/org/repo/releases/download/v1/scanner.tar.gz")]
    [InlineData("http://github.com/org/repo/releases/download/v1/scanner.tar.gz")]
    public async Task CatalogAuthoredAssetUrlPolicyRejectsBeforeNetwork(string url)
    {
        using var temporary = new TemporaryDirectory();
        using var fixture = await CatalogFixture.CreateAsync(temporary.Path);
        var requests = 0;
        var handler = new DelegateHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        using var client = new ScannerCatalogClient(
            new InstalledScannerStore(Path.Combine(temporary.Path, "store")), handler, fixture.CatalogUrl, fixture.Verifier);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.DownloadAsync(fixture.Release with { AssetUrl = new Uri(url) }, CancellationToken.None));

        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/asset")]
    [InlineData("https://example.com/asset?token=ephemeral")]
    [InlineData("https://user@release-assets.githubusercontent.com/asset")]
    [InlineData("https://release-assets.githubusercontent.com/asset#fragment")]
    public async Task PackageRedirectPolicyRejectsUnapprovedTargets(string redirectUrl)
    {
        using var temporary = new TemporaryDirectory();
        using var fixture = await CatalogFixture.CreateAsync(temporary.Path);
        var handler = new DelegateHandler(request => request.RequestUri == fixture.Release.AssetUrl
            ? Redirect(new Uri(redirectUrl))
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = new ScannerCatalogClient(
            new InstalledScannerStore(Path.Combine(temporary.Path, "store")), handler, fixture.CatalogUrl, fixture.Verifier);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.DownloadAsync(fixture.Release, CancellationToken.None));
    }

    [Fact]
    public async Task PartialDownloadAndExcessiveRedirectsLeaveNoTemporaryFiles()
    {
        using var temporary = new TemporaryDirectory();
        using var fixture = await CatalogFixture.CreateAsync(temporary.Path);
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var partial = fixture.ArchiveBytes[..^1];
        using (var partialClient = new ScannerCatalogClient(store,
                   new DelegateHandler(_ => Bytes(partial)), fixture.CatalogUrl, fixture.Verifier))
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                partialClient.DownloadAsync(fixture.Release, CancellationToken.None));
        using (var redirectClient = new ScannerCatalogClient(store,
                   new DelegateHandler(_ => Redirect(fixture.Release.AssetUrl)), fixture.CatalogUrl, fixture.Verifier))
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                redirectClient.DownloadAsync(fixture.Release, CancellationToken.None));

        Assert.Empty(DownloadFiles(store));
    }

    [Fact]
    public async Task HashOrCatalogMetadataSubstitutionPreservesPreviousActivation()
    {
        using var temporary = new TemporaryDirectory();
        using var fixture = await CatalogFixture.CreateAsync(temporary.Path);
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var localArchive = Path.Combine(temporary.Path, "local.tar.gz");
        await CreateLocalArchive(localArchive);
        var previous = await new ScannerPackageInstaller(store).InstallLocalAsync(localArchive, CancellationToken.None);
        var receiptBytes = await File.ReadAllBytesAsync(store.ReceiptPath);
        var downloaded = Path.Combine(temporary.Path, "downloaded.tar.gz");
        await File.WriteAllBytesAsync(downloaded, fixture.ArchiveBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ScannerPackageInstaller(store, fixture.Verifier).InstallCatalogAsync(
                fixture.Release with { Sha256 = new string('a', 64) }, "catalog-test-v1", downloaded, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ScannerPackageInstaller(store, fixture.Verifier).InstallCatalogAsync(
                fixture.Release with { License = "Apache-2.0" }, "catalog-test-v1", downloaded, CancellationToken.None));

        Assert.Equal(receiptBytes, await File.ReadAllBytesAsync(store.ReceiptPath));
        Assert.Equal(previous.Sha256, Assert.Single((await store.ReadAsync(CancellationToken.None)).Active).Sha256);
    }

    [Fact]
    public async Task ReleaseSignatureBindsIdentityPlatformAndDigestBeforeActivation()
    {
        using var temporary = new TemporaryDirectory();
        using var fixture = await CatalogFixture.CreateAsync(temporary.Path);
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var localArchive = Path.Combine(temporary.Path, "local.tar.gz");
        await CreateLocalArchive(localArchive);
        await new ScannerPackageInstaller(store).InstallLocalAsync(localArchive, CancellationToken.None);
        var receiptBytes = await File.ReadAllBytesAsync(store.ReceiptPath);
        var downloaded = Path.Combine(temporary.Path, "downloaded.tar.gz");
        await File.WriteAllBytesAsync(downloaded, fixture.ArchiveBytes);
        var installer = new ScannerPackageInstaller(store, fixture.Verifier);

        foreach (var substituted in new[]
                 {
                     fixture.Release with { Id = "scanner:substituted" },
                     fixture.Release with { Platform = "windows" }
                 })
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallCatalogAsync(
                substituted, "catalog-test-v1", downloaded, CancellationToken.None));
            Assert.Contains("signature is invalid", error.Message, StringComparison.Ordinal);
        }
        var changedDigest = fixture.Release with { Sha256 = new string('a', 64) };
        var digestError = Assert.Throws<InvalidDataException>(() => fixture.Verifier.VerifyRelease(
            changedDigest, Convert.FromHexString(changedDigest.Sha256)));

        Assert.Contains("signature is invalid", digestError.Message, StringComparison.Ordinal);
        Assert.Equal(receiptBytes, await File.ReadAllBytesAsync(store.ReceiptPath));
    }

    [Fact]
    public async Task UnknownCatalogKeyAndOversizedEnvelopeAreRejectedWithoutCache()
    {
        using var temporary = new TemporaryDirectory();
        using var fixture = await CatalogFixture.CreateAsync(temporary.Path);
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var unknown = JsonNode.Parse(fixture.EnvelopeBytes)!.AsObject();
        unknown["keyId"] = "catalog-supplied-key";
        var responses = new Queue<HttpResponseMessage>(
        [
            Bytes(JsonSerializer.SerializeToUtf8Bytes(unknown, ContractJson.Options)),
            Bytes(new byte[1024 * 1024 + 1])
        ]);
        using var client = new ScannerCatalogClient(
            store, new DelegateHandler(_ => responses.Dequeue()), fixture.CatalogUrl, fixture.Verifier);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.RefreshAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.RefreshAsync(CancellationToken.None));

        Assert.False(File.Exists(client.CachePath));
    }

    [Fact]
    public async Task ResolverChoosesLatestExactCompatibleStableRelease()
    {
        using var temporary = new TemporaryDirectory();
        using var fixture = await CatalogFixture.CreateAsync(temporary.Path);
        var releases = new[]
        {
            fixture.Release with { Version = "1.0.0" },
            fixture.Release with { Version = "1.2.0" },
            fixture.Release with { Version = "2.0.0-beta.1" },
            fixture.Release with { Version = "3.0.0", Revoked = true },
            fixture.Release with { Version = "4.0.0", ArchieVersionRange = "[99.0.0,100.0.0)" }
        };
        var catalog = new ScannerCatalog("scanner-catalog/v1", "v1", DateTimeOffset.UnixEpoch, releases);

        Assert.Equal("1.2.0", ScannerCommands.ResolveRelease(catalog, fixture.Release.Id, null).Version);
        Assert.Equal("1.0.0", ScannerCommands.ResolveRelease(catalog, fixture.Release.Id, "1.0.0").Version);
        var revoked = Assert.Throws<InvalidDataException>(() =>
            ScannerCommands.ResolveRelease(catalog, fixture.Release.Id, "3.0.0"));
        Assert.Contains("is revoked", revoked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateRequiresExplicitLocalTrustTransitionAndRetainsExactRollback()
    {
        using var temporary = new TemporaryDirectory();
        using var fixture = await CatalogFixture.CreateAsync(temporary.Path);
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var localArchive = Path.Combine(temporary.Path, "local.tar.gz");
        await CreateLocalArchive(localArchive);
        var local = await new ScannerPackageInstaller(store).InstallLocalAsync(localArchive, CancellationToken.None);
        var previousReceipt = await File.ReadAllBytesAsync(store.ReceiptPath);
        var validAsset = false;
        var catalogBytes = fixture.EnvelopeBytes;
        var requests = 0;
        var handler = new DelegateHandler(request =>
        {
            requests++;
            if (request.RequestUri == fixture.CatalogUrl) return Bytes(catalogBytes);
            if (request.RequestUri == fixture.Release.AssetUrl)
            {
                var bytes = fixture.ArchiveBytes.ToArray();
                if (!validAsset) bytes[^1] ^= 1;
                return Bytes(bytes);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new ScannerCatalogClient(store, handler, fixture.CatalogUrl, fixture.Verifier);

        await Assert.ThrowsAsync<InvalidDataException>(() => ScannerCommands.UpdateAsync(
            store, [local.Id], client, fixture.Verifier, CancellationToken.None));
        Assert.Equal(0, requests);
        await Assert.ThrowsAsync<InvalidDataException>(() => ScannerCommands.UpdateAsync(
            store, [local.Id, "--from", "catalog", "--version", "1.0.0"],
            client, fixture.Verifier, CancellationToken.None));
        Assert.Equal(previousReceipt, await File.ReadAllBytesAsync(store.ReceiptPath));
        Assert.Empty(DownloadFiles(store));

        var tampered = JsonNode.Parse(fixture.EnvelopeBytes)!.AsObject();
        tampered["signature"] = Convert.ToBase64String(new byte[64]);
        catalogBytes = JsonSerializer.SerializeToUtf8Bytes(tampered, ContractJson.Options);
        var requestsBeforeInvalidCatalog = requests;
        await Assert.ThrowsAsync<InvalidDataException>(() => ScannerCommands.UpdateAsync(
            store, [local.Id, "--from", "catalog"], client, fixture.Verifier, CancellationToken.None));
        Assert.Equal(requestsBeforeInvalidCatalog + 1, requests);
        Assert.Equal(previousReceipt, await File.ReadAllBytesAsync(store.ReceiptPath));

        catalogBytes = fixture.EnvelopeBytes;
        validAsset = true;
        Assert.Equal(0, await ScannerCommands.UpdateAsync(
            store, [local.Id, "--version", "1.0.0", "--from", "catalog"],
            client, fixture.Verifier, CancellationToken.None));
        var receipt = await store.ReadAsync(CancellationToken.None);
        var active = Assert.Single(receipt.Active);
        var catalogPackage = receipt.Packages.Single(item => item.Sha256 == active.Sha256);
        Assert.Equal(2, receipt.Packages.Count);
        Assert.Equal(ScannerPackageOrigin.Catalog, catalogPackage.Origin);
        Assert.Equal(ScannerTrustState.VerifiedFirstParty, catalogPackage.Trust);
        var requestsBeforeNoOp = requests;

        Assert.Equal(0, await ScannerCommands.UpdateAsync(
            store, [local.Id], client, fixture.Verifier, CancellationToken.None));
        Assert.Equal(requestsBeforeNoOp + 1, requests);

        await store.UseAsync(local.Id, local.Version, local.Sha256, CancellationToken.None);

        Assert.Equal(local.Sha256, Assert.Single((await store.ReadAsync(CancellationToken.None)).Active).Sha256);
        Assert.True(Directory.Exists(store.PackageDirectory(catalogPackage.Id, catalogPackage.Version, catalogPackage.Sha256)));
        Assert.Empty(DownloadFiles(store));
    }

    [Fact]
    public async Task RevocationWarningsDoNotMutateInstalledState()
    {
        using var temporary = new TemporaryDirectory();
        using var fixture = await CatalogFixture.CreateAsync(temporary.Path);
        var package = new InstalledScannerPackage(
            fixture.Release.Id, fixture.Release.Version, fixture.Release.Sha256, ScannerPackageOrigin.Catalog,
            ScannerTrustState.VerifiedFirstParty, "catalog-test-v1", CatalogFixture.KeyId,
            DateTimeOffset.UnixEpoch, fixture.Release.ExpandedBytes);
        var receipt = new InstalledScannerReceipt("installed-scanners/v1", [package],
            [new ActiveScanner(package.Id, package.Version, package.Sha256)]);
        var catalog = new ScannerCatalog("scanner-catalog/v1", "catalog-revoked", DateTimeOffset.UnixEpoch,
            [fixture.Release with { Revoked = true }]);

        var warning = Assert.Single(ScannerCommands.RevocationWarnings(catalog, receipt));

        Assert.Contains("active scanner", warning, StringComparison.Ordinal);
        Assert.Contains("is revoked", warning, StringComparison.Ordinal);
        Assert.Contains("was not removed or replaced", warning, StringComparison.Ordinal);
        Assert.Equal(package, Assert.Single(receipt.Packages));
        Assert.Equal(package.Sha256, Assert.Single(receipt.Active).Sha256);
    }

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static HttpResponseMessage Redirect(Uri location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = location;
        return response;
    }

    private static IEnumerable<string> DownloadFiles(InstalledScannerStore store) =>
        Directory.Exists(store.RootDirectory)
            ? Directory.EnumerateFiles(store.RootDirectory, ".download.*.tmp")
            : [];

    private static async Task CreateLocalArchive(string path)
    {
        await using var output = File.Create(path);
        await using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
        TarFile.CreateFromDirectory(
            Path.Combine(RepositoryRoot(), "tests", "fixtures", "scanner-packages", "fake"),
            gzip, includeBaseDirectory: false);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Archie.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class CatalogFixture : IDisposable
    {
        public const string KeyId = "test-key";
        private readonly ECDsa key;

        private CatalogFixture(ECDsa key, byte[] archiveBytes, ScannerCatalogRelease release, byte[] envelopeBytes)
        {
            this.key = key;
            ArchiveBytes = archiveBytes;
            Release = release;
            EnvelopeBytes = envelopeBytes;
            Verifier = new ScannerTrustVerifier(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [KeyId] = key.ExportSubjectPublicKeyInfo()
            });
        }

        public Uri CatalogUrl { get; } = new("https://catalog.test/v1/catalog.json");
        public byte[] ArchiveBytes { get; }
        public ScannerCatalogRelease Release { get; }
        public byte[] EnvelopeBytes { get; }
        public ScannerTrustVerifier Verifier { get; }

        public static async Task<CatalogFixture> CreateAsync(string root)
        {
            var packageDirectory = Path.Combine(root, $"catalog-package-{Guid.NewGuid():N}");
            Directory.CreateDirectory(packageDirectory);
            foreach (var source in Directory.EnumerateFiles(
                         Path.Combine(RepositoryRoot(), "tests", "fixtures", "scanner-packages", "fake")))
                File.Copy(source, Path.Combine(packageDirectory, Path.GetFileName(source)));
            var packagePath = Path.Combine(packageDirectory, "PACKAGE.json");
            var package = JsonNode.Parse(await File.ReadAllTextAsync(packagePath))!.AsObject();
            package["publisherKeyId"] = KeyId;
            await File.WriteAllTextAsync(packagePath, package.ToJsonString());
            var archivePath = Path.Combine(root, $"catalog-package-{Guid.NewGuid():N}.tar.gz");
            await using (var output = File.Create(archivePath))
            await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
                TarFile.CreateFromDirectory(packageDirectory, gzip, includeBaseDirectory: false);
            var archiveBytes = await File.ReadAllBytesAsync(archivePath);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(archiveBytes));
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var release = new ScannerCatalogRelease(
                package["id"]!.GetValue<string>(), "Fake Book Retail", package["version"]!.GetValue<string>(), "stable",
                package["sourceRepository"]!.GetValue<string>(), package["sourceTag"]!.GetValue<string>(),
                package["platform"]!.GetValue<string>(), package["architecture"]!.GetValue<string>(),
                package["archieVersionRange"]!.GetValue<string>(), package["protocolVersion"]!.GetValue<string>(),
                new Uri("https://github.com/archie-dev/fake-scanner/releases/download/v1.0.0/fake-scanner.tar.gz"),
                archiveBytes.Length, package["expandedBytes"]!.GetValue<long>(), package["entryCount"]!.GetValue<int>(),
                sha256, KeyId, string.Empty,
                package["capabilities"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray(),
                new ScannerPermissions(true, false, false), package["license"]!.GetValue<string>(),
                new Uri("https://github.com/archie-dev/fake-scanner/releases/tag/v1.0.0"), false);
            release = release with
            {
                PackageSignature = Convert.ToBase64String(key.SignData(
                    ScannerTrustVerifier.ReleaseSigningBytes(release), HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            };
            var payload = ContractJson.WriteScannerCatalog(new ScannerCatalog(
                "scanner-catalog/v1", "catalog-test-v1", DateTimeOffset.UnixEpoch, [release]));
            var envelope = new SignedScannerCatalog(
                "signed-scanner-catalog/v1", KeyId, Convert.ToBase64String(payload),
                Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
            return new(key, archiveBytes, release, JsonSerializer.SerializeToUtf8Bytes(envelope, ContractJson.Options));
        }

        public void Dispose() => key.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"archie-catalog-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
