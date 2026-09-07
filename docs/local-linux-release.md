# Archie local Linux release

Archie is distributed as one scanner-free, framework-dependent Linux x64 archive. It detects supported technology stacks without running repository code, but scanning requires you to install the first-party scanners you need explicitly. Installed scanners remain available offline and can be updated or rolled back independently. Archie writes deterministic architecture graphs to user-local state and serves the bundled explorer from a loopback-only address.

## Requirements

- Linux x64 with .NET 10 SDK/reference packs
- Git
- util-linux `unshare` with delegated user and PID namespaces
- A browser (optional when using `--no-browser`)

Scanners may have additional runtime requirements stated by their release. The Archie archive itself contains no scanner implementation or scanner-only runtime dependency.

## Download and verify

Download the versioned archive and adjacent `.sha256` file from [GitHub Releases](https://github.com/Entorn-dev/archie/releases), then run:

```bash
sha256sum --check archie-linux-x64-<version>.tar.gz.sha256
```

Proceed only when the command reports `OK`. The checksum detects accidental or post-download changes, but the initial release is not independently signed. GitHub is the release trust root. Independent archive signing may be added in a later release.

Archie deliberately fails closed when scanner descendant containment is unavailable. It does not restore or build the analyzed repository, contact package feeds, request credentials, run project code, or require files to be added to that repository. Existing successful restore metadata and compatible packages in the physical default NuGet cache (`~/.nuget/packages`) can improve semantic coverage. Missing dependencies and alternate cache roots reduce coverage with diagnostics rather than triggering a restore.

## License and third-party notices

Archie is licensed under Apache-2.0. Each archive contains `LICENSE`, `THIRD-PARTY-NOTICES.md`, and the license or notice material supplied by bundled dependencies under `licenses/`. In particular, the bundled `elkjs` layout engine remains under EPL-2.0, and its corresponding source is available from <https://github.com/kieler/elkjs>.

## Install scanners and open a repository

```bash
tar -xzf archie-linux-x64-<version>.tar.gz
./archie-linux-x64-<version>/archie scanner recommend /path/to/separately-cloned/repository
./archie-linux-x64-<version>/archie scanner add archie.dotnet
# Add every other scanner recommended for the repository, then:
./archie-linux-x64-<version>/archie open /path/to/separately-cloned/repository
```

`scanner recommend` is offline and only reports detected coverage. `scanner add` is the explicit network and executable-code trust boundary; `open` never downloads scanners. For an offline transfer or local package test, use `scanner add --local <archive.tar.gz>` and review the unsigned-code warning. See [Scanner installation and lifecycle](scanners.md) for trust, cache, update, and rollback details.

After applicable scanners are installed, `open` scans first, then prints and opens an `http://127.0.0.1:<port>/app` URL. If no applicable scanner is installed, it stops with exact recommendations without replacing a prior successful snapshot. The host remains in the foreground; Ctrl+C stops it. Use `--no-browser` to print the URL without opening it.

You can invoke Archie from any working directory and pass either an absolute or relative repository path. The default generated files are:

```text
$XDG_STATE_HOME/archie/repositories/<repository-name>-<path-digest>/observations.json
$XDG_STATE_HOME/archie/repositories/<repository-name>-<path-digest>/graph.json
$XDG_STATE_HOME/archie/repositories/<repository-name>-<path-digest>/source-context.json
```

When `XDG_STATE_HOME` is unset, Archie uses `~/.local/state`. Installed scanners live beneath `$XDG_DATA_HOME/archie/scanners`, or `~/.local/share/archie/scanners` when `XDG_DATA_HOME` is unset. Each repository path receives independent stable state. Override the complete repository state directory with `--state-directory <path>`.

Optional arguments are `--overlay <path>`, `--scanner-timeout <duration>` (maximum 60 minutes), `--as-of YYYY-MM-DD`, `--state-directory <path>`, and `--no-browser`.

## Connect a local MCP client

After one successful `open`, configure a standard-input/output MCP client to launch:

```bash
/absolute/path/to/archie-linux-x64-<version>/archie mcp /absolute/path/to/repository
```

A typical client configuration has this shape (the outer configuration key varies by client):

```json
{
  "archie": {
    "type": "stdio",
    "command": "/absolute/path/to/archie-linux-x64-<version>/archie",
    "args": ["mcp", "/absolute/path/to/repository"]
  }
}
```

The server lists exactly four read-only tools:

- `archie_get_context` — exact repository-relative file/line or canonical-subject context.
- `archie_get_dependencies` — bounded upstream/downstream logical dependencies.
- `archie_trace_flow` — bounded graph paths plus deterministic evidence-cited Markdown.
- `archie_assess_change_impact` — potential static impact split into direct evidence, owner-level possibilities, downstream effects, and unmatched files.

Each tool returns one compact, deterministic, answer-first Markdown block by default. Compact responses do not duplicate that text as structured content. Use progressive detail when an answer needs verification:

```json
{"path":"src/Checkout.cs","line":42}
{"path":"src/Checkout.cs","line":42,"detail":"evidence"}
{"path":"src/Checkout.cs","line":42,"detail":"full"}
```

- Omitted `detail`, or `detail: summary`, gives the smallest useful answer with qualifications, compact source citations, and expansion guidance.
- `detail: evidence` is the normal single follow-up. It adds exact evidence IDs, provenance, confidence, extraction methods, and source-ownership derivation where applicable.
- `detail: full` returns the exhaustive legacy text and structured result for existing programmatic consumers. Consumers that previously relied on structured content from an omitted-detail call should add `detail: full` when migrating.
- `maxItems` limits only the number of items shown after Archie completes the existing bounded factual query. A heading such as `Relationships (12/19)` means 12 are shown out of 19 available query results; it does not mean Archie found only 12.

Presentation defaults and allowed maxima are:

| Tool | Default `maxItems` | Maximum `maxItems` | Unit |
| --- | ---: | ---: | --- |
| `archie_get_context` | 6 | 25 | Per factual context section |
| `archie_get_dependencies` | 12 | 50 | Relationships |
| `archie_trace_flow` | 3 | 10 | Complete paths |
| `archie_assess_change_impact` | 8 | 25 | Per impact category |

To avoid repeating long repository paths, a summary includes one counted source preview; `detail: evidence` expands exact support for the displayed facts. Summary and evidence responses have atomic UTF-8 ceilings of 16 KiB and 64 KiB. If a complete item plus mandatory answer, qualification, counts, and guidance cannot fit, Archie returns `COMPACT_RESPONSE_LIMIT_EXCEEDED` rather than cutting a record or concealing omissions. Full detail retains the existing 1 MiB result and 256 KiB generated-Markdown limits. Existing depth, path, graph, and changed-file query limits always run before presentation limits.

MCP startup loads the latest successful local snapshot and never scans. If no complete snapshot exists, run `archie open` first. Refresh explicitly by running `open` again. The MCP process reads no source contents, performs no network or model calls, and does not mutate the snapshot or analyzed repository. Standard output contains MCP protocol messages only; diagnostics use standard error.

## Privacy and failure behavior

Repository source stays local. Persisted evidence and source ownership contain repository-relative locations and safe extracted metadata, not source snippets, checkout-absolute paths, credentials, connection strings, or unsafe repository remotes. Scanner failures, malformed output, timeouts, cancellation, and hard-limit breaches do not replace a prior valid observation/graph/source-context set.

The first release is intentionally single-repository. Opening another repository creates another graph; it does not compose repositories. The archive is not a daemon, cloud service, Git-provider integration, CI workflow, or general scanner sandbox. Verified first-party and explicitly accepted local scanners are executable code; lifecycle containment does not prevent filesystem or network access by malicious code.

## Troubleshooting

- `SCANNER_CONTAINMENT_UNAVAILABLE`: confirm `unshare --user --pid --fork` is permitted for the current user.
- Missing scanner coverage: run `archie scanner recommend <repository>`, explicitly install every recommended scanner, and retry `open`.
- Reduced .NET semantic coverage: restore/build the repository through its normal trusted developer workflow if desired, then rerun Archie. Archie itself will not do this or ask for private-feed credentials.
- No browser opens: rerun with `--no-browser` and open the printed loopback URL manually.
- MCP reports no successful snapshot: run `archie open <repository>` successfully before starting the MCP client, and ensure both commands use the same repository path and `XDG_STATE_HOME`/`--state-directory`.
- Invalid graph or limit diagnostic: narrow the repository or fix the reported artifact/source issue; Archie never serves a partial graph after a hard-limit failure.
