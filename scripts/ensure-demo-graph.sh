#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
mkdir -p .amp/generated

dotnet run --project src/Archie.Cli/Archie.Cli.csproj --no-restore -- \
  scan . \
  --plugin-path tests/fixtures/scanners/fake-book-retail \
  --observations .amp/generated/book-retail.scanner.json \
  --overlay tests/fixtures/overlays/book-retail.overlay.json \
  --graph .amp/generated/book-retail.graph.json \
  --source-context .amp/generated/book-retail.scanner.source-context.json \
  --as-of 2026-09-01
