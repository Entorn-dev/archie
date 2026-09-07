using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Archie.Contracts;
using Archie.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Archie.Cli;

internal static class McpServerHost
{
    private const int MaxSourceContextBytes = 64 * 1024 * 1024;
    private const int MaxGraphBytes = 256 * 1024 * 1024;
    private const int MaxObservationBytes = 128 * 1024 * 1024;
    private static readonly ContextRuntimeLimits Limits = new();
    private static readonly PresentationLimit ContextPresentation = new(6, 25);
    private static readonly PresentationLimit DependencyPresentation = new(12, 50);
    private static readonly PresentationLimit FlowPresentation = new(3, 10);
    private static readonly PresentationLimit ImpactPresentation = new(8, 25);
    private static readonly IReadOnlyList<Tool> Tools = CreateTools();

    private enum ResponseDetail { Summary, Evidence, Full }
    private sealed record PresentationLimit(int DefaultMaxItems, int MaximumMaxItems);
    private sealed record PresentationOptions(ResponseDetail Detail, int MaxItems);

    public static async Task<int> RunAsync(LocalRepositoryState state, CancellationToken cancellationToken)
    {
        var service = Load(state);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "archie",
                Title = "Archie local architecture context",
                Version = "1.0.0",
                Description = "Read-only evidence-backed architecture context from the latest successful local snapshot."
            },
            ServerInstructions = "Use Archie results as potential static architecture context. Cite repository-relative evidence and preserve uncertainty.",
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = Tools.ToList() }),
                CallToolHandler = (request, token) => ValueTask.FromResult(Call(service, request.Params, token))
            }
        };
        await using var transport = new StdioServerTransport(options, loggerFactory: null);
        await using var server = McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
        await server.RunAsync(cancellationToken);
        return 0;
    }

    private static ArchitectureContextService Load(LocalRepositoryState state)
    {
        if (!File.Exists(state.ObservationsPath) || !File.Exists(state.GraphPath) || !File.Exists(state.SourceContextPath))
            throw new InvalidDataException("No complete successful Archie snapshot exists for this repository. Run 'archie open <repository>' first.");
        var observationBytes = ReadBounded(state.ObservationsPath, MaxObservationBytes, "observation bundle");
        var graphBytes = ReadBounded(state.GraphPath, MaxGraphBytes, "canonical graph");
        var sourceBytes = ReadBounded(state.SourceContextPath, MaxSourceContextBytes, "source-context artifact");
        if (ArtifactSchemaValidator.Validate(observationBytes) != "observations/v1" ||
            ArtifactSchemaValidator.Validate(graphBytes) != "architecture/v1" ||
            ArtifactSchemaValidator.Validate(sourceBytes) != "source-context/v1")
            throw new InvalidDataException("The latest local snapshot has incompatible artifact versions. Scan the repository again.");

        using var observationStream = new MemoryStream(observationBytes, writable: false);
        var observations = ContractJson.ReadObservationBundle(observationStream);
        ObservationValidator.Validate(observations);
        using var graphStream = new MemoryStream(graphBytes, writable: false);
        var graph = ContractJson.ReadGraphSnapshot(graphStream);
        GraphValidator.Validate(graph);
        using var sourceStream = new MemoryStream(sourceBytes, writable: false);
        var source = ContractJson.ReadSourceContext(sourceStream);
        SourceContextValidator.Validate(source, graph, graphBytes);
        var observationDigest = Convert.ToHexStringLower(SHA256.HashData(observationBytes));
        if (source.ObservationBundleDigest != observationDigest || source.ScanConfigurationDigest != observations.ScanConfigurationDigest ||
            source.Repository != observations.Repository || !source.Scanners.SequenceEqual(observations.Scanners))
            throw new InvalidDataException("The source-context artifact is stale or does not match the latest successful scan. Scan the repository again.");
        return new(graph, source, Limits);
    }

    private static byte[] ReadBounded(string path, int limit, string description)
    {
        var file = new FileInfo(path);
        if (file.Length > limit) throw new InvalidDataException($"The {description} exceeds its {limit}-byte startup limit.");
        return File.ReadAllBytes(path);
    }

    internal static CallToolResult Call(ArchitectureContextService service, CallToolRequestParams request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = request.Arguments ?? new Dictionary<string, JsonElement>();
            var presentation = request.Name switch
            {
                "archie_get_context" => ParsePresentation(arguments, ContextPresentation),
                "archie_get_dependencies" => ParsePresentation(arguments, DependencyPresentation),
                "archie_trace_flow" => ParsePresentation(arguments, FlowPresentation),
                "archie_assess_change_impact" => ParsePresentation(arguments, ImpactPresentation),
                _ => null
            };
            object result = request.Name switch
            {
                "archie_get_context" => GetContext(service, arguments),
                "archie_get_dependencies" => GetDependencies(service, arguments),
                "archie_trace_flow" => TraceFlow(service, arguments),
                "archie_assess_change_impact" => AssessImpact(service, arguments),
                _ => throw new ArgumentException($"Unknown Archie tool '{Safe(request.Name)}'.")
            };
            if (presentation is { Detail: not ResponseDetail.Full })
            {
                var detail = presentation.Detail == ResponseDetail.Summary
                    ? CompactContextDetail.Summary
                    : CompactContextDetail.Evidence;
                return result switch
                {
                    ContextLookupResult context => CompactResult(service.RenderCompact(context, detail, presentation.MaxItems)),
                    DependencyResult dependencies => CompactResult(service.RenderCompact(dependencies, detail, presentation.MaxItems)),
                    FlowTraceResult flow => CompactResult(service.RenderCompact(flow, detail, presentation.MaxItems)),
                    ChangeImpactResult impact => CompactResult(service.RenderCompact(impact, detail, presentation.MaxItems)),
                    _ => throw new InvalidOperationException("Compact presentation is unsupported for this result.")
                };
            }
            return FullResult(result);
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or GraphQueryLimitException or ArtifactValidationException)
        {
            var error = new { error = ErrorCode(exception), message = Safe(exception.Message) };
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(error, ContractJson.Options) }],
                StructuredContent = JsonSerializer.SerializeToElement(error, ContractJson.Options),
                IsError = true
            };
        }
    }

    private static PresentationOptions ParsePresentation(
        IDictionary<string, JsonElement> arguments,
        PresentationLimit limit)
    {
        var detail = ResponseDetail.Summary;
        if (arguments.TryGetValue("detail", out var detailValue))
        {
            if (detailValue.ValueKind != JsonValueKind.String || detailValue.GetString() is not { } text ||
                !Enum.TryParse<ResponseDetail>(text, ignoreCase: true, out detail) ||
                !string.Equals(text, detail.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("detail must be one of: summary, evidence, full.");
        }

        var maxItems = limit.DefaultMaxItems;
        if (arguments.TryGetValue("maxItems", out var maxItemsValue) &&
            (maxItemsValue.ValueKind != JsonValueKind.Number || !maxItemsValue.TryGetInt32(out maxItems)))
            throw new ArgumentException("maxItems must be an integer.");
        if (maxItems < 1 || maxItems > limit.MaximumMaxItems)
            throw new ArgumentException($"maxItems must be between 1 and {limit.MaximumMaxItems}.");
        return new(detail, maxItems);
    }

    private static CallToolResult CompactResult(string markdown) => new()
    {
        Content = [new TextContentBlock { Text = markdown }],
        IsError = false
    };

    private static CallToolResult FullResult(object result)
    {
        var structured = JsonSerializer.SerializeToElement(result, ContractJson.Options);
        var json = JsonSerializer.Serialize(structured, ContractJson.Options);
        if (Encoding.UTF8.GetByteCount(json) > Limits.MaxResponseBytes)
            throw new GraphQueryLimitException("RESPONSE_LIMIT_EXCEEDED", $"The result exceeds {Limits.MaxResponseBytes} bytes; no partial result was returned.");
        var markdown = result switch
        {
            FlowTraceResult flow => flow.Markdown,
            ChangeImpactResult impact => impact.Markdown,
            _ => json
        };
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = markdown }],
            StructuredContent = structured,
            IsError = false
        };
    }

    private static ContextLookupResult GetContext(ArchitectureContextService service, IDictionary<string, JsonElement> arguments)
    {
        RejectUnknown(arguments, "path", "line", "subjectId", "detail", "maxItems");
        return service.GetContext(OptionalString(arguments, "path"), OptionalInt(arguments, "line"), OptionalString(arguments, "subjectId"));
    }

    private static DependencyResult GetDependencies(ArchitectureContextService service, IDictionary<string, JsonElement> arguments)
    {
        RejectUnknown(arguments, "nodeId", "direction", "depth", "nodeKinds", "edgeKinds", "detail", "maxItems");
        var direction = ParseEnum(arguments, "direction", DependencyDirection.Both);
        return service.GetDependencies(RequiredString(arguments, "nodeId"), direction, OptionalInt(arguments, "depth") ?? 1,
            ParseEnums<NodeKind>(arguments, "nodeKinds"), ParseEnums<EdgeKind>(arguments, "edgeKinds"));
    }

    private static FlowTraceResult TraceFlow(ArchitectureContextService service, IDictionary<string, JsonElement> arguments)
    {
        RejectUnknown(arguments, "from", "to", "direction", "maxDepth", "edgeKinds", "detail", "maxItems");
        var direction = ParseEnum(arguments, "direction", PathDirection.Downstream);
        return service.TraceFlow(RequiredString(arguments, "from"), RequiredString(arguments, "to"), direction,
            OptionalInt(arguments, "maxDepth") ?? 8, ParseEnums<EdgeKind>(arguments, "edgeKinds"));
    }

    private static ChangeImpactResult AssessImpact(ArchitectureContextService service, IDictionary<string, JsonElement> arguments)
    {
        RejectUnknown(arguments, "changes", "downstreamDepth", "detail", "maxItems");
        if (!arguments.TryGetValue("changes", out var value) || value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("changes must be an array.");
        var changes = value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each change must be an object.");
            var properties = item.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            RejectUnknown(properties, "path", "startLine", "endLine");
            return new ChangedSource(RequiredString(properties, "path"), OptionalInt(properties, "startLine"), OptionalInt(properties, "endLine"));
        }).ToArray();
        return service.AssessChangeImpact(changes, OptionalInt(arguments, "downstreamDepth") ?? 3);
    }

    private static string RequiredString(IDictionary<string, JsonElement> arguments, string name) =>
        OptionalString(arguments, name) ?? throw new ArgumentException($"{name} is required.");

    private static string? OptionalString(IDictionary<string, JsonElement> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 1024)
            throw new ArgumentException($"{name} must be a non-empty string of at most 1024 characters.");
        return value.GetString();
    }

    private static int? OptionalInt(IDictionary<string, JsonElement> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new ArgumentException($"{name} must be an integer.");
        return result;
    }

    private static T ParseEnum<T>(IDictionary<string, JsonElement> arguments, string name, T defaultValue) where T : struct, Enum
    {
        var value = OptionalString(arguments, name);
        if (value is null) return defaultValue;
        return EnumValues<T>().TryGetValue(value, out var parsed) ? parsed :
            throw new ArgumentException($"{name} has unsupported value '{Safe(value)}'.");
    }

    private static IReadOnlySet<T> ParseEnums<T>(IDictionary<string, JsonElement> arguments, string name) where T : struct, Enum
    {
        if (!arguments.TryGetValue(name, out var value)) return new HashSet<T>();
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 32) throw new ArgumentException($"{name} must be an array of at most 32 values.");
        var values = EnumValues<T>();
        var result = new HashSet<T>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || !values.TryGetValue(text, out var parsed))
                throw new ArgumentException($"{name} contains an unsupported value.");
            result.Add(parsed);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, T> EnumValues<T>() where T : struct, Enum =>
        Enum.GetValues<T>().ToDictionary(value => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString()), StringComparer.Ordinal);

    private static void RejectUnknown(IDictionary<string, JsonElement> arguments, params string[] allowed)
    {
        var accepted = allowed.ToHashSet(StringComparer.Ordinal);
        var unknown = arguments.Keys.FirstOrDefault(key => !accepted.Contains(key));
        if (unknown is not null) throw new ArgumentException($"Unknown argument '{Safe(unknown)}'.");
    }

    private static string ErrorCode(Exception exception) => exception switch
    {
        GraphQueryLimitException limit => limit.Code,
        KeyNotFoundException => "SUBJECT_NOT_FOUND",
        ArtifactValidationException => "INVALID_PATH",
        _ => "INVALID_ARGUMENT"
    };

    private static string Safe(string? value)
    {
        var safe = (value ?? "Invalid request.").Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return safe.Length <= 2048 ? safe : safe[..2048];
    }

    internal static IReadOnlyList<Tool> CreateTools()
    {
        var readOnly = new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false };
        return
        [
            Tool("archie_get_context", "Get evidence-backed context for one repository-relative path or canonical subject. Returns a compact summary by default; use detail 'evidence' or 'full' to expand it.",
                """{"type":"object","additionalProperties":false,"properties":{"path":{"type":"string"},"line":{"type":"integer","minimum":1},"subjectId":{"type":"string"},"detail":{"enum":["summary","evidence","full"],"default":"summary"},"maxItems":{"type":"integer","minimum":1,"maximum":25,"default":6}},"oneOf":[{"required":["path"]},{"required":["subjectId"]}]}""", readOnly),
            Tool("archie_get_dependencies", "Get a bounded logical dependency neighborhood for one canonical node. Returns a compact summary by default; use detail 'evidence' or 'full' to expand it.",
                """{"type":"object","additionalProperties":false,"required":["nodeId"],"properties":{"nodeId":{"type":"string"},"direction":{"enum":["upstream","downstream","both"]},"depth":{"type":"integer","minimum":1,"maximum":12},"nodeKinds":{"type":"array","maxItems":32,"items":{"type":"string"}},"edgeKinds":{"type":"array","maxItems":32,"items":{"type":"string"}},"detail":{"enum":["summary","evidence","full"],"default":"summary"},"maxItems":{"type":"integer","minimum":1,"maximum":50,"default":12}}}""", readOnly),
            Tool("archie_trace_flow", "Trace bounded validated graph paths. Returns a compact summary by default; use detail 'evidence' or 'full' to expand it.",
                """{"type":"object","additionalProperties":false,"required":["from","to"],"properties":{"from":{"type":"string"},"to":{"type":"string"},"direction":{"enum":["upstream","downstream"]},"maxDepth":{"type":"integer","minimum":1,"maximum":12},"edgeKinds":{"type":"array","maxItems":32,"items":{"type":"string"}},"detail":{"enum":["summary","evidence","full"],"default":"summary"},"maxItems":{"type":"integer","minimum":1,"maximum":10,"default":3}}}""", readOnly),
            Tool("archie_assess_change_impact", "Assess qualified potential static impact by category. Returns a compact summary by default; use detail 'evidence' or 'full' to expand it.",
                """{"type":"object","additionalProperties":false,"required":["changes"],"properties":{"changes":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"object","additionalProperties":false,"required":["path"],"properties":{"path":{"type":"string"},"startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1}}}},"downstreamDepth":{"type":"integer","minimum":1,"maximum":12},"detail":{"enum":["summary","evidence","full"],"default":"summary"},"maxItems":{"type":"integer","minimum":1,"maximum":25,"default":8}}}""", readOnly)
        ];
    }

    private static Tool Tool(string name, string description, string schema, ToolAnnotations annotations) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonDocument.Parse(schema).RootElement.Clone(),
        Annotations = annotations
    };
}
