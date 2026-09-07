import { expect, test, type Page } from "@playwright/test"
import AxeBuilder from "@axe-core/playwright"

type ApiGraph = {
  nodes: { name: string }[]
  edges: { label: string; evidence: { path: string; extractionMethod: string; provenance: string }[] }[]
}

async function expectNoSeriousAccessibilityViolations(page: Page) {
  const result = await new AxeBuilder({ page }).include("main").analyze()
  expect(result.violations.filter((violation) => violation.impact === "serious" || violation.impact === "critical")).toEqual([])
}

test("landing page introduces local Archie and opens the dependency map", async ({ page }) => {
  await page.goto("/")
  await expect(page.getByRole("heading", { name: /Understand your repository/ })).toBeVisible()
  await expect(page.getByLabel("Archie dependency map preview")).toBeVisible()
  await expect(page.getByText("No account is required.")).toBeVisible()
  await expectNoSeriousAccessibilityViolations(page)

  await page.getByRole("link", { name: /Get started/ }).first().click()
  await expect(page).toHaveURL(/\/app$/)
  await expect(page.getByRole("heading", { name: "How the codebase is connected" })).toBeVisible()
})

test("serves the scanner and manually grounded canonical graph", async ({ page }) => {
  const graphResponse = await page.request.get("/api/v1/graph")
  expect(graphResponse.ok()).toBeTruthy()
  const graph = await graphResponse.json() as ApiGraph

  expect(graph.nodes.some((node) => node.name === "POST /api/orders")).toBeTruthy()
  expect(graph.edges.find((edge) => edge.label === "exposes POST /api/orders")?.evidence).toContainEqual(expect.objectContaining({
    path: "Checkout/Program.cs", extractionMethod: "roslyn:semantic-minimal-api-route-host-dataflow", provenance: "scanner",
  }))
  expect(graph.edges.find((edge) => edge.label === "calls Payment external service")?.evidence).toContainEqual(expect.objectContaining({
    path: "Checkout/Program.cs", extractionMethod: "roslyn:semantic-http-client-configured-base-address", provenance: "scanner",
  }))
  expect(graph.edges.find((edge) => edge.label === "depends on Orders database")?.evidence).toContainEqual(expect.objectContaining({
    path: "demo/book-retail/infra/postgres/init.sql", extractionMethod: "governed architecture overlay", provenance: "manual",
  }))
})

test("dependency map supports local search, filters, details, and no external requests", async ({ page }) => {
  const externalRequests: string[] = []
  page.on("request", (request) => {
    const url = new URL(request.url())
    if (!url.hostname.match(/^(127\.0\.0\.1|localhost)$/)) externalRequests.push(request.url())
  })
  await page.goto("/app")
  await expect(page.getByRole("region", { name: "Dependency map" })).toBeVisible()
  await expect(page.getByLabel("Dependency architecture graph")).toBeVisible()
  await expect(page.getByText(/Ask Atlas|Saved code journeys|Take journey/)).toHaveCount(0)

  await page.getByRole("textbox", { name: "Search architecture" }).fill("Checkout Service")
  await page.getByRole("option", { name: /Checkout Service/ }).click()
  await expect(page.getByRole("complementary", { name: "Architecture details" })).toContainText("Checkout Service")
  await expect(page.getByText(/1 of .* nodes match/)).toBeVisible()

  await page.getByRole("textbox", { name: "Search architecture" }).fill("")
  await page.getByRole("button", { name: "Filters" }).click()
  await page.getByRole("combobox", { name: "Filter by node kind" }).selectOption("database")
  await expect(page.getByText(/of .* nodes match/)).toBeVisible()
  await page.getByRole("button", { name: "Clear filters" }).click()
  await expectNoSeriousAccessibilityViolations(page)
  expect(externalRequests).toEqual([])
})

test("inferred runtime map opens canonical relationship evidence and source locations", async ({ page }) => {
  await page.goto("/app")
  await page.getByRole("button", { name: "Inferred runtime map" }).click()
  await expect(page.getByRole("region", { name: "Inferred runtime map" })).toBeVisible()
  await expect(page.getByLabel("Inferred runtime architecture graph")).toBeVisible()

  const relationship = page.getByRole("button", { name: "Select relationship: calls Payment external service" })
  await relationship.focus()
  await page.keyboard.press("Enter")
  const details = page.getByRole("complementary", { name: "Architecture details" })
  await expect(details).toContainText("calls Payment external service")
  await expect(details).toContainText("Checkout/Program.cs")
  await expect(details).toContainText("roslyn:semantic-http-client-configured-base-address")
  await expectNoSeriousAccessibilityViolations(page)
})

test("inspector and focused map remain independently controllable", async ({ page }) => {
  await page.goto("/app")
  await page.getByRole("button", { name: "Hide inspector" }).click()
  await expect(page.getByRole("complementary", { name: "Architecture details" })).toBeHidden()
  await page.getByRole("button", { name: "Focus map" }).click()
  await expect(page.getByText("Map focus mode")).toBeVisible()
  await page.keyboard.press("Escape")
  await expect(page.getByText("Map focus mode")).toBeHidden()
})

test("the retired assistant prototype URL no longer opens the application", async ({ page }) => {
  await page.goto("/prototypes/assistant-explorer")
  await expect(page.getByRole("heading", { name: /Understand your repository/ })).toBeVisible()
  await expect(page.getByRole("navigation", { name: "Primary navigation" })).toHaveCount(0)
})
