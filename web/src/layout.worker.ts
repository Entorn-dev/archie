/// <reference lib="webworker" />
import ELK from "elkjs/lib/elk-api.js"
import elkWorkerUrl from "elkjs/lib/elk-worker.min.js?url"
import type { Graph } from "./api"
import { mapGraph } from "./graph"

const worker = self as unknown as DedicatedWorkerGlobalScope

worker.onmessage = async (event: MessageEvent<{ graph: Graph; direction: "RIGHT" | "DOWN" }>) => {
  const started = performance.now()
  const { graph, direction } = event.data
  const elk = new ELK({ workerUrl: elkWorkerUrl })
  const algorithm = graph.nodes.length >= 300 || graph.edges.length >= 900 ? "mrtree" : "layered"
  const layout = await elk.layout({
    id: "root",
    layoutOptions: {
      "elk.algorithm": algorithm,
      "elk.direction": direction,
      "elk.spacing.nodeNode": "55",
      "elk.layered.spacing.nodeNodeBetweenLayers": "90",
    },
    children: graph.nodes.map((node) => ({ id: node.id, width: 210, height: 96 })),
    edges: graph.edges.map((edge) => ({
      id: edge.id,
      sources: [edge.kind === "subscribes" ? edge.to : edge.from],
      targets: [edge.kind === "subscribes" ? edge.from : edge.to],
    })),
  })
  const positions = Object.fromEntries((layout.children ?? []).map((node) => [node.id, { x: node.x ?? 0, y: node.y ?? 0 }]))
  const result = mapGraph(graph, positions)
  worker.postMessage({ ...result, elapsedMs: performance.now() - started })
  elk.terminateWorker()
}
