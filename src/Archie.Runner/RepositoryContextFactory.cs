using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Archie.Runner;

public sealed class RepositoryContextFactory
{
    public async Task<RepositoryRevision> CreateAsync(
        string repositoryPath,
        IReadOnlySet<string> excludedPaths,
        CancellationToken cancellationToken)
    {
        var inventory = await RepositoryFileInventory.CreateAsync(repositoryPath, cancellationToken);
        return await CreateAsync(repositoryPath, excludedPaths, inventory, cancellationToken);
    }

    public async Task<RepositoryRevision> CreateAsync(
        string repositoryPath,
        IReadOnlySet<string> excludedPaths,
        RepositoryFileInventory inventory,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryPath);
        if (!Directory.Exists(root)) throw new ArgumentException($"Repository does not exist: {root}");
        if (!Path.GetFullPath(inventory.RepositoryRoot).Equals(root, PathComparison()))
            throw new ArgumentException("Repository inventory belongs to a different repository.", nameof(inventory));
        var revision = (await GitAsync(root, ["rev-parse", "HEAD"], cancellationToken)).Trim();
        var status = await GitAsync(root, ["status", "--porcelain=v1", "-z"], cancellationToken);
        var dirtyPaths = status.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Length > 3 ? line[3..] : line)
            .Select(path => path.Contains(" -> ", StringComparison.Ordinal) ? path.Split(" -> ")[^1] : path)
            .Select(path => Path.GetFullPath(Path.Combine(root, path)));
        var excluded = excludedPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
        var isDirty = dirtyPaths.Any(path => !excluded.Contains(path));
        var contentDigest = await ContentDigestAsync(root, excluded, inventory.Paths, cancellationToken);
        var remote = await TryGitAsync(root, ["remote", "get-url", "origin"], cancellationToken);
        var sanitizedRemote = SanitizeRemote(remote?.Trim());
        return new(
            RepositoryId(root, sanitizedRemote),
            sanitizedRemote,
            revision,
            isDirty,
            contentDigest);
    }

    private static async Task<string> ContentDigestAsync(
        string root,
        IReadOnlySet<string> excluded,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in paths)
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            if (excluded.Contains(fullPath) || !File.Exists(fullPath)) continue;
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/')));
            hash.AppendData([0]);
            var linkTarget = new FileInfo(fullPath).LinkTarget;
            hash.AppendData(linkTarget is null
                ? await File.ReadAllBytesAsync(fullPath, cancellationToken)
                : Encoding.UTF8.GetBytes(linkTarget));
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string RepositoryId(string root, string? remote)
    {
        var name = remote is not null && Uri.TryCreate(remote, UriKind.Absolute, out var uri)
            ? Path.GetFileName(uri.AbsolutePath.TrimEnd('/'))
            : Path.GetFileName(root);
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }

    private static string? SanitizeRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) || uri.IsLoopback ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return null;
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static async Task<string?> TryGitAsync(string root, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try { return await GitAsync(root, arguments, cancellationToken); }
        catch (InvalidDataException) { return null; }
    }

    private static async Task<string> GitAsync(string root, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidDataException("Could not start git.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidDataException($"Git failed: {(await error).Trim()}");
        return await output;
    }
}
