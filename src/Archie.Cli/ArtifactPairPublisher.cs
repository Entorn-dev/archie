using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Archie.Cli;

internal static class ArtifactPairPublisher
{
    internal static async Task PrepareAsync(string observationsPath, string graphPath, CancellationToken cancellationToken)
    {
        var paths = Paths.Create(observationsPath, graphPath);
        ValidateDistinct(paths.Targets);
        await RecoverAsync(paths, cancellationToken);
    }

    internal static async Task PrepareAsync(
        string observationsPath,
        string graphPath,
        string sourceContextPath,
        CancellationToken cancellationToken)
    {
        var paths = Paths.Create(observationsPath, graphPath, sourceContextPath);
        ValidateDistinct(paths.Targets);
        await RecoverAsync(paths, cancellationToken);
    }

    internal static async Task PublishAsync(
        string observationsPath,
        byte[] observationBytes,
        string graphPath,
        byte[] graphBytes,
        CancellationToken cancellationToken,
        Func<Task>? afterObservationsPublished = null)
    {
        var paths = Paths.Create(observationsPath, graphPath);
        await PublishAsync(paths, [observationBytes, graphBytes], cancellationToken, afterObservationsPublished);
    }

    internal static async Task PublishAsync(
        string observationsPath,
        byte[] observationBytes,
        string graphPath,
        byte[] graphBytes,
        string sourceContextPath,
        byte[] sourceContextBytes,
        CancellationToken cancellationToken,
        Func<Task>? afterObservationsPublished = null)
    {
        var paths = Paths.Create(observationsPath, graphPath, sourceContextPath);
        await PublishAsync(paths, [observationBytes, graphBytes, sourceContextBytes], cancellationToken, afterObservationsPublished);
    }

    private static async Task PublishAsync(
        Paths paths,
        IReadOnlyList<byte[]> contents,
        CancellationToken cancellationToken,
        Func<Task>? afterObservationsPublished)
    {
        ValidateDistinct(paths.Targets);
        await RecoverAsync(paths, cancellationToken);
        for (var index = 0; index < paths.Targets.Count; index++)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Targets[index])!);
            await File.WriteAllBytesAsync(paths.Stages[index], contents[index], cancellationToken);
        }
        var journal = new Journal(paths.Targets, paths.Stages, paths.Backups, paths.Targets.Select(File.Exists).ToArray(), false);
        await WriteJournalAsync(paths.Journal, journal, cancellationToken);

        try
        {
            for (var index = 0; index < paths.Targets.Count; index++)
            {
                if (journal.HadOriginal[index]) File.Move(paths.Targets[index], paths.Backups[index]);
                File.Move(paths.Stages[index], paths.Targets[index]);
                if (index == 0 && afterObservationsPublished is not null) await afterObservationsPublished();
            }
            await WriteJournalAsync(paths.Journal, journal with { Committed = true }, cancellationToken);
            Cleanup(paths);
        }
        catch
        {
            await RecoverAsync(paths, CancellationToken.None);
            throw;
        }
    }

    internal static void ValidateDistinct(string observationsPath, string graphPath) =>
        ValidateDistinct([observationsPath, graphPath]);

    private static void ValidateDistinct(IReadOnlyList<string> paths)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var canonical = paths.Select(CanonicalPath).ToArray();
        for (var first = 0; first < canonical.Length; first++)
            for (var second = first + 1; second < canonical.Length; second++)
                if (string.Equals(canonical[first], canonical[second], comparison))
                    throw new ArgumentException("Observation, graph, and source-context outputs must resolve to different paths; no artifacts were changed.");
    }

    private static string CanonicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new ArgumentException($"Output path has no filesystem root: {path}");
        var current = root;
        var components = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var component in components)
        {
            var candidate = Path.Combine(current, component);
            FileSystemInfo? entry = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate) ? new FileInfo(candidate) : null;
            current = entry?.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
        }
        return Path.GetFullPath(current);
    }

    private static async Task RecoverAsync(Paths paths, CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.Journal))
        {
            foreach (var stage in paths.Stages) DeleteIfExists(stage);
            DeleteIfExists($"{paths.Journal}.tmp");
            return;
        }

        var journal = JsonSerializer.Deserialize<Journal>(await File.ReadAllBytesAsync(paths.Journal, cancellationToken))
            ?? throw new InvalidDataException($"Artifact publication journal is empty: {paths.Journal}");
        if (!journal.Targets.SequenceEqual(paths.Targets, StringComparer.Ordinal) ||
            !journal.Stages.SequenceEqual(paths.Stages, StringComparer.Ordinal) ||
            !journal.Backups.SequenceEqual(paths.Backups, StringComparer.Ordinal) ||
            journal.HadOriginal.Count != paths.Targets.Count)
            throw new InvalidDataException($"Artifact publication journal does not match the requested output set: {paths.Journal}");
        if (!journal.Committed)
        {
            for (var index = 0; index < paths.Targets.Count; index++)
                Restore(paths.Targets[index], paths.Stages[index], paths.Backups[index], journal.HadOriginal[index]);
        }
        Cleanup(paths);
    }

    private static void Restore(string target, string stage, string backup, bool hadOriginal)
    {
        if (File.Exists(backup))
        {
            DeleteIfExists(target);
            File.Move(backup, target);
        }
        else if (!hadOriginal && !File.Exists(stage))
        {
            DeleteIfExists(target);
        }
    }

    private static async Task WriteJournalAsync(string path, Journal journal, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(journal), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private static void Cleanup(Paths paths)
    {
        foreach (var stage in paths.Stages) DeleteIfExists(stage);
        foreach (var backup in paths.Backups) DeleteIfExists(backup);
        DeleteIfExists(paths.Journal);
        DeleteIfExists($"{paths.Journal}.tmp");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record Journal(
        IReadOnlyList<string> Targets,
        IReadOnlyList<string> Stages,
        IReadOnlyList<string> Backups,
        IReadOnlyList<bool> HadOriginal,
        bool Committed);

    private sealed record Paths(
        IReadOnlyList<string> Targets,
        IReadOnlyList<string> Stages,
        IReadOnlyList<string> Backups,
        string Journal)
    {
        public static Paths Create(string observationsPath, string graphPath, string? sourceContextPath = null)
        {
            var targets = new[] { observationsPath, graphPath }
                .Concat(sourceContextPath is null ? [] : [sourceContextPath])
                .Select(Path.GetFullPath).ToArray();
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', targets))))[..16];
            var stages = targets.Select(target => Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{key}.stage")).ToArray();
            var backups = targets.Select(target => Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{key}.backup")).ToArray();
            return new(targets, stages, backups, Path.Combine(Path.GetDirectoryName(targets[0])!, $".archie-artifacts-{key}.journal"));
        }
    }
}
