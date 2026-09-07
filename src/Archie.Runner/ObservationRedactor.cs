using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Archie.Runner;

public sealed record RedactionResult(Observation Observation, IReadOnlyList<RedactionEvent> Redactions);
public sealed record DiagnosticRedactionResult(Diagnostic Diagnostic, IReadOnlyList<RedactionEvent> Redactions);
public sealed record SourceOwnershipRedactionResult(SourceOwnershipClaim Ownership, IReadOnlyList<RedactionEvent> Redactions);

public sealed partial class ObservationRedactor
{
    public RedactionResult Redact(Observation observation, DiscoveredScanner scanner, string repositoryRoot)
    {
        var events = new List<RedactionEvent>();
        var evidence = RedactEvidence(observation.Evidence, observation.Id, scanner, repositoryRoot, events);
        Observation redacted = observation switch
        {
            EntityObservation entity => entity with
            {
                Id = SafeId(entity.Id, "observation.id", events),
                Evidence = evidence,
                Entity = RedactCandidate(entity.Entity, "entity", events)
            },
            RelationshipObservation relationship => relationship with
            {
                Id = SafeId(relationship.Id, "observation.id", events),
                Evidence = evidence,
                From = RedactCandidate(relationship.From, "from", events),
                To = RedactCandidate(relationship.To, "to", events),
                Properties = RedactProperties(relationship.Properties, "properties", events)
            },
            _ => throw new InvalidDataException($"Unsupported observation type '{observation.GetType().Name}'.")
        };
        redacted = redacted switch
        {
            EntityObservation entity => entity with { Evidence = entity.Evidence with { ClaimId = entity.Id } },
            RelationshipObservation relationship => relationship with { Evidence = relationship.Evidence with { ClaimId = relationship.Id } },
            _ => redacted
        };
        return new(redacted, events);
    }

