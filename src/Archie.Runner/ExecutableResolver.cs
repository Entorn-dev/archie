namespace Archie.Runner;

public static class ExecutableResolver
{
    public static string Resolve(
        string executable,
        string workingDirectory,
        string? path = null,
        string? pathExtensions = null,
        bool? windows = null)
    {
        var isWindows = windows ?? OperatingSystem.IsWindows();
        var candidates = CandidateNames(executable, pathExtensions, isWindows);
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            var basePath = Path.IsPathRooted(executable) ? executable : Path.Combine(workingDirectory, executable);
            foreach (var candidate in CandidateNames(basePath, pathExtensions, isWindows))
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            throw new InvalidDataException("Scanner executable path does not exist.");
        }

        path ??= Environment.GetEnvironmentVariable("PATH");
        foreach (var directory in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath)) return Path.GetFullPath(fullPath);
            }
        throw new InvalidDataException("Scanner executable was not found on PATH.");
    }

    private static IReadOnlyList<string> CandidateNames(string executable, string? pathExtensions, bool windows)
    {
        if (!windows || Path.HasExtension(executable)) return [executable];
        pathExtensions ??= Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        return [executable, .. pathExtensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => executable + (extension.StartsWith('.') ? extension : $".{extension}"))];
    }
}
