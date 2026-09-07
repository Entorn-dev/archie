import { memo, useEffect, useRef, useState } from "react"
import { Background, Controls, Handle, Position, ReactFlow, type NodeProps } from "@xyflow/react"
import "@xyflow/react/dist/style.css"
import type { Graph } from "./api"
import { fallbackLayout, layoutGraph, type LayoutResult } from "./graph"

type Props = {
  graph: Graph
  selectedId?: string
  highlightedIds?: ReadonlySet<string>
  dimUnhighlighted?: boolean
  fitViewIds?: readonly string[]
  layoutDirection?: "RIGHT" | "DOWN"
  ariaLabel?: string
  onSelect(id: string, type: "node" | "edge"): void
}

const ArchitectureNode = memo(({ data, selected }: NodeProps) => (
  <div className={`flow-node flow-${String(data.kind)} ${selected ? "is-selected" : ""}`}>
    <Handle type="target" position={Position.Left} />
    <span>{String(data.kind)}</span>
    <strong>{String(data.label)}</strong>
    <small>{String(data.environment)} · {String(data.resolution)}</small>
    <Handle type="source" position={Position.Right} />
  </div>
))

export function GraphCanvas({ graph, selectedId, highlightedIds, dimUnhighlighted, fitViewIds, layoutDirection, ariaLabel = "Architecture graph", onSelect }: Props) {
  const shell = useRef<HTMLDivElement>(null)
  const [layout, setLayout] = useState<LayoutResult>(() => fallbackLayout(graph))
  useEffect(() => {
    let active = true
    setLayout(fallbackLayout(graph))
    void layoutGraph(graph, layoutDirection).then((result) => { if (active) setLayout(result) })
    return () => { active = false }
  }, [graph, layoutDirection])
  useEffect(() => {
    shell.current?.querySelector(".react-flow__controls")?.setAttribute("role", "group")
  }, [])
  const nodes = layout.nodes.map((node) => ({
    ...node,
    selected: node.id === selectedId,
    className: [node.className, highlightedIds?.has(node.id) ? "journey-highlight" : dimUnhighlighted ? "is-dimmed" : ""].filter(Boolean).join(" "),
  }))
  const edges = layout.edges.map((edge) => ({
    ...edge,
    selected: edge.id === selectedId,
    className: [edge.className, highlightedIds?.has(edge.id) ? "journey-highlight" : dimUnhighlighted ? "is-dimmed" : ""].filter(Boolean).join(" "),
  }))

  return (
    <div ref={shell} className="flow-shell" data-layout-ms={layout.elapsedMs.toFixed(1)}>
      {layout.warning && <p className="layout-warning" role="status">{layout.warning}</p>}
      <ReactFlow
        key={`${nodes.map((node) => `${node.id}:${node.position.x}:${node.position.y}`).join("|")}:${fitViewIds?.join(",") ?? "all"}`}
        nodes={nodes}
        edges={edges}
        nodeTypes={{ architecture: ArchitectureNode }}
        onNodeClick={(_, node) => onSelect(node.id, "node")}
        onEdgeClick={(_, edge) => onSelect(edge.id, "edge")}
        fitView
        fitViewOptions={fitViewIds?.length
          ? { nodes: fitViewIds.map((id) => ({ id })), padding: 0.3, maxZoom: 0.85 }
          : { padding: 0.18, maxZoom: 0.85 }}
        minZoom={0.2}
        maxZoom={1.8}
        aria-label={ariaLabel}
      >
        <Background color="#2a3b52" gap={22} />
        <Controls />
      </ReactFlow>
      <div className="sr-only" role="group" aria-label="Architecture relationships">
        {graph.edges.map((edge) => (
          <button key={edge.id} type="button" aria-label={`Select relationship: ${edge.label}`} onClick={() => onSelect(edge.id, "edge")}>{edge.label}</button>
        ))}
      </div>
    </div>
  )
}
