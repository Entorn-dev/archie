namespace Archie.Contracts;

public enum ScannerPackageOrigin { Catalog, LocalUnsigned }

public enum ScannerTrustState { VerifiedFirstParty, LocalUnsigned }

public enum ScannerCoverageState { Installed, Missing, Unavailable }

public sealed record SignedScannerCatalog(
    string SchemaVersion,
    string KeyId,
    string Payload,
    string Signature);

public sealed record ScannerCatalog(
    string SchemaVersion,
    string CatalogVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ScannerCatalogRelease> Releases);

public sealed record ScannerCatalogRelease(
    string Id,
    string Name,
    string Version,
    string Channel,
    string SourceRepository,
    string SourceTag,
    string Platform,
    string Architecture,
    string ArchieVersionRange,
    string ProtocolVersion,
    Uri AssetUrl,
    long CompressedBytes,
    long ExpandedBytes,
    int EntryCount,
    string Sha256,
    string SigningKeyId,
    string PackageSignature,
    IReadOnlyList<string> Capabilities,
    ScannerPermissions Permissions,
    string License,
    Uri ReleaseNotesUrl,
    bool Revoked);

public sealed record ScannerPackageMetadata(
    string SchemaVersion,
    string Id,
    string Version,
    string SourceRepository,
    string SourceTag,
    string Platform,
    string Architecture,
    string ArchieVersionRange,
    string ProtocolVersion,
    long ExpandedBytes,
    int EntryCount,
    IReadOnlyList<string> Capabilities,
    ScannerPermissions Permissions,
    string License,
    string? PublisherKeyId);

public sealed record InstalledScannerPackage(
    string Id,
    string Version,
    string Sha256,
    ScannerPackageOrigin Origin,
    ScannerTrustState Trust,
    string? CatalogVersion,
    string? VerifiedKeyId,
    DateTimeOffset InstalledAt,
    long ExpandedBytes);

public sealed record ActiveScanner(string Id, string Version, string Sha256);

public sealed record InstalledScannerReceipt(
    string SchemaVersion,
    IReadOnlyList<InstalledScannerPackage> Packages,
    IReadOnlyList<ActiveScanner> Active);

public sealed record ScannerSearchRoot(
    string Path,
    string? PackageSha256,
    ScannerPackageOrigin? Origin);

public sealed record DetectedTechnologyStack(
    string Id,
    string Name,
    string? RecommendedScannerId,
    IReadOnlyList<string> Signals);

public sealed record ScannerCoverage(
    DetectedTechnologyStack Stack,
    ScannerCoverageState State,
    string? ScannerId,
    string? ScannerVersion);
