import assert from "node:assert/strict"
import { test } from "node:test"
import { readFile } from "node:fs/promises"

test("storefront keeps catalogue and checkout relationships explicit", async () => {
  const source = await readFile(new URL("../src/server.ts", import.meta.url), "utf8")
  assert.match(source, /CATALOGUE_URL/)
  assert.match(source, /CHECKOUT_URL/)
  assert.match(source, /\/api\/books/)
  assert.match(source, /\/api\/orders/)
})
