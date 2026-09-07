import { useEffect, useMemo, useState } from "react"
import { getDiagnostics, getGraph, type Diagnostic, type Graph, type GraphEdge, type GraphNode } from "./api"
import { EvidencePanel } from "./EvidencePanel"
import { GraphCanvas } from "./GraphCanvas"
import { ScannerCoverageBanner } from "./ScannerCoverageBanner"
import { SemanticMap } from "./SemanticMap"

type Selection = { node?: GraphNode; edge?: GraphEdge }
type MapView = "dependencies" | "runtime"

export function App() {
  const [graph, setGraph] = useState<Graph>()
  const [graphError, setGraphError] = useState<string>()
  const [diagnostics, setDiagnostics] = useState<readonly Diagnostic[]>([])
  const [diagnosticsError, setDiagnosticsError] = useState<string>()
  const [activeView, setActiveView] = useState<MapView>("dependencies")
  const [selection, setSelection] = useState<Selection>({})
  const [search, setSearch] = useState("")
  const [nodeKind, setNodeKind] = useState("")
  const [environment, setEnvironment] = useState("")
  const [filtersOpen, setFiltersOpen] = useState(false)
  const [inspectorCollapsed, setInspectorCollapsed] = useState(false)
  const [focusMode, setFocusMode] = useState(false)

  useEffect(() => {
    void getGraph().then(setGraph).catch((error: Error) => setGraphError(error.message))
    void getDiagnostics()
      .then((items) => { setDiagnostics(items); setDiagnosticsError(undefined) })
      .catch((error: Error) => setDiagnosticsError(error.message))
  }, [])
  useEffect(() => {
    const exitFocus = (event: KeyboardEvent) => { if (event.key === "Escape") setFocusMode(false) }
    window.addEventListener("keydown", exitFocus)
    return () => window.removeEventListener("keydown", exitFocus)
  }, [])

  const matchingNodes = useMemo(() => graph ? findMatchingNodes(graph, search, nodeKind, environment) : [], [environment, graph, nodeKind, search])
  const filteredGraph = useMemo(() => graph ? contextualGraph(graph, matchingNodes, Boolean(search.trim() || nodeKind || environment)) : undefined, [environment, graph, matchingNodes, nodeKind, search])
  const searchResults = useMemo(() => search.trim() && graph && selection.node?.name !== search
    ? findMatchingNodes(graph, search, "", "").slice(0, 8)
    : [], [graph, search, selection.node?.name])

  if (graphError) return <main className="status" role="alert">Architecture could not be loaded: {graphError}</main>
  if (!graph || !filteredGraph) return <main className="status">Loading architecture…</main>

  const selectedId = selection.edge?.id ?? selection.node?.id
  const selectedMatch = matchingNodes.find((node) => node.id === selection.node?.id)
  const focusNode = selectedMatch ?? matchingNodes.find((node) => !["system", "module"].includes(node.kind))
  const selectedSystemId = ancestorOfKind(graph, focusNode?.id, "system")?.id ?? graph.nodes.find((node) => node.kind === "system")?.id
  const focusModuleId = ancestorOfKind(graph, focusNode?.id, "module")?.id
  const runtimeGraph = getRuntimeGraph(filteredGraph, selectedSystemId)
  const kinds = [...new Set(graph.nodes.map((node) => node.kind))].sort()
  const environments = [...new Set(graph.nodes.map((node) => node.environment).filter(Boolean))].sort()
  const filtersActive = Boolean(nodeKind || environment)
  const select = (id: string, type: "node" | "edge") => {
    setSelection(type === "node"
      ? { node: graph.nodes.find((node) => node.id === id) }
      : { edge: graph.edges.find((edge) => edge.id === id) })
  }
  const selectSearchResult = (node: GraphNode) => {
    setSelection({ node })
    setSearch(node.name)
  }

  return <main className={`app-shell ${inspectorCollapsed ? "inspector-collapsed" : ""} ${focusMode ? "focus-mode" : ""}`}>
    <nav className="navigation-rail" aria-label="Primary navigation">
      <a className="brand-mark" href="/" aria-label="Archie home">A</a>
      <div className="navigation-items">
        <button className={`navigation-item ${activeView === "dependencies" ? "is-active" : ""}`} aria-label="Dependency map" aria-pressed={activeView === "dependencies"} onClick={() => setActiveView("dependencies")}><span className="nav-icon">⌘</span><span className="nav-label">Dependencies</span></button>
        <button className={`navigation-item ${activeView === "runtime" ? "is-active" : ""}`} aria-label="Inferred runtime map" aria-pressed={activeView === "runtime"} onClick={() => setActiveView("runtime")}><span className="nav-icon">◇</span><span className="nav-label">Runtime</span></button>
      </div>
    </nav>

    <div className="application-area">
      <header className="toolbar">
        <div className="workspace-context"><span className="workspace-status" /><div><h1>{graph.workspace}</h1><p>Canonical snapshot · {graph.nodes.length} nodes</p></div></div>
        <div className="toolbar-actions">
          <label className="search-control"><span aria-hidden="true">⌕</span><input aria-label="Search architecture" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search nodes…" /></label>
          <button className={`toolbar-button ${filtersActive ? "is-active" : ""}`} aria-expanded={filtersOpen} onClick={() => setFiltersOpen((value) => !value)}><span className="filter-dot" /> Filters</button>
          <button className="toolbar-button" onClick={() => setInspectorCollapsed((value) => !value)}>{inspectorCollapsed ? "Show inspector" : "Hide inspector"}</button>
          <button className="icon-button" aria-label="Focus map" onClick={() => setFocusMode(true)}>⛶</button>
        </div>
        {searchResults.length > 0 && <div className="search-results" role="listbox" aria-label="Architecture search results">
          {searchResults.map((node) => <button key={node.id} role="option" aria-selected={selection.node?.id === node.id} onClick={() => selectSearchResult(node)}><strong>{node.name}</strong><span>{node.kind} · {node.environment}</span></button>)}
        </div>}
        {filtersOpen && <div className="filter-popover">
          <label>Node kind<select aria-label="Filter by node kind" value={nodeKind} onChange={(event) => setNodeKind(event.target.value)}><option value="">All kinds</option>{kinds.map((kind) => <option key={kind}>{kind}</option>)}</select></label>
          <label>Environment<select aria-label="Filter by environment" value={environment} onChange={(event) => setEnvironment(event.target.value)}><option value="">All environments</option>{environments.map((value) => <option key={value}>{value}</option>)}</select></label>
          <button className="clear-filters" type="button" onClick={() => { setNodeKind(""); setEnvironment("") }}>Clear filters</button>
        </div>}
      </header>

      <ScannerCoverageBanner diagnostics={diagnostics} error={diagnosticsError} />

      <section className="workspace">
        <section className="canvas map-panel" aria-label={activeView === "dependencies" ? "Dependency map" : "Inferred runtime map"}>
          <header className="map-heading">
            <div><p className="eyebrow">{activeView === "dependencies" ? "Dependency map" : "Inferred runtime"}</p><h2>{activeView === "dependencies" ? "How the codebase is connected" : "How deployables interact"}</h2></div>
            <p>{matchingNodes.length} of {graph.nodes.length} nodes match</p>
          </header>
          {matchingNodes.length === 0 ? <div className="empty-map" role="status">No architecture nodes match the current search and filters.</div>
            : activeView === "dependencies"
              ? <SemanticMap graph={filteredGraph} selectedSystemId={selectedSystemId} selectedId={selectedId} focusModuleId={focusModuleId} onOpenSystem={() => undefined} onSelect={select} />
              : <GraphCanvas graph={runtimeGraph} selectedId={selectedId} ariaLabel="Inferred runtime architecture graph" onSelect={select} />}
          {focusMode && <div className="focus-banner">Map focus mode <kbd>Esc</kbd><button type="button" onClick={() => setFocusMode(false)}>Exit focus</button></div>}
        </section>
        {!inspectorCollapsed && <Inspector selection={selection} />}
      </section>
    </div>
  </main>
}

