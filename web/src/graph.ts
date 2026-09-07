import type { Edge, Node } from "@xyflow/react"
import type { Graph } from "./api"

export type LayoutResult = {
  nodes: Node[]
  edges: Edge[]
  elapsedMs: number
  warning?: string
}

type WorkerResult = Omit<LayoutResult, "warning">

export function mapGraph(graph: Graph, positions: Readonly<Record<string, { x: number; y: number }>>): LayoutResult {
  const nodes: Node[] = graph.nodes.map((node, index) => ({
    id: node.id,
    position: positions[node.id] ?? gridPosition(index),
    width: 210,
    height: 96,
    className: `resolution-${node.resolution}`,
    data: { label: node.name, kind: node.kind, environment: node.environment, resolution: node.resolution, description: node.description },
    type: "architecture",
  }))
  const edges: Edge[] = graph.edges.map((edge) => ({
    id: edge.id,
    source: edge.kind === "subscribes" ? edge.to : edge.from,
    target: edge.kind === "subscribes" ? edge.from : edge.to,
    label: String(edge.properties.displayLabel ?? edge.kind),
    type: "smoothstep",
    animated: edge.kind === "publishes" || edge.kind === "subscribes",
    markerEnd: { type: "arrowclosed" },
    ariaLabel: `Select relationship: ${edge.label}`,
    className: edge.provenance.includes("manual")
      ? edge.provenance.length > 1 ? "provenance-mixed" : "provenance-manual"
      : edge.provenance.includes("scanner") ? "provenance-scanner" : "provenance-authored",
    data: { label: edge.label, kind: edge.kind, provenance: edge.provenance },
  }))
  return { nodes, edges, elapsedMs: 0 }
}

export function fallbackLayout(graph: Graph, warning?: string): LayoutResult {
  const result = mapGraph(graph, Object.fromEntries(graph.nodes.map((node, index) => [node.id, gridPosition(index)])))
  return { ...result, warning }
}

export async function layoutGraph(graph: Graph, direction: "RIGHT" | "DOWN" = "RIGHT", deadlineMs = 5_000): Promise<LayoutResult> {
  if (typeof Worker === "undefined") return fallbackLayout(graph, "ELK worker unavailable; deterministic grid layout is active.")
  const worker = new Worker(new URL("./layout.worker.ts", import.meta.url), { type: "module" })
  return new Promise((resolve) => {
    const timeout = window.setTimeout(() => {
      worker.terminate()
      resolve(fallbackLayout(graph, "ELK exceeded its 5-second deadline; deterministic grid layout is active."))
    }, deadlineMs)
    worker.onmessage = (event: MessageEvent<WorkerResult>) => {
      window.clearTimeout(timeout)
      worker.terminate()
      resolve(event.data)
    }
    worker.onerror = () => {
      window.clearTimeout(timeout)
      worker.terminate()
      resolve(fallbackLayout(graph, "ELK layout failed; deterministic grid layout is active."))
    }
    worker.postMessage({ graph, direction })
  })
}

function gridPosition(index: number) {
  return { x: (index % 4) * 250, y: Math.floor(index / 4) * 150 }
}
