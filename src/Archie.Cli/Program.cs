using System.Text.Json;
using System.Security.Cryptography;
using Archie.Cli;
using Archie.Contracts;
using Archie.Core;
using Archie.Runner;

if (args.FirstOrDefault() == "__scanner-supervisor") return await ScannerSupervisor.RunAsync(args[1..]);
if (args.FirstOrDefault() == "__scanner-bootstrap") return await ScannerSupervisor.RunBootstrapAsync(args[1..]);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
return await RunAsync(args, cancellation.Token);

static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0] is "-h" or "--help") return Usage();
    try
    {
        return args[0] switch
        {
            "open" => await OpenAsync(args[1..], cancellationToken),
            "mcp" => await McpAsync(args[1..], cancellationToken),
            "scan" => await ScanAsync(args[1..], cancellationToken),
            "scanner" => await ScannerCommands.RunAsync(args[1..], cancellationToken),
            "reconcile" => await ReconcileAsync(args[1..], cancellationToken),
            "compose" => await ComposeAsync(args[1..], cancellationToken),
            "validate" => Validate(args[1..]),
            "explain" => Explain(args[1..]),
            "diff" => Diff(args[1..]),
            _ => Usage($"Unknown command '{args[0]}'.")
        };
    }
    catch (OperationCanceledException) { return 130; }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
    catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or HttpRequestException or ArtifactValidationException or GraphValidationException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task<int> McpAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0) return Usage("mcp requires a repository.");
    var repositoryRoot = Path.GetFullPath(args[0]);
    var stateDirectory = Option(args, "--state-directory");
    RejectUnknownOptions(args[1..], new HashSet<string>(["--state-directory"], StringComparer.Ordinal), "mcp");
    return await McpServerHost.RunAsync(LocalRepositoryState.Resolve(repositoryRoot, stateDirectory), cancellationToken);
}

static async Task<int> OpenAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0) return Usage("open requires a repository.");
    var repositoryRoot = Path.GetFullPath(args[0]);
    var overlayPath = Option(args, "--overlay");
    var stateDirectory = Option(args, "--state-directory");
    var timeout = Option(args, "--scanner-timeout");
    var asOf = Option(args, "--as-of");
    var noBrowser = args.Contains("--no-browser", StringComparer.Ordinal);
    RejectOpenArguments(args[1..]);

    var state = LocalRepositoryState.Resolve(repositoryRoot, stateDirectory);
    var scanArguments = new List<string>
    {
        repositoryRoot,
        "--observations", state.ObservationsPath,
        "--graph", state.GraphPath,
        "--source-context", state.SourceContextPath
    };
    if (overlayPath is not null) { scanArguments.Add("--overlay"); scanArguments.Add(Path.GetFullPath(overlayPath)); }
    if (timeout is not null) { scanArguments.Add("--scanner-timeout"); scanArguments.Add(timeout); }
    if (asOf is not null) { scanArguments.Add("--as-of"); scanArguments.Add(asOf); }

    var scanExitCode = await ScanAsync(scanArguments.ToArray(), cancellationToken);
    if (scanExitCode != 0) return scanExitCode;
    return await ProductLauncher.RunHostAsync(state, !noBrowser, cancellationToken);
}

