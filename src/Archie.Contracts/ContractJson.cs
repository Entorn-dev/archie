using System.Text.Json;
using Entorn.Scanner.Contracts;

namespace Archie.Contracts;

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = ScannerContractJson.Options;

    public static GraphSnapshot ReadGraphSnapshot(Stream stream)
    {
        return JsonSerializer.Deserialize<GraphSnapshot>(stream, Options)
            ?? throw new JsonException("The canonical graph artifact is empty.");
    }

    public static ObservationBundle ReadObservationBundle(Stream stream) =>
        ScannerContractJson.ReadObservationBundle(stream);

    public static CompositionManifest ReadCompositionManifest(Stream stream) =>
        JsonSerializer.Deserialize<CompositionManifest>(stream, Options)
            ?? throw new JsonException("The composition manifest is empty.");

    public static SourceContextSnapshot ReadSourceContext(Stream stream) =>
        JsonSerializer.Deserialize<SourceContextSnapshot>(stream, Options)
            ?? throw new JsonException("The source-context artifact is empty.");

    public static ArchitectureOverlay ReadOverlay(Stream stream) =>
        JsonSerializer.Deserialize<ArchitectureOverlay>(stream, Options)
            ?? throw new JsonException("The architecture overlay is empty.");

    public static SignedScannerCatalog ReadSignedScannerCatalog(Stream stream) =>
        JsonSerializer.Deserialize<SignedScannerCatalog>(stream, Options)
            ?? throw new JsonException("The signed scanner catalog is empty.");

    public static ScannerCatalog ReadScannerCatalog(Stream stream) =>
        JsonSerializer.Deserialize<ScannerCatalog>(stream, Options)
            ?? throw new JsonException("The scanner catalog is empty.");

    public static ScannerPackageMetadata ReadScannerPackage(Stream stream) =>
        JsonSerializer.Deserialize<ScannerPackageMetadata>(stream, Options)
            ?? throw new JsonException("The scanner package metadata is empty.");

    public static InstalledScannerReceipt ReadInstalledScanners(Stream stream) =>
        JsonSerializer.Deserialize<InstalledScannerReceipt>(stream, Options)
            ?? throw new JsonException("The installed-scanners receipt is empty.");

    public static byte[] WriteGraphSnapshot(GraphSnapshot snapshot)
    {
        var normalized = snapshot with
        {
            Composition = snapshot.Composition with
            {
                Inputs = snapshot.Composition.Inputs.OrderBy(input => input.Repository.RepositoryId, StringComparer.Ordinal).ToArray()
            },
            Nodes = snapshot.Nodes.Select(Normalize).OrderBy(node => node.Id, StringComparer.Ordinal).ToArray(),
            Edges = snapshot.Edges.Select(Normalize).OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray(),
            Evidence = snapshot.Evidence.Select(Normalize).OrderBy(evidence => evidence.Id, StringComparer.Ordinal).ToArray(),
            MergeDecisions = snapshot.MergeDecisions
                .Select(item => item with { CandidateIds = item.CandidateIds.Order(StringComparer.Ordinal).ToArray() })
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToArray(),
            Diagnostics = snapshot.Diagnostics.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            Redactions = snapshot.Redactions.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray()
        };

        return JsonSerializer.SerializeToUtf8Bytes(normalized, Options);
    }

    public static byte[] WriteObservationBundle(ObservationBundle bundle) =>
        ScannerContractJson.WriteObservationBundle(bundle);

    public static byte[] WriteCompositionManifest(CompositionManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest with
        {
            Inputs = manifest.Inputs.OrderBy(input => input.RepositoryId, StringComparer.Ordinal).ToArray()
        }, Options);

    public static byte[] WriteSourceContext(SourceContextSnapshot snapshot)
    {
        var normalized = snapshot with
        {
            Scanners = snapshot.Scanners.OrderBy(scanner => scanner.Id, StringComparer.Ordinal).ToArray(),
            Ownership = snapshot.Ownership
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.OwnerId, StringComparer.Ordinal)
                .ThenBy(item => item.ScannerId, StringComparer.Ordinal)
                .ThenBy(item => item.OwnerCandidateKey, StringComparer.Ordinal)
                .ToArray()
        };
        return JsonSerializer.SerializeToUtf8Bytes(normalized, Options);
    }

    public static byte[] WriteOverlay(ArchitectureOverlay overlay)
    {
        var normalized = overlay with
        {
            Entries = overlay.Entries.Select(Normalize).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray()
        };
        return JsonSerializer.SerializeToUtf8Bytes(normalized, Options);
    }

    public static byte[] WriteInstalledScanners(InstalledScannerReceipt receipt)
    {
        var normalized = receipt with
        {
            Packages = receipt.Packages
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Version, StringComparer.Ordinal)
                .ThenBy(item => item.Sha256, StringComparer.Ordinal)
                .ToArray(),
            Active = receipt.Active
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Version, StringComparer.Ordinal)
                .ThenBy(item => item.Sha256, StringComparer.Ordinal)
                .ToArray()
        };
        return JsonSerializer.SerializeToUtf8Bytes(normalized, Options);
    }

    public static byte[] WriteScannerCatalog(ScannerCatalog catalog)
    {
        var normalized = catalog with
        {
            Releases = catalog.Releases
                .Select(item => item with { Capabilities = item.Capabilities.Order(StringComparer.Ordinal).ToArray() })
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Version, StringComparer.Ordinal)
                .ThenBy(item => item.Sha256, StringComparer.Ordinal)
                .ToArray()
        };
        return JsonSerializer.SerializeToUtf8Bytes(normalized, Options);
    }

    private static GraphNode Normalize(GraphNode node) => node with
    {
        Aliases = node.Aliases.Order(StringComparer.Ordinal).ToArray(),
        Properties = SortProperties(node.Properties),
        EvidenceIds = node.EvidenceIds.Order(StringComparer.Ordinal).ToArray()
    };

    private static GraphEdge Normalize(GraphEdge edge) => edge with
    {
        Provenance = edge.Provenance.OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray(),
        Properties = SortProperties(edge.Properties),
        EvidenceIds = edge.EvidenceIds.Order(StringComparer.Ordinal).ToArray()
    };

    private static Evidence Normalize(Evidence evidence) => evidence with
    {
        Properties = SortProperties(evidence.Properties)
    };

    private static OverlayEntry Normalize(OverlayEntry entry) => entry switch
    {
        ManualNodeOverlay node => node with { Node = Normalize(node.Node) },
        ManualRelationshipOverlay relationship => relationship with { Properties = SortProperties(relationship.Properties) },
        AnnotationOverlay annotation => annotation with
        {
            Aliases = annotation.Aliases.Order(StringComparer.Ordinal).ToArray(),
            Properties = SortProperties(annotation.Properties)
        },
        MergeOverlay merge => merge with { CandidateKeys = merge.CandidateKeys.Order(StringComparer.Ordinal).ToArray() },
        _ => entry
    };

    private static IReadOnlyDictionary<string, JsonElement> SortProperties(IReadOnlyDictionary<string, JsonElement> properties) =>
        new SortedDictionary<string, JsonElement>(
            properties.ToDictionary(pair => pair.Key, pair => NormalizeJson(pair.Value), StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static JsonElement NormalizeJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => JsonSerializer.SerializeToElement(
            value.EnumerateObject().ToDictionary(
                property => property.Name,
                property => NormalizeJson(property.Value),
                StringComparer.Ordinal)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
        JsonValueKind.Array => JsonSerializer.SerializeToElement(value.EnumerateArray().Select(NormalizeJson).ToArray()),
        _ => value.Clone()
    };

}
