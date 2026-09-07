using Archie.Cli;
using Xunit;

namespace Archie.Cli.Tests;

public sealed class LocalRepositoryStateTests
{
    [Fact]
    public void ExplicitStateDirectoryOwnsAllGeneratedProductState()
    {
        var configured = Path.Combine(Path.GetTempPath(), "archie-state", Guid.NewGuid().ToString("N"));

        var state = LocalRepositoryState.Resolve("relative-repository", configured);

        Assert.Equal(Path.GetFullPath(configured), state.Directory);
        Assert.Equal(Path.Combine(Path.GetFullPath(configured), "observations.json"), state.ObservationsPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(configured), "graph.json"), state.GraphPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(configured), "views"), state.ViewsPath);
    }

    [Fact]
    public void SeparateRepositoryPathsHaveIndependentStableStateDirectories()
    {
        var original = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var stateRoot = Path.Combine(Path.GetTempPath(), "archie-xdg", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", stateRoot);
            var first = LocalRepositoryState.Resolve(Path.Combine("one", "shared-name"), null);
            var repeated = LocalRepositoryState.Resolve(Path.Combine("one", "shared-name"), null);
            var second = LocalRepositoryState.Resolve(Path.Combine("two", "shared-name"), null);

            Assert.Equal(first, repeated);
            Assert.NotEqual(first.Directory, second.Directory);
            Assert.StartsWith(Path.Combine(Path.GetFullPath(stateRoot), "archie", "repositories"), first.Directory, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", original);
        }
    }
}