static async Task<int> ScanAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0) return Usage("scan requires a repository.");
    var repositoryRoot = Path.GetFullPath(args[0]);
    var observationsPath = Option(args, "--observations") ?? throw new ArgumentException("--observations is required.");
    var graphPath = Option(args, "--graph") ?? throw new ArgumentException("--graph is required.");
    var sourceContextPath = Option(args, "--source-context") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(graphPath))!, "source-context.json");
    await ArtifactPairPublisher.PrepareAsync(observationsPath, graphPath, sourceContextPath, cancellationToken);
    var overlayPath = Option(args, "--overlay");
    var asOf = ParseAsOf(Option(args, "--as-of"));
    var timeout = ParseTimeout(Option(args, "--scanner-timeout"));
    var repositoryId = Option(args, "--repository-id");
    var expectedRevision = Option(args, "--expected-revision");
    var pluginPaths = Options(args, "--plugin-path").ToArray();
    IReadOnlyList<ScannerSearchRoot> searchRoots;
    if (pluginPaths.Length > 0)
    {
        searchRoots = pluginPaths.Select(path => new ScannerSearchRoot(path, null, null)).ToArray();
    }
    else
    {
        var configured = Environment.GetEnvironmentVariable("AIP_PLUGIN_PATHS");
        if (!string.IsNullOrWhiteSpace(configured))
            searchRoots = configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => new ScannerSearchRoot(path, null, null))
                .ToArray();
        else
            searchRoots = await InstalledScannerStore.CreateDefault().ResolveActiveAsync(cancellationToken);
    }
    RejectUnknownOptions(args[1..], new HashSet<string>(["--observations", "--graph", "--source-context", "--overlay", "--plugin-path", "--scanner-timeout", "--as-of", "--repository-id", "--expected-revision"], StringComparer.Ordinal), "scan");

    var limits = ScannerRuntimeLimits.Default with { ExecutionTimeout = timeout };
    var inventory = await RepositoryFileInventory.CreateAsync(repositoryRoot, cancellationToken);
    var manifests = await new ScannerManifestLoader().DiscoverAsync(searchRoots, inventory, cancellationToken);
    var stackDetector = new TechnologyStackDetector();
    var coverage = stackDetector.AssessCoverage(stackDetector.Detect(inventory), manifests);
    var coverageDiagnostics = stackDetector.ToDiagnostics(coverage);
    var excluded = new HashSet<string>(
        [Path.GetFullPath(observationsPath), Path.GetFullPath(graphPath), Path.GetFullPath(sourceContextPath)],
        StringComparer.Ordinal);
    var repository = await new RepositoryContextFactory().CreateAsync(repositoryRoot, excluded, inventory, cancellationToken);
    if (expectedRevision is not null && repository.Revision != expectedRevision)
        throw new InvalidDataException($"Repository HEAD does not match expected revision '{expectedRevision}'.");
    if (repositoryId is not null)
    {
        if (string.IsNullOrWhiteSpace(repositoryId) || repositoryId.Any(char.IsWhiteSpace))
            throw new ArgumentException("--repository-id must be a non-empty ID without whitespace.");
        repository = repository with { RepositoryId = repositoryId };
    }
    var reuse = await TryReuseScanAsync(observationsPath, graphPath, sourceContextPath, repository, manifests, limits, cancellationToken);
    ObservationBundle bundle;
    IReadOnlyList<SourceOwnershipClaim> sourceOwnership;
    var printedDiagnostics = new HashSet<string>(StringComparer.Ordinal);
    if (reuse is null)
    {
        var scan = await new ScanCoordinator().ScanAsync(
            repositoryRoot, repository, manifests, coverageDiagnostics, limits, cancellationToken);
        foreach (var diagnostic in scan.Diagnostics)
        {
            WriteDiagnostic(diagnostic);
            printedDiagnostics.Add(diagnostic.Id);
        }
        if (!scan.Succeeded || scan.Bundle is null) return 1;
        bundle = scan.Bundle;
        sourceOwnership = scan.SourceOwnership;
    }
    else
    {
        bundle = reuse.Value.Bundle;
        sourceOwnership = reuse.Value.SourceOwnership;
        Console.WriteLine($"Reused {manifests.Count} scanner result(s) from {observationsPath}.");
    }

    var observationBytes = ContractJson.WriteObservationBundle(bundle);
    ArtifactSchemaValidator.Validate(observationBytes);
    ArchitectureOverlay? overlay = overlayPath is null ? null : await ReadOverlayAsync(overlayPath, cancellationToken);
    var result = new Reconciler().Reconcile(bundle, overlay, asOf);
    var graphBytes = ContractJson.WriteGraphSnapshot(result.Snapshot);
    ArtifactSchemaValidator.Validate(graphBytes);
    var sourceContext = SourceContextBuilder.Build(bundle, result.Snapshot, graphBytes, result.CandidateResolutions, sourceOwnership);
    var sourceContextBytes = ContractJson.WriteSourceContext(sourceContext);
    ArtifactSchemaValidator.Validate(sourceContextBytes);
    await ArtifactPairPublisher.PublishAsync(
        observationsPath, observationBytes, graphPath, graphBytes, sourceContextPath, sourceContextBytes, cancellationToken);
    Console.WriteLine($"Discovered {manifests.Count} scanner(s); wrote {bundle.Observations.Count} observations to {observationsPath}.");
    Console.WriteLine($"Wrote {result.Snapshot.Nodes.Count} nodes and {result.Snapshot.Edges.Count} edges to {graphPath}.");
    foreach (var diagnostic in result.Diagnostics.Where(diagnostic => printedDiagnostics.Add(diagnostic.Id))) WriteDiagnostic(diagnostic);
    return result.Diagnostics.Any(item => item.Severity == "error") ? 1 : 0;
}

