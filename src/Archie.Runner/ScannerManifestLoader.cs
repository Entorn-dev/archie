using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archie.Contracts;

namespace Archie.Runner;

public sealed record DiscoveredScanner(
    ScannerManifest Manifest,
    string ManifestPath,
    string? PackageSha256 = null,
    ScannerPackageOrigin? Origin = null)
{
    public string Directory => Path.GetDirectoryName(ManifestPath)!;
}

public sealed class ScannerManifestLoader
{
    private const int MaxPluginEntries = 10_000;
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxArtifactGlobs = 256;
    public async Task<IReadOnlyList<DiscoveredScanner>> DiscoverAsync(
        IEnumerable<string> searchPaths,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inventory = await RepositoryFileInventory.CreateAsync(repositoryRoot, cancellationToken);
        var roots = searchPaths.Select(path => new ScannerSearchRoot(path, null, null));
        return await DiscoverAsync(roots, inventory, cancellationToken);
    }

    public async Task<IReadOnlyList<DiscoveredScanner>> DiscoverAsync(
        IEnumerable<ScannerSearchRoot> searchRoots,
        RepositoryFileInventory inventory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifests = new List<DiscoveredScanner>();
        var roots = searchRoots
            .Select(root => root with { Path = Path.GetFullPath(root.Path) })
            .GroupBy(root => root.Path, PathComparer())
            .Select(group => group.First());
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in DiscoverManifestPaths(root.Path, cancellationToken))
            {
                if (new FileInfo(path).Length > MaxManifestBytes)
                    throw new InvalidDataException($"Scanner manifest exceeded the {MaxManifestBytes} byte limit: {path}");
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                RejectExcessArtifactGlobs(bytes, path);
                ScannerSchemaValidator.Validate(bytes, "scanner-manifest.schema.json", "Scanner manifest", path);
                var manifest = JsonSerializer.Deserialize<ScannerManifest>(bytes, ScannerContractJson.Options)
                    ?? throw new InvalidDataException($"Scanner manifest is empty: {path}");
                if (manifest.SchemaVersion != "scanner-manifest/v1")
                    throw new InvalidDataException($"Unsupported scanner manifest version '{manifest.SchemaVersion}' in {path}.");
                if (!manifest.Permissions.ReadRepository || manifest.Permissions.Network || manifest.Permissions.Environment)
                    throw new InvalidDataException($"Scanner '{manifest.Id}' requests permissions outside the approved declared scanner permissions.");
                if (IsApplicable(inventory.Paths, manifest.ArtifactGlobs, cancellationToken))
                    manifests.Add(new(manifest, path, root.PackageSha256, root.Origin));
            }
        }

        var duplicate = manifests.GroupBy(item => item.Manifest.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Duplicate scanner ID '{duplicate.Key}' was discovered.");
        return manifests.OrderBy(item => item.Manifest.Id, StringComparer.Ordinal).ToArray();
    }

    public static bool MatchesGlob(string glob, string path)
        => CompileGlob(glob).IsMatch(path.Replace('\\', '/'));

    internal static bool IsApplicable(
        IReadOnlyList<string> repositoryFiles,
        IReadOnlyList<string> artifactGlobs,
        CancellationToken cancellationToken)
    {
        if (artifactGlobs.Count > MaxArtifactGlobs)
            throw new InvalidDataException($"Scanner manifest exceeded the {MaxArtifactGlobs} artifact-glob limit.");
        var matchers = new Regex[artifactGlobs.Count];
        for (var index = 0; index < artifactGlobs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            matchers[index] = CompileGlob(artifactGlobs[index]);
        }
        foreach (var file in repositoryFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = file.Replace('\\', '/');
            foreach (var matcher in matchers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (matcher.IsMatch(normalized)) return true;
            }
        }
        return false;
    }

    private static Regex CompileGlob(string glob)
    {
        glob = glob.Replace('\\', '/');
        var pattern = new StringBuilder("^");
        for (var index = 0; index < glob.Length; index++)
        {
            if (glob[index] == '*')
            {
                if (index + 1 < glob.Length && glob[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < glob.Length && glob[index + 1] == '/')
                    {
                        index++;
                        pattern.Append("(?:.*/)?");
                    }
                    else pattern.Append(".*");
                }
                else pattern.Append("[^/]*");
            }
            else if (glob[index] == '?') pattern.Append("[^/]");
            else pattern.Append(Regex.Escape(glob[index].ToString()));
        }
        pattern.Append('$');
        return new(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    }

    private static IEnumerable<string> DiscoverManifestPaths(string searchPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(searchPath)) throw new InvalidDataException($"Scanner plugin path does not exist: {searchPath}");
        var root = new DirectoryInfo(searchPath);
        RejectSymlink(root, root.FullName);
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        var manifests = new List<string>();
        var entries = 0;
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = stack.Pop();
            var children = new List<FileSystemInfo>();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > MaxPluginEntries) throw new InvalidDataException($"Scanner plugin discovery exceeded the {MaxPluginEntries} entry limit.");
                children.Add(entry);
            }
            children.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            foreach (var entry in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    RejectSymlink(entry, root.FullName);
                    continue;
                }
                if (entry is DirectoryInfo child) stack.Push(child);
                else if (entry.Name == "scanner.json") manifests.Add(entry.FullName);
            }
        }
        return manifests.Order(StringComparer.Ordinal);
    }

    private static void RejectExcessArtifactGlobs(ReadOnlySpan<byte> bytes, string path)
    {
        var instance = JsonNode.Parse(bytes);
        if (instance?["artifactGlobs"] is JsonArray globs && globs.Count > MaxArtifactGlobs)
            throw new InvalidDataException($"Scanner manifest exceeded the {MaxArtifactGlobs} artifact-glob limit: {path}");
    }

    private static void RejectSymlink(FileSystemInfo entry, string root)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) return;
        var target = entry.ResolveLinkTarget(returnFinalTarget: true);
        var outside = target is null || Path.GetRelativePath(root, target.FullName) is var relative &&
            (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        throw new InvalidDataException(outside
            ? "Scanner plugin discovery rejected a symlink that escapes its approved root."
            : "Scanner plugin discovery does not follow symlinks.");
    }

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

}
