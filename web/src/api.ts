export type GraphNode = {
  id: string
  kind: string
  name: string
  description: string
  resolution: string
  environment: string
  purpose: string
  owner: string
  glossary: readonly string[]
  onboarding: readonly string[]
}

export type Evidence = {
  id: string
  repository: string
  revision: string
  path: string
  startLine: number
  endLine: number
  extractionMethod: string
  provenance: string
  confidence: string
  owner?: string
  reviewer?: string
  reviewStatus?: string
  rationale?: string
  sourceUrl?: string
}

export type GraphEdge = {
  id: string
  kind: string
  from: string
  to: string
  label: string
  confidence: string
  resolution: string
  provenance: readonly string[]
  properties: Readonly<Record<string, unknown>>
  evidence: readonly Evidence[]
}

export type Graph = {
  schemaVersion: string
  snapshotId: string
  workspace: string
  description: string
  nodes: readonly GraphNode[]
  edges: readonly GraphEdge[]
}

export type Diagnostic = {
  id: string
  code: string
  severity: "info" | "warning" | "error"
  message: string
  subjectId: string | null
}

export class ApiError extends Error {
  constructor(public readonly code: string, message: string) { super(message) }
}

async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(path)
  if (!response.ok) {
    const diagnostic = await response.json().catch(() => undefined) as { code?: string; message?: string } | undefined
    throw new ApiError(diagnostic?.code ?? `HTTP_${response.status}`, diagnostic?.message ?? `Request failed: ${response.status}`)
  }
  return response.json() as Promise<T>
}

export function getGraph(filters?: { environment?: string; nodeKind?: string }): Promise<Graph> {
  const query = new URLSearchParams()
  if (filters?.environment) query.set("environment", filters.environment)
  if (filters?.nodeKind) query.set("nodeKind", filters.nodeKind)
  return getJson<Graph>(`/api/v1/graph${query.size ? `?${query}` : ""}`)
}

export function getDiagnostics(): Promise<readonly Diagnostic[]> {
  return getJson<readonly Diagnostic[]>("/api/v1/diagnostics")
}
