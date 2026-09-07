# Contributing to Archie

Contributions are welcome through GitHub pull requests.

## Developer Certificate of Origin

By contributing, you certify the [Developer Certificate of Origin 1.1](https://developercertificate.org/). Add a sign-off to every commit with `git commit -s`; the sign-off records that you have the right to submit the contribution under the repository's [Apache License 2.0](LICENSE).

## Local setup

Run `.agents/setup` to install the pinned `mise` toolchains, locked dependencies, Playwright Chromium, and the hash-pinned public scanner releases used by the demo. The script is idempotent. Never commit `.amp/runtime`, `.amp/generated`, `.amp/releases`, `node_modules`, `bin`, `obj`, or `web/dist`.

## Required checks

Before requesting review, run:

```bash
dotnet build Archie.slnx --no-restore
dotnet test Archie.slnx --no-build --no-restore
pnpm typecheck
pnpm test
pnpm build
pnpm package:linux-x64
scripts/verify-linux-package.sh
scripts/audit-generated-artifacts.sh <generated-artifact>...
```

Changes to the viewer also require Playwright coverage and inspection of rendered affected states. Changes to scanner execution, reconciliation, packaging, MCP, or shared contracts require the corresponding focused .NET tests and end-to-end package acceptance.

## Ownership boundaries

- Archie owns contracts, scanner supervision, reconciliation, CLI/MCP, the loopback API, and the minimal local viewer.
- Language-specific scanner implementations remain in their independent repositories. Change their protocol through the versioned schemas and conformance fixtures rather than importing scanner implementation here.
- Hosted SaaS, Git-provider integration, cloud execution, tenant state, and operations do not belong in this repository.

Pull requests should be narrowly scoped, explain the user-visible or contract outcome, include tests that fail before the change, and call out security or compatibility effects. Generated fixtures must remain deterministic and contain no source snippets, credentials, private remotes, or local absolute paths.