function Inspector({ selection }: { selection: Selection }) {
  if (selection.edge) return <aside className="details-panel" aria-label="Architecture details"><div className="inspector-header">Relationship evidence</div><div className="inspector-content"><EvidencePanel edge={selection.edge} /></div></aside>
  const node = selection.node
  return <aside className="details-panel" aria-label="Architecture details"><div className="inspector-header">Component details</div><div className="inspector-content">
    {node ? <><p className="eyebrow">{node.kind}</p><h2>{node.name}</h2><p>{node.purpose || node.description || "No description is available."}</p><h3>Architecture metadata</h3><dl className="guide-meta"><div><dt>Environment</dt><dd>{node.environment || "Unspecified"}</dd></div><div><dt>Resolution</dt><dd>{node.resolution}</dd></div>{node.owner && <div><dt>Owner</dt><dd>{node.owner}</dd></div>}</dl></>
      : <div className="empty-panel"><h2>Inspect architecture</h2><p>Select a component or relationship to inspect its canonical details and evidence.</p></div>}
  </div></aside>
}

function findMatchingNodes(graph: Graph, search: string, nodeKind: string, environment: string): GraphNode[] {
  const query = search.trim().toLowerCase()
  return graph.nodes.filter((node) =>
    (!nodeKind || node.kind === nodeKind) &&
    (!environment || node.environment === environment) &&
    (!query || [node.name, node.id, node.purpose, node.description].some((value) => value.toLowerCase().includes(query))))
}

