using System.Security.Cryptography;
using System.Text.Json;
using Archie.Contracts;

namespace Archie.Core;

public sealed record ReconciliationResult(
    GraphSnapshot Snapshot,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<MergeDecision> MergeDecisions,
    IReadOnlyList<CandidateResolution> CandidateResolutions);

public sealed class Reconciler
{
    public ReconciliationResult Reconcile(ObservationBundle bundle, ArchitectureOverlay? overlay, DateOnly asOf)
        => ReconcileInternal($"graph:{bundle.Repository.RepositoryId}:reconciled", "single-repository", [bundle], overlay, asOf);

    public ReconciliationResult ReconcileProject(
        string projectId,
        IReadOnlyList<ObservationBundle> bundles,
        ArchitectureOverlay? overlay,
        DateOnly asOf)
    {
        if (string.IsNullOrWhiteSpace(projectId) || projectId.Any(char.IsWhiteSpace))
            throw new ArtifactValidationException(["Project ID must be a non-empty ID without whitespace."]);
        if (bundles.Count is < 1 or > 10)
            throw new ArtifactValidationException(["Project composition requires between 1 and 10 observation bundles."]);
        if (overlay is not null)
            throw new ArtifactValidationException(["Project composition overlays are deferred until repository-scoped candidate bindings are defined."]);
        return ReconcileInternal($"graph:{projectId}:reconciled", "multi-repository", bundles, overlay, asOf);
    }

