#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
mkdir -p .amp/generated

dotnet_plugin="$repo_root/.amp/runtime/scanners/dotnet-1.3.0"
php_plugin="$repo_root/.amp/runtime/scanners/php-2.0.0"
for plugin in "$dotnet_plugin" "$php_plugin"; do
  [[ -f "$plugin/scanner.json" ]] || {
    echo "Pinned public scanner release is missing at $plugin; rerun .agents/setup." >&2
    exit 1
  }
done

dotnet run --project src/Archie.Cli/Archie.Cli.csproj --no-restore -- \
  scan demo/book-retail \
  --plugin-path "$dotnet_plugin" \
  --plugin-path "$php_plugin" \
  --observations .amp/generated/book-retail.observations.json \
  --overlay demo/book-retail/architecture.overlay.json \
  --graph .amp/generated/book-retail.graph.json \
  --source-context .amp/generated/book-retail.source-context.json \
  --as-of 2026-09-02