function contextualGraph(graph: Graph, matches: readonly GraphNode[], filtered: boolean): Graph {
  if (!filtered) return graph
  const matchingIds = new Set(matches.map((node) => node.id))
  const included = new Set(matchingIds)
  for (const edge of graph.edges) {
    if (edge.kind !== "contains" && (matchingIds.has(edge.from) || matchingIds.has(edge.to))) {
      included.add(edge.from)
      included.add(edge.to)
    }
  }
  let changed = true
  while (changed) {
    changed = false
    for (const edge of graph.edges) {
      if (edge.kind === "contains" && included.has(edge.to) && !included.has(edge.from)) { included.add(edge.from); changed = true }
    }
  }
  return { ...graph, nodes: graph.nodes.filter((node) => included.has(node.id)), edges: graph.edges.filter((edge) => included.has(edge.from) && included.has(edge.to)) }
}

function ancestorOfKind(graph: Graph, nodeId: string | undefined, kind: string): GraphNode | undefined {
  let current = graph.nodes.find((node) => node.id === nodeId)
  while (current) {
    if (current.kind === kind) return current
    const parentId = graph.edges.find((edge) => edge.kind === "contains" && edge.to === current?.id)?.from
    current = graph.nodes.find((node) => node.id === parentId)
  }
  return undefined
}

function getRuntimeGraph(graph: Graph, systemId?: string): Graph {
  const childIds = new Set<string>()
  const pending = systemId ? [systemId] : []
  while (pending.length) {
    const parent = pending.pop()
    for (const edge of graph.edges.filter((candidate) => candidate.kind === "contains" && candidate.from === parent)) {
      if (!childIds.has(edge.to)) { childIds.add(edge.to); pending.push(edge.to) }
    }
  }
  const deployableIds = new Set(graph.nodes.filter((node) =>
    (!systemId || childIds.has(node.id)) && node.kind === "deployable").map((node) => node.id))
  const edges = graph.edges.filter((edge) => edge.kind !== "contains" && (deployableIds.has(edge.from) || deployableIds.has(edge.to)))
  const nodeIds = new Set([...deployableIds, ...edges.flatMap((edge) => [edge.from, edge.to])])
  return { ...graph, nodes: graph.nodes.filter((node) => nodeIds.has(node.id)), edges }
}
