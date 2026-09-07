import { createServer, type IncomingMessage, type ServerResponse } from "node:http"

const catalogueUrl = process.env.CATALOGUE_URL ?? "http://127.0.0.1:4401"
const checkoutUrl = process.env.CHECKOUT_URL ?? "http://127.0.0.1:4404"
const port = Number(process.env.PORT ?? "4400")

async function proxy(response: ServerResponse, target: string, request?: IncomingMessage): Promise<void> {
  const body = request ? await readBody(request) : undefined
  const upstream = await fetch(target, {
    method: request?.method ?? "GET",
    headers: body ? { "content-type": "application/json" } : undefined,
    body,
  })
  response.writeHead(upstream.status, { "content-type": upstream.headers.get("content-type") ?? "application/json" })
  response.end(await upstream.text())
}

async function readBody(request: IncomingMessage): Promise<string> {
  const chunks: Buffer[] = []
  for await (const chunk of request) chunks.push(Buffer.from(chunk))
  return Buffer.concat(chunks).toString("utf8")
}

export const server = createServer(async (request, response) => {
  try {
    if (request.method === "GET" && request.url === "/health") {
      const [catalogue, checkout] = await Promise.all([
        fetch(`${catalogueUrl}/health`),
        fetch(`${checkoutUrl}/health`),
      ])
      response.writeHead(catalogue.ok && checkout.ok ? 200 : 503, { "content-type": "application/json" })
      response.end(JSON.stringify({ status: catalogue.ok && checkout.ok ? "ready" : "starting" }))
    } else if (request.method === "GET" && request.url === "/api/books") {
      await proxy(response, `${catalogueUrl}/api/books`)
    } else if (request.method === "POST" && request.url === "/api/orders") {
      await proxy(response, `${checkoutUrl}/api/orders`, request)
    } else {
      response.writeHead(404, { "content-type": "application/json" })
      response.end(JSON.stringify({ error: "not found" }))
    }
  } catch (error) {
    response.writeHead(502, { "content-type": "application/json" })
    response.end(JSON.stringify({ error: error instanceof Error ? error.message : "upstream unavailable" }))
  }
})

if (process.env.NODE_ENV !== "test") {
  server.listen(port, "127.0.0.1", () => console.log(`Storefront listening on ${port}`))
}