    private static ReconciliationResult ReconcileInternal(
        string graphId,
        string compositionPolicy,
        IReadOnlyList<ObservationBundle> inputBundles,
        ArchitectureOverlay? overlay,
        DateOnly asOf)
    {
        foreach (var bundle in inputBundles) ObservationValidator.Validate(bundle);
        var bundles = inputBundles.OrderBy(item => item.Repository.RepositoryId, StringComparer.Ordinal).ToArray();
        if (bundles.Select(item => item.Repository.RepositoryId).Distinct(StringComparer.Ordinal).Count() != bundles.Length)
            throw new ArtifactValidationException(["Project composition cannot contain the same repository more than once."]);
        if (overlay is not null) OverlayValidator.Validate(overlay);

        var isProjectComposition = compositionPolicy == "multi-repository";
        var isMultiRepository = bundles.Length > 1;
        var observations = bundles.SelectMany(bundle => bundle.Observations.Select(observation =>
            isProjectComposition ? ScopeObservation(bundle.Repository, observation) : observation)).ToArray();
        var diagnostics = bundles.SelectMany(bundle => bundle.Diagnostics.Select(diagnostic =>
            isProjectComposition ? ScopeDiagnostic(bundle.Repository.RepositoryId, diagnostic) : diagnostic)).ToList();
        var decisions = new List<MergeDecision>();
        var activeEntries = ActiveEntries(overlay, asOf, diagnostics).ToArray();
        var overlayTargets = BuildOverlayTargets(activeEntries);
        var candidates = observations.SelectMany(Candidates).OrderBy(candidate => candidate.Key, StringComparer.Ordinal).ToArray();
        var repositoryIdsByCandidateKey = bundles
            .SelectMany(bundle => bundle.Observations.SelectMany(Candidates)
                .Select(candidate => new
                {
                    Key = isProjectComposition ? ScopeId(bundle.Repository.RepositoryId, candidate.Key) : candidate.Key,
                    bundle.Repository.RepositoryId
                }))
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.RepositoryId).Distinct(StringComparer.Ordinal).Single(), StringComparer.Ordinal);
        var explicitSignals = BuildExplicitStrongTargets(candidates);
        var weakAmbiguities = candidates
            .Where(candidate => candidate.ExplicitId is null && candidate.IdentitySignals.Count == 0)
            .GroupBy(candidate => $"{candidate.Kind}:{candidate.Name}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(candidate => candidate.Key).Distinct(StringComparer.Ordinal).Count() > 1)
            .SelectMany(group => group.Select(candidate => candidate.Key))
            .ToHashSet(StringComparer.Ordinal);
        var resolver = new IdentityResolver(repositoryIdsByCandidateKey, overlayTargets, explicitSignals, weakAmbiguities, isMultiRepository);
        var resolvedByKey = new Dictionary<string, ResolvedIdentity>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var resolved = resolver.Resolve(candidate);
            if (resolvedByKey.TryGetValue(candidate.Key, out var previous) && previous.Id != resolved.Id)
                throw new ArtifactValidationException([$"Candidate key '{candidate.Key}' resolves to both '{previous.Id}' and '{resolved.Id}'."]);
            resolvedByKey[candidate.Key] = resolved;
            if (resolved.Rationale is "unique strong identity signal" or "strong identity signal" or "governed overlay binding" or "conflicting strong identity signals" or "weak name-only match left separate")
                decisions.Add(new($"decision:{candidate.Key}", resolved.Resolution == Resolution.Ambiguous ? "ambiguous" : "merged", [candidate.Key, resolved.Id], resolved.Rationale));
            if (resolved.Resolution == Resolution.Ambiguous)
                diagnostics.Add(DiagnosticFor("IDENTITY_AMBIGUOUS", candidate.Key, $"Candidate '{candidate.Name}' remains separate because {resolved.Rationale}."));
            if (resolved.Resolution == Resolution.Unresolved)
                diagnostics.Add(DiagnosticFor("TARGET_UNRESOLVED", resolved.Id, $"Unresolved dependency '{candidate.Name}' remains visible as a placeholder."));
        }

        var evidence = new Dictionary<string, Evidence>(StringComparer.Ordinal);
        var nodeParts = new Dictionary<string, List<(EntityCandidate Candidate, Evidence Evidence)>>(StringComparer.Ordinal);
        var relationshipParts = new List<(string Id, RelationshipObservation Observation, string From, string To)>();
        foreach (var observation in observations.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            AddEvidence(evidence, observation.Evidence);
            if (observation is EntityObservation entity)
            {
                var id = resolvedByKey[entity.Entity.Key].Id;
                nodeParts.TryAdd(id, []);
                nodeParts[id].Add((entity.Entity, entity.Evidence));
            }
            else if (observation is RelationshipObservation relationship)
            {
                relationshipParts.Add((observation.Id, relationship, resolvedByKey[relationship.From.Key].Id, resolvedByKey[relationship.To.Key].Id));
                EnsureCandidateNode(nodeParts, relationship.From, resolvedByKey[relationship.From.Key], relationship.Evidence);
                EnsureCandidateNode(nodeParts, relationship.To, resolvedByKey[relationship.To.Key], relationship.Evidence);
            }
        }

        var nodes = nodeParts.Select(pair => BuildNode(pair.Key, pair.Value, resolvedByKey)).ToDictionary(node => node.Id, StringComparer.Ordinal);
        if (overlay is not null)
        {
            ApplyManualNodes(activeEntries, overlay, nodes, evidence, asOf);
            ApplyAnnotations(activeEntries, overlay, nodes, evidence, asOf);
        }
        var edges = relationshipParts.Select(part => new GraphEdge(
            part.Id, part.Observation.Relationship, part.From, part.To, part.Observation.Evidence.Confidence,
            MostUncertain(resolvedByKey[part.Observation.From.Key].Resolution, resolvedByKey[part.Observation.To.Key].Resolution),
            [part.Observation.Evidence.Provenance], part.Observation.Properties, [part.Observation.Evidence.Id])).ToList();
        if (overlay is not null) ApplyManualRelationships(activeEntries, overlay, edges, evidence, asOf);
        edges = MergeEdges(edges, decisions);
        ApplySuppressions(activeEntries, nodes, edges, decisions);

        var overlayBytes = overlay is null ? null : ContractJson.WriteOverlay(overlay);
        var snapshot = new GraphSnapshot(
            "architecture/v1", graphId,
            new(compositionPolicy, bundles.Select(bundle => new GraphCompositionInput(
                bundle.Repository, Digest(ContractJson.WriteObservationBundle(bundle)))).ToArray()),
            overlayBytes is null ? null : Digest(overlayBytes), asOf,
            nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray(),
            edges.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray(),
            evidence.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            decisions.DistinctBy(item => item.Id).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            diagnostics.DistinctBy(item => item.Id).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            bundles.SelectMany(bundle => bundle.Redactions.Select(redaction =>
                    isProjectComposition ? redaction with { Id = ScopeId(bundle.Repository.RepositoryId, redaction.Id) } : redaction))
                .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
        GraphValidator.Validate(snapshot);
        var candidateResolutions = resolvedByKey
            .Select(pair => new CandidateResolution(pair.Key, pair.Value.Id, pair.Value.Resolution))
            .OrderBy(item => item.CandidateKey, StringComparer.Ordinal)
            .ToArray();
        return new(snapshot, snapshot.Diagnostics, snapshot.MergeDecisions, candidateResolutions);
    }

    private static Observation ScopeObservation(RepositoryRevision repository, Observation observation)
    {
        var id = ScopeId(repository.RepositoryId, observation.Id);
        var evidence = ScopeEvidence(repository, observation.Evidence, id);
        return observation switch
        {
            EntityObservation entity => entity with
            {
                Id = id,
                Evidence = evidence,
                Entity = ScopeCandidate(repository.RepositoryId, entity.Entity)
            },
            RelationshipObservation relationship => relationship with
            {
                Id = id,
                Evidence = evidence,
                From = ScopeCandidate(repository.RepositoryId, relationship.From),
                To = ScopeCandidate(repository.RepositoryId, relationship.To)
            },
            _ => throw new InvalidOperationException($"Unsupported observation type {observation.GetType().Name}.")
        };
    }

    private static EntityCandidate ScopeCandidate(string repositoryId, EntityCandidate candidate) =>
        candidate with { Key = ScopeId(repositoryId, candidate.Key) };

    private static Evidence ScopeEvidence(RepositoryRevision repository, Evidence evidence, string claimId)
    {
        var properties = new Dictionary<string, JsonElement>(evidence.Properties, StringComparer.Ordinal)
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(repository.RepositoryId),
            ["revision"] = JsonSerializer.SerializeToElement(repository.Revision)
        };
        return evidence with
        {
            Id = ScopeId(repository.RepositoryId, evidence.Id),
            ClaimId = claimId,
            Properties = properties
        };
    }

    private static Diagnostic ScopeDiagnostic(string repositoryId, Diagnostic diagnostic) => diagnostic with
    {
        Id = ScopeId(repositoryId, diagnostic.Id),
        SubjectId = diagnostic.SubjectId is null ? null : ScopeId(repositoryId, diagnostic.SubjectId)
    };

    private static string ScopeId(string repositoryId, string id) =>
        $"repo-{Digest(System.Text.Encoding.UTF8.GetBytes(repositoryId))[..12]}:{id}";

    private static IEnumerable<EntityCandidate> Candidates(Observation observation) => observation switch
    {
        EntityObservation entity => [entity.Entity],
        RelationshipObservation relationship => [relationship.From, relationship.To],
        _ => []
    };

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildExplicitStrongTargets(IEnumerable<EntityCandidate> candidates)
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var group in candidates.Where(candidate => candidate.ExplicitId is not null)
                     .SelectMany(candidate => candidate.IdentitySignals.Select(signal => (Signal: $"{signal.Key}={signal.Value}", ExplicitId: candidate.ExplicitId!)))
                     .GroupBy(item => item.Signal, StringComparer.Ordinal))
            result[group.Key] = group.Select(item => item.ExplicitId).ToHashSet(StringComparer.Ordinal);
        return result;
    }

    private static IReadOnlyDictionary<string, string> BuildOverlayTargets(IEnumerable<OverlayEntry> entries)
    {
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in entries.OfType<BindingOverlay>()) targets.Add(binding.CandidateKey, binding.TargetId);
        foreach (var merge in entries.OfType<MergeOverlay>())
            foreach (var key in merge.CandidateKeys) targets.Add(key, merge.TargetId);
        return targets;
    }

    private static IEnumerable<OverlayEntry> ActiveEntries(ArchitectureOverlay? overlay, DateOnly asOf, ICollection<Diagnostic> diagnostics)
    {
        if (overlay is null) yield break;
        foreach (var entry in overlay.Entries.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var governance = entry.Governance;
            var reviewed = governance.LastReviewedOn ?? governance.CreatedOn;
            if (governance.CreatedOn > asOf || reviewed > asOf)
                throw new ArtifactValidationException([$"Overlay entry '{entry.Id}' has governance dates after the as-of date {asOf:yyyy-MM-dd}."]);
            var expires = governance.ExpiresOn ?? (entry is SuppressionOverlay ? reviewed.AddDays(30) : null);
            if (expires < asOf)
            {
                diagnostics.Add(DiagnosticFor(entry is SuppressionOverlay ? "OVERLAY_SUPPRESSION_EXPIRED" : "OVERLAY_ENTRY_EXPIRED", entry.Id, $"Overlay entry '{entry.Id}' expired on {expires:yyyy-MM-dd}."));
                continue;
            }
            if (reviewed.AddDays(governance.ReviewIntervalDays ?? 90) < asOf)
                diagnostics.Add(DiagnosticFor("OVERLAY_REVIEW_OVERDUE", entry.Id, $"Overlay entry '{entry.Id}' is overdue for review but remains active."));
            yield return entry;
        }
    }

    private static void EnsureCandidateNode(
        IDictionary<string, List<(EntityCandidate Candidate, Evidence Evidence)>> nodes,
        EntityCandidate candidate,
        ResolvedIdentity identity,
        Evidence evidence)
    {
        if (nodes.ContainsKey(identity.Id)) return;
        nodes[identity.Id] = [(candidate, evidence)];
    }

    private static GraphNode BuildNode(string id, List<(EntityCandidate Candidate, Evidence Evidence)> parts, IReadOnlyDictionary<string, ResolvedIdentity> identities)
    {
        var ordered = parts.OrderBy(part => part.Candidate.Key, StringComparer.Ordinal).ToArray();
        var first = ordered[0].Candidate;
        var resolution = ordered.Select(part => identities[part.Candidate.Key].Resolution).Aggregate(MostUncertain);
        return new(id, first.Kind, first.Name, resolution,
            ordered.Select(part => part.Candidate.Name).Where(name => name != first.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            first.Properties,
            ordered.Select(part => part.Evidence.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static void ApplyManualNodes(IEnumerable<OverlayEntry> entries, ArchitectureOverlay overlay, IDictionary<string, GraphNode> nodes, IDictionary<string, Evidence> evidence, DateOnly asOf)
    {
        foreach (var entry in entries.OfType<ManualNodeOverlay>())
        {
            var itemEvidence = ManualEvidence(entry.Id, entry.Governance, overlay.DefaultOwner, entry.EvidencePath, entry.Range, Confidence.Confirmed, asOf);
            AddEvidence(evidence, itemEvidence);
            var node = entry.Node with { EvidenceIds = entry.Node.EvidenceIds.Append(itemEvidence.Id).Distinct(StringComparer.Ordinal).ToArray() };
            if (!nodes.TryAdd(node.Id, node)) throw new ArtifactValidationException([$"Manual node '{node.Id}' conflicts with a generated node."]);
        }
    }

    private static void ApplyManualRelationships(IEnumerable<OverlayEntry> entries, ArchitectureOverlay overlay, ICollection<GraphEdge> edges, IDictionary<string, Evidence> evidence, DateOnly asOf)
    {
        foreach (var entry in entries.OfType<ManualRelationshipOverlay>())
        {
            var itemEvidence = ManualEvidence(entry.Id, entry.Governance, overlay.DefaultOwner, entry.EvidencePath, entry.Range, entry.Confidence, asOf);
            AddEvidence(evidence, itemEvidence);
            edges.Add(new(entry.Id, entry.Relationship, entry.From, entry.To, entry.Confidence, entry.Resolution, [EvidenceProvenance.Manual], entry.Properties, [itemEvidence.Id]));
        }
    }

    private static void ApplyAnnotations(IEnumerable<OverlayEntry> entries, ArchitectureOverlay overlay, IDictionary<string, GraphNode> nodes, IDictionary<string, Evidence> evidence, DateOnly asOf)
    {
        foreach (var entry in entries.OfType<AnnotationOverlay>())
        {
            if (!nodes.TryGetValue(entry.TargetId, out var node))
                throw new ArtifactValidationException([$"Annotation '{entry.Id}' targets missing node '{entry.TargetId}'."]);
            var itemEvidence = ManualEvidence(entry.Id, entry.Governance, overlay.DefaultOwner, entry.EvidencePath, entry.Range, Confidence.Confirmed, asOf);
            AddEvidence(evidence, itemEvidence);
            nodes[entry.TargetId] = node with
            {
                Aliases = node.Aliases.Concat(entry.Aliases).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                Properties = node.Properties.Concat(entry.Properties).GroupBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal),
                EvidenceIds = node.EvidenceIds.Append(itemEvidence.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            };
        }
    }

    private static List<GraphEdge> MergeEdges(IEnumerable<GraphEdge> edges, ICollection<MergeDecision> decisions)
    {
        var result = new List<GraphEdge>();
        foreach (var group in edges.GroupBy(edge => (edge.Kind, edge.From, edge.To)))
        {
            var ordered = group.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray();
            var first = ordered[0];
            if (ordered.Length > 1)
                decisions.Add(new($"decision:relationship:{first.Id}", "merged", ordered.Select(edge => edge.Id).Distinct(StringComparer.Ordinal).ToArray(), "same relationship kind and canonical endpoints"));
            result.Add(first with
            {
                Confidence = ordered.Any(edge => edge.Confidence == Confidence.Confirmed) ? Confidence.Confirmed : Confidence.Inferred,
                Resolution = ordered.Select(edge => edge.Resolution).Aggregate(MostUncertain),
                Provenance = ordered.SelectMany(edge => edge.Provenance).Distinct().OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray(),
                EvidenceIds = ordered.SelectMany(edge => edge.EvidenceIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            });
        }
        return result;
    }

    private static void ApplySuppressions(IEnumerable<OverlayEntry> entries, IDictionary<string, GraphNode> nodes, ICollection<GraphEdge> edges, ICollection<MergeDecision> decisions)
    {
        foreach (var suppression in entries.OfType<SuppressionOverlay>())
        {
            var removed = nodes.Remove(suppression.TargetId);
            var edge = edges.FirstOrDefault(item => item.Id == suppression.TargetId);
            if (edge is not null) { edges.Remove(edge); removed = true; }
            if (!removed) throw new ArtifactValidationException([$"Suppression '{suppression.Id}' targets missing subject '{suppression.TargetId}'."]);
            decisions.Add(new($"decision:{suppression.Id}", "suppressed", [suppression.TargetId], suppression.Governance.Rationale));
        }
    }

    private static Evidence ManualEvidence(string id, OverlayEntryGovernance governance, string defaultOwner, string path, SourceRange? range, Confidence confidence, DateOnly asOf)
    {
        var reviewed = governance.LastReviewedOn ?? governance.CreatedOn;
        var reviewStatus = reviewed.AddDays(governance.ReviewIntervalDays ?? 90) < asOf ? "overdue" : "current";
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["owner"] = JsonSerializer.SerializeToElement(governance.Owner ?? defaultOwner),
            ["reviewer"] = JsonSerializer.SerializeToElement(governance.Reviewer),
            ["rationale"] = JsonSerializer.SerializeToElement(governance.Rationale),
            ["reviewStatus"] = JsonSerializer.SerializeToElement(reviewStatus),
            ["lastReviewedOn"] = JsonSerializer.SerializeToElement(reviewed)
        };
        return new($"evidence:overlay:{id}", id, EvidenceProvenance.Manual, null, null, "governed architecture overlay", path, range, confidence, properties);
    }

    private static void AddEvidence(IDictionary<string, Evidence> target, Evidence item)
    {
        if (target.TryGetValue(item.Id, out var existing) && ContractJson.Options.GetConverter(typeof(Evidence)) is not null && existing != item)
            throw new ArtifactValidationException([$"Evidence ID '{item.Id}' has conflicting values."]);
        target[item.Id] = item;
    }

    private static Resolution MostUncertain(Resolution left, Resolution right) =>
        left == Resolution.Unresolved || right == Resolution.Unresolved ? Resolution.Unresolved :
        left == Resolution.Ambiguous || right == Resolution.Ambiguous ? Resolution.Ambiguous : Resolution.Resolved;

    private static Diagnostic DiagnosticFor(string code, string subject, string message) =>
        new($"diagnostic:{code.ToLowerInvariant()}:{subject}", code, "warning", message, subject);

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
