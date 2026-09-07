using Archie.Contracts;
using Archie.Runner;
using Xunit;

namespace Archie.Runner.Tests;

public sealed class InstalledScannerStoreTests
{
    [Fact]
    public async Task ActivationResolvesOneImmutablePackageAndRemovalClearsIt()
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(temporary.Path);
        var digest = new string('a', 64);
        var packagePath = store.PackageDirectory("archie.fixture", "1.0.0", digest);
        Directory.CreateDirectory(packagePath);
        await File.WriteAllTextAsync(Path.Combine(packagePath, "scanner.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(packagePath, "PACKAGE.json"), "{}");
        var package = new InstalledScannerPackage(
            "archie.fixture", "1.0.0", digest, ScannerPackageOrigin.LocalUnsigned,
            ScannerTrustState.LocalUnsigned, null, null, DateTimeOffset.UnixEpoch, 42);

        await store.ActivateAsync(package, CancellationToken.None);
        var receipt = await store.ReadAsync(CancellationToken.None);
        var roots = await store.ResolveActiveAsync(CancellationToken.None);

        Assert.Equal(package, Assert.Single(receipt.Packages));
        Assert.Equal(new ActiveScanner(package.Id, package.Version, package.Sha256), Assert.Single(receipt.Active));
        Assert.Equal(packagePath, Assert.Single(roots).Path);
        Assert.DoesNotContain(Directory.EnumerateFiles(temporary.Path), path => path.EndsWith(".tmp", StringComparison.Ordinal));

        var removed = await store.RemoveAsync(package.Id, CancellationToken.None);

        Assert.Equal(package, Assert.Single(removed));
        Assert.Empty((await store.ReadAsync(CancellationToken.None)).Packages);
        Assert.False(Directory.Exists(packagePath));
    }

    [Fact]
    public async Task StrictReceiptRejectsUnknownFields()
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(temporary.Path);
        await File.WriteAllTextAsync(store.ReceiptPath,
            "{\"schemaVersion\":\"installed-scanners/v1\",\"packages\":[],\"active\":[],\"unexpected\":true}");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StrictReceiptRejectsMissingRequiredFields()
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(temporary.Path);
        await File.WriteAllTextAsync(store.ReceiptPath,
            "{\"schemaVersion\":\"installed-scanners/v1\",\"packages\":[]}");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentActivationsSerializeWithoutLosingEitherPackage()
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(temporary.Path);
        var first = Package("archie.first", 'a');
        var second = Package("archie.second", 'b');
        PreparePackage(store, first);
        PreparePackage(store, second);

        await Task.WhenAll(
            store.ActivateAsync(first, CancellationToken.None),
            store.ActivateAsync(second, CancellationToken.None));

        var receipt = await store.ReadAsync(CancellationToken.None);
        Assert.Equal(2, receipt.Packages.Count);
        Assert.Equal(2, receipt.Active.Count);
    }

    [Fact]
    public async Task CancellationWhileLockIsHeldPreservesReceipt()
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(temporary.Path);
        var first = Package("archie.first", 'a');
        var second = Package("archie.second", 'b');
        PreparePackage(store, first);
        PreparePackage(store, second);
        await store.ActivateAsync(first, CancellationToken.None);
        var previous = await File.ReadAllBytesAsync(store.ReceiptPath);
        await using var heldLock = new FileStream(store.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ActivateAsync(second, cancellation.Token));

        Assert.Equal(previous, await File.ReadAllBytesAsync(store.ReceiptPath));
        Assert.Equal(first.Sha256, Assert.Single((await store.ReadAsync(CancellationToken.None)).Active).Sha256);
    }

    [Fact]
    public async Task LockContentionHasABoundedFailureAndPreservesReceipt()
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(temporary.Path, TimeSpan.FromMilliseconds(100));
        var package = Package("archie.first", 'a');
        PreparePackage(store, package);
        await using var heldLock = new FileStream(store.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var error = await Assert.ThrowsAsync<IOException>(() => store.ActivateAsync(package, CancellationToken.None));

        Assert.Contains("Timed out waiting", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(store.ReceiptPath));
    }

    [Fact]
    public async Task CatalogActivationRequiresExplicitLocalOriginTransitionUnderLock()
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(temporary.Path);
        var local = Package("archie.first", 'a');
        PreparePackage(store, local);
        await store.ActivateAsync(local, CancellationToken.None);
        var previousReceipt = await File.ReadAllBytesAsync(store.ReceiptPath);
        var catalog = new InstalledScannerPackage(
            local.Id, "2.0.0", new string('b', 64), ScannerPackageOrigin.Catalog,
            ScannerTrustState.VerifiedFirstParty, "catalog-v2", "key-v1", DateTimeOffset.UnixEpoch, 42);
        var extracted = Path.Combine(temporary.Path, "extracted");
        Directory.CreateDirectory(extracted);
        await File.WriteAllTextAsync(Path.Combine(extracted, "scanner.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(extracted, "PACKAGE.json"), "{}");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.InstallAndActivateAsync(catalog, extracted, CancellationToken.None));

        Assert.Equal(previousReceipt, await File.ReadAllBytesAsync(store.ReceiptPath));
        Assert.True(Directory.Exists(extracted));

        await store.InstallAndActivateAsync(catalog, extracted, CancellationToken.None, allowLocalOriginTransition: true);

        var receipt = await store.ReadAsync(CancellationToken.None);
        Assert.Equal(2, receipt.Packages.Count);
        Assert.Equal(catalog.Sha256, Assert.Single(receipt.Active).Sha256);
    }

    private static InstalledScannerPackage Package(string id, char digest) => new(
        id, "1.0.0", new string(digest, 64), ScannerPackageOrigin.LocalUnsigned,
        ScannerTrustState.LocalUnsigned, null, null, DateTimeOffset.UnixEpoch, 42);

    private static void PreparePackage(InstalledScannerStore store, InstalledScannerPackage package)
    {
        var path = store.PackageDirectory(package.Id, package.Version, package.Sha256);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "scanner.json"), "{}");
        File.WriteAllText(Path.Combine(path, "PACKAGE.json"), "{}");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"archie-store-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
