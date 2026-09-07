import type { GraphEdge } from "./api"

const propertyLabels: Readonly<Record<string, string>> = {
  configurationKey: "Configuration key",
  provider: "Provider",
  resourceKind: "Resource kind",
  host: "Host",
  scheme: "Scheme",
  port: "Port",
  contextType: "Context",
}

export function EvidencePanel({ edge }: { edge?: GraphEdge }) {
  if (!edge) return <div className="empty-panel"><h2>Inspect evidence</h2><p>Select any relationship to see why Archie shows it.</p></div>
  const properties = Object.entries(propertyLabels)
    .map(([key, label]) => ({ label, value: edge.properties[key] }))
    .filter((property): property is { label: string; value: string } => typeof property.value === "string" && property.value.length > 0)
  return (
    <div>
      <p className="eyebrow">Relationship evidence</p>
      <h2>{edge.label}</h2>
      <div className="badges"><span>{edge.confidence}</span><span>{edge.resolution}</span>{edge.provenance.map((item) => <span className={`provenance-${item}`} key={item}>{item.toUpperCase()}</span>)}</div>
      {properties.length > 0 && (
        <article className="evidence-card">
          <strong>Discovered configuration</strong>
          <dl>{properties.map(({ label, value }) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
        </article>
      )}
      {edge.evidence.map((evidence) => (
        <article className="evidence-card" key={evidence.id}>
          <strong>{evidence.provenance.toUpperCase()} evidence</strong>
          <dl>
            <div><dt>Source</dt><dd>{evidence.sourceUrl ? <a href={evidence.sourceUrl} target="_blank" rel="noreferrer">{evidence.path}:{evidence.startLine}</a> : `${evidence.path}:${evidence.startLine}`}</dd></div>
            <div><dt>Method</dt><dd>{evidence.extractionMethod}</dd></div>
            <div><dt>Revision</dt><dd><code>{evidence.revision.slice(0, 12)}</code></dd></div>
            {evidence.owner && <div><dt>Owner</dt><dd>{evidence.owner}</dd></div>}
            {evidence.reviewer && <div><dt>Reviewer</dt><dd>{evidence.reviewer}</dd></div>}
            {evidence.reviewStatus && <div><dt>Review</dt><dd>{evidence.reviewStatus}</dd></div>}
            {evidence.rationale && <div><dt>Rationale</dt><dd>{evidence.rationale}</dd></div>}
          </dl>
        </article>
      ))}
    </div>
  )
}
