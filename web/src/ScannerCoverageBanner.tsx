import { useEffect, useState } from "react"
import type { Diagnostic } from "./api"

export function ScannerCoverageBanner({ diagnostics, error }: { diagnostics: readonly Diagnostic[]; error?: string }) {
  const missing = diagnostics.filter((diagnostic) => diagnostic.code === "SCANNER_COVERAGE_MISSING")
  const stateKey = error ? `error:${error}` : missing.map((diagnostic) => diagnostic.id).join("|")
  const [dismissed, setDismissed] = useState(false)

  useEffect(() => setDismissed(false), [stateKey])

  if (dismissed || (!error && missing.length === 0)) return null

  return <section className="scanner-coverage-banner" role="status" aria-labelledby="scanner-coverage-title">
    <span className="scanner-coverage-icon" aria-hidden="true">!</span>
    <div>
      <strong id="scanner-coverage-title">{error ? "Scanner coverage is unavailable" : "Scanner coverage is incomplete"}</strong>
      {error
        ? <p>Coverage diagnostics could not be loaded. The architecture graph remains available, but its scanner coverage cannot be confirmed.</p>
        : missing.map((diagnostic) => <p key={diagnostic.id}>{diagnostic.message}</p>)}
    </div>
    <button type="button" onClick={() => setDismissed(true)}>Dismiss</button>
  </section>
}
