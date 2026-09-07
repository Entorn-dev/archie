using Archie.Contracts;

namespace Archie.Runner;

public sealed class InstalledScannerStore
{
    private const string ReceiptSchemaVersion = "installed-scanners/v1";
    private const long MaxReceiptBytes = 16L * 1024 * 1024;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly TimeSpan lockTimeout;

    public InstalledScannerStore(string dataRoot) : this(dataRoot, TimeSpan.FromSeconds(5)) { }

    internal InstalledScannerStore(string dataRoot, TimeSpan lockTimeout)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("Scanner data root is required.", nameof(dataRoot));
        if (lockTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        RootDirectory = Path.GetFullPath(dataRoot);
        PackagesDirectory = Path.Combine(RootDirectory, "packages");
        ReceiptPath = Path.Combine(RootDirectory, "installed.json");
        this.lockTimeout = lockTimeout;
    }

    public string RootDirectory { get; }
    public string PackagesDirectory { get; }
    public string ReceiptPath { get; }
    internal string LockPath => Path.Combine(RootDirectory, ".installed.lock");

    public static InstalledScannerStore CreateDefault()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string dataHome;
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            if (!Path.IsPathFullyQualified(xdg))
                throw new InvalidDataException("XDG_DATA_HOME must be an absolute path.");
            dataHome = xdg;
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) throw new InvalidDataException("Could not resolve the user data directory.");
            dataHome = Path.Combine(home, ".local", "share");
        }
        return new(Path.Combine(dataHome, "archie", "scanners"));
    }

    public async Task<InstalledScannerReceipt> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ReceiptPath)) return Empty();
        if (new FileInfo(ReceiptPath).Length > MaxReceiptBytes)
            throw new InvalidDataException($"Installed scanner receipt exceeds the {MaxReceiptBytes} byte limit.");
        var bytes = await File.ReadAllBytesAsync(ReceiptPath, cancellationToken);
        ScannerSchemaValidator.Validate(bytes, "installed-scanners.schema.json", "Installed scanner receipt", ReceiptPath);
        using var stream = new MemoryStream(bytes, writable: false);
        var receipt = ContractJson.ReadInstalledScanners(stream);
        Validate(receipt);
        return receipt;
    }

    public async Task<IReadOnlyList<ScannerSearchRoot>> ResolveActiveAsync(CancellationToken cancellationToken)
    {
        var receipt = await ReadAsync(cancellationToken);
        var packages = receipt.Packages.ToDictionary(PackageKey, StringComparer.Ordinal);
        var roots = new List<ScannerSearchRoot>();
        foreach (var active in receipt.Active.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = packages[PackageKey(active.Id, active.Version, active.Sha256)];
            var path = PackageDirectory(active.Id, active.Version, active.Sha256);
            if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "scanner.json")) || !File.Exists(Path.Combine(path, "PACKAGE.json")))
                throw new InvalidDataException($"Active scanner package '{active.Id}' is missing or incomplete.");
            roots.Add(new(path, package.Sha256, package.Origin));
        }
        return roots;
    }

    public async Task<InstalledScannerPackage> InstallAndActivateAsync(
        InstalledScannerPackage package,
        string extractedPackagePath,
        CancellationToken cancellationToken,
        bool allowLocalOriginTransition = false)
    {
        ValidateSegment(package.Id, "scanner ID");
        ValidateSegment(package.Version, "scanner version");
        ValidateDigest(package.Sha256);
        var extracted = Path.GetFullPath(extractedPackagePath);
        if (!Directory.Exists(extracted)) throw new InvalidDataException("Extracted scanner package is missing.");
        await using var mutationLock = await AcquireLockAsync(cancellationToken);
        var receipt = await ReadAsync(cancellationToken);
        var key = PackageKey(package);
        var existing = receipt.Packages.FirstOrDefault(item => PackageKey(item) == key);
        var recorded = existing ?? package;
        var previousActive = receipt.Active.FirstOrDefault(item => item.Id == package.Id);
        var previousPackage = previousActive is null ? null : receipt.Packages.Single(item =>
            item.Id == previousActive.Id && item.Version == previousActive.Version && item.Sha256 == previousActive.Sha256);
        if (package.Origin == ScannerPackageOrigin.Catalog && previousPackage?.Origin == ScannerPackageOrigin.LocalUnsigned &&
            !allowLocalOriginTransition)
            throw new InvalidDataException(
                $"Scanner '{package.Id}' is active from a local unsigned package; an explicit catalog trust transition is required.");
        var destination = PackageDirectory(package.Id, package.Version, package.Sha256);
        var promoted = false;
        if (Directory.Exists(destination))
        {
            ValidatePackageDirectory(destination, package.Id);
        }
        else
        {
            EnsureOwnerOnlyDirectory(PackagesDirectory);
            EnsureOwnerOnlyDirectory(Path.Combine(PackagesDirectory, package.Id));
            EnsureOwnerOnlyDirectory(Path.Combine(PackagesDirectory, package.Id, package.Version));
            Directory.Move(extracted, destination);
            promoted = true;
        }

        var retainedKeys = new HashSet<string>(StringComparer.Ordinal) { key };
        if (previousActive is not null) retainedKeys.Add(PackageKey(previousActive.Id, previousActive.Version, previousActive.Sha256));
        var packages = receipt.Packages
            .Where(item => item.Id != package.Id || retainedKeys.Contains(PackageKey(item)))
            .Where(item => PackageKey(item) != key)
            .Append(recorded)
            .ToArray();
        var active = receipt.Active
            .Where(item => item.Id != package.Id)
            .Append(new ActiveScanner(package.Id, package.Version, package.Sha256))
            .ToArray();
        try
        {
            await WriteUnlockedAsync(new(ReceiptSchemaVersion, packages, active));
        }
        catch
        {
            if (promoted && Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            throw;
        }
        CleanupUnreferenced(receipt.Packages.Where(item => item.Id == package.Id && !retainedKeys.Contains(PackageKey(item))));
        return recorded;
    }

    public async Task ActivateAsync(InstalledScannerPackage package, CancellationToken cancellationToken)
    {
        ValidateSegment(package.Id, "scanner ID");
        ValidateSegment(package.Version, "scanner version");
        ValidateDigest(package.Sha256);
        await using var mutationLock = await AcquireLockAsync(cancellationToken);
        ValidatePackageDirectory(PackageDirectory(package.Id, package.Version, package.Sha256), package.Id);
        var receipt = await ReadAsync(cancellationToken);
        var packages = receipt.Packages
            .Where(item => PackageKey(item) != PackageKey(package))
            .Append(package)
            .ToArray();
        var active = receipt.Active
            .Where(item => item.Id != package.Id)
            .Append(new ActiveScanner(package.Id, package.Version, package.Sha256))
            .ToArray();
        await WriteUnlockedAsync(new(ReceiptSchemaVersion, packages, active));
    }

    public async Task<InstalledScannerPackage> UseAsync(
        string id,
        string version,
        string? sha256,
        CancellationToken cancellationToken)
    {
        ValidateSegment(id, "scanner ID");
        ValidateSegment(version, "scanner version");
        if (sha256 is not null) ValidateDigest(sha256);
        await using var mutationLock = await AcquireLockAsync(cancellationToken);
        var receipt = await ReadAsync(cancellationToken);
        var matches = receipt.Packages
            .Where(item => item.Id == id && item.Version == version && (sha256 is null || item.Sha256 == sha256))
            .ToArray();
        if (matches.Length == 0) throw new InvalidDataException($"Scanner '{id}' {version} is not installed.");
        if (matches.Length > 1)
            throw new InvalidDataException($"Scanner '{id}' {version} has multiple installed digests; specify the SHA-256.");
        var package = matches[0];
        ValidatePackageDirectory(PackageDirectory(package.Id, package.Version, package.Sha256), package.Id);
        var active = receipt.Active
            .Where(item => item.Id != id)
            .Append(new ActiveScanner(package.Id, package.Version, package.Sha256))
            .ToArray();
        await WriteUnlockedAsync(receipt with { Active = active });
        return package;
    }

    public async Task<IReadOnlyList<InstalledScannerPackage>> RemoveAsync(
        string id,
        string? version,
        string? sha256,
        CancellationToken cancellationToken)
    {
        ValidateSegment(id, "scanner ID");
        if (version is not null) ValidateSegment(version, "scanner version");
        if (sha256 is not null)
        {
            if (version is null) throw new InvalidDataException("A scanner version is required when selecting a digest.");
            ValidateDigest(sha256);
        }
        await using var mutationLock = await AcquireLockAsync(cancellationToken);
        var receipt = await ReadAsync(cancellationToken);
        var removed = receipt.Packages.Where(item =>
            item.Id == id && (version is null || item.Version == version) && (sha256 is null || item.Sha256 == sha256)).ToArray();
        if (removed.Length == 0) return [];
        var removedKeys = removed.Select(PackageKey).ToHashSet(StringComparer.Ordinal);
        var remaining = receipt.Packages.Where(item => !removedKeys.Contains(PackageKey(item))).ToArray();
        var active = receipt.Active.Where(item => item.Id != id).ToList();
        var removedActive = receipt.Active.Any(item => item.Id == id && removedKeys.Contains(PackageKey(item.Id, item.Version, item.Sha256)));
        if (removedActive)
        {
            var replacement = remaining.Where(item => item.Id == id)
                .OrderByDescending(item => item.Version, StringComparer.Ordinal)
                .ThenBy(item => item.Sha256, StringComparer.Ordinal)
                .FirstOrDefault();
            if (replacement is not null)
                active.Add(new ActiveScanner(replacement.Id, replacement.Version, replacement.Sha256));
        }
        await WriteUnlockedAsync(new(ReceiptSchemaVersion, remaining, active));
        CleanupUnreferenced(removed);
        return removed;
    }

    public Task<IReadOnlyList<InstalledScannerPackage>> RemoveAsync(string id, CancellationToken cancellationToken) =>
        RemoveAsync(id, null, null, cancellationToken);

    public string PackageDirectory(string id, string version, string sha256)
    {
        ValidateSegment(id, "scanner ID");
        ValidateSegment(version, "scanner version");
        ValidateDigest(sha256);
        return Path.Combine(PackagesDirectory, id, version, sha256);
    }

    private async Task WriteUnlockedAsync(InstalledScannerReceipt receipt)
    {
        Validate(receipt);
        var bytes = ContractJson.WriteInstalledScanners(receipt);
        Directory.CreateDirectory(RootDirectory);
        SetOwnerOnly(RootDirectory);
        var temporary = Path.Combine(RootDirectory, $".installed.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
            File.Move(temporary, ReceiptPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RootDirectory);
        SetOwnerOnly(RootDirectory);
        var deadline = DateTime.UtcNow + lockTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(LockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return stream;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(LockRetryDelay, cancellationToken);
            }
            catch (IOException exception)
            {
                throw new IOException($"Timed out waiting {lockTimeout.TotalSeconds:0.###} seconds for the scanner installation lock.", exception);
            }
        }
    }

    private void ValidatePackageDirectory(string path, string id)
    {
        if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "scanner.json")) || !File.Exists(Path.Combine(path, "PACKAGE.json")))
            throw new InvalidDataException($"Installed scanner package '{id}' is missing or incomplete.");
    }

    private void CleanupUnreferenced(IEnumerable<InstalledScannerPackage> packages)
    {
        foreach (var package in packages)
        {
            try
            {
                var path = PackageDirectory(package.Id, package.Version, package.Sha256);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The receipt is authoritative. A failed best-effort deletion leaves inert, unreferenced bytes only.
            }
        }
    }

    private static InstalledScannerReceipt Empty() => new(ReceiptSchemaVersion, [], []);

    private static void Validate(InstalledScannerReceipt receipt)
    {
        if (receipt.SchemaVersion != ReceiptSchemaVersion)
            throw new InvalidDataException($"Unsupported installed scanner receipt version '{receipt.SchemaVersion}'.");
        var packageKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in receipt.Packages)
        {
            ValidateSegment(package.Id, "scanner ID");
            ValidateSegment(package.Version, "scanner version");
            ValidateDigest(package.Sha256);
            if (package.ExpandedBytes <= 0 || !packageKeys.Add(PackageKey(package)))
                throw new InvalidDataException("Installed scanner receipt contains an invalid or duplicate package.");
            if (package.Origin == ScannerPackageOrigin.LocalUnsigned &&
                (package.Trust != ScannerTrustState.LocalUnsigned || package.CatalogVersion is not null || package.VerifiedKeyId is not null))
                throw new InvalidDataException("Local unsigned scanner receipt metadata is inconsistent.");
            if (package.Origin == ScannerPackageOrigin.Catalog &&
                (package.Trust != ScannerTrustState.VerifiedFirstParty ||
                 string.IsNullOrWhiteSpace(package.CatalogVersion) || string.IsNullOrWhiteSpace(package.VerifiedKeyId)))
                throw new InvalidDataException("Catalog scanner receipt metadata is inconsistent.");
        }
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var active in receipt.Active)
        {
            ValidateSegment(active.Id, "scanner ID");
            ValidateSegment(active.Version, "scanner version");
            ValidateDigest(active.Sha256);
            if (!activeIds.Add(active.Id) || !packageKeys.Contains(PackageKey(active.Id, active.Version, active.Sha256)))
                throw new InvalidDataException("Installed scanner receipt contains an invalid active selection.");
        }
    }

    private static string PackageKey(InstalledScannerPackage package) =>
        PackageKey(package.Id, package.Version, package.Sha256);

    private static string PackageKey(string id, string version, string sha256) => $"{id}\0{version}\0{sha256}";

    private static void ValidateSegment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new InvalidDataException($"The {name} is not a safe storage segment.");
    }

    private static void ValidateDigest(string value)
    {
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("Scanner package SHA-256 must be 64 lowercase hexadecimal characters.");
    }

    private static void SetOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void EnsureOwnerOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        SetOwnerOnly(path);
    }
}
