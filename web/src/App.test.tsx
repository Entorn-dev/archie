import { cleanup, fireEvent, render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { afterEach, describe, expect, it, vi } from "vitest"
import { App } from "./App"
import type { Diagnostic, Graph, GraphEdge, GraphNode } from "./api"

const node = (id: string, kind: string, name: string, environment = "current"): GraphNode => ({
  id, kind, name, description: `${name} description.`, resolution: "resolved", environment,
  purpose: `${name} purpose.`, owner: "Platform", glossary: [], onboarding: [],
})

const edge = (id: string, kind: string, from: string, to: string, label: string): GraphEdge => ({
  id, kind, from, to, label, confidence: "confirmed", resolution: "resolved", provenance: ["scanner"], properties: {},
  evidence: [{
    id: `${id}:evidence`, repository: "archie", revision: "abc123", path: "Checkout/Program.cs", startLine: 42,
    endLine: 42, extractionMethod: "scanner", provenance: "scanner", confidence: "confirmed",
    sourceUrl: "https://example.com/archie/blob/abc123/Checkout/Program.cs#L42",
  }],
})

const graph: Graph = {
  schemaVersion: "architecture/v1", snapshotId: "snapshot:test", workspace: "Book Retail", description: "Test graph",
  nodes: [
    node("system:bookretail", "system", "BookRetail"),
    node("module:checkout", "module", "Checkout"),
    node("module:ordering", "module", "Ordering"),
    node("deployable:checkout-service", "deployable", "Checkout Service"),
    node("channel:order-submitted", "message-channel", "order.submitted"),
    node("deployable:ordering-service", "deployable", "Ordering Service"),
    node("database:orders", "database", "Orders", "legacy"),
  ],
  edges: [
    edge("contains:system-checkout", "contains", "system:bookretail", "module:checkout", "contains Checkout"),
    edge("contains:system-ordering", "contains", "system:bookretail", "module:ordering", "contains Ordering"),
    edge("contains:checkout-service", "contains", "module:checkout", "deployable:checkout-service", "contains Checkout Service"),
    edge("contains:ordering-service", "contains", "module:ordering", "deployable:ordering-service", "contains Ordering Service"),
    edge("publishes:order", "publishes", "deployable:checkout-service", "channel:order-submitted", "publishes order.submitted"),
    edge("subscribes:order", "subscribes", "deployable:ordering-service", "channel:order-submitted", "subscribes to order.submitted"),
    edge("depends:orders", "depends-on", "deployable:ordering-service", "database:orders", "stores orders"),
  ],
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function stubGraph(diagnostics: readonly Diagnostic[] = [], value: Graph = graph) {
  const fetch = vi.fn(async (input: RequestInfo | URL) => {
    const body = String(input).endsWith("/api/v1/diagnostics") ? diagnostics : value
    return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } })
  })
  vi.stubGlobal("fetch", fetch)
  return fetch
}

