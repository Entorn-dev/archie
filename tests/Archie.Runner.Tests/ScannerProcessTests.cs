using System.Diagnostics;
using System.Text.Json;
using Archie.Contracts;
using Archie.Runner;
using Xunit;

namespace Archie.Runner.Tests;

public sealed class ScannerProcessTests
{
    [Fact]
    public void ApprovedRuntimeBoundsAreFixed()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), ScannerRuntimeLimits.Default.ExecutionTimeout);
        Assert.Equal(TimeSpan.FromMinutes(60), ScannerRuntimeLimits.MaximumExecutionTimeout);
        Assert.Equal(128L * 1024 * 1024, ScannerRuntimeLimits.Default.MaxStandardOutputBytes);
        Assert.Equal(100_000, ScannerRuntimeLimits.Default.MaxObservations);
    }

    [Fact]
    public void ScanConfigurationDigestChangesWithScannerVersionAndRuntimeConfiguration()
    {
        using var worker = Worker("send({protocolVersion,type:'completed',summary:{observationCount:0}})");
        var baseline = ScanCoordinator.ConfigurationDigest([worker.Scanner], Limits());
        var upgraded = worker.Scanner with
        {
            Manifest = worker.Scanner.Manifest with { Version = "2.0.0" }
        };

        Assert.NotEqual(baseline, ScanCoordinator.ConfigurationDigest([upgraded], Limits()));
        Assert.NotEqual(baseline, ScanCoordinator.ConfigurationDigest(
            [worker.Scanner], Limits(execution: TimeSpan.FromSeconds(10))));
        Assert.NotEqual(baseline, ScanCoordinator.ConfigurationDigest(
            [worker.Scanner with { PackageSha256 = new string('a', 64) }], Limits()));
        Assert.NotEqual(baseline, ScanCoordinator.ConfigurationDigest(
            [worker.Scanner], Limits(), "technology-stack-rules/v2"));
        Assert.Equal(baseline, ScanCoordinator.ConfigurationDigest([worker.Scanner], Limits()));
    }

    [Fact]
    public async Task CancellationTerminatesWorkerAndPropagatesCancellation()
    {
        using var worker = Worker("setTimeout(() => {}, 10000)");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(worker, Limits(), cancellation.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TimeoutReturnsStructuredDiagnostic()
    {
        using var worker = Worker("setTimeout(() => {}, 10000)");

        var result = await Run(worker, Limits(execution: TimeSpan.FromMilliseconds(150)));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, item => item.Code == "SCANNER_TIMEOUT" && item.Severity == "error");
    }

    [Fact]
    public async Task MalformedNdjsonReturnsStructuredDiagnostic()
    {
        using var worker = Worker("process.stdout.write('{not-json}\\n')");

        var result = await Run(worker, Limits());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, item => item.Code == "SCANNER_MALFORMED_OUTPUT");
    }

    [Fact]
    public async Task ProcessFailureSanitizesStderr()
    {
        using var worker = Worker("process.stderr.write('password=' + 'worker-value'); process.exit(7)");

        var result = await Run(worker, Limits());
        var json = JsonSerializer.Serialize(result.Diagnostics, ContractJson.Options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, item => item.Code == "SCANNER_PROCESS_FAILED");
        Assert.DoesNotContain("worker-value", json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObservationMessageAndOutputLimitsFailWithoutPartialResults()
    {
        var observation = Observation();
        var json = JsonSerializer.Serialize<Observation>(observation, ContractJson.Options);
        using var tooMany = Worker($"const observation={json}; send({{protocolVersion,type:'observation',observation}}); send({{protocolVersion,type:'observation',observation}})");
        using var tooLarge = Worker("process.stdout.write('x'.repeat(300) + '\\n')");
        using var tooMuchOutput = Worker("process.stdout.write('x'.repeat(300) + '\\n')");

        var observationResult = await Run(tooMany, Limits(maxObservations: 1));
        var messageResult = await Run(tooLarge, Limits(maxMessageBytes: 128));
        var outputResult = await Run(tooMuchOutput, Limits(maxOutputBytes: 200));

        Assert.False(observationResult.Succeeded);
        Assert.Empty(observationResult.Observations);
        Assert.Contains(observationResult.Diagnostics, item => item.Code == "SCANNER_OBSERVATION_LIMIT_EXCEEDED");
        Assert.False(messageResult.Succeeded);
        Assert.Empty(messageResult.Observations);
        Assert.Contains(messageResult.Diagnostics, item => item.Code == "SCANNER_MESSAGE_LIMIT_EXCEEDED");
        Assert.False(outputResult.Succeeded);
        Assert.Empty(outputResult.Observations);
        Assert.Contains(outputResult.Diagnostics, item => item.Code == "SCANNER_OUTPUT_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task RedactionRemovesSensitiveValuesAndPreservesSafeEvidence()
    {
        var sensitive = string.Concat("top", "-", "secret", "-", "value");
        var apiKey = string.Concat("Payment:Api", "_Key");
        var authorization = string.Concat("Client:", "Authorization");
        var privateKey = string.Concat("Signing:Private", "-Key");
        var cookie = string.Concat("Session:", "Cookie");
        var auth = string.Concat("Payment:", "Auth");
        var fullwidthAuth = "Payment:ａｕｔｈ";
        var compatibilityAuth = "Payment:ᴬᵁᵀᴴ";
        var combiningAuth = "Payment:a\u0301uth";
        var observation = Observation(new Dictionary<string, JsonElement>
        {
            ["password"] = JsonSerializer.SerializeToElement(sensitive),
            ["provider"] = JsonSerializer.SerializeToElement("fake-provider"),
            ["resourceKind"] = JsonSerializer.SerializeToElement("queue"),
            ["configurationKey"] = JsonSerializer.SerializeToElement("ConnectionStrings:Orders"),
            ["nested"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["configurationKey"] = apiKey,
                [authorization] = sensitive,
                [privateKey] = sensitive,
                [cookie] = sensitive
            }),
            ["configurationKeys"] = JsonSerializer.SerializeToElement(new[]
            {
                new Dictionary<string, string> { ["configurationKey"] = auth },
                new Dictionary<string, string> { ["configurationKey"] = fullwidthAuth },
                new Dictionary<string, string> { ["configurationKey"] = compatibilityAuth },
                new Dictionary<string, string> { ["configurationKey"] = combiningAuth },
                new Dictionary<string, string> { ["configurationKey"] = "Payment:Author" },
                new Dictionary<string, string> { ["configurationKey"] = "Payment:Authority" }
            })
        });
        var json = JsonSerializer.Serialize<Observation>(observation, ContractJson.Options);
        using var worker = Worker($"const observation={json}; send({{protocolVersion,type:'observation',observation}}); send({{protocolVersion,type:'completed',summary:{{observationCount:1}}}})");

        var result = await Run(worker, Limits());
        var persisted = JsonSerializer.Serialize(result, ContractJson.Options);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(sensitive, persisted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", persisted, StringComparison.Ordinal);
        Assert.Contains("fake-provider", persisted, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings:Orders", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(authorization, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(privateKey, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(cookie, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{auth}\"", persisted, StringComparison.Ordinal);
        foreach (var unsafeKey in new[] { fullwidthAuth, compatibilityAuth, combiningAuth })
            Assert.DoesNotContain(JsonSerializer.Serialize(unsafeKey, ContractJson.Options).Trim('"'), persisted,
                StringComparison.Ordinal);
        Assert.Contains("Payment:Author", persisted, StringComparison.Ordinal);
        Assert.Contains("Payment:Authority", persisted, StringComparison.Ordinal);
        Assert.Contains(result.Redactions, item => item.Kind == "sensitive-value");
    }

    [Fact]
    public async Task EveryWorkerControlledStringIsRedactedBeforeBundleAndGraphSerialization()
    {
        const string secret = "diagnostic-secret-value";
        var properties = new Dictionary<string, JsonElement>
        {
            [$"token={secret}"] = JsonSerializer.SerializeToElement("hidden"),
            ["safeEvidence"] = JsonSerializer.SerializeToElement("useful-safe-value"),
            ["opaqueValue"] = JsonSerializer.SerializeToElement(secret),
            ["nested"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                [$"password={secret}"] = secret,
                ["safeNested"] = "safe-nested-value"
            })
        };
        var observation = Observation(properties) with
        {
            Id = $"observation:token={secret}",
            Evidence = Observation(properties).Evidence with
            {
                Id = $"evidence:password={secret}",
                Path = $"src/token={secret}/fixture.cs"
            },
            Entity = Observation(properties).Entity with
            {
                Key = $"deployable:credential={secret}",
                IdentitySignals = new Dictionary<string, string>
                {
                    [$"apiKey={secret}"] = secret,
                    ["project"] = "safe-project"
                }
            }
        };
        var observationJson = JsonSerializer.Serialize<Observation>(observation, ContractJson.Options);
        var diagnostic = JsonSerializer.Serialize(new
        {
            protocolVersion = "scanner/v1",
            type = "diagnostic",
            diagnostic = new
            {
                id = $"diagnostic:password={secret}",
                code = $"TOKEN={secret}",
                severity = "warning",
                message = $"Useful context; password={secret}",
                subjectId = $"subject:credential={secret}"
            }
        });
        using var worker = Worker($"send({diagnostic}); const observation={observationJson}; send({{protocolVersion,type:'observation',observation}}); send({{protocolVersion,type:'completed',summary:{{observationCount:1}}}})");

        var result = await Run(worker, Limits());
        var bundle = new ObservationBundle(
            "observations/v1", ObservationSource.Scanner, new string('a', 64),
            new("test-repository", null, "abc123", false, new string('b', 64)),
            [new("test.scanner", "1.0.0")], result.Observations, result.Diagnostics, result.Redactions);
        var bundleJson = System.Text.Encoding.UTF8.GetString(ContractJson.WriteObservationBundle(bundle));
        var graphJson = System.Text.Encoding.UTF8.GetString(ContractJson.WriteGraphSnapshot(new Archie.Core.Reconciler().Reconcile(bundle, null, new DateOnly(2026, 9, 1)).Snapshot));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(secret, bundleJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, graphJson, StringComparison.Ordinal);
        Assert.Contains("Useful context", bundleJson, StringComparison.Ordinal);
        Assert.Contains("useful-safe-value", graphJson, StringComparison.Ordinal);
        Assert.Contains("safe-nested-value", graphJson, StringComparison.Ordinal);
        Assert.Contains("safe-project", bundleJson, StringComparison.Ordinal);
        Assert.Contains("SCANNER_DIAGNOSTIC_REDACTED", bundleJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cancellation")]
    [InlineData("timeout")]
    [InlineData("malformed")]
    [InlineData("failure")]
    [InlineData("normal")]
    public async Task SupervisorTerminatesRealGrandchildForEveryExitPath(string scenario)
    {
        if (!OperatingSystem.IsLinux()) return;
        var behavior = scenario switch
        {
            "malformed" => "process.stdout.write('{not-json}\\n')",
            "failure" => "process.exit(7)",
            "normal" => "send({protocolVersion,type:'completed',summary:{observationCount:0}})",
            _ => "setTimeout(() => {}, 10000)"
        };
        using var worker = Worker(behavior, grandchild: true);
        (ProcessIdentity Worker, ProcessIdentity Grandchild) identities;
        if (scenario == "cancellation")
        {
            using var cancellation = new CancellationTokenSource();
            var run = Run(worker, Limits(), cancellation.Token);
            identities = await worker.ReadProcessIdentitiesAsync();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        else
        {
            var limits = scenario == "timeout" ? Limits(execution: TimeSpan.FromSeconds(1)) : Limits();
            await Run(worker, limits);
            identities = await worker.ReadProcessIdentitiesAsync();
        }

        Assert.NotEqual(identities.Worker.ProcessGroup, identities.Grandchild.ProcessGroup);
        Assert.NotEqual(identities.Worker.Session, identities.Grandchild.Session);
        var stoppedHeartbeat = await File.ReadAllTextAsync(worker.GrandchildHeartbeatPath);
        await Task.Delay(300);
        Assert.Equal(stoppedHeartbeat, await File.ReadAllTextAsync(worker.GrandchildHeartbeatPath));
    }

    [Fact]
    public async Task LinuxSupervisorFailsClosedWhenPidNamespaceContainmentCannotStart()
    {
        if (!OperatingSystem.IsLinux()) return;

        var result = await ScannerSupervisor.RunAsync([
            "--grace-ms", "50", "--linux-unshare", Path.Combine(Path.GetTempPath(), $"missing-unshare-{Guid.NewGuid():N}"), "--", "/bin/true"]);

        Assert.Equal(ScannerSupervisor.ContainmentUnavailableExitCode, result);
    }

    [Fact]
    public async Task NonzeroHandshakeEofIsAProcessFailure()
    {
        using var worker = Worker("process.exit(9)", emitReady: false);

        var result = await Run(worker, Limits());

        Assert.Contains(result.Diagnostics, item => item.Code == "SCANNER_PROCESS_FAILED");
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "SCANNER_PROTOCOL_ERROR");
    }

    [Fact]
    public void WindowsBareExecutableResolutionUsesPathExtWithoutAShell()
    {
        using var temporary = new TestWorkerDirectory();
        var executable = Path.Combine(temporary.Path, "scanner.EXE");
        File.WriteAllText(executable, "fixture");

        var resolved = ExecutableResolver.Resolve("scanner", temporary.Path, temporary.Path, ".COM;.EXE", windows: true);

        Assert.Equal(executable, resolved);
    }

    [Fact]
    public void AppHostSelfInvocationDoesNotPassEntryAssembly()
    {
        var invocation = ScannerSupervisor.ResolveSelfInvocation("/opt/archie/archie", "/opt/archie/archie.dll");

        Assert.Equal("/opt/archie/archie", invocation.Executable);
        Assert.Null(invocation.AssemblyArgument);
    }

    [Fact]
    public void MuxerSelfInvocationPassesEntryAssembly()
    {
        var invocation = ScannerSupervisor.ResolveSelfInvocation("/opt/dotnet/dotnet", "/opt/archie/archie.dll");

        Assert.Equal("/opt/dotnet/dotnet", invocation.Executable);
        Assert.Equal("/opt/archie/archie.dll", invocation.AssemblyArgument);
    }

    private static async Task<ScannerProcessResult> Run(
        TestWorker worker,
        ScannerRuntimeLimits limits,
        CancellationToken cancellationToken = default)
    {
        using var configuration = JsonDocument.Parse("{}");
        var context = new ScanContext(
            new("test-repository", null, "abc123", false, new string('a', 64)),
            worker.Directory,
            configuration.RootElement.Clone());
        return await new ScannerProcess().RunAsync(worker.Scanner, context, limits, cancellationToken);
    }

    private static ScannerRuntimeLimits Limits(
        TimeSpan? execution = null,
        int maxMessageBytes = 1024 * 1024,
        int maxOutputBytes = 1024 * 1024,
        int maxObservations = 100_000) => new(
            TimeSpan.FromSeconds(5),
            execution ?? TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(50),
            maxMessageBytes,
            maxOutputBytes,
            1024,
            maxObservations);

    private static TestWorker Worker(string behavior, bool grandchild = false, bool emitReady = true)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"archie-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "worker.mjs");
        var workerIdentityPath = Path.Combine(directory, "worker.identity.json");
        var grandchildIdentityPath = Path.Combine(directory, "grandchild.identity.json");
        var heartbeatPath = Path.Combine(directory, "grandchild.heartbeat");
        if (grandchild)
            File.WriteAllText(Path.Combine(directory, "grandchild.mjs"), """
                import { readFileSync, writeFileSync } from 'node:fs'
                const [identity, heartbeat] = process.argv.slice(2)
                const stat = readFileSync('/proc/self/stat', 'utf8')
                const fields = stat.slice(stat.lastIndexOf(')') + 2).split(' ')
                writeFileSync(identity, JSON.stringify({ Pid: process.pid, ProcessGroup: Number(fields[2]), Session: Number(fields[3]) }))
                writeFileSync(heartbeat, String(Date.now()))
                setInterval(() => writeFileSync(heartbeat, String(Date.now())), 25)
                """);
        var prelude = grandchild
            ? $"const {{ spawn }} = await import('node:child_process'); const {{ existsSync, readFileSync, writeFileSync }} = await import('node:fs'); const stat=readFileSync('/proc/self/stat','utf8'); const fields=stat.slice(stat.lastIndexOf(')')+2).split(' '); writeFileSync({JsonSerializer.Serialize(workerIdentityPath)},JSON.stringify({{Pid:process.pid,ProcessGroup:Number(fields[2]),Session:Number(fields[3])}})); const child=spawn(process.execPath,['grandchild.mjs',{JsonSerializer.Serialize(grandchildIdentityPath)},{JsonSerializer.Serialize(heartbeatPath)}],{{detached:true,stdio:'ignore'}}); child.unref(); while(!existsSync({JsonSerializer.Serialize(grandchildIdentityPath)})||!existsSync({JsonSerializer.Serialize(heartbeatPath)})) await new Promise(resolve=>setTimeout(resolve,10));"
            : string.Empty;
        var protocolBody = emitReady
            ? $$"""
              send({ protocolVersion, type: 'ready', scanner: { id: 'test.scanner', version: '1.0.0' } })
              const input = createInterface({ input: process.stdin, crlfDelay: Infinity })
              for await (const line of input) {
                JSON.parse(line)
                {{prelude}}
                {{behavior}}
                break
              }
              """
            : behavior;
        File.WriteAllText(script, $$"""
            import { createInterface } from 'node:readline'
            const protocolVersion = 'scanner/v1'
            const send = value => process.stdout.write(JSON.stringify(value) + '\n')
            {{protocolBody}}
            """);
        using var configuration = JsonDocument.Parse("{}");
        var manifest = new ScannerManifest(
            "scanner-manifest/v1", "test.scanner", "1.0.0", "node", ["worker.mjs"], ["fixture.txt"], [],
            configuration.RootElement.Clone(), new(true, false, false));
        return new(directory, new(manifest, Path.Combine(directory, "scanner.json")), workerIdentityPath, grandchildIdentityPath, heartbeatPath);
    }

    private static EntityObservation Observation(IReadOnlyDictionary<string, JsonElement>? properties = null)
    {
        var evidence = new Evidence(
            "evidence:test", "observation:test", EvidenceProvenance.Deterministic, "test.scanner", "1.0.0",
            "fake test scanner", "fixture.txt", null, Confidence.Confirmed, properties ?? new Dictionary<string, JsonElement>());
        var candidate = new EntityCandidate(
            "deployable:test", NodeKind.Deployable, null, "Test service", Resolution.Resolved,
            new Dictionary<string, string>(), new Dictionary<string, JsonElement>());
        return new("observation:test", evidence, candidate);
    }

    private sealed record TestWorker(
        string Directory,
        DiscoveredScanner Scanner,
        string WorkerIdentityPath,
        string GrandchildIdentityPath,
        string GrandchildHeartbeatPath) : IDisposable
    {
        public async Task<(ProcessIdentity Worker, ProcessIdentity Grandchild)> ReadProcessIdentitiesAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(WorkerIdentityPath) && File.Exists(GrandchildIdentityPath) && File.Exists(GrandchildHeartbeatPath))
                {
                    var worker = JsonSerializer.Deserialize<ProcessIdentity>(await File.ReadAllTextAsync(WorkerIdentityPath));
                    var grandchild = JsonSerializer.Deserialize<ProcessIdentity>(await File.ReadAllTextAsync(GrandchildIdentityPath));
                    if (worker is not null && grandchild is not null) return (worker, grandchild);
                }
                await Task.Delay(10);
            }
            throw new Xunit.Sdk.XunitException("Worker did not start its grandchild.");
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed record ProcessIdentity(int Pid, int ProcessGroup, int Session);

    private sealed class TestWorkerDirectory : IDisposable
    {
        public TestWorkerDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"archie-resolver-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
