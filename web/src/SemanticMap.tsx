import { useEffect, useMemo, useState } from "react"
import type { Graph, GraphEdge, GraphNode } from "./api"
import { GraphCanvas } from "./GraphCanvas"

type Props = {
  graph: Graph
  selectedSystemId?: string
  selectedId?: string
  highlightedIds?: ReadonlySet<string>
  focusModuleId?: string
  focusView?: boolean
  focusExpandedRelationships?: boolean
  onOpenSystem(id: string): void
  onSelect(id: string, type: "node" | "edge"): void
}

export function SemanticMap({ graph, selectedSystemId, selectedId, highlightedIds, focusModuleId, focusView, focusExpandedRelationships = true, onOpenSystem, onSelect }: Props) {
  const [expandedModuleId, setExpandedModuleId] = useState<string>()
  useEffect(() => setExpandedModuleId(undefined), [selectedSystemId])
  useEffect(() => { if (focusModuleId) setExpandedModuleId(focusModuleId) }, [focusModuleId])
  const projectedGraph = useMemo(() => projectGraph(graph, selectedSystemId, expandedModuleId), [graph, selectedSystemId, expandedModuleId])
  const expandedModule = graph.nodes.find((node) => node.id === expandedModuleId)
  const focusIds = useMemo(() => {
    const ids = new Set(highlightedIds)
    if (expandedModuleId && focusExpandedRelationships) ids.add(expandedModuleId)
    for (const edge of projectedGraph.edges.filter((candidate) => (focusExpandedRelationships && candidate.properties.focusPath) || highlightedIds?.has(String(candidate.properties.originEdgeId)))) {
      ids.add(edge.id); ids.add(edge.from); ids.add(edge.to)
    }
    return ids
  }, [expandedModuleId, focusExpandedRelationships, highlightedIds, projectedGraph])

  function handleSelect(id: string, type: "node" | "edge") {
    if (type === "edge" && id.startsWith("zoom:")) return
    if (type === "edge" && id.startsWith("semantic:")) {
      const sourceId = projectedGraph.edges.find((edge) => edge.id === id)?.properties.originEdgeId
      if (sourceId) onSelect(String(sourceId), "edge")
      return
    }
    if (type === "node") {
      const node = graph.nodes.find((candidate) => candidate.id === id)
      if (node?.kind === "system") { onOpenSystem(id); return }
      if (node?.kind === "module") setExpandedModuleId((current) => current === id ? undefined : id)
    }
    onSelect(id, type)
  }

  return <div className="semantic-map">
    <GraphCanvas graph={projectedGraph} selectedId={selectedId} highlightedIds={focusIds} dimUnhighlighted={Boolean(expandedModuleId)} fitViewIds={focusView ? [...focusIds] : undefined} ariaLabel="Dependency architecture graph" onSelect={handleSelect} />
    <div className="zoom-guide">
      <div><strong>{expandedModule ? `${expandedModule.name} expanded` : selectedSystemId ? "System expanded" : "Systems collapsed"}</strong><span>Select a node to reveal its next level</span></div>
      {expandedModule && <button type="button" onClick={() => setExpandedModuleId(undefined)}>Collapse module</button>}
    </div>
  </div>
}

