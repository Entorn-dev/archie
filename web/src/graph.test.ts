import { describe, expect, it } from "vitest"
import { fallbackLayout, mapGraph } from "./graph"
import type { Graph } from "./api"

const graph = {
  schemaVersion: "architecture/v1", snapshotId: "fixture", workspace: "Book Retail", description: "fixture",
  nodes: [
    { id: "channel:event", kind: "message-channel", name: "event", description: "", resolution: "resolved", environment: "current", purpose: "", owner: "", glossary: [], onboarding: [] },
    { id: "deployable:worker", kind: "deployable", name: "Worker", description: "", resolution: "resolved", environment: "current", purpose: "", owner: "", glossary: [], onboarding: [] },
  ],
  edges: [{ id: "edge:subscription", kind: "subscribes", from: "deployable:worker", to: "channel:event", label: "subscribes", confidence: "confirmed", resolution: "resolved", provenance: ["deterministic"], properties: { label: "subscribes" }, evidence: [] }],
} satisfies Graph

describe("graph abstraction", () => {
  it("maps subscriptions in message-flow direction without changing canonical data", () => {
    const result = mapGraph(graph, {})
    expect(result.edges[0]).toMatchObject({ source: "channel:event", target: "deployable:worker" })
    expect(graph.edges[0]).toMatchObject({ from: "deployable:worker", to: "channel:event" })
  })

  it("uses stable deterministic fallback positions", () => {
    expect(fallbackLayout(graph).nodes.map((node) => node.position)).toEqual(fallbackLayout(graph).nodes.map((node) => node.position))
  })

  it("marks manual and mixed relationships without changing relationship-kind styling", () => {
    const manual = { ...graph, edges: [{ ...graph.edges[0], provenance: ["manual", "scanner"] }] }
    expect(mapGraph(manual, {}).edges[0].className).toBe("provenance-mixed")
  })

  it("keeps unresolved placeholders visible and structurally marked", () => {
    const unresolved = { ...graph, nodes: [{ ...graph.nodes[0], resolution: "unresolved" }, graph.nodes[1]] }
    expect(mapGraph(unresolved, {}).nodes[0]).toMatchObject({ className: "resolution-unresolved", data: { resolution: "unresolved" } })
  })
})
