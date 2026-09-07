import { createServer } from "node:http"

createServer(async (request, response) => {
  if (request.method === "GET" && request.url === "/health") {
    response.writeHead(200, { "content-type": "application/json" })
    response.end(JSON.stringify({ status: "ready" }))
    return
  }
  if (request.method === "POST" && request.url === "/payments") {
    for await (const _ of request) { /* discard the synthetic payment token */ }
    response.writeHead(200, { "content-type": "application/json" })
    response.end(JSON.stringify({ status: "authorized", providerReference: "local-demo" }))
    return
  }
  response.writeHead(404).end()
}).listen(4402, "127.0.0.1", () => console.log("Payment stub listening on 4402"))
