using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Archie.Contracts;
using Archie.Runner;
using NuGet.Versioning;

namespace Archie.Cli;

internal sealed class ScannerPackageInstaller
{
    private const long MaxCompressedBytes = 1024L * 1024 * 1024;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxMetadataBytes = 1024L * 1024;
    private const int MaxEntries = 50_000;
    private readonly InstalledScannerStore store;
    private readonly ScannerTrustVerifier verifier;

    public ScannerPackageInstaller(InstalledScannerStore store) : this(store, new ScannerTrustVerifier()) { }

    internal ScannerPackageInstaller(InstalledScannerStore store, ScannerTrustVerifier verifier)
    {
        this.store = store;
        this.verifier = verifier;
    }

    public Task<InstalledScannerPackage> InstallLocalAsync(string archivePath, CancellationToken cancellationToken) =>
        InstallAsync(archivePath, null, null, cancellationToken);

    public Task<InstalledScannerPackage> InstallCatalogAsync(
        ScannerCatalogRelease release,
        string catalogVersion,
        string archivePath,
        CancellationToken cancellationToken,
        bool allowLocalOriginTransition = false) =>
        InstallAsync(archivePath, release, catalogVersion, cancellationToken, allowLocalOriginTransition);

    private async Task<InstalledScannerPackage> InstallAsync(
        string archivePath,
        ScannerCatalogRelease? release,
        string? catalogVersion,
        CancellationToken cancellationToken,
        bool allowLocalOriginTransition = false)
    {
        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath)) throw new FileNotFoundException("Local scanner archive was not found.", fullArchivePath);
        await using var archive = new FileStream(
            fullArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (archive.Length is <= 0 or > MaxCompressedBytes)
            throw new InvalidDataException($"Local scanner archive must contain between 1 and {MaxCompressedBytes} bytes.");
        if (release is not null && archive.Length != release.CompressedBytes)
            throw new InvalidDataException("Scanner package size does not match the signed catalog.");

        var digest = await DigestAsync(archive, cancellationToken);
        if (release is not null) verifier.VerifyRelease(release, Convert.FromHexString(digest));
        archive.Position = 0;
        Directory.CreateDirectory(store.RootDirectory);
        SetOwnerOnly(store.RootDirectory);
        var temporary = Path.Combine(store.RootDirectory, $".extract.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporary);
        SetOwnerOnly(temporary);
        try
        {
            var extraction = await ExtractAsync(archive, temporary, cancellationToken);
            var packagePath = Path.Combine(temporary, "PACKAGE.json");
            var manifestPath = Path.Combine(temporary, "scanner.json");
            if (!File.Exists(packagePath) || !File.Exists(manifestPath))
                throw new InvalidDataException("Scanner archive must contain root PACKAGE.json and scanner.json files.");
            if (new FileInfo(packagePath).Length > MaxMetadataBytes || new FileInfo(manifestPath).Length > MaxMetadataBytes)
                throw new InvalidDataException($"Scanner package metadata exceeds the {MaxMetadataBytes} byte limit.");

            var packageBytes = await File.ReadAllBytesAsync(packagePath, cancellationToken);
            ArtifactSchemaValidator.Validate(packageBytes);
            using var packageStream = new MemoryStream(packageBytes, writable: false);
            var metadata = ContractJson.ReadScannerPackage(packageStream);
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            ArtifactSchemaValidator.Validate(manifestBytes);
            var manifest = JsonSerializer.Deserialize<ScannerManifest>(manifestBytes, ScannerContractJson.Options)
                ?? throw new InvalidDataException("Scanner manifest is empty.");
            Validate(metadata, manifest, extraction, release);

            var origin = release is null ? ScannerPackageOrigin.LocalUnsigned : ScannerPackageOrigin.Catalog;
            var trust = release is null ? ScannerTrustState.LocalUnsigned : ScannerTrustState.VerifiedFirstParty;

            var installed = new InstalledScannerPackage(
                metadata.Id,
                metadata.Version,
                digest,
                origin,
                trust,
                catalogVersion,
                release?.SigningKeyId,
                DateTimeOffset.UtcNow,
                extraction.ExpandedBytes);
            return await store.InstallAndActivateAsync(
                installed, temporary, cancellationToken, allowLocalOriginTransition);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static async Task<ExtractionResult> ExtractAsync(
        Stream archive,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long expandedBytes = 0;
        var entryCount = 0;
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(gzip, leaveOpen: false);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            entryCount++;
            if (entryCount > MaxEntries) throw new InvalidDataException($"Scanner archive exceeds the {MaxEntries} entry limit.");
            if (entry.Length < 0 || entry.Length > MaxExpandedBytes - expandedBytes)
                throw new InvalidDataException($"Scanner archive exceeds the {MaxExpandedBytes} expanded-byte limit.");
            var relative = NormalizeEntry(entry.Name);
            if (!seen.Add(relative)) throw new InvalidDataException($"Scanner archive contains duplicate path '{relative}'.");
            var target = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            var pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!target.StartsWith(destinationRoot + Path.DirectorySeparatorChar, pathComparison))
                throw new InvalidDataException("Scanner archive entry escapes the extraction directory.");

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    CreateOwnerOnlyDirectory(target);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    CreateOwnerOnlyDirectory(Path.GetDirectoryName(target)!);
                    if (entry.DataStream is null) throw new InvalidDataException($"Scanner archive file '{relative}' has no data.");
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await entry.DataStream.ReadAsync(buffer, cancellationToken)) != 0)
                        {
                            expandedBytes += read;
                            if (expandedBytes > MaxExpandedBytes)
                                throw new InvalidDataException($"Scanner archive exceeds the {MaxExpandedBytes} expanded-byte limit.");
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        }
                    }
                    if (!OperatingSystem.IsWindows())
                    {
                        var mode = entry.Mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                        File.SetUnixFileMode(target, mode | UnixFileMode.UserRead);
                    }
                    break;
                default:
                    throw new InvalidDataException($"Scanner archive entry '{relative}' has unsupported type '{entry.EntryType}'.");
            }
        }
        return new(entryCount, expandedBytes);
    }

    private static string NormalizeEntry(string name)
    {
        var normalized = name.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || Path.IsPathFullyQualified(normalized) ||
            normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Scanner archive contains unsafe path '{name}'.");
        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static void Validate(
        ScannerPackageMetadata metadata,
        ScannerManifest manifest,
        ExtractionResult extraction,
        ScannerCatalogRelease? release)
    {
        if (metadata.SchemaVersion != "scanner-package/v1" || metadata.ProtocolVersion != "scanner/v1")
            throw new InvalidDataException("Scanner package uses an unsupported contract version.");
        if (!NuGetVersion.TryParse(metadata.Version, out _) ||
            !VersionRange.TryParse(metadata.ArchieVersionRange, out var compatibility))
            throw new InvalidDataException("Scanner package contains an invalid semantic version or Archie compatibility range.");
        var assemblyVersion = typeof(ScannerPackageInstaller).Assembly.GetName().Version
            ?? throw new InvalidDataException("Could not determine the current Archie version.");
        var archieVersion = new NuGetVersion(assemblyVersion.Major, assemblyVersion.Minor, Math.Max(assemblyVersion.Build, 0));
        if (!compatibility.Satisfies(archieVersion))
            throw new InvalidDataException($"Scanner package is not compatible with Archie {archieVersion.ToNormalizedString()}.");
        if (metadata.Id != manifest.Id || metadata.Version != manifest.Version ||
            !metadata.Capabilities.Order(StringComparer.Ordinal).SequenceEqual(manifest.Capabilities.Order(StringComparer.Ordinal)) ||
            metadata.Permissions != manifest.Permissions)
            throw new InvalidDataException("Scanner package metadata does not match scanner.json.");
        if (!metadata.Permissions.ReadRepository || metadata.Permissions.Network || metadata.Permissions.Environment)
            throw new InvalidDataException("Scanner package requests permissions outside the approved local scanner permissions.");
        if (release is null && metadata.PublisherKeyId is not null)
            throw new InvalidDataException("A local unsigned package cannot assert a verified publisher key.");
        if (metadata.EntryCount < extraction.EntryCount || metadata.ExpandedBytes < extraction.ExpandedBytes)
            throw new InvalidDataException("Scanner archive exceeds its declared package bounds.");
        var platform = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "unsupported";
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        if (metadata.Platform != platform || metadata.Architecture != architecture)
            throw new InvalidDataException($"Scanner package targets {metadata.Platform}-{metadata.Architecture}, not {platform}-{architecture}.");
        if (release is not null &&
            (metadata.Id != release.Id || metadata.Version != release.Version ||
             metadata.SourceRepository != release.SourceRepository || metadata.SourceTag != release.SourceTag ||
             metadata.Platform != release.Platform || metadata.Architecture != release.Architecture ||
             metadata.ArchieVersionRange != release.ArchieVersionRange || metadata.ProtocolVersion != release.ProtocolVersion ||
             metadata.ExpandedBytes != release.ExpandedBytes || metadata.EntryCount != release.EntryCount ||
             !metadata.Capabilities.Order(StringComparer.Ordinal).SequenceEqual(release.Capabilities.Order(StringComparer.Ordinal)) ||
             metadata.Permissions != release.Permissions || metadata.License != release.License ||
             metadata.PublisherKeyId != release.SigningKeyId))
            throw new InvalidDataException("Scanner package metadata does not match the signed catalog release.");
    }

    private static async Task<string> DigestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static void SetOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void CreateOwnerOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        SetOwnerOnly(path);
    }

    private sealed record ExtractionResult(int EntryCount, long ExpandedBytes);
}
