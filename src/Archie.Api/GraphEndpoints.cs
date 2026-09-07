using System.Text.Json;
using System.Text;
using Archie.Contracts;
using Archie.Core;

namespace Archie.Api;

public sealed record GraphNodeResponse(
    string Id,
    string Kind,
    string Name,
    string Description,
    string Resolution,
    string Environment,
    string Purpose,
    string Owner,
    IReadOnlyList<string> Glossary,
    IReadOnlyList<string> Onboarding);

public sealed record EvidenceResponse(
    string Id,
    string Repository,
    string Revision,
    string Path,
    int StartLine,
    int EndLine,
    string ExtractionMethod,
    string Provenance,
    string Confidence,
    string? Owner,
    string? Reviewer,
    string? ReviewStatus,
    string? Rationale,
    string? SourceUrl);

public sealed record GraphEdgeResponse(
    string Id,
    string Kind,
    string From,
    string To,
    string Label,
    string Confidence,
    string Resolution,
    IReadOnlyList<string> Provenance,
    IReadOnlyDictionary<string, JsonElement> Properties,
    IReadOnlyList<EvidenceResponse> Evidence);

public sealed record GraphResponse(
    string SchemaVersion,
    string SnapshotId,
    string Workspace,
    string Description,
    IReadOnlyList<GraphNodeResponse> Nodes,
    IReadOnlyList<GraphEdgeResponse> Edges);

public sealed record NodeDetailsResponse(
    GraphNodeResponse Node,
    IReadOnlyList<string> IncomingEdgeIds,
    IReadOnlyList<string> OutgoingEdgeIds,
    IReadOnlyList<string> UpstreamEdgeIds,
    IReadOnlyList<string> DownstreamEdgeIds);

public sealed record PathResponse(IReadOnlyList<string> NodeIds, IReadOnlyList<string> EdgeIds);
public sealed record SavedViewSummaryResponse(string Id, string Name, string Description);
public sealed record DiagnosticResponse(
    string Id,
    string Code,
    string Severity,
    string Message,
    string? SubjectId);

public static class GraphEndpoints
{
    public const string RelationshipId = "edge:checkout-publishes-order-submitted";
    private const int MaxSavedViews = 100;
    private const long MaxSavedViewBytes = 1_048_576;

