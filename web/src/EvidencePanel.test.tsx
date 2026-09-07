import { render, screen } from "@testing-library/react"
import { describe, expect, it } from "vitest"
import { EvidencePanel } from "./EvidencePanel"
import type { GraphEdge } from "./api"

describe("EvidencePanel", () => {
  it("groups scanner and governed manual support on a mixed relationship", () => {
    const edge: GraphEdge = {
      id: "edge:mixed", kind: "calls", from: "a", to: "b", label: "calls payments",
      confidence: "confirmed", resolution: "resolved", provenance: ["scanner", "manual"],
      properties: { configurationKey: "Payment:BaseUrl", provider: "http", label: "calls payments" },
      evidence: [
        { id: "scanner", repository: "repo", revision: "abc", path: "src/a.cs", startLine: 1, endLine: 1, extractionMethod: "semantic call", provenance: "scanner", confidence: "confirmed" },
        { id: "manual", repository: "repo", revision: "abc", path: "architecture.overlay.json", startLine: 1, endLine: 1, extractionMethod: "governed architecture overlay", provenance: "manual", confidence: "confirmed", owner: "team:checkout", reviewer: "role:architect", reviewStatus: "current", rationale: "Approved external binding." },
      ],
    }

    render(<EvidencePanel edge={edge} />)

    expect(screen.getByText("SCANNER evidence")).toBeVisible()
    expect(screen.getByText("MANUAL evidence")).toBeVisible()
    expect(screen.getByText("Payment:BaseUrl")).toBeVisible()
    expect(screen.getByText("http")).toBeVisible()
    expect(screen.getByText("team:checkout")).toBeVisible()
    expect(screen.getByText("Approved external binding.")).toBeVisible()
  })
})
