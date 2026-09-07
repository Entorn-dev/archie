using System.Diagnostics;
using System.Text.Json;
using Archie.Contracts;
using Archie.Runner;
using Xunit;

namespace Archie.Runner.Tests;

public sealed class TechnologyStackDetectorTests
{
    [Fact]
    public async Task DetectsSupportedAndUnavailableStacksFromFilenamesDeterministically()
    {
        using var repository = new TemporaryDirectory();
        foreach (var path in new[]
                 {
                     "src/App.csproj", "shop/composer.json", "web/package.json", "service/go.mod",
                     "deploy/main.tf", "deploy/Dockerfile"
                 })
        {
            var fullPath = Path.Combine(repository.Path, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, "fixture");
        }
        RunGit(repository.Path, "init", "--quiet");
        RunGit(repository.Path, "add", ".");
        var inventory = await RepositoryFileInventory.CreateAsync(repository.Path, CancellationToken.None);
        var detector = new TechnologyStackDetector();

        var stacks = detector.Detect(inventory);
        var coverage = detector.AssessCoverage(stacks, [Scanner("archie.dotnet", "1.3.0")]);
        var diagnostics = detector.ToDiagnostics(coverage);

        Assert.Equal(["dotnet", "go", "infrastructure", "node", "php"], stacks.Select(stack => stack.Id));
        Assert.Equal([ScannerCoverageState.Installed, ScannerCoverageState.Unavailable,
            ScannerCoverageState.Unavailable, ScannerCoverageState.Unavailable, ScannerCoverageState.Missing],
            coverage.Select(item => item.State));
        Assert.Equal(["SCANNER_COVERAGE_INSTALLED", "SCANNER_COVERAGE_MISSING"], diagnostics.Select(item => item.Code));
        Assert.Contains("archie scanner add archie.php", diagnostics[1].Message, StringComparison.Ordinal);
        Assert.Equal("stack:php", diagnostics[1].SubjectId);
    }

    private static DiscoveredScanner Scanner(string id, string version)
    {
        using var configuration = JsonDocument.Parse("{}");
        var manifest = new ScannerManifest(
            "scanner-manifest/v1", id, version, "worker", [], ["**/*"], [],
            configuration.RootElement.Clone(), new(true, false, false));
        return new(manifest, Path.Combine(Path.GetTempPath(), id, "scanner.json"));
    }

    private static void RunGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"archie-stack-detector-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
