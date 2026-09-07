using System.Runtime.InteropServices;
using Archie.Contracts;
using Archie.Runner;
using NuGet.Versioning;

namespace Archie.Cli;

internal static class ScannerCommands
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0) return Usage("scanner requires a subcommand.");
        var store = InstalledScannerStore.CreateDefault();
        return args[0] switch
        {
            "add" => await AddAsync(store, args[1..], cancellationToken),
            "info" => await InfoAsync(store, args[1..], cancellationToken),
            "list" => await ListAsync(store, args[1..], cancellationToken),
            "recommend" => await RecommendAsync(store, args[1..], cancellationToken),
            "remove" => await RemoveAsync(store, args[1..], cancellationToken),
            "search" => await SearchAsync(store, args[1..], cancellationToken),
            "update" => await UpdateAsync(store, args[1..], cancellationToken),
            "use" => await UseAsync(store, args[1..], cancellationToken),
            _ => Usage($"Unknown scanner command '{args[0]}'.")
        };
    }

    private static async Task<int> AddAsync(
        InstalledScannerStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 2 && args[0] == "--local")
        {
            Console.Error.WriteLine("WARNING: Local scanner packages are unsigned and unverified executable code.");
            var localPackage = await new ScannerPackageInstaller(store).InstallLocalAsync(args[1], cancellationToken);
            Console.WriteLine($"Installed and activated {localPackage.Id} {localPackage.Version}.");
            WritePackage(localPackage, active: true);
            return 0;
        }
        if (args.Length is not (1 or 3) || args.Length == 3 && args[1] != "--version")
            return Usage("scanner add requires <id> [--version <version>] or --local <archive>.");
        var receipt = await store.ReadAsync(cancellationToken);
        var active = receipt.Active.FirstOrDefault(item => item.Id == args[0]);
        var activePackage = active is null ? null : receipt.Packages.Single(item =>
            item.Id == active.Id && item.Version == active.Version && item.Sha256 == active.Sha256);
        if (activePackage?.Origin == ScannerPackageOrigin.LocalUnsigned)
            throw new InvalidDataException(
                $"Scanner '{args[0]}' is active from a local unsigned package; use 'archie scanner update {args[0]} --from catalog' to change trust origin.");
        var verifier = new ScannerTrustVerifier();
        using var catalogClient = new ScannerCatalogClient(store);
        var catalog = await LoadCatalogAsync(store, catalogClient, allowCachedCatalog: false, cancellationToken);
        var release = ResolveRelease(catalog, args[0], args.Length == 3 ? args[2] : null);
        var package = await DownloadAndInstallAsync(
            store, catalogClient, verifier, catalog, release, allowLocalOriginTransition: false, cancellationToken);
        Console.WriteLine($"Installed and activated {package.Id} {package.Version}.");
        WritePackage(package, active: true);
        return 0;
    }

    private static async Task<int> SearchAsync(
        InstalledScannerStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length > 1) return Usage("scanner search accepts at most one query.");
        using var client = new ScannerCatalogClient(store);
        var catalog = await LoadCatalogAsync(store, client, allowCachedCatalog: true, cancellationToken);
        var query = args.FirstOrDefault();
        var releases = catalog.Releases
            .Where(IsCompatible)
            .Where(item => query is null || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => NuGetVersion.Parse(item.Version)).First())
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (releases.Length == 0)
        {
            Console.WriteLine("No compatible scanners found.");
            return 0;
        }
        foreach (var release in releases) WriteRelease(release);
        return 0;
    }

    private static async Task<int> InfoAsync(
        InstalledScannerStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length != 1) return Usage("scanner info requires one scanner ID.");
        using var client = new ScannerCatalogClient(store);
        var catalog = await LoadCatalogAsync(store, client, allowCachedCatalog: true, cancellationToken);
        var releases = catalog.Releases.Where(item => item.Id == args[0] && IsCompatible(item))
            .OrderByDescending(item => NuGetVersion.Parse(item.Version)).ToArray();
        if (releases.Length == 0) throw new InvalidDataException($"No compatible catalog release exists for scanner '{args[0]}'.");
        foreach (var release in releases) WriteRelease(release);
        return 0;
    }

    private static async Task<int> UpdateAsync(
        InstalledScannerStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        using var client = new ScannerCatalogClient(store);
        return await UpdateAsync(store, args, client, new ScannerTrustVerifier(), cancellationToken);
    }

    internal static async Task<int> UpdateAsync(
        InstalledScannerStore store,
        string[] args,
        ScannerCatalogClient client,
        ScannerTrustVerifier verifier,
        CancellationToken cancellationToken)
    {
        if (args.Length < 1 || (args.Length - 1) % 2 != 0)
            return Usage("scanner update requires <id> [--version <version>] [--from catalog].");
        string? version = null;
        var fromCatalog = false;
        for (var index = 1; index < args.Length; index += 2)
        {
            switch (args[index])
            {
                case "--version" when version is null:
                    version = args[index + 1];
                    break;
                case "--from" when !fromCatalog && args[index + 1] == "catalog":
                    fromCatalog = true;
                    break;
                default:
                    return Usage("scanner update requires <id> [--version <version>] [--from catalog].");
            }
        }

        var receipt = await store.ReadAsync(cancellationToken);
        var active = receipt.Active.FirstOrDefault(item => item.Id == args[0])
            ?? throw new InvalidDataException($"Scanner '{args[0]}' is not active; install it with 'archie scanner add {args[0]}'.");
        var current = receipt.Packages.Single(item =>
            item.Id == active.Id && item.Version == active.Version && item.Sha256 == active.Sha256);
        if (current.Origin == ScannerPackageOrigin.LocalUnsigned && !fromCatalog)
            throw new InvalidDataException(
                $"Scanner '{args[0]}' is active from a local unsigned package; repeat with '--from catalog' to change trust origin.");

        var catalog = await LoadCatalogAsync(store, client, allowCachedCatalog: false, cancellationToken);
        var release = ResolveRelease(catalog, args[0], version);
        if (current.Origin == ScannerPackageOrigin.Catalog && current.Version == release.Version && current.Sha256 == release.Sha256)
        {
            Console.WriteLine($"{current.Id} {current.Version} is already the selected catalog release.");
            return 0;
        }

        Console.WriteLine(
            $"Updating {current.Id} from {current.Version} ({Value(current.Origin)}) to {release.Version} " +
            $"(verified-first-party, key={release.SigningKeyId}).");
        var package = await DownloadAndInstallAsync(
            store, client, verifier, catalog, release, allowLocalOriginTransition: fromCatalog, cancellationToken);
        Console.WriteLine($"Installed and activated {package.Id} {package.Version}.");
        WritePackage(package, active: true);
        return 0;
    }

    private static async Task<int> ListAsync(
        InstalledScannerStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length != 0) return Usage("scanner list does not accept arguments.");
        var receipt = await store.ReadAsync(cancellationToken);
        if (receipt.Packages.Count == 0)
        {
            Console.WriteLine("No scanners installed.");
            return 0;
        }
        var active = receipt.Active.Select(item => $"{item.Id}\0{item.Version}\0{item.Sha256}").ToHashSet(StringComparer.Ordinal);
        foreach (var package in receipt.Packages
                     .OrderBy(item => item.Id, StringComparer.Ordinal)
                     .ThenBy(item => item.Version, StringComparer.Ordinal)
                     .ThenBy(item => item.Sha256, StringComparer.Ordinal))
            WritePackage(package, active.Contains($"{package.Id}\0{package.Version}\0{package.Sha256}"));
        return 0;
    }

    private static async Task<int> RemoveAsync(
        InstalledScannerStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length is < 1 or > 3) return Usage("scanner remove requires <id> [<version> [<sha256>]].");
        var removed = await store.RemoveAsync(
            args[0], args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null, cancellationToken);
        if (removed.Count == 0) throw new InvalidDataException($"Scanner '{args[0]}' is not installed.");
        Console.WriteLine($"Removed {removed.Count} package(s) for {args[0]}.");
        return 0;
    }

    private static async Task<int> UseAsync(
        InstalledScannerStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length is < 2 or > 3) return Usage("scanner use requires <id> <version> [<sha256>].");
        var package = await store.UseAsync(args[0], args[1], args.Length == 3 ? args[2] : null, cancellationToken);
        Console.WriteLine($"Activated {package.Id} {package.Version} sha256={package.Sha256}.");
        return 0;
    }

    private static async Task<int> RecommendAsync(
        InstalledScannerStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length > 1) return Usage("scanner recommend accepts at most one repository path.");
        var repository = Path.GetFullPath(args.FirstOrDefault() ?? Environment.CurrentDirectory);
        var inventory = await RepositoryFileInventory.CreateAsync(repository, cancellationToken);
        var roots = await store.ResolveActiveAsync(cancellationToken);
        var scanners = await new ScannerManifestLoader().DiscoverAsync(roots, inventory, cancellationToken);
        var detector = new TechnologyStackDetector();
        var coverage = detector.AssessCoverage(detector.Detect(inventory), scanners);
        if (coverage.Count == 0)
        {
            Console.WriteLine("No recognized technology stacks were detected.");
            return 0;
        }
        foreach (var item in coverage)
        {
            var scanner = item.State switch
            {
                ScannerCoverageState.Installed => $"{item.ScannerId} {item.ScannerVersion}",
                ScannerCoverageState.Missing => $"{item.ScannerId}; install with archie scanner add {item.ScannerId}",
                _ => "no first-party scanner is available"
            };
            Console.WriteLine($"{Value(item.State)} {item.Stack.Name}: {scanner}");
            Console.WriteLine($"  signals: {Signals(item.Stack.Signals)}");
        }
        return 0;
    }

    private static void WritePackage(InstalledScannerPackage package, bool active)
    {
        Console.WriteLine(
            $"{(active ? "*" : " ")} {package.Id} {package.Version} " +
            $"origin={Value(package.Origin)} trust={Value(package.Trust)} sha256={package.Sha256} expandedBytes={package.ExpandedBytes}");
    }

    private static void WriteRelease(ScannerCatalogRelease release)
    {
        Console.WriteLine($"{release.Id} {release.Version} name={release.Name} compressedBytes={release.CompressedBytes} license={release.License}");
        Console.WriteLine($"  source={release.SourceRepository} tag={release.SourceTag} notes={release.ReleaseNotesUrl}");
    }

    internal static ScannerCatalogRelease ResolveRelease(ScannerCatalog catalog, string id, string? version)
    {
        NuGetVersion? requested = null;
        if (version is not null && !NuGetVersion.TryParse(version, out requested))
            throw new InvalidDataException($"Requested scanner version '{version}' is not a valid semantic version.");
        var matches = catalog.Releases
            .Where(item => item.Id == id && IsCompatible(item))
            .Where(item => requested is null || NuGetVersion.Parse(item.Version) == requested)
            .OrderByDescending(item => NuGetVersion.Parse(item.Version))
            .ToArray();
        if (matches.Length == 0 && requested is not null && catalog.Releases.Any(item =>
                item.Id == id && NuGetVersion.Parse(item.Version) == requested && item.Revoked))
            throw new InvalidDataException($"Catalog release '{id}' {version} is revoked and cannot be activated.");
        if (matches.Length == 0)
            throw new InvalidDataException($"No compatible catalog release exists for scanner '{id}'{(version is null ? "." : $" at version {version}.")}");
        return matches[0];
    }

    private static async Task<ScannerCatalog> LoadCatalogAsync(
        InstalledScannerStore store,
        ScannerCatalogClient client,
        bool allowCachedCatalog,
        CancellationToken cancellationToken)
    {
        var loaded = allowCachedCatalog
            ? await client.RefreshOrCachedAsync(cancellationToken)
            : new ScannerCatalogLoad(await client.RefreshAsync(cancellationToken), false, DateTimeOffset.UtcNow, null);
        if (loaded.UsedCachedCatalog)
            Console.Error.WriteLine(
                $"WARNING: Catalog refresh failed; using the verified catalog cached at {loaded.RetrievedAt:O}. {loaded.RefreshFailure}");
        var receipt = await store.ReadAsync(cancellationToken);
        foreach (var warning in RevocationWarnings(loaded.Catalog, receipt)) Console.Error.WriteLine(warning);
        return loaded.Catalog;
    }

    internal static IReadOnlyList<string> RevocationWarnings(
        ScannerCatalog catalog,
        InstalledScannerReceipt receipt)
    {
        var activeKeys = receipt.Active.Select(item => $"{item.Id}\0{item.Version}\0{item.Sha256}").ToHashSet(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var package in receipt.Packages.OrderBy(item => item.Id, StringComparer.Ordinal)
                     .ThenBy(item => item.Version, StringComparer.Ordinal).ThenBy(item => item.Sha256, StringComparer.Ordinal))
        {
            if (!catalog.Releases.Any(item =>
                    item.Revoked && item.Id == package.Id && item.Version == package.Version && item.Sha256 == package.Sha256))
                continue;
            var state = activeKeys.Contains($"{package.Id}\0{package.Version}\0{package.Sha256}") ? "active" : "installed";
            warnings.Add(
                $"WARNING: {state} scanner {package.Id} {package.Version} sha256={package.Sha256} is revoked in catalog {catalog.CatalogVersion}; it was not removed or replaced.");
        }
        return warnings;
    }

    private static async Task<InstalledScannerPackage> DownloadAndInstallAsync(
        InstalledScannerStore store,
        ScannerCatalogClient client,
        ScannerTrustVerifier verifier,
        ScannerCatalog catalog,
        ScannerCatalogRelease release,
        bool allowLocalOriginTransition,
        CancellationToken cancellationToken)
    {
        var archive = await client.DownloadAsync(release, cancellationToken);
        try
        {
            return await new ScannerPackageInstaller(store, verifier).InstallCatalogAsync(
                release, catalog.CatalogVersion, archive, cancellationToken, allowLocalOriginTransition);
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    private static bool IsCompatible(ScannerCatalogRelease release)
    {
        var version = NuGetVersion.Parse(release.Version);
        var range = VersionRange.Parse(release.ArchieVersionRange);
        var assemblyVersion = typeof(ScannerCommands).Assembly.GetName().Version!;
        var archieVersion = new NuGetVersion(assemblyVersion.Major, assemblyVersion.Minor, Math.Max(assemblyVersion.Build, 0));
        var platform = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "unsupported";
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return !release.Revoked && release.Channel == "stable" && !version.IsPrerelease &&
               release.ProtocolVersion == "scanner/v1" && release.Platform == platform &&
               release.Architecture == architecture && range.Satisfies(archieVersion);
    }

    private static string Value<T>(T value) where T : struct, Enum =>
        System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());

    private static string Signals(IReadOnlyList<string> signals)
    {
        var shown = signals.Take(3).ToArray();
        var remainder = signals.Count - shown.Length;
        return $"{string.Join(", ", shown)}{(remainder == 0 ? string.Empty : $" (+{remainder} more)")}";
    }

    private static int Usage(string error)
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine("Usage:\n  archie scanner add <id> [--version <version>]\n  archie scanner add --local <archive.tar.gz>\n  archie scanner search [<query>]\n  archie scanner info <id>\n  archie scanner list\n  archie scanner recommend [<repository>]\n  archie scanner update <id> [--version <version>] [--from catalog]\n  archie scanner use <id> <version> [<sha256>]\n  archie scanner remove <id> [<version> [<sha256>]]");
        return 2;
    }
}
