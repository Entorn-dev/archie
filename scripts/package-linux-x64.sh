#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${1:-dev}"
release_root="${ARCHIE_RELEASE_ROOT:-$repo_root/.amp/releases}"
stage="$release_root/archie-linux-x64-$version"
archive="$release_root/archie-linux-x64-$version.tar.gz"
checksum="$archive.sha256"

[[ "$version" =~ ^[A-Za-z0-9._-]+$ ]] || { echo "Version may contain only letters, digits, dots, underscores, and hyphens." >&2; exit 2; }

rm -rf "$stage" "$archive" "$checksum"
mkdir -p "$stage/host" "$stage/web" "$stage/licenses"

pnpm --dir "$repo_root/web" build
restore_args=(--locked-mode)
if [[ "${ARCHIE_OFFLINE:-}" == 1 ]]; then
  restore_args+=(--ignore-failed-sources -p:NuGetAudit=false)
fi
dotnet restore "$repo_root/src/Archie.Cli/Archie.Cli.csproj" "${restore_args[@]}"
dotnet restore "$repo_root/src/Archie.Api/Archie.Api.csproj" "${restore_args[@]}"
dotnet publish "$repo_root/src/Archie.Cli/Archie.Cli.csproj" \
  --configuration Release --self-contained false --no-restore -p:UseAppHost=false --output "$stage"
dotnet publish "$repo_root/src/Archie.Api/Archie.Api.csproj" \
  --configuration Release --self-contained false --no-restore -p:UseAppHost=false --output "$stage/host"
cp -a "$repo_root/web/dist/." "$stage/web/"
cp "$repo_root/docs/local-linux-release.md" "$stage/README.md"
cp "$repo_root/docs/scanners.md" "$stage/scanners.md"
cp "$repo_root/LICENSE" "$stage/LICENSE"
cp "$repo_root/THIRD-PARTY-NOTICES.md" "$stage/THIRD-PARTY-NOTICES.md"
cp "$repo_root/docs/licenses/ModelContextProtocol.Core-2.2.0.LICENSE.txt" "$stage/licenses/"
node "$repo_root/scripts/collect-third-party-notices.mjs" "$stage"
cat > "$stage/archie" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
install_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export ARCHIE_SCANNER_EXECUTABLE_PATH="$PATH"
exec dotnet "$install_root/archie.dll" "$@"
EOF
chmod +x "$stage/archie"

tar --sort=name --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner \
  -C "$release_root" -czf "$archive" "$(basename "$stage")"
(cd "$release_root" && sha256sum "$(basename "$archive")" > "$(basename "$checksum")")

printf 'Packaged Archie %s\n' "$archive"
printf 'Checksum written to %s\n' "$checksum"
