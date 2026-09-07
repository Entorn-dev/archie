# Scanner installation and lifecycle

Archie installs scanners into the current user's data directory. Scanner installation is explicit: opening, scanning, recommending, serving a graph, and starting MCP never download or update executable code.

## Commands

```text
archie scanner search [<query>]
archie scanner info <id>
archie scanner add <id> [--version <version>]
archie scanner add --local <archive.tar.gz>
archie scanner update <id> [--version <version>] [--from catalog]
archie scanner list
archie scanner use <id> <version> [<sha256>]
archie scanner remove <id> [<version> [<sha256>]]
archie scanner recommend [<repository>]
```

`add`, `search`, `info`, and `update` are the only commands above that may contact the catalog. `--version` selects one exact semantic version. `use` activates an already installed immutable package without network access; provide its SHA-256 when multiple local builds declare the same ID and version.

`update` selects the latest compatible stable catalog release unless an exact version is requested. Archie installs and validates the new package before atomically changing activation, retaining the previously active package for rollback. A local unsigned scanner is never silently replaced with a catalog package: use `scanner update <id> --from catalog` to explicitly change its trust origin.

## Trust and verification

Catalog packages are first-party executable code. Archie verifies the signed catalog with a built-in ECDSA P-256 public key, then checks the selected GitHub Release URL, compressed size, SHA-256, package signature, metadata, permissions, compatibility, and bounded archive extraction before activation. The receipt labels these packages `catalog` and `verified-first-party`.

`scanner add --local` uses the same compatibility, permissions, extraction, locking, and atomic-activation checks, but publisher authenticity cannot be established. Archie prints an executable-code warning and records the package as `local-unsigned`. A signature establishes publisher and byte integrity; it is not a general execution sandbox.

## Cache, outages, and revocation

Archie stores only the last fully verified catalog under the scanner data root. If `search` or `info` cannot refresh it, Archie may use that verified cache and prints its retrieval time plus the refresh failure. An invalid or interrupted refresh never replaces the cache. State-changing `add` and `update` require a fresh verified catalog, so refresh failure cannot change installed or active packages.

A refreshed signed catalog can mark a release revoked. Archie refuses new activation of that release and warns when matching bytes remain installed or active. It does not automatically delete or replace them; use `update`, `use`, or `remove` explicitly.

Failed downloads, redirects, hashes, signatures, compatibility checks, extraction, or receipt replacement remove temporary files and preserve the previous activation. `recommend`, `open`, and `scan` continue using installed packages during catalog or GitHub outages.

## Storage and rollback

The scanner root is `$XDG_DATA_HOME/archie/scanners`, or `~/.local/share/archie/scanners` when `XDG_DATA_HOME` is unset. Packages are immutable and digest-addressed beneath `packages/<id>/<version>/<sha256>/`; `installed.json` is the authoritative atomically replaced receipt.

After an update, inspect retained versions with `scanner list` and roll back without downloading:

```text
archie scanner use <id> <version> [<sha256>]
```

Removal updates the receipt before best-effort byte cleanup. Removing the active package selects a retained package deterministically when one exists; otherwise that scanner becomes inactive.
