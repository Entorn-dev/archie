namespace Archie.Contracts;

public sealed record CompositionManifestEntry(
    string RepositoryId,
    string ObservationPath,
    string ObservationBundleDigest);

public sealed record CompositionManifest(
    string SchemaVersion,
    string ProjectId,
    IReadOnlyList<CompositionManifestEntry> Inputs);
