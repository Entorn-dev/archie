import { readFile } from "node:fs/promises"
import { createInterface } from "node:readline"

const fixture = async (name) => JSON.parse(await readFile(new URL(`valid/${name}.json`, import.meta.url), "utf8"))
const send = (message) => process.stdout.write(`${JSON.stringify(message)}\n`)

send({ protocolVersion: "scanner/v1", type: "ready", scanner: { id: "archie.fixture", version: "1.0.0" } })

const lines = createInterface({ input: process.stdin, crlfDelay: Infinity })
for await (const line of lines) {
  const request = JSON.parse(line)
  if (request.protocolVersion !== "scanner/v1" || request.type !== "scan-request") process.exit(2)
  send(await fixture("entity-observation"))
  send(await fixture("relationship-observation"))
  send(await fixture("source-ownership"))
  send(await fixture("diagnostic"))
  send(await fixture("completed"))
  break
}
