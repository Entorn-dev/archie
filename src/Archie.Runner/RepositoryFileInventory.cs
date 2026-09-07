using System.Diagnostics;
using System.Text;

namespace Archie.Runner;

public sealed class RepositoryFileInventory
{
    private const int MaxPaths = 200_000;
    private const int MaxOutputBytes = 32 * 1024 * 1024;

    private RepositoryFileInventory(string repositoryRoot, IReadOnlyList<string> paths)
    {
        RepositoryRoot = repositoryRoot;
        Paths = paths;
    }

    public string RepositoryRoot { get; }
    public IReadOnlyList<string> Paths { get; }

    public static async Task<RepositoryFileInventory> CreateAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root)) throw new ArgumentException($"Repository does not exist: {root}");
        var git = ExecutableResolver.Resolve("git", root);
        var start = new ProcessStartInfo(git)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("-z");
        start.ArgumentList.Add("--cached");
        start.ArgumentList.Add("--others");
        start.ArgumentList.Add("--exclude-standard");
        using var process = Process.Start(start) ?? throw new InvalidDataException("Could not enumerate bounded repository inputs.");
        try
        {
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (output.Length + read > MaxOutputBytes)
                    throw new InvalidDataException($"Repository input discovery exceeded the {MaxOutputBytes} byte limit.");
                output.Write(buffer, 0, read);
            }
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new InvalidDataException($"Could not enumerate bounded repository inputs with git: {(await error).Trim()}");
            var paths = Encoding.UTF8.GetString(output.ToArray())
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (paths.Length > MaxPaths)
                throw new InvalidDataException($"Repository input discovery exceeded the {MaxPaths} file limit.");
            return new(root, paths.Select(path => ValidateRelativePath(root, path)).Order(StringComparer.Ordinal).ToArray());
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
    }

    private static string ValidateRelativePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Repository input discovery returned an unsafe path.");
        var fullPath = Path.GetFullPath(Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException("Repository input discovery returned a path outside the repository.");
        return relative.Replace('\\', '/');
    }
}