static async Task<int> ReconcileAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0) return Usage("reconcile requires an observation bundle.");
    var observationsPath = args[0];
    var graphPath = Option(args, "--graph") ?? throw new ArgumentException("--graph is required.");
    var overlayPath = Option(args, "--overlay");
    var asOf = ParseAsOf(Option(args, "--as-of"));
    RejectUnknownOptions(args[1..], new HashSet<string>(["--graph", "--overlay", "--as-of"], StringComparer.Ordinal), "reconcile");

    var observationBytes = await File.ReadAllBytesAsync(observationsPath, cancellationToken);
    ArtifactSchemaValidator.Validate(observationBytes);
    using var observationStream = new MemoryStream(observationBytes, writable: false);
    var bundle = ContractJson.ReadObservationBundle(observationStream);
    ArchitectureOverlay? overlay = null;
    if (overlayPath is not null)
    {
        overlay = await ReadOverlayAsync(overlayPath, cancellationToken);
    }

    var result = new Reconciler().Reconcile(bundle, overlay, asOf);
    var graphBytes = ContractJson.WriteGraphSnapshot(result.Snapshot);
    ArtifactSchemaValidator.Validate(graphBytes);
    await WriteAtomicallyAsync(graphPath, graphBytes, cancellationToken);
    Console.WriteLine($"Wrote {result.Snapshot.Nodes.Count} nodes and {result.Snapshot.Edges.Count} edges to {graphPath}.");
    foreach (var diagnostic in result.Diagnostics) Console.Error.WriteLine($"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.Message}");
    return result.Diagnostics.Any(item => item.Severity == "error") ? 1 : 0;
}

static async Task<int> ComposeAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0) return Usage("compose requires a composition manifest.");
    var manifestPath = Path.GetFullPath(args[0]);
    var graphPath = Option(args, "--graph") ?? throw new ArgumentException("--graph is required.");
    var asOf = ParseAsOf(Option(args, "--as-of"));
    RejectUnknownOptions(args[1..], new HashSet<string>(["--graph", "--as-of"], StringComparer.Ordinal), "compose");

    if (new FileInfo(manifestPath).Length > 1024 * 1024)
        throw new InvalidDataException("Composition manifest exceeds 1 MiB.");
    var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
    if (ArtifactSchemaValidator.Validate(manifestBytes) != "composition-manifest/v1")
        throw new InvalidDataException("compose requires a composition-manifest/v1 artifact.");
    using var manifestStream = new MemoryStream(manifestBytes, writable: false);
    var manifest = ContractJson.ReadCompositionManifest(manifestStream);
    if (manifest.Inputs.Select(input => input.RepositoryId).Distinct(StringComparer.Ordinal).Count() != manifest.Inputs.Count)
        throw new InvalidDataException("Composition manifest repository IDs must be unique.");

    var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
    var bundles = new List<ObservationBundle>(manifest.Inputs.Count);
    foreach (var input in manifest.Inputs.OrderBy(item => item.RepositoryId, StringComparer.Ordinal))
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observationPath = Path.GetFullPath(input.ObservationPath, manifestDirectory);
        if (new FileInfo(observationPath).Length > 128 * 1024 * 1024)
            throw new InvalidDataException($"Observation bundle for repository '{input.RepositoryId}' exceeds 128 MiB.");
        var bytes = await File.ReadAllBytesAsync(observationPath, cancellationToken);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (digest != input.ObservationBundleDigest)
            throw new InvalidDataException($"Observation bundle digest does not match for repository '{input.RepositoryId}'.");
        if (ArtifactSchemaValidator.Validate(bytes) != "observations/v1")
            throw new InvalidDataException($"Composition input for repository '{input.RepositoryId}' is not observations/v1.");
        using var stream = new MemoryStream(bytes, writable: false);
        var bundle = ContractJson.ReadObservationBundle(stream);
        if (bundle.Repository.RepositoryId != input.RepositoryId)
            throw new InvalidDataException($"Observation bundle repository identity does not match '{input.RepositoryId}'.");
        bundles.Add(bundle);
    }

    var result = new Reconciler().ReconcileProject(manifest.ProjectId, bundles, null, asOf);
    var graphBytes = ContractJson.WriteGraphSnapshot(result.Snapshot);
    ArtifactSchemaValidator.Validate(graphBytes);
    await WriteAtomicallyAsync(graphPath, graphBytes, cancellationToken);
    Console.WriteLine($"Composed {bundles.Count} repositories into {result.Snapshot.Nodes.Count} nodes and {result.Snapshot.Edges.Count} edges at {graphPath}.");
    foreach (var diagnostic in result.Diagnostics) WriteDiagnostic(diagnostic);
    return result.Diagnostics.Any(item => item.Severity == "error") ? 1 : 0;
}

