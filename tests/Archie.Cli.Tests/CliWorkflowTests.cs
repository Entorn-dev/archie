using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Archie.Cli;
using Archie.Contracts;
using Xunit;

namespace Archie.Cli.Tests;

public sealed class CliWorkflowTests
{
    [Fact]
    public async Task LocalUnsignedPackageInstallsRunsListsAndRemovesWithoutRepositoryWritesOrNetwork()
    {
        using var temporary = new TemporaryDirectory();
        var dataHome = Path.Combine(temporary.Path, "data");
        var archive = Path.Combine(temporary.Path, "fake-scanner.tar.gz");
        await using (var output = File.Create(archive))
        await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            TarFile.CreateFromDirectory(
                Path.Combine(RepositoryRoot(), "tests", "fixtures", "scanner-packages", "fake"),
                gzip,
                includeBaseDirectory: false);

        var repository = Path.Combine(temporary.Path, "repository");
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "PROJECT_BRIEF.md"), "Installed package fixture.");
        await File.WriteAllTextAsync(Path.Combine(repository, "App.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(repository, "composer.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(repository, "package.json"), "{}");
        var observationDirectory = Path.Combine(repository, "tests", "fixtures", "observations");
        Directory.CreateDirectory(observationDirectory);
        File.Copy(Fixture("observations", "book-retail.authored.json"),
            Path.Combine(observationDirectory, "book-retail.authored.json"));
        await RunGit(repository, "init", "--quiet");
        await RunGit(repository, "add", ".");
        await RunGit(repository, "-c", "user.name=Archie Tests", "-c", "user.email=archie@example.invalid",
            "commit", "--quiet", "-m", "fixture");
        var statusBefore = await RunGit(repository, "status", "--porcelain");

        var add = await RunWithDataHome(dataHome, "scanner", "add", "--local", archive);
        var receiptPath = Path.Combine(dataHome, "archie", "scanners", "installed.json");
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
        var package = receipt["packages"]!.AsArray().Single()!;
        var digest = package["sha256"]!.GetValue<string>();
        await using var archiveStream = File.OpenRead(archive);
        var expectedDigest = Convert.ToHexStringLower(await SHA256.HashDataAsync(archiveStream));
        var packagePath = Path.Combine(dataHome, "archie", "scanners", "packages",
            "archie.fake-book-retail", "1.0.0", digest);

        Assert.Equal(0, add.ExitCode);
        Assert.Contains("unsigned and unverified executable code", add.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("origin=local-unsigned", add.Output, StringComparison.Ordinal);
        Assert.Contains("trust=local-unsigned", add.Output, StringComparison.Ordinal);
        Assert.Contains($"sha256={digest}", add.Output, StringComparison.Ordinal);
        Assert.Equal(expectedDigest, digest);
        Assert.True(File.Exists(Path.Combine(packagePath, "scanner.json")));
        Assert.Single(receipt["active"]!.AsArray());
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.GetDirectoryName(receiptPath)!),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));

        var list = await RunWithDataHome(dataHome, "scanner", "list");
        Assert.Equal(0, list.ExitCode);
        Assert.Contains($"* archie.fake-book-retail 1.0.0 origin=local-unsigned trust=local-unsigned sha256={digest}",
            list.Output, StringComparison.Ordinal);

        var use = await RunWithDataHome(dataHome, "scanner", "use", "archie.fake-book-retail", "1.0.0", digest);
        Assert.Equal(0, use.ExitCode);
        Assert.Contains($"Activated archie.fake-book-retail 1.0.0 sha256={digest}", use.Output, StringComparison.Ordinal);

        var recommend = await RunWithDataHome(dataHome, "scanner", "recommend", repository);
        Assert.Equal(0, recommend.ExitCode);
        Assert.Contains("missing .NET: archie.dotnet; install with archie scanner add archie.dotnet", recommend.Output, StringComparison.Ordinal);
        Assert.Contains("unavailable Node.js / TypeScript: no first-party scanner is available", recommend.Output, StringComparison.Ordinal);
        Assert.Contains("missing PHP / Laravel: archie.php; install with archie scanner add archie.php", recommend.Output, StringComparison.Ordinal);

        var observations = Path.Combine(temporary.Path, "observations.json");
        var graph = Path.Combine(temporary.Path, "graph.json");
        var scan = await RunWithDataHome(dataHome,
            "scan", repository,
            "--observations", observations,
            "--graph", graph,
            "--as-of", "2026-09-04");
        Assert.True(scan.ExitCode == 0, scan.Output + scan.Error);
        var persisted = await File.ReadAllTextAsync(observations) + await File.ReadAllTextAsync(graph);
        Assert.Contains("installed local fixture scanner", persisted, StringComparison.Ordinal);
        Assert.Contains("SCANNER_COVERAGE_MISSING", persisted, StringComparison.Ordinal);
        Assert.Contains("archie scanner add archie.dotnet", persisted, StringComparison.Ordinal);
        Assert.Contains("archie scanner add archie.php", persisted, StringComparison.Ordinal);
        Assert.Equal(statusBefore, await RunGit(repository, "status", "--porcelain"));

        var firstConfigurationDigest = JsonNode.Parse(await File.ReadAllTextAsync(observations))!["scanConfigurationDigest"]!.GetValue<string>();
        var reused = await RunWithDataHome(dataHome,
            "scan", repository, "--observations", observations, "--graph", graph, "--as-of", "2026-09-04");
        Assert.Contains("Reused 1 scanner result(s)", reused.Output, StringComparison.Ordinal);

        var changedPackage = Path.Combine(temporary.Path, "changed-package");
        CopyDirectory(Path.Combine(RepositoryRoot(), "tests", "fixtures", "scanner-packages", "fake"), changedPackage);
        await File.AppendAllTextAsync(Path.Combine(changedPackage, "LICENSE"), "changed package digest");
        var changedArchive = Path.Combine(temporary.Path, "fake-scanner-changed.tar.gz");
        await using (var output = File.Create(changedArchive))
        await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            TarFile.CreateFromDirectory(changedPackage, gzip, includeBaseDirectory: false);
        var changedAdd = await RunWithDataHome(dataHome, "scanner", "add", "--local", changedArchive);
        var changedScan = await RunWithDataHome(dataHome,
            "scan", repository, "--observations", observations, "--graph", graph, "--as-of", "2026-09-04");
        var changedConfigurationDigest = JsonNode.Parse(await File.ReadAllTextAsync(observations))!["scanConfigurationDigest"]!.GetValue<string>();
        Assert.Equal(0, changedAdd.ExitCode);
        Assert.Equal(0, changedScan.ExitCode);
        Assert.DoesNotContain("Reused", changedScan.Output, StringComparison.Ordinal);
        Assert.NotEqual(firstConfigurationDigest, changedConfigurationDigest);

        var rollback = await RunWithDataHome(dataHome,
            "scanner", "use", "archie.fake-book-retail", "1.0.0", digest);
        var rollbackScan = await RunWithDataHome(dataHome,
            "scan", repository, "--observations", observations, "--graph", graph, "--as-of", "2026-09-04");
        Assert.Equal(0, rollback.ExitCode);
        Assert.DoesNotContain("Reused", rollbackScan.Output, StringComparison.Ordinal);
        Assert.Equal(firstConfigurationDigest,
            JsonNode.Parse(await File.ReadAllTextAsync(observations))!["scanConfigurationDigest"]!.GetValue<string>());

        var remove = await RunWithDataHome(dataHome, "scanner", "remove", "archie.fake-book-retail");
        var emptyList = await RunWithDataHome(dataHome, "scanner", "list");
        Assert.Equal(0, remove.ExitCode);
        Assert.False(Directory.Exists(packagePath));
        Assert.Equal("No scanners installed.", emptyList.Output.Trim());
    }

    [Fact]
    public async Task DefaultScanWithoutApplicableScannerStopsWithRecommendationsAndPreservesArtifacts()
    {
        using var temporary = new TemporaryDirectory();
        var dataHome = Path.Combine(temporary.Path, "data");
        var repository = Path.Combine(temporary.Path, "repository");
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "App.csproj"), "<Project />");
        await RunGit(repository, "init", "--quiet");
        await RunGit(repository, "add", ".");
        await RunGit(repository, "-c", "user.name=Archie Tests", "-c", "user.email=archie@example.invalid",
            "commit", "--quiet", "-m", "fixture");
        var observations = Path.Combine(temporary.Path, "observations.json");
        var graph = Path.Combine(temporary.Path, "graph.json");
        var sourceContext = Path.Combine(temporary.Path, "source-context.json");
        await File.WriteAllTextAsync(observations, "prior-observations");
        await File.WriteAllTextAsync(graph, "prior-graph");
        await File.WriteAllTextAsync(sourceContext, "prior-source-context");

        var result = await RunWithDataHome(dataHome,
            "scan", repository,
            "--observations", observations,
            "--graph", graph,
            "--source-context", sourceContext);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("SCANNER_COVERAGE_MISSING", result.Error, StringComparison.Ordinal);
        Assert.Contains("archie scanner add archie.dotnet", result.Error, StringComparison.Ordinal);
        Assert.Contains("SCANNER_NOT_FOUND", result.Error, StringComparison.Ordinal);
        Assert.Contains("archie scanner recommend", result.Error, StringComparison.Ordinal);
        Assert.Equal("prior-observations", await File.ReadAllTextAsync(observations));
        Assert.Equal("prior-graph", await File.ReadAllTextAsync(graph));
        Assert.Equal("prior-source-context", await File.ReadAllTextAsync(sourceContext));

        var stateDirectory = Path.Combine(temporary.Path, "open-state");
        var open = await RunWithDataHome(dataHome,
            "open", repository, "--state-directory", stateDirectory, "--no-browser");
        Assert.Equal(1, open.ExitCode);
        Assert.Contains("archie scanner add archie.dotnet", open.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(stateDirectory, "graph.json")));
    }

    [Fact]
    public async Task ReconcileIsByteIdenticalAndExplainNeedsNoHiddenState()
    {
        using var temporary = new TemporaryDirectory();
        var first = Path.Combine(temporary.Path, "first.json");
        var second = Path.Combine(temporary.Path, "second.json");
        var observations = Fixture("observations", "book-retail.authored.json");
        var overlay = Fixture("overlays", "book-retail.overlay.json");

        Assert.Equal(0, (await Run("reconcile", observations, "--overlay", overlay, "--graph", first, "--as-of", "2026-09-01")).ExitCode);
        Assert.Equal(0, (await Run("reconcile", observations, "--overlay", overlay, "--graph", second, "--as-of", "2026-09-01")).ExitCode);
        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(RepositoryRoot(), "tests", "fixtures", "graphs", "book-retail-minimal.json")),
            await File.ReadAllBytesAsync(first));

        var explanation = await Run("explain", first, "edge:checkout-calls-payment");
        Assert.Equal(0, explanation.ExitCode);
        Assert.Contains("governed architecture overlay", explanation.Output, StringComparison.Ordinal);
        Assert.Contains("same relationship kind and canonical endpoints", explanation.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateRejectsInvalidArtifactWithDiagnostic()
    {
        using var temporary = new TemporaryDirectory();
        var invalid = Path.Combine(temporary.Path, "invalid.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(Fixture("observations", "book-retail.authored.json")))!.AsObject();
        root["unexpected"] = true;
        await File.WriteAllTextAsync(invalid, root.ToJsonString());

        var result = await Run("validate", invalid);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does not conform to observations/v1", result.Error, StringComparison.Ordinal);
        Assert.Contains("unexpected", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeValidatesManifestDigestsAndProducesDeterministicProjectGraph()
    {
        using var temporary = new TemporaryDirectory();
        await using var source = File.OpenRead(Fixture("observations", "book-retail.authored.json"));
        var original = ContractJson.ReadObservationBundle(source);
        var first = original with { Repository = original.Repository with { RepositoryId = "repo-a", Revision = "aaaaaaaa" } };
        var second = original with { Repository = original.Repository with { RepositoryId = "repo-b", Revision = "bbbbbbbb" } };
        var firstBytes = ContractJson.WriteObservationBundle(first);
        var secondBytes = ContractJson.WriteObservationBundle(second);
        await File.WriteAllBytesAsync(Path.Combine(temporary.Path, "a.json"), firstBytes);
        await File.WriteAllBytesAsync(Path.Combine(temporary.Path, "b.json"), secondBytes);
        var manifest = new CompositionManifest("composition-manifest/v1", "project:test",
        [
            new("repo-b", "b.json", Digest(secondBytes)),
            new("repo-a", "a.json", Digest(firstBytes))
        ]);
        var manifestPath = Path.Combine(temporary.Path, "manifest.json");
        await File.WriteAllBytesAsync(manifestPath, ContractJson.WriteCompositionManifest(manifest));
        var firstGraph = Path.Combine(temporary.Path, "first-graph.json");
        var secondGraph = Path.Combine(temporary.Path, "second-graph.json");

        var firstResult = await Run("compose", manifestPath, "--graph", firstGraph, "--as-of", "2026-09-03");
        var secondResult = await Run("compose", manifestPath, "--graph", secondGraph, "--as-of", "2026-09-03");
        var graph = JsonNode.Parse(await File.ReadAllTextAsync(firstGraph))!.AsObject();

        Assert.Equal(0, firstResult.ExitCode);
        Assert.Equal(0, secondResult.ExitCode);
        Assert.Equal(await File.ReadAllBytesAsync(firstGraph), await File.ReadAllBytesAsync(secondGraph));
        Assert.Equal("graph:project:test:reconciled", graph["id"]!.GetValue<string>());
        Assert.Equal("multi-repository", graph["composition"]!["policy"]!.GetValue<string>());
        Assert.Equal(["repo-a", "repo-b"], graph["composition"]!["inputs"]!.AsArray()
            .Select(input => input!["repository"]!["repositoryId"]!.GetValue<string>()));

        manifest = manifest with { Inputs = [manifest.Inputs[0] with { ObservationBundleDigest = new string('0', 64) }, manifest.Inputs[1]] };
        await File.WriteAllBytesAsync(manifestPath, ContractJson.WriteCompositionManifest(manifest));
        var invalidResult = await Run("compose", manifestPath, "--graph", Path.Combine(temporary.Path, "invalid.json"));
        Assert.Equal(1, invalidResult.ExitCode);
        Assert.Contains("digest does not match", invalidResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpWithoutSuccessfulSnapshotExitsWithActionableDiagnostic()
    {
        using var temporary = new TemporaryDirectory();

        var result = await Run("mcp", RepositoryRoot(), "--state-directory", temporary.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("No complete successful Archie snapshot", result.Error, StringComparison.Ordinal);
        Assert.Contains("archie open <repository>", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanDiscoversFakeManifestAndReconcilesCanonicalGraph()
    {
        using var temporary = new TemporaryDirectory();
        var root = RepositoryRoot();
        var observations = Path.Combine(temporary.Path, "observations.json");
        var graph = Path.Combine(temporary.Path, "graph.json");
        var sourceContext = Path.Combine(temporary.Path, "source-context.json");
        var secondObservations = Path.Combine(temporary.Path, "observations-second.json");
        var secondGraph = Path.Combine(temporary.Path, "graph-second.json");
        var secondSourceContext = Path.Combine(temporary.Path, "source-context-second.json");

        var result = await Run(
            "scan", root,
            "--plugin-path", Path.Combine(root, "tests", "fixtures", "scanners", "fake-book-retail"),
            "--observations", observations,
            "--overlay", Path.Combine(root, "tests", "fixtures", "overlays", "book-retail.overlay.json"),
            "--graph", graph,
            "--source-context", sourceContext,
            "--as-of", "2026-09-01");
        var secondResult = await Run(
            "scan", root,
            "--plugin-path", Path.Combine(root, "tests", "fixtures", "scanners", "fake-book-retail"),
            "--observations", secondObservations,
            "--overlay", Path.Combine(root, "tests", "fixtures", "overlays", "book-retail.overlay.json"),
            "--graph", secondGraph,
            "--source-context", secondSourceContext,
            "--as-of", "2026-09-01");
        var incrementalResult = await Run(
            "scan", root,
            "--plugin-path", Path.Combine(root, "tests", "fixtures", "scanners", "fake-book-retail"),
            "--observations", observations,
            "--overlay", Path.Combine(root, "tests", "fixtures", "overlays", "book-retail.overlay.json"),
            "--graph", graph,
            "--source-context", sourceContext,
            "--as-of", "2026-09-01");
        var bundle = JsonNode.Parse(await File.ReadAllTextAsync(observations))!.AsObject();
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(graph))!.AsObject();
        var persisted = await File.ReadAllTextAsync(observations) + await File.ReadAllTextAsync(graph);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, secondResult.ExitCode);
        Assert.Equal(0, incrementalResult.ExitCode);
        Assert.Contains("Reused 1 scanner result(s)", incrementalResult.Output, StringComparison.Ordinal);
        Assert.Equal(await File.ReadAllBytesAsync(observations), await File.ReadAllBytesAsync(secondObservations));
        Assert.Equal(await File.ReadAllBytesAsync(graph), await File.ReadAllBytesAsync(secondGraph));
        Assert.Equal(await File.ReadAllBytesAsync(sourceContext), await File.ReadAllBytesAsync(secondSourceContext));
        Assert.Equal((await RunGit(root, "rev-parse", "HEAD")).Trim(), bundle["repository"]!["revision"]!.GetValue<string>());
        Assert.Contains("Discovered 1 scanner(s)", result.Output, StringComparison.Ordinal);
        Assert.Equal("scanner", bundle["source"]!.GetValue<string>());
        Assert.Equal("archie.fake-book-retail", bundle["scanners"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(26, bundle["observations"]!.AsArray().Count);
        Assert.NotEmpty(bundle["redactions"]!.AsArray());
        Assert.Equal($"graph:{bundle["repository"]!["repositoryId"]!.GetValue<string>()}:reconciled", snapshot["id"]!.GetValue<string>());
        Assert.Equal(14, snapshot["nodes"]!.AsArray().Count);
        Assert.Equal(13, snapshot["edges"]!.AsArray().Count);
        Assert.Contains("[REDACTED]", persisted, StringComparison.Ordinal);
        Assert.Contains("BOOK_RETAIL_ENDPOINT", persisted, StringComparison.Ordinal);
        Assert.Contains("fake language-neutral scanner observation", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiffSupportsStableTextAndJsonReports()
    {
        var graph = Path.Combine(RepositoryRoot(), "tests", "fixtures", "graphs", "book-retail-minimal.json");

        var text = await Run("diff", graph, graph);
        var json = await Run("diff", graph, graph, "--format", "json");
        var report = JsonNode.Parse(json.Output)!.AsObject();

        Assert.Equal(0, text.ExitCode);
        Assert.Contains("semantically unchanged", text.Output, StringComparison.Ordinal);
        Assert.Contains("Nodes changed (0)", text.Output, StringComparison.Ordinal);
        Assert.Equal(0, json.ExitCode);
        Assert.False(report["hasChanges"]!.GetValue<bool>());
        Assert.Empty(report["changedNodes"]!.AsArray());
    }

    [Fact]
    public async Task ScanOmitsLocalOriginFromArtifactsAndSecurityAuditFailsOnInjectedPaths()
    {
        using var temporary = new TemporaryDirectory();
        var root = RepositoryRoot();
        var repository = Path.Combine(temporary.Path, "repository");
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "PROJECT_BRIEF.md"), "Local-origin fixture.");
        var fixtureDirectory = Path.Combine(repository, "tests", "fixtures", "observations");
        Directory.CreateDirectory(fixtureDirectory);
        File.Copy(Fixture("observations", "book-retail.authored.json"),
            Path.Combine(fixtureDirectory, "book-retail.authored.json"));
        await RunGit(repository, "init", "--quiet");
        await RunGit(repository, "add", ".");
        await RunGit(repository, "-c", "user.name=Archie Tests", "-c", "user.email=archie@example.invalid",
            "commit", "--quiet", "-m", "fixture");
        await RunGit(repository, "remote", "add", "origin", repository);
        var observations = Path.Combine(temporary.Path, "observations.json");
        var graph = Path.Combine(temporary.Path, "graph.json");

        var result = await Run(
            "scan", repository,
            "--plugin-path", Path.Combine(root, "tests", "fixtures", "scanners", "fake-book-retail"),
            "--observations", observations,
            "--graph", graph,
            "--as-of", "2026-09-02");
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        var persisted = await File.ReadAllTextAsync(observations) + await File.ReadAllTextAsync(graph);
        var audit = Path.Combine(root, "scripts", "audit-generated-artifacts.sh");
        var cleanAudit = await RunBash(audit, observations, graph);
        var injected = Path.Combine(temporary.Path, "injected.json");
        await File.WriteAllTextAsync(injected, new JsonObject
        {
            ["remoteUrl"] = repository,
            ["token"] = "SYNTHETIC_AUDIT_SECRET"
        }.ToJsonString());
        var rejectedAudit = await RunBash(audit, injected);

        Assert.DoesNotContain(repository, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("file:", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, cleanAudit.ExitCode);
        Assert.Equal(1, rejectedAudit.ExitCode);
        Assert.Contains("Generated artifact security audit found", rejectedAudit.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://user:SYNTHETIC_PASSWORD@example.com/org/repo.git", "SYNTHETIC_PASSWORD")]
    [InlineData("https://example.com/org/repo.git?token=SYNTHETIC_TOKEN", "SYNTHETIC_TOKEN")]
    [InlineData("https://example.com/org/repo.git#SYNTHETIC_FRAGMENT", "SYNTHETIC_FRAGMENT")]
    public async Task ScanOmitsEntireUnsafeWebOriginFromArtifacts(string remote, string marker)
    {
        using var temporary = new TemporaryDirectory();
        var root = RepositoryRoot();
        var repository = Path.Combine(temporary.Path, "repository");
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "PROJECT_BRIEF.md"), "Unsafe-origin fixture.");
        var fixtureDirectory = Path.Combine(repository, "tests", "fixtures", "observations");
        Directory.CreateDirectory(fixtureDirectory);
        File.Copy(Fixture("observations", "book-retail.authored.json"),
            Path.Combine(fixtureDirectory, "book-retail.authored.json"));
        await RunGit(repository, "init", "--quiet");
        await RunGit(repository, "add", ".");
        await RunGit(repository, "-c", "user.name=Archie Tests", "-c", "user.email=archie@example.invalid",
            "commit", "--quiet", "-m", "fixture");
        await RunGit(repository, "remote", "add", "origin", remote);
        var observations = Path.Combine(temporary.Path, "observations.json");
        var graph = Path.Combine(temporary.Path, "graph.json");

        var result = await Run(
            "scan", repository,
            "--plugin-path", Path.Combine(root, "tests", "fixtures", "scanners", "fake-book-retail"),
            "--observations", observations,
            "--graph", graph,
            "--as-of", "2026-09-02");
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        var observationJson = JsonNode.Parse(await File.ReadAllTextAsync(observations))!.AsObject();
        var graphJson = JsonNode.Parse(await File.ReadAllTextAsync(graph))!.AsObject();
        var persisted = observationJson.ToJsonString() + graphJson.ToJsonString();

        Assert.Null(observationJson["repository"]!["remoteUrl"]);
        Assert.Null(graphJson["composition"]!["inputs"]![0]!["repository"]!["remoteUrl"]);
        Assert.DoesNotContain(marker, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", persisted, StringComparison.Ordinal);
    }


    [Fact]
    public async Task CanonicallyEquivalentOutputPathsAreRejectedBeforeScanningWithoutMutation()
    {
        using var temporary = new TemporaryDirectory();
        var artifact = Path.Combine(temporary.Path, "artifact.json");
        await File.WriteAllTextAsync(artifact, "prior-artifact");

        var result = await Run(
            "scan", RepositoryRoot(), "--plugin-path", Path.Combine(temporary.Path, "missing-plugin"),
            "--observations", artifact, "--graph", Path.Combine(temporary.Path, ".", "artifact.json"));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("must resolve to different paths", result.Error, StringComparison.Ordinal);
        Assert.Equal("prior-artifact", await File.ReadAllTextAsync(artifact));
    }

    [Fact]
    public async Task OutputAliasesThroughAncestorSymlinkAreRejectedForExistingAndMissingLeaves()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var physical = Path.Combine(temporary.Path, "physical", "nested");
        var alias = Path.Combine(temporary.Path, "alias");
        Directory.CreateDirectory(physical);
        Directory.CreateSymbolicLink(alias, Path.Combine(temporary.Path, "physical"));
        var existing = Path.Combine(physical, "artifact.json");
        await File.WriteAllTextAsync(existing, "prior-artifact");

        var existingResult = await Run(
            "scan", RepositoryRoot(), "--plugin-path", Path.Combine(temporary.Path, "missing-plugin"),
            "--observations", existing, "--graph", Path.Combine(alias, "nested", "artifact.json"));
        var missingPhysical = Path.Combine(physical, "future.json");
        var missingAlias = Path.Combine(alias, "nested", "future.json");
        var missingResult = await Run(
            "scan", RepositoryRoot(), "--plugin-path", Path.Combine(temporary.Path, "missing-plugin"),
            "--observations", missingPhysical, "--graph", missingAlias);

        Assert.Equal(2, existingResult.ExitCode);
        Assert.Contains("must resolve to different paths", existingResult.Error, StringComparison.Ordinal);
        Assert.Equal("prior-artifact", await File.ReadAllTextAsync(existing));
        Assert.Equal(2, missingResult.ExitCode);
        Assert.Contains("must resolve to different paths", missingResult.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(missingPhysical));
        Assert.False(File.Exists(missingAlias));
    }

    [Fact]
    public async Task PairPublicationRollsBackBothArtifactsWhenSecondReplacementFails()
    {
        using var temporary = new TemporaryDirectory();
        var observations = Path.Combine(temporary.Path, "observations.json");
        var graph = Path.Combine(temporary.Path, "graph.json");
        await File.WriteAllTextAsync(observations, "prior-observations");
        await File.WriteAllTextAsync(graph, "prior-graph");

        await Assert.ThrowsAsync<IOException>(() => ArtifactPairPublisher.PublishAsync(
            observations, "next-observations"u8.ToArray(), graph, "next-graph"u8.ToArray(), CancellationToken.None,
            () => throw new IOException("injected second replacement failure")));

        Assert.Equal("prior-observations", await File.ReadAllTextAsync(observations));
        Assert.Equal("prior-graph", await File.ReadAllTextAsync(graph));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, ".archie-artifacts-*.journal"));
    }

    [Fact]
    public async Task SnapshotPublicationRollsBackAllThreeArtifactsOnFailure()
    {
        using var temporary = new TemporaryDirectory();
        var observations = Path.Combine(temporary.Path, "observations.json");
        var graph = Path.Combine(temporary.Path, "graph.json");
        var sourceContext = Path.Combine(temporary.Path, "source-context.json");
        await File.WriteAllTextAsync(observations, "prior-observations");
        await File.WriteAllTextAsync(graph, "prior-graph");
        await File.WriteAllTextAsync(sourceContext, "prior-source-context");

        await Assert.ThrowsAsync<IOException>(() => ArtifactPairPublisher.PublishAsync(
            observations, "next-observations"u8.ToArray(), graph, "next-graph"u8.ToArray(),
            sourceContext, "next-source-context"u8.ToArray(), CancellationToken.None,
            () => throw new IOException("injected snapshot replacement failure")));

        Assert.Equal("prior-observations", await File.ReadAllTextAsync(observations));
        Assert.Equal("prior-graph", await File.ReadAllTextAsync(graph));
        Assert.Equal("prior-source-context", await File.ReadAllTextAsync(sourceContext));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, ".archie-artifacts-*.journal"));
    }

    [Fact]
    public async Task CheckedFixtureRemainsAuthoredWhileRuntimeScannerArtifactsAreIgnored()
    {
        var root = RepositoryRoot();
        var authored = JsonNode.Parse(await File.ReadAllTextAsync(Fixture("observations", "book-retail.authored.json")))!.AsObject();
        var graph = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tests", "fixtures", "graphs", "book-retail-minimal.json")))!.AsObject();

        Assert.Equal("authored", authored["source"]!.GetValue<string>());
        Assert.Empty(authored["scanners"]!.AsArray());
        Assert.All(graph["evidence"]!.AsArray(), evidence => Assert.True(evidence!["scannerId"] is null));
        Assert.Equal(".amp/generated/book-retail.graph.json", (await RunGit(root, "check-ignore", ".amp/generated/book-retail.graph.json")).Trim());
    }

    [Fact]
    public async Task MaliciousDiagnosticFieldsNeverReachCliOrPersistedArtifacts()
    {
        const string secret = "cli-secret-value";
        using var temporary = new TemporaryDirectory();
        var plugin = Path.Combine(temporary.Path, "plugin");
        Directory.CreateDirectory(plugin);
        await File.WriteAllTextAsync(Path.Combine(plugin, "scanner.json"), """
            {
              "schemaVersion": "scanner-manifest/v1",
              "id": "test.diagnostic",
              "version": "1.0.0",
              "executable": "node",
              "arguments": ["worker.mjs"],
              "artifactGlobs": ["Archie.slnx"],
              "capabilities": [],
              "configurationSchema": {},
              "permissions": { "readRepository": true, "network": false, "environment": false }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(plugin, "worker.mjs"), $$"""
            import { createInterface } from 'node:readline'
            const send = value => process.stdout.write(JSON.stringify(value) + '\n')
            send({ protocolVersion: 'scanner/v1', type: 'ready', scanner: { id: 'test.diagnostic', version: '1.0.0' } })
            const input = createInterface({ input: process.stdin, crlfDelay: Infinity })
            for await (const line of input) {
              JSON.parse(line)
              send({ protocolVersion: 'scanner/v1', type: 'diagnostic', diagnostic: {
                id: 'diagnostic:password={{secret}}', code: 'TOKEN={{secret}}', severity: 'warning',
                message: 'Useful scanner warning password={{secret}}', subjectId: 'subject:credential={{secret}}'
              } })
              send({ protocolVersion: 'scanner/v1', type: 'completed', summary: { observationCount: 0 } })
              break
            }
            """);
        var observations = Path.Combine(temporary.Path, "observations.json");
        var graph = Path.Combine(temporary.Path, "graph.json");

        var result = await Run(
            "scan", RepositoryRoot(), "--plugin-path", plugin,
            "--observations", observations, "--graph", graph, "--as-of", "2026-09-01");
        var surfaced = result.Output + result.Error + await File.ReadAllTextAsync(observations) + await File.ReadAllTextAsync(graph);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(secret, surfaced, StringComparison.Ordinal);
        Assert.Contains("Useful scanner warning", surfaced, StringComparison.Ordinal);
        Assert.Contains("SCANNER_DIAGNOSTIC_REDACTED", surfaced, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedScanPreservesPriorValidArtifacts()
    {
        using var temporary = new TemporaryDirectory();
        var plugin = Path.Combine(temporary.Path, "plugin");
        Directory.CreateDirectory(plugin);
        await File.WriteAllTextAsync(Path.Combine(plugin, "scanner.json"), """
            {
              "schemaVersion": "scanner-manifest/v1",
              "id": "test.timeout",
              "version": "1.0.0",
              "executable": "node",
              "arguments": ["worker.mjs"],
              "artifactGlobs": ["Archie.slnx"],
              "capabilities": [],
              "configurationSchema": {},
              "permissions": { "readRepository": true, "network": false, "environment": false }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(plugin, "worker.mjs"), """
            import { createInterface } from 'node:readline'
            process.stdout.write(JSON.stringify({ protocolVersion: 'scanner/v1', type: 'ready', scanner: { id: 'test.timeout', version: '1.0.0' } }) + '\n')
            const input = createInterface({ input: process.stdin, crlfDelay: Infinity })
            for await (const line of input) { JSON.parse(line); setTimeout(() => {}, 10000); break }
            """);
        var observations = Path.Combine(temporary.Path, "observations.json");
        var graph = Path.Combine(temporary.Path, "graph.json");
        await File.WriteAllTextAsync(observations, "prior-observations");
        await File.WriteAllTextAsync(graph, "prior-graph");

        var result = await Run(
            "scan", RepositoryRoot(), "--plugin-path", plugin, "--observations", observations, "--graph", graph,
            "--scanner-timeout", "100ms", "--as-of", "2026-09-01");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("\"code\":\"SCANNER_TIMEOUT\"", result.Error, StringComparison.Ordinal);
        Assert.Equal("prior-observations", await File.ReadAllTextAsync(observations));
        Assert.Equal("prior-graph", await File.ReadAllTextAsync(graph));
    }

    private static string Fixture(string kind, string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", kind, name);

    private static void AssertScannerEdge(JsonArray edges, string kind, string from, string to) =>
        Assert.Contains(edges, item => item!["kind"]!.GetValue<string>() == kind &&
            item["from"]!.GetValue<string>() == from && item["to"]!.GetValue<string>() == to &&
            item["provenance"]!.AsArray().Select(value => value!.GetValue<string>()).SequenceEqual(["deterministic"]));

    private static void AssertManualEdge(JsonArray edges, string kind, string from, string to) =>
        Assert.Contains(edges, item => item!["kind"]!.GetValue<string>() == kind &&
            item["from"]!.GetValue<string>() == from && item["to"]!.GetValue<string>() == to &&
            item["provenance"]!.AsArray().Select(value => value!.GetValue<string>()).SequenceEqual(["manual"]));

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Configuration() => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Archie.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "archie.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Archie CLI.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunWithDataHome(
        string dataHome,
        params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["XDG_DATA_HOME"] = dataHome;
        start.Environment["HTTP_PROXY"] = "http://127.0.0.1:1";
        start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:1";
        start.Environment["NO_PROXY"] = "";
        start.Environment["ARCHIE_SCANNER_CATALOG_URL"] = "https://127.0.0.1:1/catalog.json";
        start.Environment.Remove("AIP_PLUGIN_PATHS");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "archie.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Archie CLI.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunBash(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start artifact audit.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunDotNet(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    private static async Task<string> RunGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        return output;
    }

    private static async Task<string> RunMsBuild(string project, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(project),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(project);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"archie-cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
