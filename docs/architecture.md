# Archie architecture

Archie turns scanner observations into local, evidence-backed architecture context while keeping authority and side effects explicit.

```text
repository files
    │ bounded inert input
    ▼
installed scanners ── observations + ownership claims
    │
    ▼
Archie.Runner ── validates protocol, limits, redaction, lifecycle
    │
    ▼
Archie.Core ── reconciles observations + optional governed overlay
    │
    ├── observations.json
    ├── graph.json
    └── source-context.json
           │
           ├── Archie.Api ── loopback read-only API ── local viewer
           └── Archie.Cli MCP ── read-only stdio tools
```

## Ownership

- `Archie.Contracts` owns canonical models and deterministic JSON contracts.
- `Archie.Runner` owns scanner discovery, process supervision, limits, protocol validation, redaction, and stack detection.
- `Archie.Core` is the sole canonical graph authority: identity resolution, reconciliation, validation, queries, diffs, and architecture context.
- `Archie.Cli` owns commands, user-local repository state, scanner package/catalog lifecycle, packaging entry points, and MCP.
- `Archie.Api` adapts one validated graph to the bounded loopback API used by `archie open`.
- `web` owns the API client and minimal dependency/runtime presentation. It has no account, model, hosted, or persistence boundary.

Language-specific scanner implementations are independently released. Hosted SaaS and cloud execution are private and outside this repository.

## Trust and data flow

`archie scan` inventories repository-relative paths and sends only allowed bounded inputs to explicitly installed workers. Workers emit versioned NDJSON observations; they never write the graph. Archie validates and redacts output before reconciliation, then transactionally publishes a matched observation/graph/source-context set under user-local state.

`archie open` performs that scan, starts `Archie.Api` on `127.0.0.1`, and serves the compiled viewer. The viewer requests only graph and diagnostic data from the same loopback origin. `archie mcp` loads the latest complete snapshot without rescanning and exposes four bounded, read-only context tools over stdio.

Evidence contains repository-relative source locations and safe extracted metadata, not source snippets, connection strings, credentials, checkout-absolute paths, or unsafe remotes. A cancellation, malformed scanner result, timeout, or limit breach leaves the prior complete snapshot untouched.
