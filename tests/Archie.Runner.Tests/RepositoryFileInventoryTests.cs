using System.Diagnostics;
using Archie.Runner;
using Xunit;

namespace Archie.Runner.Tests;

public sealed class RepositoryFileInventoryTests
{
    [Fact]
    public async Task InventoryIsBoundedToSortedRepositoryFilesAndDoesNotFollowLinks()
    {
        using var repository = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "z.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "a.txt"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(repository.Path, ".gitignore"), "ignored.txt\n");
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "ignored.txt"), "ignored");
        await File.WriteAllTextAsync(Path.Combine(external.Path, "outside.csproj"), "<Project />");
        if (!OperatingSystem.IsWindows())
            Directory.CreateSymbolicLink(Path.Combine(repository.Path, "external"), external.Path);
        RunGit(repository.Path, "init", "--quiet");
        RunGit(repository.Path, "add", ".");

        var inventory = await RepositoryFileInventory.CreateAsync(repository.Path, CancellationToken.None);

        var expected = OperatingSystem.IsWindows()
            ? new[] { ".gitignore", "a.txt", "z.csproj" }
            : new[] { ".gitignore", "a.txt", "external", "z.csproj" };
        Assert.Equal(expected, inventory.Paths);
        Assert.DoesNotContain(inventory.Paths, path => path.Contains("outside", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InventoryHonorsCancellationBeforeStartingGit()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RepositoryFileInventory.CreateAsync("does-not-exist", cancellation.Token));
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"archie-inventory-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
