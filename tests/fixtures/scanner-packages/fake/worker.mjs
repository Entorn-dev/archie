import { readFile } from "node:fs/promises"
import { createInterface } from "node:readline"

const protocolVersion = "scanner/v1"
const scanner = { id: "archie.fake-book-retail", version: "1.0.0" }
const send = (message) => process.stdout.write(`${JSON.stringify(message)}\n`)

send({ protocolVersion, type: "ready", scanner })

const lines = createInterface({ input: process.stdin, crlfDelay: Infinity })
for await (const line of lines) {
  const request = JSON.parse(line)
  if (request.protocolVersion !== protocolVersion || request.type !== "scan-request") process.exit(2)
  const fixture = JSON.parse(await readFile(
    `${request.context.checkoutPath}/tests/fixtures/observations/book-retail.authored.json`, "utf8"))
  for (const source of fixture.observations) {
    const observation = structuredClone(source)
    observation.evidence.scannerId = scanner.id
    observation.evidence.scannerVersion = scanner.version
    observation.evidence.extractionMethod = "installed local fixture scanner"
    send({ protocolVersion, type: "observation", observation })
  }
  send({
    protocolVersion,
    type: "source-ownership",
    ownership: {
      scannerId: scanner.id,
      scannerVersion: scanner.version,
      path: "Program.cs",
      ownerCandidateKey: "deployable:checkout-service",
      ownershipKind: "deployable",
      confidence: "confirmed",
      resolution: "resolved",
      derivationRule: "installed-fixture:project-membership"
    }
  })
  send({ protocolVersion, type: "completed", summary: { observationCount: fixture.observations.length } })
  break
}