static int Validate(string[] args)
{
    if (args.Length != 1) return Usage("validate requires exactly one artifact path.");
    var bytes = File.ReadAllBytes(args[0]);
    var version = ArtifactSchemaValidator.Validate(bytes);
    using var stream = new MemoryStream(bytes, writable: false);
    switch (version)
    {
        case "architecture/v1": GraphValidator.Validate(ContractJson.ReadGraphSnapshot(stream)); break;
        case "observations/v1": ObservationValidator.Validate(ContractJson.ReadObservationBundle(stream)); break;
        case "composition-manifest/v1": ContractJson.ReadCompositionManifest(stream); break;
        case "source-context/v1": ContractJson.ReadSourceContext(stream); break;
        case "overlay/v1": OverlayValidator.Validate(ContractJson.ReadOverlay(stream)); break;
    }
    Console.WriteLine($"Valid {version} artifact: {args[0]}");
    return 0;
}

static int Explain(string[] args)
{
    if (args.Length != 2) return Usage("explain requires a graph path and node or edge ID.");
    var bytes = File.ReadAllBytes(args[0]);
    if (ArtifactSchemaValidator.Validate(bytes) != "architecture/v1") throw new InvalidDataException("explain requires an architecture/v1 graph.");
    using var stream = new MemoryStream(bytes, writable: false);
    var graph = ContractJson.ReadGraphSnapshot(stream);
    GraphValidator.Validate(graph);
    var id = args[1];
    object? subject = graph.Nodes.FirstOrDefault(item => item.Id == id);
    subject ??= graph.Edges.FirstOrDefault(item => item.Id == id);
    if (subject is null) throw new InvalidDataException($"Subject '{id}' does not exist in the graph.");
    var evidenceIds = subject switch { GraphNode node => node.EvidenceIds, GraphEdge edge => edge.EvidenceIds, _ => [] };
    var explanation = new
    {
        snapshotId = graph.Id,
        governanceDate = graph.GovernanceDate,
        subject,
        evidence = graph.Evidence.Where(item => evidenceIds.Contains(item.Id, StringComparer.Ordinal)).ToArray(),
        mergeDecisions = graph.MergeDecisions.Where(item => item.CandidateIds.Contains(id, StringComparer.Ordinal)).ToArray(),
        diagnostics = graph.Diagnostics.Where(item => item.SubjectId == id).ToArray()
    };
    Console.WriteLine(JsonSerializer.Serialize(explanation, ContractJson.Options));
    return 0;
}

static int Diff(string[] args)
{
    if (args.Length < 2) return Usage("diff requires before and after graph paths.");
    var format = Option(args, "--format") ?? "text";
    RejectUnknownOptions(args[2..], new HashSet<string>(["--format"], StringComparer.Ordinal), "diff");
    if (format is not ("text" or "json")) throw new ArgumentException("--format must be text or json.");
    var before = ReadGraph(args[0]);
    var after = ReadGraph(args[1]);
    var diff = new GraphDiffer().Compare(before, after);
    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(diff, ContractJson.Options));
        return 0;
    }

    Console.WriteLine(diff.HasChanges ? "Graph snapshots differ." : "Graph snapshots are semantically unchanged.");
    Console.WriteLine($"Composition: {(diff.CompositionChanged ? "changed" : "unchanged")}");
    WriteChanges("Nodes", diff.AddedNodes.Select(item => item.Id), diff.RemovedNodes.Select(item => item.Id), diff.ChangedNodes.Select(item => item.Id));
    WriteChanges("Edges", diff.AddedEdges.Select(item => item.Id), diff.RemovedEdges.Select(item => item.Id), diff.ChangedEdges.Select(item => item.Id));
    WriteChanges("Evidence", diff.AddedEvidence.Select(item => item.Id), diff.RemovedEvidence.Select(item => item.Id), diff.ChangedEvidence.Select(item => item.Id));
    WriteList("Unresolved added", diff.AddedUnresolved);
    WriteList("Resolved", diff.Resolved);
    foreach (var scanner in diff.ScannerVersionChanges)
        Console.WriteLine($"Scanner {scanner.Id}: {Versions(scanner.BeforeVersions)} -> {Versions(scanner.AfterVersions)}");
    return 0;
}

