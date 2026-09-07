using System.Diagnostics;
using System.Text.Json;
using Archie.Contracts;
using Archie.Runner;
using Xunit;

namespace Archie.Runner.Tests;

public sealed class RepositoryContextFactoryTests
{
    [Theory]
    [InlineData("absolute")]
    [InlineData("file")]
    public async Task LocalFilesystemOriginsAreOmittedFromRepositoryArtifacts(string kind)
    {
        using var repository = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "fixture.txt"), "fixture");
        RunGit(repository.Path, "init", "--quiet");
        RunGit(repository.Path, "add", "fixture.txt");
        RunGit(repository.Path, "-c", "user.name=Archie Tests", "-c", "user.email=archie@example.invalid",
            "commit", "--quiet", "-m", "fixture");
        var remote = kind == "file" ? new Uri(repository.Path).AbsoluteUri : repository.Path;
        RunGit(repository.Path, "remote", "add", "origin", remote);

        var revision = await new RepositoryContextFactory().CreateAsync(
            repository.Path, new HashSet<string>(StringComparer.Ordinal), CancellationToken.None);
        var persisted = JsonSerializer.Serialize(revision, ContractJson.Options);

        Assert.Null(revision.RemoteUrl);
        Assert.DoesNotContain(repository.Path, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("file:", persisted, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://user:SYNTHETIC_PASSWORD@example.com/org/repo.git")]
    [InlineData("https://example.com/org/repo.git?token=SYNTHETIC_TOKEN")]
    [InlineData("https://example.com/org/repo.git#SYNTHETIC_FRAGMENT")]
    public async Task WebOriginsWithCredentialsQueryOrFragmentAreEntirelyOmitted(string remote)
    {
        using var repository = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "fixture.txt"), "fixture");
        RunGit(repository.Path, "init", "--quiet");
        RunGit(repository.Path, "add", "fixture.txt");
        RunGit(repository.Path, "-c", "user.name=Archie Tests", "-c", "user.email=archie@example.invalid",
            "commit", "--quiet", "-m", "fixture");
        RunGit(repository.Path, "remote", "add", "origin", remote);

        var revision = await new RepositoryContextFactory().CreateAsync(
            repository.Path, new HashSet<string>(StringComparer.Ordinal), CancellationToken.None);
        var persisted = JsonSerializer.Serialize(revision, ContractJson.Options);

        Assert.Null(revision.RemoteUrl);
        Assert.DoesNotContain("SYNTHETIC_", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryWebOriginIsNormalizedAndPersisted()
    {
        using var repository = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "fixture.txt"), "fixture");
        RunGit(repository.Path, "init", "--quiet");
        RunGit(repository.Path, "add", "fixture.txt");
        RunGit(repository.Path, "-c", "user.name=Archie Tests", "-c", "user.email=archie@example.invalid",
            "commit", "--quiet", "-m", "fixture");
        RunGit(repository.Path, "remote", "add", "origin", "https://Example.COM/org/repo.git/");

        var revision = await new RepositoryContextFactory().CreateAsync(
            repository.Path, new HashSet<string>(StringComparer.Ordinal), CancellationToken.None);

        Assert.Equal("https://example.com/org/repo.git", revision.RemoteUrl);
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"archie-repository-context-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