describe("App", () => {
  it("shows a useful error when the canonical graph cannot load", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ message: "Graph is unavailable." }), { status: 503, headers: { "Content-Type": "application/json" } })))
    render(<App />)

    expect(await screen.findByRole("alert")).toHaveTextContent("Architecture could not be loaded: Graph is unavailable.")
  })

  it("opens directly on the dependency map without assistant, saved-view, or network APIs", async () => {
    const fetch = stubGraph()
    render(<App />)

    expect(await screen.findByRole("heading", { name: "How the codebase is connected" })).toBeVisible()
    expect(screen.getByRole("region", { name: "Dependency map" })).toBeVisible()
    expect(screen.getByLabelText("Dependency architecture graph")).toBeVisible()
    expect(screen.queryByText(/Ask Atlas|Saved code journeys|Take journey/)).not.toBeInTheDocument()
    expect(fetch.mock.calls.map(([input]) => String(input)).sort()).toEqual(["/api/v1/diagnostics", "/api/v1/graph"])
  })

  it("shows and dismisses missing scanner coverage while keeping the map usable", async () => {
    stubGraph([
      { id: "diagnostic:installed", code: "SCANNER_COVERAGE_INSTALLED", severity: "info", message: ".NET coverage is installed.", subjectId: "stack:dotnet" },
      { id: "diagnostic:missing", code: "SCANNER_COVERAGE_MISSING", severity: "warning", message: "Laravel has no scanner. Install with archie scanner add archie.php.", subjectId: "stack:laravel" },
    ])
    const user = userEvent.setup()
    render(<App />)

    const banner = await screen.findByRole("status", { name: "Scanner coverage is incomplete" })
    expect(banner).toHaveTextContent("archie scanner add archie.php")
    expect(banner).not.toHaveTextContent(".NET coverage is installed")
    await user.click(screen.getByRole("button", { name: "Dismiss" }))
    expect(screen.queryByRole("status", { name: "Scanner coverage is incomplete" })).not.toBeInTheDocument()
    expect(screen.getByRole("region", { name: "Dependency map" })).toBeVisible()
  })

  it("keeps the graph available when diagnostics cannot load", async () => {
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => String(input).endsWith("/api/v1/diagnostics")
      ? new Response(JSON.stringify({ message: "Diagnostics are unavailable." }), { status: 503, headers: { "Content-Type": "application/json" } })
      : new Response(JSON.stringify(graph), { status: 200, headers: { "Content-Type": "application/json" } })))
    render(<App />)

    expect(await screen.findByRole("heading", { name: "How the codebase is connected" })).toBeVisible()
    expect(await screen.findByRole("status", { name: "Scanner coverage is unavailable" })).toHaveTextContent("coverage cannot be confirmed")
  })

  it("searches, filters, and opens component details", async () => {
    stubGraph()
    const user = userEvent.setup()
    render(<App />)
    const search = await screen.findByRole("textbox", { name: "Search architecture" })

    await user.type(search, "checkout service")
    await user.click(screen.getByRole("option", { name: /Checkout Service/ }))
    expect(screen.getByRole("heading", { name: "Checkout Service" })).toBeVisible()
    expect(screen.getByText("1 of 7 nodes match")).toBeVisible()

    await user.clear(search)
    await user.click(screen.getByRole("button", { name: "Filters" }))
    await user.selectOptions(screen.getByRole("combobox", { name: "Filter by node kind" }), "database")
    expect(screen.getByText("1 of 7 nodes match")).toBeVisible()
    await user.selectOptions(screen.getByRole("combobox", { name: "Filter by environment" }), "current")
    expect(screen.getByRole("status")).toHaveTextContent("No architecture nodes match")
    await user.click(screen.getByRole("button", { name: "Clear filters" }))
    expect(screen.getByText("7 of 7 nodes match")).toBeVisible()
  })

  it("shows inferred runtime relationships with source evidence", async () => {
    stubGraph()
    const user = userEvent.setup()
    render(<App />)
    await screen.findByRole("heading", { name: "How the codebase is connected" })

    await user.click(screen.getByRole("button", { name: "Inferred runtime map" }))
    expect(screen.getByRole("region", { name: "Inferred runtime map" })).toBeVisible()
    expect(screen.getByLabelText("Inferred runtime architecture graph")).toBeVisible()
    await user.click(screen.getByRole("button", { name: "Select relationship: stores orders" }))
    expect(screen.getByRole("heading", { name: "stores orders" })).toBeVisible()
    expect(screen.getByRole("link", { name: "Checkout/Program.cs:42" })).toHaveAttribute("href", "https://example.com/archie/blob/abc123/Checkout/Program.cs#L42")
  })

  it("renders dependency and runtime maps for flat scanner graphs", async () => {
    const flatGraph = {
      ...graph,
      nodes: graph.nodes.filter((item) => !["system", "module"].includes(item.kind)),
      edges: graph.edges.filter((item) => item.kind !== "contains"),
    }
    stubGraph([], flatGraph)
    const user = userEvent.setup()
    render(<App />)

    expect(await screen.findByText("Checkout Service")).toBeVisible()
    expect(screen.getByRole("button", { name: "Select relationship: stores orders" })).toBeInTheDocument()
    await user.click(screen.getByRole("button", { name: "Inferred runtime map" }))
    expect(screen.getByText("Ordering Service")).toBeVisible()
    expect(screen.getByRole("button", { name: "Select relationship: publishes order.submitted" })).toBeInTheDocument()
  })

  it("collapses the inspector and exits focus mode with Escape", async () => {
    stubGraph()
    const user = userEvent.setup()
    render(<App />)
    await screen.findByRole("heading", { name: "How the codebase is connected" })

    await user.click(screen.getByRole("button", { name: "Hide inspector" }))
    expect(screen.queryByRole("complementary", { name: "Architecture details" })).not.toBeInTheDocument()
    await user.click(screen.getByRole("button", { name: "Focus map" }))
    expect(screen.getByText("Map focus mode")).toBeVisible()
    fireEvent.keyDown(window, { key: "Escape" })
    expect(screen.queryByText("Map focus mode")).not.toBeInTheDocument()
  })
})