static async Task<(ObservationBundle Bundle, IReadOnlyList<SourceOwnershipClaim> SourceOwnership)?> TryReuseScanAsync(
    string observationsPath,
    string graphPath,
    string sourceContextPath,
    RepositoryRevision repository,
    IReadOnlyList<DiscoveredScanner> scanners,
    ScannerRuntimeLimits limits,
    CancellationToken cancellationToken)
{
    if (!File.Exists(observationsPath) || !File.Exists(graphPath) || !File.Exists(sourceContextPath)) return null;
    try
    {
        var bytes = await File.ReadAllBytesAsync(observationsPath, cancellationToken);
        if (ArtifactSchemaValidator.Validate(bytes) != "observations/v1") return null;
        using var stream = new MemoryStream(bytes, writable: false);
        var previous = ContractJson.ReadObservationBundle(stream);
        ObservationValidator.Validate(previous);
        var identities = scanners.Select(item => new ScannerIdentity(item.Manifest.Id, item.Manifest.Version)).ToArray();
        if (previous.Source != ObservationSource.Scanner ||
            previous.Repository.RepositoryId != repository.RepositoryId ||
            previous.Repository.ContentDigest != repository.ContentDigest ||
            previous.ScanConfigurationDigest != ScanCoordinator.ConfigurationDigest(scanners, limits) ||
            !previous.Scanners.SequenceEqual(identities)) return null;
        var graphBytes = await File.ReadAllBytesAsync(graphPath, cancellationToken);
        if (ArtifactSchemaValidator.Validate(graphBytes) != "architecture/v1") return null;
        using var graphStream = new MemoryStream(graphBytes, writable: false);
        var graph = ContractJson.ReadGraphSnapshot(graphStream);
        GraphValidator.Validate(graph);
        var sourceBytes = await File.ReadAllBytesAsync(sourceContextPath, cancellationToken);
        if (ArtifactSchemaValidator.Validate(sourceBytes) != "source-context/v1") return null;
        using var sourceStream = new MemoryStream(sourceBytes, writable: false);
        var source = ContractJson.ReadSourceContext(sourceStream);
        SourceContextValidator.Validate(source, graph, graphBytes);
        if (source.ScanConfigurationDigest != previous.ScanConfigurationDigest || !source.Scanners.SequenceEqual(previous.Scanners)) return null;
        var ownership = source.Ownership.Select(item => new SourceOwnershipClaim(
            item.ScannerId, item.ScannerVersion, item.Path, item.OwnerCandidateKey,
            item.OwnershipKind, item.Confidence, item.ClaimResolution, item.DerivationRule)).ToArray();
        return (previous with { Repository = repository }, ownership);
    }
    catch (Exception exception) when (exception is JsonException or InvalidDataException or ArtifactValidationException)
    {
        return null;
    }
}

static GraphSnapshot ReadGraph(string path)
{
    var bytes = File.ReadAllBytes(path);
    if (ArtifactSchemaValidator.Validate(bytes) != "architecture/v1")
        throw new InvalidDataException($"diff requires architecture/v1 graphs: {path}");
    using var stream = new MemoryStream(bytes, writable: false);
    var graph = ContractJson.ReadGraphSnapshot(stream);
    GraphValidator.Validate(graph);
    return graph;
}

static void WriteChanges(string label, IEnumerable<string> added, IEnumerable<string> removed, IEnumerable<string> changed)
{
    WriteList($"{label} added", added);
    WriteList($"{label} removed", removed);
    WriteList($"{label} changed", changed);
}

static void WriteList(string label, IEnumerable<string> values)
{
    var ordered = values.Order(StringComparer.Ordinal).ToArray();
    Console.WriteLine($"{label} ({ordered.Length}):{(ordered.Length == 0 ? string.Empty : $" {string.Join(", ", ordered)}")}");
}

static string Versions(IReadOnlyList<string> versions) => versions.Count == 0 ? "none" : string.Join(",", versions);

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0) return null;
    if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"{name} requires a value.");
    return args[index + 1];
}

