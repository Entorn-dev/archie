# Archie

Archie is a local-first architecture tool for understanding a repository without uploading its source or creating an account. It runs explicitly installed scanners, reconciles their observations into one canonical graph, and serves a loopback-only viewer with dependency and inferred-runtime maps, diagnostics, evidence, and source locations.

Archie is licensed under the [Apache License 2.0](LICENSE). Third-party components retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Install and open a repository

The current package targets Linux x64 and requires the .NET 10 runtime, Git, and util-linux `unshare` with delegated user/PID namespaces.

Download the archive and adjacent `.sha256` file from [GitHub Releases](https://github.com/Entorn-dev/archie/releases), then verify it before extraction:

```bash
sha256sum --check archie-linux-x64-<version>.tar.gz.sha256
```

The checksum detects accidental or post-download changes, but the initial release is not independently signed. GitHub is the release trust root.

```bash
tar -xzf archie-linux-x64-<version>.tar.gz
./archie-linux-x64-<version>/archie scanner recommend /path/to/repository
./archie-linux-x64-<version>/archie scanner add archie.dotnet
./archie-linux-x64-<version>/archie open /path/to/repository
```

Install every scanner recommended for the repository. `scanner recommend`, scanning, `open`, and MCP are offline; only explicit catalog commands such as `scanner add` may use the network. `open` writes its deterministic observations, graph, and source context beneath `$XDG_STATE_HOME/archie/repositories` (or `~/.local/state/archie/repositories`), never into the analyzed checkout. It then prints an `http://127.0.0.1:<port>/app` URL and keeps the local host in the foreground.

The viewer opens on the dependency map. Switch to the inferred-runtime map to inspect deployable interactions, then select a node or relationship for canonical metadata, provenance, and source evidence. Search, kind/environment filters, pan, zoom, inspector collapse, and focus mode are all local. No model, account, telemetry service, or external browser request is involved.

See [the Linux release guide](docs/local-linux-release.md) for complete installation and MCP instructions and [the scanner guide](docs/scanners.md) for trust, updates, rollback, and offline installation.

## Give a coding agent architecture context

After one successful `archie open`, configure an MCP client to launch:

```bash
/absolute/path/to/archie mcp /absolute/path/to/repository
```

The read-only stdio server exposes bounded tools for file/subject context, dependencies, flow tracing, and potential change impact. It loads the exact latest successful local snapshot, makes no network or model calls, reads no source contents, and mutates neither the graph nor the repository.

## Build and test

The repository pins toolchains in `mise.toml` and dependencies in lockfiles.

```bash
.agents/setup
dotnet build Archie.slnx --no-restore
dotnet test Archie.slnx --no-build --no-restore
pnpm typecheck
pnpm test
pnpm build
pnpm package:linux-x64
scripts/verify-linux-package.sh
```

The multi-language `demo/book-retail` fixture and `.amp/services.yaml` provide contributor acceptance in an Amp orb. See [the architecture](docs/architecture.md), [scanner protocol](docs/scanner-protocol.md), and [contribution guide](CONTRIBUTING.md).

## Security and scope

Repository source stays local. Scanners are executable code and are installed only through an explicit trust boundary; process-lifetime containment is not a complete filesystem or network sandbox. Archie does not restore, build, or execute the analyzed repository. See [SECURITY.md](SECURITY.md) before reporting a vulnerability.

The first release is intentionally single-repository and scanner-free. Hosted repository connections, teams, persistent history, billing, cloud execution, and operations are not part of this repository.

## Contributing

Contributions are welcome through GitHub pull requests and require Developer Certificate of Origin 1.1 sign-off. See [CONTRIBUTING.md](CONTRIBUTING.md) for the contribution process and required checks, and [SUPPORT.md](SUPPORT.md) for safe issue-reporting guidance.
