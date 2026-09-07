using Archie.Contracts;

namespace Archie.Runner;

public sealed class TechnologyStackDetector
{
    public const string RuleVersion = "technology-stack-rules/v1";

    public IReadOnlyList<DetectedTechnologyStack> Detect(RepositoryFileInventory inventory)
    {
        var paths = inventory.Paths;
        return new[]
        {
            Stack("dotnet", ".NET", "archie.dotnet", paths, IsDotNet),
            Stack("go", "Go", null, paths, path => FileName(path) is "go.mod" or "go.sum"),
            Stack("infrastructure", "Infrastructure as code", null, paths, IsInfrastructure),
            Stack("node", "Node.js / TypeScript", null, paths, IsNode),
            Stack("php", "PHP / Laravel", "archie.php", paths, IsPhp)
        }.Where(stack => stack.Signals.Count > 0).ToArray();
    }

    public IReadOnlyList<ScannerCoverage> AssessCoverage(
        IReadOnlyList<DetectedTechnologyStack> stacks,
        IReadOnlyList<DiscoveredScanner> activeScanners)
    {
        var scanners = activeScanners.ToDictionary(item => item.Manifest.Id, StringComparer.Ordinal);
        return stacks.OrderBy(stack => stack.Id, StringComparer.Ordinal).Select(stack =>
        {
            if (stack.RecommendedScannerId is null)
                return new ScannerCoverage(stack, ScannerCoverageState.Unavailable, null, null);
            return scanners.TryGetValue(stack.RecommendedScannerId, out var scanner)
                ? new(stack, ScannerCoverageState.Installed, scanner.Manifest.Id, scanner.Manifest.Version)
                : new(stack, ScannerCoverageState.Missing, stack.RecommendedScannerId, null);
        }).ToArray();
    }

    public IReadOnlyList<Diagnostic> ToDiagnostics(IReadOnlyList<ScannerCoverage> coverage) =>
        coverage.Where(item => item.State != ScannerCoverageState.Unavailable)
            .Select(item => item.State == ScannerCoverageState.Installed
                ? new Diagnostic(
                    $"diagnostic:scanner-coverage:{item.Stack.Id}",
                    "SCANNER_COVERAGE_INSTALLED",
                    "info",
                    $"{item.Stack.Name} scanner coverage is installed with {item.ScannerId} {item.ScannerVersion}.",
                    $"stack:{item.Stack.Id}")
                : new Diagnostic(
                    $"diagnostic:scanner-coverage:{item.Stack.Id}",
                    "SCANNER_COVERAGE_MISSING",
                    "warning",
                    $"{item.Stack.Name} was detected but has no installed scanner. Install with archie scanner add {item.ScannerId}.",
                    $"stack:{item.Stack.Id}"))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

    private static DetectedTechnologyStack Stack(
        string id,
        string name,
        string? scannerId,
        IReadOnlyList<string> paths,
        Func<string, bool> predicate) =>
        new(id, name, scannerId, paths.Where(predicate).Order(StringComparer.Ordinal).ToArray());

    private static bool IsDotNet(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static bool IsPhp(string path) =>
        FileName(path) is "composer.json" or "composer.lock" or "artisan" ||
        path.EndsWith(".php", StringComparison.OrdinalIgnoreCase);

    private static bool IsNode(string path) => FileName(path) is
        "package.json" or "package-lock.json" or "npm-shrinkwrap.json" or
        "pnpm-lock.yaml" or "yarn.lock" or "bun.lock" or "bun.lockb";

    private static bool IsInfrastructure(string path)
    {
        var name = FileName(path);
        return path.EndsWith(".tf", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".tf.json", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
               name is "compose.yaml" or "compose.yml" or "docker-compose.yaml" or "docker-compose.yml" or
                   "Chart.yaml" or "kustomization.yaml" or "kustomization.yml";
    }

    private static string FileName(string path) => Path.GetFileName(path);
}
