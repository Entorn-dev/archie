using System.Security.Cryptography;
using System.Text;

namespace Archie.Cli;

internal sealed record LocalRepositoryState(
    string Directory,
    string ObservationsPath,
    string GraphPath,
    string SourceContextPath,
    string ViewsPath)
{
    public static LocalRepositoryState Resolve(string repositoryRoot, string? configuredDirectory)
    {
        var directory = configuredDirectory is null
            ? Path.Combine(DefaultStateRoot(), "archie", "repositories", RepositoryDirectoryName(repositoryRoot))
            : Path.GetFullPath(configuredDirectory);
        return new(
            directory,
            Path.Combine(directory, "observations.json"),
            Path.Combine(directory, "graph.json"),
            Path.Combine(directory, "source-context.json"),
            Path.Combine(directory, "views"));
    }

    private static string DefaultStateRoot()
    {
        var configured = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            throw new InvalidDataException("Could not resolve the user state directory; set XDG_STATE_HOME or use --state-directory.");
        return Path.Combine(home, ".local", "state");
    }

    private static string RepositoryDirectoryName(string repositoryRoot)
    {
        var fullPath = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(fullPath).ToLowerInvariant();
        var safeName = new string(name.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
            ? character
            : '-').ToArray()).Trim('-');
        if (safeName.Length == 0) safeName = "repository";
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)))[..12];
        return $"{safeName}-{digest}";
    }
}
