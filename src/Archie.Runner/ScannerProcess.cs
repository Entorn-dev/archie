using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Archie.Runner;

public sealed record ScannerProcessResult(
    bool Succeeded,
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<SourceOwnershipClaim> SourceOwnership,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<RedactionEvent> Redactions);

public sealed class ScannerProcess
{
    private const string ProtocolVersion = "scanner/v1";
    private static readonly JsonSerializerOptions ProtocolJson = CreateProtocolJson();
    private readonly ObservationRedactor redactor = new();
    private readonly JsonSchema protocolSchema;
    private readonly EvaluationOptions schemaOptions;

    public ScannerProcess()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "schemas", "v1");
        schemaOptions = new() { OutputFormat = OutputFormat.Flag, RequireFormatValidation = true };
        foreach (var file in Directory.EnumerateFiles(directory, "*.schema.json"))
            schemaOptions.SchemaRegistry.Register(JsonSchema.FromText(File.ReadAllText(file)));
        protocolSchema = JsonSchema.FromText(File.ReadAllText(Path.Combine(directory, "protocol-message.schema.json")));
    }

    public async Task<ScannerProcessResult> RunAsync(
        DiscoveredScanner scanner,
        ScanContext context,
        ScannerRuntimeLimits limits,
        CancellationToken cancellationToken)
    {
        using var process = Start(scanner, limits);
        var stderrTask = CaptureStandardErrorAsync(process.StandardError.BaseStream, limits.MaxStandardErrorBytes);
        var reader = new BoundedNdjsonReader(process.StandardOutput.BaseStream, limits.MaxProtocolMessageBytes, limits.MaxStandardOutputBytes);
        try
        {
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            execution.CancelAfter(limits.ExecutionTimeout);
            string? readyLine;
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(execution.Token))
            {
                handshake.CancelAfter(limits.HandshakeTimeout);
                try { readyLine = await reader.ReadLineAsync(handshake.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !execution.IsCancellationRequested)
                {
                    return await FailAsync(process, stderrTask, limits, scanner, "SCANNER_HANDSHAKE_TIMEOUT", "Scanner did not become ready within the handshake limit.");
                }
            }
            if (readyLine is null)
            {
                await process.WaitForExitAsync();
                var capturedError = await stderrTask;
                if (process.ExitCode == ScannerSupervisor.ContainmentUnavailableExitCode)
                    return Failure(scanner, "SCANNER_CONTAINMENT_UNAVAILABLE", "Required scanner descendant containment is unavailable on this host.", capturedError);
                return process.ExitCode == 0
                    ? Failure(scanner, "SCANNER_PROTOCOL_ERROR", "Scanner exited before its ready message.", capturedError)
                    : Failure(scanner, "SCANNER_PROCESS_FAILED", $"Scanner process exited with code {process.ExitCode} before its ready message.", capturedError);
            }
            var readyMessage = Deserialize<ProtocolMessage>(readyLine, "ready");
            if (readyMessage is not ReadyMessage ready)
                return await FailAsync(process, stderrTask, limits, scanner, "SCANNER_PROTOCOL_ERROR", "The first scanner message must be ready.");
            if (ready.ProtocolVersion != ProtocolVersion || ready.Scanner.Id != scanner.Manifest.Id || ready.Scanner.Version != scanner.Manifest.Version)
                return await FailAsync(process, stderrTask, limits, scanner, "SCANNER_PROTOCOL_ERROR", "Scanner ready identity or protocol version does not match its manifest.");

            var request = new ScanRequestMessage(ProtocolVersion, context);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize<ProtocolMessage>(request, ProtocolJson).AsMemory(), execution.Token);
            await process.StandardInput.FlushAsync(execution.Token);

            var observations = new List<Observation>();
            var sourceOwnership = new List<SourceOwnershipClaim>();
            var diagnostics = new List<Diagnostic>();
            var redactions = new List<RedactionEvent>();
            CompletedMessage? completed = null;
            while (completed is null)
            {
                string? line;
                try { line = await reader.ReadLineAsync(execution.Token); }
                catch (LimitException exception)
                {
                    return await FailAsync(process, stderrTask, limits, scanner, exception.Code, exception.Message);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return await FailAsync(process, stderrTask, limits, scanner, "SCANNER_TIMEOUT", $"Scanner exceeded the {limits.ExecutionTimeout} execution limit.");
                }
                if (line is null)
                {
                    await process.WaitForExitAsync(execution.Token);
                    var capturedError = await stderrTask;
                    return process.ExitCode == 0
                        ? Failure(scanner, "SCANNER_PROTOCOL_ERROR", "Scanner output ended before a completed message.", capturedError)
                        : Failure(scanner, "SCANNER_PROCESS_FAILED", $"Scanner process exited with code {process.ExitCode}.", capturedError);
                }
                ProtocolMessage message;
                try { message = Deserialize<ProtocolMessage>(line, "protocol"); }
                catch (InvalidDataException exception)
                {
                    return await FailAsync(process, stderrTask, limits, scanner, "SCANNER_MALFORMED_OUTPUT", exception.Message);
                }
                if (message.ProtocolVersion != ProtocolVersion)
                    return await FailAsync(process, stderrTask, limits, scanner, "SCANNER_PROTOCOL_ERROR", $"Unsupported protocol version '{message.ProtocolVersion}'.");
                switch (message)
                {
                    case ObservationMessage observationMessage:
                        if (observations.Count >= limits.MaxObservations)
                            return await FailAsync(process, stderrTask, limits, scanner, "SCANNER_OBSERVATION_LIMIT_EXCEEDED", $"Scanner exceeded the {limits.MaxObservations} observation limit.");
                        var result = redactor.Redact(observationMessage.Observation, scanner, context.CheckoutPath);
                        observations.Add(result.Observation);
                        redactions.AddRange(result.Redactions);
                        break;
                    case SourceOwnershipMessage ownershipMessage:
                        if (sourceOwnership.Count >= limits.MaxSourceOwnership)
                            return await FailAsync(process, stderrTask, limits, scanner, "SCANNER_SOURCE_OWNERSHIP_LIMIT_EXCEEDED", $"Scanner exceeded the {limits.MaxSourceOwnership} source-ownership limit.");
                        var ownershipResult = redactor.RedactSourceOwnership(ownershipMessage.Ownership, scanner, context.CheckoutPath);
                        ValidateOwnership(ownershipResult.Ownership, scanner);
                        sourceOwnership.Add(ownershipResult.Ownership);
                        redactions.AddRange(ownershipResult.Redactions);
                        break;
                    case DiagnosticMessage diagnosticMessage:
                        var diagnosticResult = redactor.RedactDiagnostic(diagnosticMessage.Diagnostic);
                        diagnostics.Add(diagnosticResult.Diagnostic);
                        redactions.AddRange(diagnosticResult.Redactions);
                        break;
                    case CompletedMessage completedMessage:
                        completed = completedMessage;
                        break;
                    default:
                        return await FailAsync(process, stderrTask, limits, scanner, "SCANNER_PROTOCOL_ERROR", $"Unexpected '{message.GetType().Name}' message after handshake.");
                }
            }
            process.StandardInput.Close();
            string? trailingLine;
            while ((trailingLine = await reader.ReadLineAsync(execution.Token)) is not null)
            {
                if (!string.IsNullOrWhiteSpace(trailingLine))
                    return await FailAsync(process, stderrTask, limits, scanner, "SCANNER_PROTOCOL_ERROR", "Scanner emitted output after its completed message.");
            }
            try { await process.WaitForExitAsync(execution.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return await FailAsync(process, stderrTask, limits, scanner, "SCANNER_TIMEOUT", $"Scanner exceeded the {limits.ExecutionTimeout} execution limit.");
            }
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                return Failure(scanner, "SCANNER_PROCESS_FAILED", $"Scanner process exited with code {process.ExitCode}.", stderr);
            if (completed.Summary.ObservationCount != observations.Count)
                return Failure(scanner, "SCANNER_PROTOCOL_ERROR", "Scanner completion count does not match emitted observations.", stderr);
            AddStderrDiagnostics(scanner, stderr, diagnostics);
            return new(true, observations, sourceOwnership, diagnostics, redactions.DistinctBy(item => item.Id).ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(process, stderrTask, limits, scanner, "SCANNER_TIMEOUT", $"Scanner exceeded the {limits.ExecutionTimeout} execution limit.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TerminateAsync(process, limits.CancellationGracePeriod);
            await stderrTask;
            throw;
        }
        catch (LimitException exception)
        {
            return await FailAsync(process, stderrTask, limits, scanner, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
        {
            return await FailAsync(process, stderrTask, limits, scanner, "SCANNER_MALFORMED_OUTPUT", exception.Message);
        }
    }

    private static Process Start(DiscoveredScanner scanner, ScannerRuntimeLimits limits)
    {
        var executable = ExecutableResolver.Resolve(
            scanner.Manifest.Executable,
            scanner.Directory,
            Environment.GetEnvironmentVariable("ARCHIE_SCANNER_EXECUTABLE_PATH"));
        var processPath = Environment.ProcessPath;
        var launcher = Path.Combine(AppContext.BaseDirectory, "archie.dll");
        if (processPath is null || !File.Exists(processPath) || !File.Exists(launcher))
            throw new InvalidDataException("The trusted scanner supervisor is not available.");
        var self = ScannerSupervisor.ResolveSelfInvocation(processPath, launcher);
        var start = new ProcessStartInfo(self.Executable)
        {
            WorkingDirectory = scanner.Directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        if (self.AssemblyArgument is not null) start.ArgumentList.Add(self.AssemblyArgument);
        start.ArgumentList.Add("__scanner-supervisor");
        start.ArgumentList.Add("--grace-ms");
        start.ArgumentList.Add(((int)limits.CancellationGracePeriod.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (OperatingSystem.IsLinux())
        {
            string unshare;
            try { unshare = ExecutableResolver.Resolve("unshare", scanner.Directory); }
            catch (InvalidDataException) { throw new ScannerContainmentUnavailableException("Linux scanner containment requires the util-linux unshare executable."); }
            start.ArgumentList.Add("--linux-unshare");
            start.ArgumentList.Add(unshare);
        }
        else if (!OperatingSystem.IsWindows())
        {
            throw new ScannerContainmentUnavailableException("Scanner descendant containment is not implemented on this platform.");
        }
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(executable);
        foreach (var argument in scanner.Manifest.Arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidDataException($"Could not start scanner '{scanner.Manifest.Id}'.");
    }

    private T Deserialize<T>(string line, string expected) where T : ProtocolMessage
    {
        try
        {
            var instance = JsonNode.Parse(line) ?? throw new InvalidDataException($"Scanner emitted an empty {expected} message.");
            if (!protocolSchema.Evaluate(instance, schemaOptions).IsValid)
                throw new InvalidDataException($"Scanner {expected} message does not conform to scanner/v1.");
            var message = JsonSerializer.Deserialize<T>(line, ScannerContractJson.Options);
            return message ?? throw new InvalidDataException($"Scanner emitted an empty {expected} message.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException($"Scanner emitted malformed {expected} NDJSON: {ObservationRedactor.SanitizeText(exception.Message)}");
        }
    }

    private static JsonSerializerOptions CreateProtocolJson()
    {
        var options = new JsonSerializerOptions(ScannerContractJson.Options) { WriteIndented = false };
        return options;
    }

    private static void ValidateOwnership(SourceOwnershipClaim ownership, DiscoveredScanner scanner)
    {
        if (ownership.ScannerId != scanner.Manifest.Id || ownership.ScannerVersion != scanner.Manifest.Version)
            throw new InvalidDataException("Source ownership scanner identity does not match its manifest.");
        if (string.IsNullOrWhiteSpace(ownership.OwnerCandidateKey) || string.IsNullOrWhiteSpace(ownership.DerivationRule) ||
            string.IsNullOrWhiteSpace(ownership.Path) || ownership.Path.Length > 1024 || Path.IsPathRooted(ownership.Path) ||
            ownership.Path.Contains('\\') || ownership.Path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Source ownership must contain a normalized repository-relative path and bounded derivation metadata.");
    }

    private static async Task<ScannerProcessResult> FailAsync(
        Process process,
        Task<CapturedError> stderrTask,
        ScannerRuntimeLimits limits,
        DiscoveredScanner scanner,
        string code,
        string message)
    {
        await TerminateAsync(process, limits.CancellationGracePeriod);
        return Failure(scanner, code, message, await stderrTask);
    }

    private static ScannerProcessResult Failure(DiscoveredScanner scanner, string code, string message, CapturedError stderr)
    {
        var diagnostics = new List<Diagnostic>
        {
            new($"diagnostic:{scanner.Manifest.Id}:{code.ToLowerInvariant()}", code, "error", ObservationRedactor.SanitizeText(message), scanner.Manifest.Id)
        };
        AddStderrDiagnostics(scanner, stderr, diagnostics);
        return new(false, [], [], diagnostics.Select(ObservationRedactor.SanitizeDiagnostic).ToArray(), []);
    }

    private static void AddStderrDiagnostics(DiscoveredScanner scanner, CapturedError stderr, ICollection<Diagnostic> diagnostics)
    {
        if (stderr.Truncated)
            diagnostics.Add(new($"diagnostic:{scanner.Manifest.Id}:stderr-limit", "SCANNER_STDERR_LIMIT_EXCEEDED", "warning", "Scanner stderr exceeded 4 MiB; only bounded diagnostic context was retained.", scanner.Manifest.Id));
        if (!string.IsNullOrWhiteSpace(stderr.Text))
            diagnostics.Add(new($"diagnostic:{scanner.Manifest.Id}:stderr", "SCANNER_STDERR", "warning", $"Scanner stderr: {ObservationRedactor.SanitizeText(stderr.Text.Trim())}", scanner.Manifest.Id));
    }

    private static async Task TerminateAsync(Process process, TimeSpan gracePeriod)
    {
        try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
        await process.WaitForExitAsync();
    }

    private static async Task<CapturedError> CaptureStandardErrorAsync(Stream stream, long limit)
    {
        var buffer = new byte[8192];
        var retained = new byte[checked((int)limit)];
        var writePosition = 0;
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                retained[writePosition] = buffer[index];
                writePosition = (writePosition + 1) % retained.Length;
            }
            total += read;
        }
        var length = (int)Math.Min(total, limit);
        var ordered = new byte[length];
        var start = total > limit ? writePosition : 0;
        for (var index = 0; index < length; index++) ordered[index] = retained[(start + index) % retained.Length];
        return new(Encoding.UTF8.GetString(ordered), total > limit);
    }

    private sealed record CapturedError(string Text, bool Truncated);

    private sealed class LimitException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed class BoundedNdjsonReader(Stream stream, int lineLimit, long outputLimit)
    {
        private readonly byte[] buffer = new byte[8192];
        private int position;
        private int available;
        private long total;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();
            while (true)
            {
                if (position == available)
                {
                    available = await stream.ReadAsync(buffer, cancellationToken);
                    position = 0;
                    if (available == 0) return line.Length == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
                }
                var value = buffer[position++];
                total++;
                if (total > outputLimit) throw new LimitException("SCANNER_OUTPUT_LIMIT_EXCEEDED", $"Scanner stdout exceeded the {outputLimit} byte limit.");
                if (value == (byte)'\n') return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                line.WriteByte(value);
                if (line.Length > lineLimit) throw new LimitException("SCANNER_MESSAGE_LIMIT_EXCEEDED", $"Scanner emitted an NDJSON line larger than {lineLimit} bytes.");
            }
        }
    }
}

public sealed class ScannerContainmentUnavailableException(string message) : Exception(message);