    public static IEndpointRouteBuilder MapGraphEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/workspace", (LoadedGraph graph) => Results.Ok(new
        {
            id = "book-retail",
            name = "Book Retail",
            mode = "canonical-artifact",
            schemaVersion = graph.Snapshot.SchemaVersion,
            snapshotId = graph.Snapshot.Id
        }));

        endpoints.MapGet("/api/v1/graph", (HttpRequest request, LoadedGraph graph) =>
        {
            var parsed = ParseGraphQuery(request.Query, graph.Limits);
            if (parsed.Error is not null) return parsed.Error;
            try
            {
                var result = new GraphQueryService(graph.Snapshot, graph.Indexes).Query(parsed.Query!);
                return Results.Ok(ToGraphResponse(graph, result));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (GraphQueryLimitException exception) { return LimitError(exception); }
        });

        endpoints.MapGet("/api/v1/paths", (HttpRequest request, LoadedGraph graph) =>
        {
            var from = request.Query["from"].ToString();
            if (string.IsNullOrWhiteSpace(from))
                return Results.BadRequest(new { code = "PATH_QUERY_INVALID", message = "from is required." });
            if (!TryEnum(request.Query["direction"].ToString(), PathDirection.Downstream, out PathDirection direction) ||
                !TryInt(request.Query["maxDepth"].ToString(), graph.Limits.DefaultPathDepth, 1, graph.Limits.MaxPathDepth, out var maxDepth) ||
                !TryEnums(request.Query["edgeKind"].ToString(), out HashSet<EdgeKind> edgeKinds))
                return Results.BadRequest(new { code = "PATH_QUERY_INVALID", message = $"direction, edgeKind, or maxDepth is invalid; maxDepth must be 1-{graph.Limits.MaxPathDepth}." });
            try
            {
                var service = new GraphQueryService(graph.Snapshot, graph.Indexes, graph.Limits.PathLimits);
                var paths = service.FindPaths(new PathQuery(from, EmptyToNull(request.Query["to"]), direction, maxDepth, edgeKinds));
                return Results.Ok(paths.Select(path => new PathResponse(path.NodeIds, path.EdgeIds)).ToArray());
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (GraphQueryLimitException exception) { return LimitError(exception); }
        });

        endpoints.MapGet("/api/v1/nodes/{nodeId}", (string nodeId, LoadedGraph graph) =>
        {
            if (!graph.Indexes.Nodes.TryGetValue(nodeId, out var node)) return Results.NotFound();
            return Results.Ok(new NodeDetailsResponse(
                ToNodeResponse(node),
                graph.Indexes.Incoming[nodeId].Select(edge => edge.Id).ToArray(),
                graph.Indexes.Outgoing[nodeId].Select(edge => edge.Id).ToArray(),
                graph.Indexes.Upstream[nodeId].Select(edge => edge.Id).ToArray(),
                graph.Indexes.Downstream[nodeId].Select(edge => edge.Id).ToArray()));
        });

        endpoints.MapGet("/api/v1/edges/{edgeId}", (string edgeId, LoadedGraph graph) =>
            graph.Indexes.Edges.TryGetValue(edgeId, out var edge)
                ? Results.Ok(ToEdgeResponse(edge, graph))
                : Results.NotFound());

        endpoints.MapGet("/api/v1/diagnostics", (LoadedGraph graph) =>
            Results.Ok(graph.Snapshot.Diagnostics
                .OrderBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
                .Select(diagnostic => new DiagnosticResponse(
                    diagnostic.Id,
                    diagnostic.Code,
                    diagnostic.Severity,
                    diagnostic.Message,
                    diagnostic.SubjectId))
                .ToArray()));

        endpoints.MapGet("/api/v1/views", (LoadedGraph graph, IConfiguration configuration) =>
            Results.Ok(LoadSavedViews(graph, configuration)
                .Select(view => new SavedViewSummaryResponse(view.Id, view.Name, view.Description))
                .ToArray()));

        endpoints.MapGet("/api/v1/views/{viewId}", (string viewId, LoadedGraph graph, IConfiguration configuration) =>
        {
            var view = LoadSavedViews(graph, configuration)
                .SingleOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, viewId));
            return view is null ? Results.NotFound() : Results.Json(view, ContractJson.Options);
        });

        return endpoints;
    }

    private static IReadOnlyList<SavedView> LoadSavedViews(LoadedGraph graph, IConfiguration configuration)
    {
        var directory = configuration["AIP_VIEW_PATH"] ?? Path.Combine(AppContext.BaseDirectory, "fixtures", "views");
        if (!Directory.Exists(directory)) return [];
        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Take(MaxSavedViews + 1).ToArray();
        if (files.Length > MaxSavedViews)
            throw new InvalidDataException($"The saved-view directory exceeds its {MaxSavedViews}-view limit.");

        var views = files.Select(path =>
        {
            var file = new FileInfo(path);
            if (file.Length > MaxSavedViewBytes)
                throw new InvalidDataException($"Saved view '{file.Name}' exceeds its {MaxSavedViewBytes}-byte limit.");
            return JsonSerializer.Deserialize<SavedView>(File.ReadAllBytes(path), ContractJson.Options)
                ?? throw new InvalidDataException($"Saved view '{file.Name}' is empty.");
        }).ToArray();

        foreach (var view in views) ValidateSavedView(view, graph);
        if (views.Select(view => view.Id).Distinct(StringComparer.Ordinal).Count() != views.Length)
            throw new InvalidDataException("Saved view IDs must be unique.");
        return views.OrderBy(view => view.Id, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateSavedView(SavedView view, LoadedGraph graph)
    {
        var selectedExists = view.SelectedId is null || graph.Indexes.Nodes.ContainsKey(view.SelectedId) || graph.Indexes.Edges.ContainsKey(view.SelectedId);
        if (view.SchemaVersion != "architecture-view/v1" ||
            string.IsNullOrWhiteSpace(view.Id) || view.Id.Any(char.IsWhiteSpace) ||
            string.IsNullOrWhiteSpace(view.Name) || string.IsNullOrWhiteSpace(view.Description) ||
            view.LayoutDirection is not ("RIGHT" or "DOWN") ||
            view.PathQuery.MaxDepth is < 1 || view.PathQuery.MaxDepth > graph.Limits.MaxPathDepth ||
            !graph.Indexes.Nodes.ContainsKey(view.PathQuery.From) ||
            view.PathQuery.To is not null && !graph.Indexes.Nodes.ContainsKey(view.PathQuery.To) ||
            !selectedExists)
            throw new InvalidDataException($"Saved view '{view.Id}' is invalid for the loaded canonical graph.");
    }

    private static (GraphQuery? Query, IResult? Error) ParseGraphQuery(IQueryCollection values, GraphRuntimeLimits limits)
    {
        if (!TryInt(values["nodeLimit"], limits.DefaultSubgraphNodes, 1, limits.MaxSubgraphNodes, out var nodeLimit) ||
            !TryInt(values["edgeLimit"], limits.DefaultSubgraphEdges, 1, limits.MaxSubgraphEdges, out var edgeLimit) ||
            !TryInt(values["depth"], 2, 0, limits.MaxPathDepth, out var depth) ||
            !TryEnums(values["nodeKind"], out HashSet<NodeKind> nodeKinds) ||
            !TryEnums(values["edgeKind"], out HashSet<EdgeKind> edgeKinds) ||
            !TryEnums(values["confidence"], out HashSet<Confidence> confidence) ||
            !TryEnums(values["resolution"], out HashSet<Resolution> resolution))
        {
            return (null, Results.BadRequest(new
            {
                code = "QUERY_LIMIT_INVALID",
                message = $"Query filters are invalid; nodeLimit must be 1-{limits.MaxSubgraphNodes}, edgeLimit 1-{limits.MaxSubgraphEdges}, and depth 0-{limits.MaxPathDepth}."
            }));
        }

        return (new GraphQuery(
            EmptyToNull(values["root"]), depth, nodeKinds, edgeKinds, confidence, resolution,
            EmptyToNull(values["environment"]), nodeLimit, edgeLimit), null);
    }

    private static GraphResponse ToGraphResponse(LoadedGraph graph, GraphSubgraph subgraph) => new(
        graph.Snapshot.SchemaVersion,
        graph.Snapshot.Id,
        "Book Retail",
        "Evidence-backed architecture for exploring the place-an-order journey.",
        subgraph.Nodes.Select(ToNodeResponse).ToArray(),
        subgraph.Edges.Select(edge => ToEdgeResponse(edge, graph)).ToArray());

    private static GraphNodeResponse ToNodeResponse(GraphNode node) => new(
        node.Id,
        ToContractValue(node.Kind),
        node.Name,
        PropertyString(node.Properties, "description"),
        ToContractValue(node.Resolution),
        PropertyString(node.Properties, "environment"),
        PropertyString(node.Properties, "purpose"),
        PropertyString(node.Properties, "owner"),
        PropertyStrings(node.Properties, "glossary"),
        PropertyStrings(node.Properties, "onboarding"));

    private static GraphEdgeResponse ToEdgeResponse(GraphEdge edge, LoadedGraph graph) => new(
        edge.Id,
        ToContractValue(edge.Kind),
        edge.From,
        edge.To,
        PropertyString(edge.Properties, "label"),
        ToContractValue(edge.Confidence),
        ToContractValue(edge.Resolution),
        edge.EvidenceIds.Select(id => EvidenceSourceType(graph.Indexes.Evidence[id])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        SafeRelationshipProperties(edge.Properties),
        edge.EvidenceIds.Select(id => ToEvidenceResponse(graph.Indexes.Evidence[id], graph)).ToArray());

    private static IReadOnlyDictionary<string, JsonElement> SafeRelationshipProperties(
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        AddString("provider", value => IsLowerIdentifier(value));
        AddString("resourceKind", value => IsLowerIdentifier(value));
        AddString("scheme", value => value is "http" or "https");
        AddString("host", value => value == value.ToLowerInvariant() &&
            Uri.CheckHostName(value) != UriHostNameType.Unknown);
        AddString("contextType", value => value.Length <= 256 &&
            value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '+'));
        AddString("configurationKey", IsSafeConfigurationKey, rejectSensitiveMarker: false);
        if (properties.TryGetValue("port", out var port) && port.ValueKind == JsonValueKind.Number &&
            port.TryGetInt32(out var number) && number is >= 1 and <= 65535)
            result["port"] = JsonSerializer.SerializeToElement(number);
        return result;

        void AddString(string name, Func<string, bool> validate, bool rejectSensitiveMarker = true)
        {
            if (!properties.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String) return;
            var text = value.GetString()!;
            if (text.Length is < 1 or > 256 || rejectSensitiveMarker && ContainsSensitiveMarker(text) || !validate(text)) return;
            result[name] = JsonSerializer.SerializeToElement(text);
        }
    }

    private static bool IsLowerIdentifier(string value) =>
        char.IsLower(value[0]) && value.All(character => char.IsLower(character) || char.IsDigit(character) || character is '-' or '.');

    private static bool IsSafeConfigurationKey(string value)
    {
        if (!TryNormalize(value, out var normalized) ||
            normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not (':' or '.' or '-' or '_'))) return false;
        var name = normalized.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)
            ? normalized["ConnectionStrings:".Length..] : normalized;
        return name.Length > 0 && !ContainsSensitiveMarker(name);
    }

    private static bool ContainsSensitiveMarker(string value)
    {
        if (value.Split([':', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.Equals("auth", StringComparison.OrdinalIgnoreCase))) return true;
        var normalized = new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return new[]
        {
            "password", "passwd", "pwd", "token", "secret", "credential", "apikey", "authorization", "cookie",
            "privatekey", "connectionstring"
        }.Any(normalized.Contains);
    }

    private static bool TryNormalize(string value, out string normalized)
    {
        try
        {
            normalized = value.Normalize(NormalizationForm.FormKC);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private static EvidenceResponse ToEvidenceResponse(Evidence evidence, LoadedGraph graph)
    {
        var repository = graph.Snapshot.Composition.Inputs[0].Repository;
        var sourceUrl = ApprovedWebRemote(repository.RemoteUrl) is { } remote ?
            $"{remote}/blob/{repository.Revision}/{evidence.Path}#L{evidence.Range?.StartLine ?? 1}" : null;
        return new(
            evidence.Id, repository.RepositoryId, repository.Revision, evidence.Path,
            evidence.Range?.StartLine ?? 0, evidence.Range?.EndLine ?? 0, evidence.ExtractionMethod,
            EvidenceSourceType(evidence), ToContractValue(evidence.Confidence),
            PropertyNullableString(evidence.Properties, "owner"),
            PropertyNullableString(evidence.Properties, "reviewer"),
            PropertyNullableString(evidence.Properties, "reviewStatus"),
            PropertyNullableString(evidence.Properties, "rationale"),
            sourceUrl);
    }

    private static string? ApprovedWebRemote(string? remote)
    {
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) || uri.IsLoopback ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static string EvidenceSourceType(Evidence evidence) => evidence.Provenance switch
    {
        EvidenceProvenance.Manual => "manual",
        EvidenceProvenance.Deterministic when evidence.ScannerId is not null => "scanner",
        EvidenceProvenance.Deterministic when PropertyNullableString(evidence.Properties, "observationSource") == "authored" => "authored",
        _ => ToContractValue(evidence.Provenance)
    };

    private static string PropertyString(IReadOnlyDictionary<string, JsonElement> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;

    private static string? PropertyNullableString(IReadOnlyDictionary<string, JsonElement> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> PropertyStrings(IReadOnlyDictionary<string, JsonElement> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];

    private static IResult LimitError(GraphQueryLimitException exception) => Results.Json(new
    {
        code = exception.Code,
        message = exception.Message
    }, statusCode: StatusCodes.Status422UnprocessableEntity);

    private static bool TryInt(string? value, int defaultValue, int minimum, int maximum, out int result)
    {
        if (string.IsNullOrEmpty(value)) result = defaultValue;
        else if (!int.TryParse(value, out result)) return false;
        return result >= minimum && result <= maximum;
    }

    private static bool TryEnums<T>(string? value, out HashSet<T> result) where T : struct, Enum
    {
        result = [];
        foreach (var item in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryEnum(item, default(T), out T parsed)) return false;
            result.Add(parsed);
        }
        return true;
    }

    private static bool TryEnum<T>(string value, T defaultValue, out T result) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(value)) { result = defaultValue; return true; }
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse(normalized, ignoreCase: true, out result);
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string ToContractValue<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
}
