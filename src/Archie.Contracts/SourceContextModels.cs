using Entorn.Scanner.Contracts;

namespace Archie.Contracts;

public sealed record SourceOwnership(
    string ScannerId,
    string ScannerVersion,
    string Path,
    string OwnerCandidateKey,
    string OwnerId,
    SourceOwnershipKind OwnershipKind,
    Confidence Confidence,
    Resolution ClaimResolution,
    Resolution Resolution,
    string DerivationRule);

public sealed record SourceContextSnapshot(
    string SchemaVersion,
    RepositoryRevision Repository,
    string ScanConfigurationDigest,
    IReadOnlyList<ScannerIdentity> Scanners,
    string ObservationBundleDigest,
    string GraphDigest,
    IReadOnlyList<SourceOwnership> Ownership);