function projectGraph(graph: Graph, selectedSystemId?: string, expandedModuleId?: string): Graph {
  const nodeById = new Map(graph.nodes.map((node) => [node.id, node]))
  const parentById = new Map(graph.edges.filter((edge) => edge.kind === "contains").map((edge) => [edge.to, edge.from]))
  const systems = graph.nodes.filter((node) => node.kind === "system")
  if (systems.length === 0) return graph
  const visible = new Map<string, GraphNode>()

  function ancestorOfKind(nodeId: string, kind: string): GraphNode | undefined {
    let current = nodeById.get(nodeId)
    while (current) {
      if (current.kind === kind) return current
      current = nodeById.get(parentById.get(current.id) ?? "")
    }
  }

  function descendants(rootId: string, kind?: string): GraphNode[] {
    const result: GraphNode[] = []
    const pending = [rootId]
    while (pending.length) {
      const parent = pending.pop()
      for (const edge of graph.edges) {
        if (edge.kind !== "contains" || edge.from !== parent) continue
        const child = nodeById.get(edge.to)
        if (!child) continue
        if (!kind || child.kind === kind) result.push(child)
        pending.push(child.id)
      }
    }
    return result
  }

  for (const system of systems) {
    const moduleCount = descendants(system.id, "module").length
    visible.set(system.id, { ...system, environment: system.id === selectedSystemId ? "expanded" : `${moduleCount} modules`, resolution: "system" })
  }
  if (selectedSystemId) {
    for (const module of descendants(selectedSystemId, "module").filter((node) => parentById.get(node.id) === selectedSystemId)) {
      const deployableCount = descendants(module.id, "deployable").length
      visible.set(module.id, { ...module, environment: module.id === expandedModuleId ? "expanded" : `${deployableCount} deployables`, resolution: "module" })
    }
  }
  if (expandedModuleId) {
    const internalKinds = new Set(["deployable", "http-endpoint"])
    for (const node of descendants(expandedModuleId)) if (internalKinds.has(node.kind)) visible.set(node.id, node)
  }

  function representative(nodeId: string): GraphNode | undefined {
    const node = nodeById.get(nodeId)
    if (!node) return undefined
    const system = ancestorOfKind(nodeId, "system")
    const module = ancestorOfKind(nodeId, "module")
    if (!system) return node
    if (system.id !== selectedSystemId) return visible.get(system.id)
    if (!module) return visible.get(system.id)
    if (module.id === expandedModuleId && visible.has(node.id)) return visible.get(node.id)
    return visible.get(module.id)
  }

  const projectedEdges = new Map<string, GraphEdge & { count: number }>()
  const boundaryKinds = new Set(["database", "external-service", "infrastructure-resource"])
  if (expandedModuleId) for (const kind of ["deployable", "message-channel", "event-contract"]) boundaryKinds.add(kind)

  function addProjectedEdge(sourceId: string, targetId: string, edge: GraphEdge, kind: string, displayLabel: string, allowUnowned = false) {
    const source = representative(sourceId)
    const target = representative(targetId)
    if (!source || !target || source.id === target.id) return
    const sourceOwned = Boolean(ancestorOfKind(sourceId, "system"))
    const targetOwned = Boolean(ancestorOfKind(targetId, "system"))
    if (!sourceOwned && !targetOwned && !allowUnowned) return
    if (expandedModuleId && sourceOwned !== targetOwned && !allowUnowned) {
      const ownedNodeId = sourceOwned ? sourceId : targetId
      if (ancestorOfKind(ownedNodeId, "module")?.id !== expandedModuleId) return
    }
    if (!selectedSystemId) {
      const outside = sourceOwned ? target : source
      if (!sourceOwned || !targetOwned) {
        if (outside.kind !== "external-service") return
        visible.set(outside.id, outside)
      }
    } else {
      if (!sourceOwned) { if (!boundaryKinds.has(source.kind)) return; visible.set(source.id, source) }
      if (!targetOwned) { if (!boundaryKinds.has(target.kind)) return; visible.set(target.id, target) }
    }
    const key = `${source.id}→${target.id}:${kind}`
    const focusPath = Boolean(expandedModuleId && (ancestorOfKind(sourceId, "module")?.id === expandedModuleId || ancestorOfKind(targetId, "module")?.id === expandedModuleId || allowUnowned))
    const existing = projectedEdges.get(key)
    if (existing) { existing.count += 1; if (focusPath) existing.properties = { ...existing.properties, focusPath: true } }
    else projectedEdges.set(key, { ...edge, id: `semantic:${edge.id}:${source.id}:${target.id}:${kind}`, from: source.id, to: target.id, kind, properties: { ...edge.properties, displayLabel, originEdgeId: edge.id, focusPath }, count: 1 })
  }

  for (const edge of graph.edges) {
    if (["contains", "publishes", "subscribes"].includes(edge.kind)) continue
    addProjectedEdge(edge.from, edge.to, edge, edge.kind, edge.kind)
  }
  const flowEdges = graph.edges.filter((edge) => edge.kind === "publishes" || edge.kind === "subscribes")
  for (const channel of graph.nodes.filter((node) => node.kind === "message-channel")) {
    const publishers = flowEdges.filter((edge) => edge.kind === "publishes" && edge.to === channel.id)
    const subscribers = flowEdges.filter((edge) => edge.kind === "subscribes" && edge.to === channel.id)
    const touchesExpandedModule = expandedModuleId && [...publishers, ...subscribers].some((edge) => ancestorOfKind(edge.from, "module")?.id === expandedModuleId)
    if (touchesExpandedModule) {
      visible.set(channel.id, channel)
      for (const publisher of publishers) addProjectedEdge(publisher.from, channel.id, publisher, "publishes", "publishes", true)
      for (const subscriber of subscribers) addProjectedEdge(channel.id, subscriber.from, subscriber, "listener", "listened by", true)
      for (const contractEdge of graph.edges.filter((edge) => edge.kind === "uses-contract" && edge.from === channel.id)) {
        const contract = nodeById.get(contractEdge.to)
        if (contract) {
          visible.set(contract.id, contract)
          addProjectedEdge(channel.id, contract.id, contractEdge, "uses-contract", "contract", true)
        }
      }
    } else {
      for (const publisher of publishers) for (const subscriber of subscribers) {
        addProjectedEdge(publisher.from, subscriber.from, publisher, "message-flow", channel.name)
      }
    }
  }

  const edges: GraphEdge[] = [...projectedEdges.values()].map(({ count, ...edge }) => {
    const displayLabel = count === 1 ? edge.properties.displayLabel : `${count} ${edge.kind === "message-flow" ? "message flows" : "relationships"}`
    return { ...edge, label: count === 1 ? edge.label : String(displayLabel), properties: { ...edge.properties, displayLabel } }
  })
  if (selectedSystemId) {
    for (const module of [...visible.values()].filter((node) => node.kind === "module")) edges.push(containmentEdge(selectedSystemId, module.id))
  }
  if (expandedModuleId) {
    for (const node of [...visible.values()].filter((node) => ancestorOfKind(node.id, "module")?.id === expandedModuleId && node.id !== expandedModuleId)) edges.push(containmentEdge(expandedModuleId, node.id, true))
  }
  return { ...graph, nodes: [...visible.values()], edges }
}

function containmentEdge(from: string, to: string, focusPath = false): GraphEdge {
  return { id: `zoom:${from}:${to}`, kind: "contains", from, to, label: "contains", confidence: "confirmed", resolution: "resolved", provenance: ["derived"], properties: { focusPath }, evidence: [] }
}
