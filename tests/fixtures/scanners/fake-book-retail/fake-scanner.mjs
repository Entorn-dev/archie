import { readFile } from "node:fs/promises"
import { createInterface } from "node:readline"

const protocolVersion = "scanner/v1"
const scanner = { id: "archie.fake-book-retail", version: "1.0.0" }
const send = (message) => process.stdout.write(`${JSON.stringify(message)}\n`)

send({ protocolVersion, type: "ready", scanner })

const lines = createInterface({ input: process.stdin, crlfDelay: Infinity })
for await (const line of lines) {
  const request = JSON.parse(line)
  if (request.protocolVersion !== protocolVersion || request.type !== "scan-request") {
    process.stderr.write("Invalid scan request\n")
    process.exitCode = 2
    break
  }

  const fixturePath = `${request.context.checkoutPath}/tests/fixtures/observations/book-retail.authored.json`
  const fixture = JSON.parse(await readFile(fixturePath, "utf8"))
  for (const [index, source] of fixture.observations.entries()) {
    const observation = structuredClone(source)
    observation.evidence.scannerId = scanner.id
    observation.evidence.scannerVersion = scanner.version
    observation.evidence.extractionMethod = "fake language-neutral scanner observation"
    observation.evidence.properties.observationSource = "scanner"
    if (index === 0) {
      observation.evidence.properties.provider = "fake"
      observation.evidence.properties.resourceKind = "book-retail-fixture"
      observation.evidence.properties.configurationKey = "BOOK_RETAIL_ENDPOINT"
      observation.evidence.properties.password = "slice-five-sensitive-value"
    }
    send({ protocolVersion, type: "observation", observation })
  }
  send({ protocolVersion, type: "completed", summary: { observationCount: fixture.observations.length } })
  break
}
