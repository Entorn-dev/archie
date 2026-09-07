using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Archie.Cli;
using Archie.Runner;
using Xunit;

namespace Archie.Cli.Tests;

public sealed class ScannerPackageInstallerTests
{
    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("nested/../escape")]
    public async Task UnsafeArchivePathsNeverMutateReceipt(string path)
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var archive = await ArchiveWithEntries(temporary.Path, (TarEntryType.RegularFile, path), (TarEntryType.RegularFile, "safe"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ScannerPackageInstaller(store).InstallLocalAsync(archive, CancellationToken.None));

        Assert.False(File.Exists(store.ReceiptPath));
        Assert.False(File.Exists(Path.Combine(temporary.Path, "escape")));
        Assert.Empty(ExtractionDirectories(store));
    }

    [Fact]
    public async Task DuplicateNormalizedPathsNeverMutateReceipt()
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var archive = await ArchiveWithEntries(temporary.Path,
            (TarEntryType.RegularFile, "nested/file"), (TarEntryType.RegularFile, "nested\\file"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ScannerPackageInstaller(store).InstallLocalAsync(archive, CancellationToken.None));

        Assert.Contains("duplicate path", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(store.ReceiptPath));
        Assert.Empty(ExtractionDirectories(store));
    }

    [Fact]
    public async Task DeclaredExpandedArchiveBombNeverMutatesReceipt()
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var archive = Path.Combine(temporary.Path, "bomb.tar.gz");
        await WriteDeclaredSizeArchive(archive, 2L * 1024 * 1024 * 1024 + 1);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ScannerPackageInstaller(store).InstallLocalAsync(archive, CancellationToken.None));

        Assert.Contains("expanded-byte limit", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(store.ReceiptPath));
        Assert.Empty(ExtractionDirectories(store));
    }

    [Fact]
    public async Task SocketEntryNeverMutatesReceipt()
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var archive = Path.Combine(temporary.Path, "socket.tar.gz");
        await WriteRawArchive(archive, 0, (byte)'s');

        await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            new ScannerPackageInstaller(store).InstallLocalAsync(archive, CancellationToken.None));

        Assert.False(File.Exists(store.ReceiptPath));
        Assert.Empty(ExtractionDirectories(store));
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    [InlineData(TarEntryType.Fifo)]
    [InlineData(TarEntryType.BlockDevice)]
    [InlineData(TarEntryType.CharacterDevice)]
    public async Task LinksDevicesAndUnsupportedEntriesNeverMutateReceipt(TarEntryType type)
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var archive = await ArchiveWithEntries(temporary.Path, (type, "hostile"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ScannerPackageInstaller(store).InstallLocalAsync(archive, CancellationToken.None));

        Assert.False(File.Exists(store.ReceiptPath));
        Assert.Empty(ExtractionDirectories(store));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("compatibility")]
    [InlineData("permissions")]
    [InlineData("declared-bounds")]
    [InlineData("publisher-key")]
    public async Task MetadataMismatchOrArchiveBombNeverReplacesPreviousReceipt(string failure)
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var validArchive = await PackageArchive(temporary.Path, "valid", null, "first");
        var installer = new ScannerPackageInstaller(store);
        var previous = await installer.InstallLocalAsync(validArchive, CancellationToken.None);
        var previousReceipt = await File.ReadAllBytesAsync(store.ReceiptPath);
        var invalidArchive = await PackageArchive(temporary.Path, failure, (package, manifest) =>
        {
            switch (failure)
            {
                case "identity": manifest["id"] = "archie.other"; break;
                case "compatibility": package["archieVersionRange"] = "[99.0.0,100.0.0)"; break;
                case "permissions": manifest["permissions"]!["network"] = true; break;
                case "declared-bounds": package["expandedBytes"] = 1; break;
                case "publisher-key": package["publisherKeyId"] = "untrusted-package-key"; break;
            }
        }, "second");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallLocalAsync(invalidArchive, CancellationToken.None));

        Assert.Equal(previousReceipt, await File.ReadAllBytesAsync(store.ReceiptPath));
        Assert.Equal(previous.Sha256, Assert.Single((await store.ReadAsync(CancellationToken.None)).Active).Sha256);
        Assert.Empty(ExtractionDirectories(store));
    }

    [Fact]
    public async Task SameIdentityAndVersionDigestsRemainIsolatedAndUseRequiresExplicitDigest()
    {
        using var temporary = new TemporaryDirectory();
        var store = new InstalledScannerStore(Path.Combine(temporary.Path, "store"));
        var installer = new ScannerPackageInstaller(store);
        var first = await installer.InstallLocalAsync(
            await PackageArchive(temporary.Path, "first", null, "first"), CancellationToken.None);
        var second = await installer.InstallLocalAsync(
            await PackageArchive(temporary.Path, "second", null, "second"), CancellationToken.None);

        var receipt = await store.ReadAsync(CancellationToken.None);
        Assert.NotEqual(first.Sha256, second.Sha256);
        Assert.Equal(2, receipt.Packages.Count);
        Assert.True(Directory.Exists(store.PackageDirectory(first.Id, first.Version, first.Sha256)));
        Assert.True(Directory.Exists(store.PackageDirectory(second.Id, second.Version, second.Sha256)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.UseAsync(first.Id, first.Version, null, CancellationToken.None));

        var selected = await store.UseAsync(first.Id, first.Version, first.Sha256, CancellationToken.None);

        Assert.Equal(first.Sha256, selected.Sha256);
        Assert.Equal(first.Sha256, Assert.Single((await store.ReadAsync(CancellationToken.None)).Active).Sha256);

        var third = await installer.InstallLocalAsync(
            await PackageArchive(temporary.Path, "third", null, "third"), CancellationToken.None);
        receipt = await store.ReadAsync(CancellationToken.None);
        Assert.Equal(
            new[] { first.Sha256, third.Sha256 }.Order(StringComparer.Ordinal),
            receipt.Packages.Select(item => item.Sha256).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(store.PackageDirectory(second.Id, second.Version, second.Sha256)));

        var removed = await store.RemoveAsync(third.Id, third.Version, third.Sha256, CancellationToken.None);
        receipt = await store.ReadAsync(CancellationToken.None);
        Assert.Equal(third.Sha256, Assert.Single(removed).Sha256);
        Assert.Equal(first.Sha256, Assert.Single(receipt.Active).Sha256);
        Assert.False(Directory.Exists(store.PackageDirectory(third.Id, third.Version, third.Sha256)));
    }

    private static IEnumerable<string> ExtractionDirectories(InstalledScannerStore store) =>
        Directory.Exists(store.RootDirectory)
            ? Directory.EnumerateDirectories(store.RootDirectory, ".extract.*.tmp")
            : [];

    private static async Task<string> ArchiveWithEntries(
        string root,
        params (TarEntryType Type, string Name)[] entries)
    {
        var path = Path.Combine(root, $"hostile-{Guid.NewGuid():N}.tar.gz");
        await using var output = File.Create(path);
        await using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
        using var writer = new TarWriter(gzip, leaveOpen: false);
        foreach (var (type, name) in entries)
        {
            var entry = new PaxTarEntry(type, name);
            if (type is TarEntryType.RegularFile) entry.DataStream = new MemoryStream("data"u8.ToArray());
            if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = "target";
            writer.WriteEntry(entry);
        }
        return path;
    }

    private static async Task<string> PackageArchive(
        string root,
        string name,
        Action<JsonObject, JsonObject>? mutate,
        string marker)
    {
        var directory = Path.Combine(root, $"package-{name}");
        CopyDirectory(Path.Combine(RepositoryRoot(), "tests", "fixtures", "scanner-packages", "fake"), directory);
        var packagePath = Path.Combine(directory, "PACKAGE.json");
        var manifestPath = Path.Combine(directory, "scanner.json");
        var package = JsonNode.Parse(await File.ReadAllTextAsync(packagePath))!.AsObject();
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        mutate?.Invoke(package, manifest);
        await File.WriteAllTextAsync(packagePath, package.ToJsonString());
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        await File.AppendAllTextAsync(Path.Combine(directory, "LICENSE"), marker);
        var archive = Path.Combine(root, $"{name}.tar.gz");
        await using var output = File.Create(archive);
        await using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
        TarFile.CreateFromDirectory(directory, gzip, includeBaseDirectory: false);
        return archive;
    }

    private static async Task WriteDeclaredSizeArchive(string path, long size)
        => await WriteRawArchive(path, size, (byte)'0');

    private static async Task WriteRawArchive(string path, long size, byte type)
    {
        var header = new byte[512];
        WriteAscii(header, 0, 100, "bomb");
        WriteOctal(header, 100, 8, 384);
        WriteOctal(header, 108, 8, 0);
        WriteOctal(header, 116, 8, 0);
        WriteOctal(header, 124, 12, size);
        WriteOctal(header, 136, 12, 0);
        Array.Fill(header, (byte)' ', 148, 8);
        header[156] = type;
        WriteAscii(header, 257, 6, "ustar");
        WriteAscii(header, 263, 2, "00");
        WriteOctal(header, 148, 8, header.Sum(value => (long)value));
        await using var output = File.Create(path);
        await using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
        await gzip.WriteAsync(header);
        await gzip.WriteAsync(new byte[1024]);
    }

    private static void WriteAscii(byte[] destination, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, destination, offset, Math.Min(bytes.Length, length));
    }

    private static void WriteOctal(byte[] destination, int offset, int length, long value)
    {
        var text = Convert.ToString(value, 8).PadLeft(length - 1, '0') + '\0';
        WriteAscii(destination, offset, length, text);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Archie.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"archie-installer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