    public static string SanitizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sanitized = AssignmentSecret().Replace(text, "$1=[REDACTED]");
        sanitized = ConnectionStringSecret().Replace(sanitized, "$1=[REDACTED]");
        sanitized = BearerSecret().Replace(sanitized, "Bearer [REDACTED]");
        return sanitized;
    }

    public DiagnosticRedactionResult RedactDiagnostic(Diagnostic diagnostic)
    {
        var events = new List<RedactionEvent>();
        var code = SafeDiagnosticCode(diagnostic.Code, events);
        return new(diagnostic with
        {
            Id = SafeId(diagnostic.Id, "diagnostic.id", events),
            Code = code,
            Severity = SafeDiagnosticSeverity(diagnostic.Severity, events),
            Message = SafeText(diagnostic.Message, "diagnostic.message", events),
            SubjectId = diagnostic.SubjectId is null ? null : SafeId(diagnostic.SubjectId, "diagnostic.subjectId", events)
        }, events);
    }

    public SourceOwnershipRedactionResult RedactSourceOwnership(
        SourceOwnershipClaim ownership,
        DiscoveredScanner scanner,
        string repositoryRoot)
    {
        var events = new List<RedactionEvent>();
        var path = SafeRepositoryPath(ownership.Path, "sourceOwnership.path", repositoryRoot, events);
        return new(ownership with
        {
            ScannerId = scanner.Manifest.Id,
            ScannerVersion = scanner.Manifest.Version,
            Path = path,
            OwnerCandidateKey = SafeId(ownership.OwnerCandidateKey, "sourceOwnership.ownerCandidateKey", events),
            DerivationRule = SafeText(ownership.DerivationRule, "sourceOwnership.derivationRule", events)
        }, events);
    }

    public static Diagnostic SanitizeDiagnostic(Diagnostic diagnostic) => new ObservationRedactor().RedactDiagnostic(diagnostic).Diagnostic;

    private static Evidence RedactEvidence(
        Evidence evidence,
        string observationId,
        DiscoveredScanner scanner,
        string repositoryRoot,
        ICollection<RedactionEvent> events)
    {
        var path = SafeRepositoryPath(evidence.Path, "evidence.path", repositoryRoot, events);
        return evidence with
        {
            Id = SafeId(evidence.Id, "evidence.id", events),
            ClaimId = SafeId(observationId, "evidence.claimId", events),
            ScannerId = scanner.Manifest.Id,
            ScannerVersion = scanner.Manifest.Version,
            ExtractionMethod = SafeText(evidence.ExtractionMethod, "evidence.extractionMethod", events),
            Path = path,
            Properties = RedactProperties(evidence.Properties, "evidence.properties", events)
        };
    }

    private static string SafeRepositoryPath(
        string value,
        string field,
        string repositoryRoot,
        ICollection<RedactionEvent> events)
    {
        var path = value.Replace('\\', '/');
        if (Path.IsPathRooted(path) || WindowsAbsolutePath().IsMatch(path) || path.StartsWith("//", StringComparison.Ordinal))
        {
            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(repositoryRoot, fullPath).Replace('\\', '/');
            path = relative.StartsWith("../", StringComparison.Ordinal) ? "redacted-path" : relative;
            AddEvent(events, "absolute-path", field, "Removed an absolute checkout path.");
        }
        if (path.Split('/').Contains("..", StringComparer.Ordinal))
        {
            path = "redacted-path";
            AddEvent(events, "unsafe-path", field, "Removed an unsafe repository path.");
        }
        if (SanitizeText(path) != path || HasSensitiveMarker(path))
        {
            path = "redacted-path";
            AddEvent(events, "sensitive-path", field, "Removed sensitive data from a repository path.");
        }
        return path;
    }

    private static EntityCandidate RedactCandidate(EntityCandidate candidate, string path, ICollection<RedactionEvent> events) => candidate with
    {
        Key = SafeId(candidate.Key, $"{path}.key", events),
        ExplicitId = candidate.ExplicitId is null ? null : SafeId(candidate.ExplicitId, $"{path}.explicitId", events),
        Name = SafeText(candidate.Name, $"{path}.name", events),
        IdentitySignals = RedactIdentitySignals(candidate.IdentitySignals, $"{path}.identitySignals", events),
        Properties = RedactProperties(candidate.Properties, $"{path}.properties", events)
    };

    private static IReadOnlyDictionary<string, string> RedactIdentitySignals(
        IReadOnlyDictionary<string, string> signals,
        string path,
        ICollection<RedactionEvent> events)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in signals)
        {
            var sensitive = HasSensitiveMarker(pair.Key) || SanitizeText(pair.Key) != pair.Key;
            var key = SafeKey(pair.Key, $"{path}.key", events);
            result[key] = sensitive
                ? RedactedValue(pair.Value, $"{path}.{key}", events)
                : SafeText(pair.Value, $"{path}.{key}", events);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> RedactProperties(
        IReadOnlyDictionary<string, JsonElement> properties,
        string path,
        ICollection<RedactionEvent> events)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in properties)
        {
            var sensitive = HasSensitiveMarker(pair.Key) || SanitizeText(pair.Key) != pair.Key;
            var key = SafeKey(pair.Key, $"{path}.key", events);
            result[key] = RedactElement(pair.Value, $"{path}.{key}", sensitive, events);
        }
        return result;
    }

    private static JsonElement RedactElement(JsonElement value, string path, bool sensitive, ICollection<RedactionEvent> events)
    {
        if (sensitive)
        {
            AddEvent(events, "sensitive-value", path, "Removed a value whose key denotes sensitive data.");
            return JsonSerializer.SerializeToElement("[REDACTED]");
        }
        return value.ValueKind switch
        {
            JsonValueKind.Object => JsonSerializer.SerializeToElement(RedactProperties(
                value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal),
                path,
                events)),
            JsonValueKind.Array => JsonSerializer.SerializeToElement(value.EnumerateArray().Select((item, index) =>
                RedactElement(item, $"{path}[{index}]", false, events)).ToArray()),
            JsonValueKind.String when path.EndsWith(".configurationKey", StringComparison.Ordinal) =>
                RedactConfigurationKey(value.GetString()!, path, events),
            JsonValueKind.String => JsonSerializer.SerializeToElement(SafeText(value.GetString()!, path, events)),
            _ => value.Clone()
        };
    }

    private static string RedactedValue(string value, string path, ICollection<RedactionEvent> events)
    {
        AddEvent(events, "sensitive-value", path, "Removed a value whose key denotes sensitive data.");
        return "[REDACTED]";
    }

    private static JsonElement RedactConfigurationKey(string value, string path, ICollection<RedactionEvent> events)
    {
        if (IsSafeConfigurationKey(value)) return JsonSerializer.SerializeToElement(value);
        AddEvent(events, "sensitive-value", path, "Removed an unsafe or sensitive configuration key.");
        return JsonSerializer.SerializeToElement("[REDACTED]");
    }

    private static string SafeText(string value, string path, ICollection<RedactionEvent> events)
    {
        var sanitized = SanitizeText(value);
        if (sanitized != value)
        {
            AddEvent(events, "secret-pattern", path, "Removed a value matching a sensitive-data pattern.");
            return sanitized;
        }
        if (HasSensitiveMarker(value))
        {
            AddEvent(events, "sensitive-string", path, "Removed a string containing a sensitive-data marker.");
            return "[REDACTED]";
        }
        return sanitized;
    }

    private static bool IsSafeConfigurationKey(string value)
    {
        if (!TryNormalize(value, out var normalizedValue) || string.IsNullOrWhiteSpace(normalizedValue) || normalizedValue.Length > 256 ||
            normalizedValue.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not (':' or '.' or '-' or '_'))) return false;
        var name = normalizedValue.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)
            ? normalizedValue["ConnectionStrings:".Length..] : normalizedValue;
        if (name.Split([':', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.Equals("auth", StringComparison.OrdinalIgnoreCase))) return false;
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return !new[]
        {
            "password", "passwd", "pwd", "token", "secret", "credential", "apikey", "authorization", "cookie",
            "privatekey", "connectionstring"
        }.Any(normalized.Contains);
    }

    private static string SafeId(string value, string path, ICollection<RedactionEvent> events)
    {
        if (SanitizeText(value) == value && !HasSensitiveMarker(value)) return value;
        AddEvent(events, "secret-pattern", path, "Replaced an identifier containing sensitive data.");
        return $"redacted:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16]}";
    }

    private static string SafeKey(string value, string path, ICollection<RedactionEvent> events)
    {
        if (SanitizeText(value) == value && !HasSensitiveMarker(value)) return value;
        AddEvent(events, "sensitive-key", path, "Replaced a key containing sensitive data.");
        return $"redacted-key-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16]}";
    }

    private static string SafeDiagnosticCode(string value, ICollection<RedactionEvent> events)
    {
        if (DiagnosticCode().IsMatch(value) && SanitizeText(value) == value && !HasSensitiveMarker(value)) return value;
        AddEvent(events, "unsafe-diagnostic-code", "diagnostic.code", "Replaced an unsafe diagnostic code.");
        return "SCANNER_DIAGNOSTIC_REDACTED";
    }

    private static string SafeDiagnosticSeverity(string value, ICollection<RedactionEvent> events)
    {
        if (value is "info" or "warning" or "error") return value;
        AddEvent(events, "unsafe-diagnostic-severity", "diagnostic.severity", "Replaced an unsafe diagnostic severity.");
        return "warning";
    }

    private static void AddEvent(ICollection<RedactionEvent> events, string kind, string path, string message)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\0{path}")))[..16];
        events.Add(new($"redaction:{id}", kind, path, message));
    }

    private static bool HasSensitiveMarker(string value) =>
        !TryNormalize(value, out var normalized) || SensitiveKey().IsMatch(normalized);

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

    [GeneratedRegex("(?i)(password|passwd|pwd|token|secret|credential|api[-_.]?key|authorization|cookie|private[-_.]?key|connection[-_.]?string|(?:^|[:._-])auth(?:$|[:._-]))")]
    private static partial Regex SensitiveKey();

    [GeneratedRegex("(?i)\\b(password|passwd|pwd|token|secret|api[-_]?key|client[-_]?secret)\\s*[:=]\\s*[^;,\\s]+")]
    private static partial Regex AssignmentSecret();

    [GeneratedRegex("(?i)\\b(Password|Pwd|User ID|AccountKey|SharedAccessKey)\\s*=\\s*[^;]+")]
    private static partial Regex ConnectionStringSecret();

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex BearerSecret();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,127}$")]
    private static partial Regex DiagnosticCode();

    [GeneratedRegex("^[A-Za-z]:/")]
    private static partial Regex WindowsAbsolutePath();
}