static IEnumerable<string> Options(string[] args, string name)
{
    for (var index = 0; index < args.Length; index++)
        if (args[index] == name)
        {
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{name} requires a value.");
            yield return args[index + 1];
        }
}

static void RejectUnknownOptions(string[] args, IReadOnlySet<string> known, string command)
{
    for (var index = 0; index < args.Length; index += 2)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal) || !known.Contains(args[index]) || index + 1 >= args.Length)
            throw new ArgumentException($"Invalid {command} argument '{args[index]}'.");
    }
}

static void RejectOpenArguments(string[] args)
{
    var valued = new HashSet<string>(["--overlay", "--state-directory", "--scanner-timeout", "--as-of"], StringComparer.Ordinal);
    for (var index = 0; index < args.Length; index++)
    {
        if (args[index] == "--no-browser") continue;
        if (!valued.Contains(args[index])) throw new ArgumentException($"Invalid open argument '{args[index]}'.");
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{args[index - 1]} requires a value.");
    }
}

static DateOnly ParseAsOf(string? value) => value is null ? DateOnly.FromDateTime(DateTime.UtcNow) :
    DateOnly.TryParseExact(value, "yyyy-MM-dd", out var parsed) ? parsed : throw new ArgumentException("--as-of must be YYYY-MM-DD.");

static TimeSpan ParseTimeout(string? value)
{
    if (value is null) return ScannerRuntimeLimits.Default.ExecutionTimeout;
    TimeSpan timeout;
    if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase) && double.TryParse(value[..^2], out var milliseconds)) timeout = TimeSpan.FromMilliseconds(milliseconds);
    else if (value.EndsWith('s') && double.TryParse(value[..^1], out var seconds)) timeout = TimeSpan.FromSeconds(seconds);
    else if (value.EndsWith('m') && double.TryParse(value[..^1], out var minutes)) timeout = TimeSpan.FromMinutes(minutes);
    else if (value.EndsWith('h') && double.TryParse(value[..^1], out var hours)) timeout = TimeSpan.FromHours(hours);
    else if (!TimeSpan.TryParse(value, out timeout)) throw new ArgumentException("--scanner-timeout must be a duration such as 30s, 10m, or 01:00:00.");
    if (timeout <= TimeSpan.Zero || timeout > ScannerRuntimeLimits.MaximumExecutionTimeout)
        throw new ArgumentException("--scanner-timeout must be greater than zero and no more than 60 minutes.");
    return timeout;
}

static async Task<ArchitectureOverlay> ReadOverlayAsync(string path, CancellationToken cancellationToken)
{
    var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
    ArtifactSchemaValidator.Validate(bytes);
    using var stream = new MemoryStream(bytes, writable: false);
    return ContractJson.ReadOverlay(stream);
}

static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
    Directory.CreateDirectory(directory);
    var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    try
    {
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
}

static void WriteDiagnostic(Diagnostic diagnostic) =>
    Console.Error.WriteLine(JsonSerializer.Serialize(
        ObservationRedactor.SanitizeDiagnostic(diagnostic),
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

static int Usage(string? error = null)
{
    if (error is not null) Console.Error.WriteLine(error);
    Console.Error.WriteLine("Usage:\n  archie open <repository> [--overlay <path>] [--state-directory <path>] [--no-browser] [--scanner-timeout <duration>] [--as-of YYYY-MM-DD]\n  archie mcp <repository> [--state-directory <path>]\n  archie scan <repository> --observations <path> --graph <path> [--source-context <path>] [--overlay <path>] [--plugin-path <path>...] [--scanner-timeout <duration>] [--repository-id <id>] [--expected-revision <sha>] [--as-of YYYY-MM-DD]\n  archie scanner add <id> [--version <version>]\n  archie scanner add --local <archive.tar.gz>\n  archie scanner search [<query>]\n  archie scanner info <id>\n  archie scanner list\n  archie scanner recommend [<repository>]\n  archie scanner update <id> [--version <version>] [--from catalog]\n  archie scanner use <id> <version> [<sha256>]\n  archie scanner remove <id> [<version> [<sha256>]]\n  archie reconcile <observations> --graph <path> [--overlay <path>] [--as-of YYYY-MM-DD]\n  archie compose <manifest> --graph <path> [--as-of YYYY-MM-DD]\n  archie validate <artifact>\n  archie explain <graph> <node-or-edge-id>\n  archie diff <before-graph> <after-graph> [--format text|json]");
    return error is null ? 0 : 2;
}
