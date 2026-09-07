using System.Diagnostics;
using System.Text.Json;
using Archie.Runner;
using Xunit;

namespace Archie.Runner.Tests;

public sealed class ScannerManifestLoaderTests
{
    [Theory]
    [InlineData("src/**/*.cs", "src/App.cs", true)]
    [InlineData("src/**/*.cs", "src/features/App.cs", true)]
    [InlineData("src/**/*.cs", "source/App.cs", false)]
    [InlineData("src/*.cs", "src/features/App.cs", false)]
    [InlineData("*.cs", "notes.cs.txt", false)]
    public void GlobContractIsSegmentAwareAndAnchored(string glob, string path, bool expected) =>
        Assert.Equal(expected, ScannerManifestLoader.MatchesGlob(glob, path));

    [Fact]
    public async Task DiscoveryRejectsSymlinkEscapeWithoutFollowingIt()
    {
        if (OperatingSystem.IsWindows()) return;
        using var repository = new TemporaryDirectory();
        using var plugins = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        InitializeRepository(repository.Path);
        Directory.CreateSymbolicLink(Path.Combine(plugins.Path, "external"), external.Path);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ScannerManifestLoader().DiscoverAsync([plugins.Path], repository.Path, CancellationToken.None));

        Assert.Contains("escapes its approved root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryHonorsCancellationBeforeFilesystemOrProcessWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ScannerManifestLoader().DiscoverAsync(["does-not-exist"], "does-not-exist", cancellation.Token));
    }

    [Fact]
    public void ApplicabilityHonorsCancellationAfterMatchingBegins()
    {
        using var cancellation = new CancellationTokenSource();
        var files = new CancelingFileList(cancellation);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ScannerManifestLoader.IsApplicable(files, ["**/*.cs"], cancellation.Token));
    }

    [Fact]
    public async Task DiscoveryRejectsOversizedManifestBeforeParsing()
    {
        using var repository = new TemporaryDirectory();
        using var plugins = new TemporaryDirectory();
        InitializeRepository(repository.Path);
        await File.WriteAllTextAsync(Path.Combine(plugins.Path, "scanner.json"), new string(' ', 1024 * 1024 + 1));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ScannerManifestLoader().DiscoverAsync([plugins.Path], repository.Path, CancellationToken.None));

        Assert.Contains("1048576 byte limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryRejectsMoreThanBoundedArtifactGlobs()
    {
        using var repository = new TemporaryDirectory();
        using var plugins = new TemporaryDirectory();
        InitializeRepository(repository.Path);
        await File.WriteAllTextAsync(Path.Combine(plugins.Path, "scanner.json"), Manifest(Enumerable.Range(0, 257).Select(index => $"src/{index}.cs")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ScannerManifestLoader().DiscoverAsync([plugins.Path], repository.Path, CancellationToken.None));

        Assert.Contains("256 artifact-glob limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryCountsDirectoryEntriesBeforeSortingThem()
    {
        using var repository = new TemporaryDirectory();
        using var plugins = new TemporaryDirectory();
        InitializeRepository(repository.Path);
        for (var index = 0; index <= 10_000; index++) File.WriteAllText(Path.Combine(plugins.Path, $"entry-{index:D5}"), string.Empty);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ScannerManifestLoader().DiscoverAsync([plugins.Path], repository.Path, CancellationToken.None));

        Assert.Contains("10000 entry limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstalledSearchRootPropagatesPackageIdentity()
    {
        using var repository = new TemporaryDirectory();
        using var plugins = new TemporaryDirectory();
        InitializeRepository(repository.Path);
        await File.WriteAllTextAsync(Path.Combine(plugins.Path, "scanner.json"), Manifest(["**/*.cs"]));
        var digest = new string('a', 64);
        var inventory = await RepositoryFileInventory.CreateAsync(repository.Path, CancellationToken.None);

        var scanners = await new ScannerManifestLoader().DiscoverAsync(
            [new(plugins.Path, digest, Archie.Contracts.ScannerPackageOrigin.LocalUnsigned)],
            inventory,
            CancellationToken.None);

        var scanner = Assert.Single(scanners);
        Assert.Equal(digest, scanner.PackageSha256);
        Assert.Equal(Archie.Contracts.ScannerPackageOrigin.LocalUnsigned, scanner.Origin);
    }

    private static string Manifest(IEnumerable<string> globs) => $$"""
        {
          "schemaVersion": "scanner-manifest/v1",
          "id": "test.scanner",
          "version": "1.0.0",
          "executable": "node",
          "arguments": ["worker.mjs"],
          "artifactGlobs": {{JsonSerializer.Serialize(globs)}},
          "capabilities": [],
          "configurationSchema": {},
          "permissions": { "readRepository": true, "network": false, "environment": false }
        }
        """;

    private static void InitializeRepository(string path)
    {
        File.WriteAllText(Path.Combine(path, "fixture.cs"), "fixture");
        RunGit(path, "init");
        RunGit(path, "add", "fixture.cs");
    }

    private static void RunGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"archie-manifests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class CancelingFileList(CancellationTokenSource cancellation) : IReadOnlyList<string>
    {
        public int Count => 2;
        public string this[int index] => index == 0 ? "first.txt" : "second.txt";

        public IEnumerator<string> GetEnumerator()
        {
            yield return this[0];
            cancellation.Cancel();
            yield return this[1];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
