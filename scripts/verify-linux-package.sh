#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
install_root="${1:-$repo_root/.amp/releases/archie-linux-x64-dev}"
[[ -d "$install_root" ]] || { echo "Packaged CLI directory not found at $install_root; run pnpm package:linux-x64 first." >&2; exit 2; }
install_root="$(cd "$install_root" && pwd)"
archie="$install_root/archie"
[[ -x "$archie" ]] || { echo "Packaged CLI not found at $archie; run pnpm package:linux-x64 first." >&2; exit 2; }
test -f "$install_root/LICENSE"
grep -q 'Apache License' "$install_root/LICENSE"
test -f "$install_root/THIRD-PARTY-NOTICES.md"
grep -q 'ModelContextProtocol.Core' "$install_root/THIRD-PARTY-NOTICES.md"
grep -q 'elkjs' "$install_root/THIRD-PARTY-NOTICES.md"
test -f "$install_root/licenses/ModelContextProtocol.Core-2.2.0.LICENSE.txt"
grep -q 'Apache License' "$install_root/licenses/ModelContextProtocol.Core-2.2.0.LICENSE.txt"
grep -q 'MIT License' "$install_root/licenses/ModelContextProtocol.Core-2.2.0.LICENSE.txt"
test -f "$install_root/licenses/npm/README.txt"
grep -q '^elkjs 0.11.0$' "$install_root/licenses/npm/README.txt"
test -f "$install_root/licenses/npm/elkjs-0.11.0-LICENSE.md"
grep -q 'Eclipse Public License - v 2.0' "$install_root/licenses/npm/elkjs-0.11.0-LICENSE.md"
test -f "$install_root/licenses/nuget/README.txt"
grep -q '^ModelContextProtocol.Core 2.2.0$' "$install_root/licenses/nuget/README.txt"
test ! -e "$install_root/scanners"
if find "$install_root" -type f \( \
    -name scanner.json -o \
    -name 'Archie.Scanner.DotNet*' -o \
    -name 'Archie.Scanner.Php*' -o \
    -name 'TreeSitter*' \
  \) -print -quit | grep -q .; then
  echo "Packaged Archie contains a scanner payload or scanner-only dependency." >&2
  exit 1
fi

temporary="$(mktemp -d)"
pid=""
cleanup() {
  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then kill -INT "$pid" 2>/dev/null || true; fi
  rm -rf "$temporary"
}
trap cleanup EXIT

repository="$temporary/target/sample-repository"
working="$temporary/unrelated-working-directory"
state="$temporary/state"
data="$temporary/data"
fixture_archive="$temporary/fake-scanner.tar.gz"
launcher_bin="$temporary/launcher-bin"
poisoned_bin="$temporary/poisoned-bin"
dotnet_host="${DOTNET_ROOT:-}/dotnet"
[[ -x "$dotnet_host" ]] || dotnet_host="$(type -P dotnet)"
node_host="$(node -p 'process.execPath')"
mkdir -p "$repository/tests/fixtures/observations" "$working" "$state" "$data" "$launcher_bin" "$poisoned_bin"
cat > "$launcher_bin/dotnet" <<EOF
#!/usr/bin/env bash
PATH="$poisoned_bin:/usr/bin:/bin" exec "$dotnet_host" "\$@"
EOF
cat > "$poisoned_bin/node" <<'EOF'
#!/usr/bin/env bash
echo "poisoned Node shim must not execute" >&2
exit 99
EOF
chmod +x "$launcher_bin/dotnet" "$poisoned_bin/node"
cp "$repo_root/tests/fixtures/observations/book-retail.authored.json" \
  "$repository/tests/fixtures/observations/book-retail.authored.json"
printf 'Package acceptance fixture.\n' > "$repository/PROJECT_BRIEF.md"
cat > "$repository/Sample.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
EOF
cat > "$repository/Program.cs" <<'EOF'
Console.WriteLine("Package acceptance fixture");
EOF
cat > "$repository/composer.json" <<'EOF'
{
  "name": "archie/package-acceptance",
  "require": { "php": "^8.3", "laravel/framework": "^12.0" },
  "autoload": { "psr-4": { "App\\": "app/" } }
}
EOF
cat > "$repository/composer.lock" <<'EOF'
{ "packages": [{ "name": "laravel/framework", "version": "v12.0.0" }], "packages-dev": [] }
EOF
mkdir -p "$repository/bootstrap" "$repository/routes" "$repository/app/Http/Controllers"
printf '#!/usr/bin/env php\n<?php\n' > "$repository/artisan"
printf '<?php\nreturn null;\n' > "$repository/bootstrap/app.php"
cat > "$repository/routes/web.php" <<'EOF'
<?php
use App\Http\Controllers\CheckoutController;
use Illuminate\Support\Facades\Route;
Route::post('/acceptance/orders', [CheckoutController::class, 'store']);
EOF
cat > "$repository/app/Http/Controllers/CheckoutController.php" <<'EOF'
<?php
namespace App\Http\Controllers;
final class CheckoutController { public function store(): void {} }
EOF
git -C "$repository" init --quiet
git -C "$repository" add .
git -C "$repository" -c user.name='Archie Acceptance' -c user.email='archie@example.invalid' commit --quiet -m fixture
before="$(git -C "$repository" status --porcelain=v1)"

offline=(env
  XDG_DATA_HOME="$data"
  HTTP_PROXY=http://127.0.0.1:1
  HTTPS_PROXY=http://127.0.0.1:1
  NO_PROXY=127.0.0.1,localhost
  PATH="$launcher_bin:$(dirname "$node_host"):$PATH")

"${offline[@]}" "$archie" scanner recommend "$repository" >"$temporary/recommend-before.log"
grep -q 'missing .NET: archie.dotnet; install with archie scanner add archie.dotnet' "$temporary/recommend-before.log"
grep -q 'missing PHP / Laravel: archie.php; install with archie scanner add archie.php' "$temporary/recommend-before.log"

no_scanner_observations="$temporary/no-scanner-observations.json"
no_scanner_graph="$temporary/no-scanner-graph.json"
no_scanner_context="$temporary/no-scanner-source-context.json"
printf 'prior observations' >"$no_scanner_observations"
printf 'prior graph' >"$no_scanner_graph"
printf 'prior source context' >"$no_scanner_context"
set +e
"${offline[@]}" "$archie" scan "$repository" \
  --observations "$no_scanner_observations" \
  --graph "$no_scanner_graph" \
  --source-context "$no_scanner_context" >"$temporary/no-scanner.log" 2>&1
no_scanner_exit=$?
set -e
[[ "$no_scanner_exit" == 1 ]]
grep -q 'SCANNER_COVERAGE_MISSING' "$temporary/no-scanner.log"
grep -q 'SCANNER_NOT_FOUND' "$temporary/no-scanner.log"
[[ "$(cat "$no_scanner_observations")" == 'prior observations' ]]
[[ "$(cat "$no_scanner_graph")" == 'prior graph' ]]
[[ "$(cat "$no_scanner_context")" == 'prior source context' ]]

tar --sort=name --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner \
  -C "$repo_root/tests/fixtures/scanner-packages/fake" \
  -czf "$fixture_archive" LICENSE PACKAGE.json scanner.json worker.mjs
"${offline[@]}" "$archie" scanner add --local "$fixture_archive" >"$temporary/install.log" 2>&1
grep -q 'unsigned and unverified executable code' "$temporary/install.log"
grep -q 'origin=local-unsigned' "$temporary/install.log"
"${offline[@]}" "$archie" scanner list | grep -q '^\* archie.fake-book-retail 1.0.0 origin=local-unsigned'

(
  cd "$working"
  "${offline[@]}" XDG_STATE_HOME="$state" \
    "$archie" open ../target/sample-repository --no-browser --as-of 2026-09-03
) >"$temporary/open.log" 2>&1 &
pid=$!

url=""
for _ in {1..600}; do
  if ! kill -0 "$pid" 2>/dev/null; then cat "$temporary/open.log" >&2; wait "$pid"; fi
  url="$(sed -n 's/^Archie is ready at \(http:\/\/127\.0\.0\.1:[0-9][0-9]*\/app\)$/\1/p' "$temporary/open.log" | tail -1)"
  [[ -n "$url" ]] && break
  sleep 0.1
done
[[ -n "$url" ]] || { cat "$temporary/open.log" >&2; echo "Packaged Archie did not become ready." >&2; exit 1; }

origin="${url%/app}"
curl -fsS "$origin/health" | grep -q '"status":"ready"'
curl -fsS "$url" | grep -q '<div id="root"></div>'
curl -fsS "$origin/api/v1/workspace" | grep -q '"schemaVersion":"architecture/v1"'
curl -fsS "$origin/api/v1/views" | grep -q '^\[\]$'
port="${origin##*:}"
ss -ltn "sport = :$port" | grep -q '127.0.0.1'
! ss -ltn "sport = :$port" | grep -Eq '0\.0\.0\.0|\[::\]'

(
  cd "$repo_root/web"
  ARCHIE_ACCEPTANCE_URL="$url" node --input-type=module <<'EOF'
import { chromium } from "@playwright/test"
const browser = await chromium.launch({ headless: true })
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
  const errors = []
  const externalRequests = []
  page.on("pageerror", (error) => errors.push(error.message))
  page.on("request", (request) => {
    const target = new URL(request.url())
    if (target.origin !== new URL(process.env.ARCHIE_ACCEPTANCE_URL).origin)
      externalRequests.push(request.url())
  })
  await page.goto(process.env.ARCHIE_ACCEPTANCE_URL, { waitUntil: "networkidle" })
  await page.waitForTimeout(2000)
  const body = await page.locator("body").innerText()
  const navigation = await page.getByRole("navigation", { name: "Primary navigation" }).count()
  const map = await page.getByRole("region", { name: "Dependency map" }).count()
  const graph = await page.getByLabel("Dependency architecture graph").count()
  if (!navigation || !map || !graph || body.includes("Architecture could not be loaded") || errors.length || externalRequests.length)
    throw new Error(`${body}\n${errors.join("\n")}\n${externalRequests.join("\n")}`)
  await page.getByRole("button", { name: "Inferred runtime map" }).click()
  await page.getByRole("region", { name: "Inferred runtime map" }).waitFor()
  await page.getByLabel("Inferred runtime architecture graph").waitFor()
  const coverage = await page.getByRole("status", { name: "Scanner coverage is incomplete" }).innerText()
  if (!coverage.includes("archie scanner add archie.dotnet") || !coverage.includes("archie scanner add archie.php"))
    throw new Error(coverage)
} finally {
  await browser.close()
}
EOF
)

[[ "$(git -C "$repository" status --porcelain=v1)" == "$before" ]]
[[ ! -e "$repository/.archie" ]]
test -f "$state/archie/repositories/"*/observations.json
test -f "$state/archie/repositories/"*/graph.json
test -f "$state/archie/repositories/"*/source-context.json
jq -e '.scanners == [{"id":"archie.fake-book-retail","version":"1.0.0"}]' "$state/archie/repositories/"*/observations.json >/dev/null
jq -e '[.diagnostics[] | select(.code == "SCANNER_COVERAGE_MISSING")] | length == 2' "$state/archie/repositories/"*/graph.json >/dev/null
jq -e '.evidence[] | select(.scannerId == "archie.fake-book-retail")' "$state/archie/repositories/"*/graph.json >/dev/null
jq -e '.schemaVersion == "source-context/v1" and (.ownership[] | select(.path == "Program.cs" and .ownerCandidateKey == "deployable:checkout-service"))' "$state/archie/repositories/"*/source-context.json >/dev/null

ARCHIE_CLI="$archie" ARCHIE_REPOSITORY="$repository" ARCHIE_STATE="$state" python3 <<'PY'
import hashlib
import json
import os
import pathlib
import subprocess

state = next((pathlib.Path(os.environ["ARCHIE_STATE"]) / "archie" / "repositories").iterdir())
artifacts = [state / name for name in ("observations.json", "graph.json", "source-context.json")]
before = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in artifacts}
graph = json.loads((state / "graph.json").read_text())
edge = next(item for item in graph["edges"] if item["kind"] != "contains" and item["evidenceIds"])
flow_from, flow_to = (edge["to"], edge["from"]) if edge["kind"] == "subscribes" else (edge["from"], edge["to"])
process = subprocess.Popen(
    [os.environ["ARCHIE_CLI"], "mcp", os.environ["ARCHIE_REPOSITORY"]],
    env={**os.environ, "XDG_STATE_HOME": os.environ["ARCHIE_STATE"]},
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

def request(identifier, method, params):
    process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": identifier, "method": method, "params": params}, separators=(",", ":")) + "\n")
    process.stdin.flush()
    while True:
        response = json.loads(process.stdout.readline())
        if response.get("id") == identifier:
            return response["result"]

request(1, "initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "archie-package-acceptance", "version": "1.0"}})
process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}\n')
process.stdin.flush()
tools = request(2, "tools/list", {})["tools"]
assert [tool["name"] for tool in tools] == [
    "archie_get_context", "archie_get_dependencies", "archie_trace_flow", "archie_assess_change_impact"]
assert all(tool["annotations"]["readOnlyHint"] is True and tool["annotations"]["destructiveHint"] is False for tool in tools)

expected_presentation = {
    "archie_get_context": (6, 25),
    "archie_get_dependencies": (12, 50),
    "archie_trace_flow": (3, 10),
    "archie_assess_change_impact": (8, 25),
}
for tool in tools:
    properties = tool["inputSchema"]["properties"]
    assert properties["detail"] == {"enum": ["summary", "evidence", "full"], "default": "summary"}
    default_items, maximum_items = expected_presentation[tool["name"]]
    assert properties["maxItems"]["default"] == default_items
    assert properties["maxItems"]["minimum"] == 1
    assert properties["maxItems"]["maximum"] == maximum_items

def call(identifier, name, arguments):
    return request(identifier, "tools/call", {"name": name, "arguments": arguments})

def text(result):
    assert result.get("isError") is False
    assert len(result["content"]) == 1 and result["content"][0]["type"] == "text"
    return result["content"][0]["text"]

def compact(result, required):
    value = text(result)
    assert "structuredContent" not in result
    assert all(expected in value for expected in required), value
    return value

def visible_bytes(result):
    size = sum(len(item["text"].encode()) for item in result["content"] if item["type"] == "text")
    if "structuredContent" in result:
        size += len(json.dumps(result["structuredContent"], ensure_ascii=False, separators=(",", ":")).encode())
    return size

requests = {
    "context": ("archie_get_context", {"path": "Program.cs"}),
    "dependencies": ("archie_get_dependencies", {"nodeId": flow_from, "direction": "both", "depth": 1}),
    "flow": ("archie_trace_flow", {"from": flow_from, "to": flow_to, "maxDepth": 1}),
    "impact": ("archie_assess_change_impact", {
        "changes": [{"path": "Program.cs"}, {"path": "unknown.txt"}], "downstreamDepth": 2}),
}
summary_required = {
    "context": ["# Context for `Program.cs`", "## Ownership (", "detail: evidence"],
    "dependencies": ["# Dependencies for ", "## Relationships (", flow_from, flow_to, "detail: evidence"],
    "flow": ["# Flow trace (1/1 paths)", flow_from, flow_to, "detail: evidence"],
    "impact": ["# Potential static change impact", "Internal code reachability remains unknown", "## Owner-level possibilities (", "## Unmatched files (1/1)", "`unknown.txt`", "detail: evidence"],
}

summaries = {}
evidence = {}
full = {}
identifier = 3
for key, (name, arguments) in requests.items():
    summaries[key] = call(identifier, name, arguments)
    identifier += 1
    summary_text = compact(summaries[key], summary_required[key])
    assert len(summary_text.encode()) <= 16 * 1024

    evidence[key] = call(identifier, name, {**arguments, "detail": "evidence"})
    identifier += 1
    evidence_text = compact(evidence[key], ["Expand: `detail: full`."])
    assert "detail: evidence" not in evidence_text
    assert len(evidence_text.encode()) <= 64 * 1024
    assert len(evidence_text) >= len(summary_text)

    full[key] = call(identifier, name, {**arguments, "detail": "full"})
    identifier += 1
    text(full[key])
    assert "structuredContent" in full[key]

assert "claim " in text(evidence["context"]) and "rule `" in text(evidence["context"])
assert "Evidence `" in text(evidence["dependencies"])
assert "Evidence `" in text(evidence["flow"])
assert "Ownership `Program.cs`" in text(evidence["impact"])

assert any(item["path"] == "Program.cs" for item in full["context"]["structuredContent"]["ownership"])
assert any(item["id"] == edge["id"] for item in full["dependencies"]["structuredContent"]["edges"])
assert any(any(item["id"] == edge["id"] for item in path["edges"]) for path in full["flow"]["structuredContent"]["paths"])
assert full["impact"]["structuredContent"]["assessment"] == "potential static impact"
assert full["impact"]["structuredContent"]["unmatched"] == ["unknown.txt"]
assert "Potential static impact only" in text(full["impact"])

summary_sizes = {key: visible_bytes(value) for key, value in summaries.items()}
full_text_sizes = {key: sum(len(item["text"].encode()) for item in value["content"] if item["type"] == "text")
                   for key, value in full.items()}
full_sizes = {key: visible_bytes(value) for key, value in full.items()}
assert all(full_text_sizes[key] <= full_sizes[key] and summary_sizes[key] <= full_sizes[key] for key in requests), (summary_sizes, full_text_sizes, full_sizes)
summary_total = sum(summary_sizes.values())
full_text_total = sum(full_text_sizes.values())
full_total = sum(full_sizes.values())
text_reduction = 1 - summary_total / full_text_total
reduction = 1 - summary_total / full_total
assert reduction >= 0.75, (summary_sizes, full_sizes, reduction)
print("Packaged MCP model-visible bytes:", json.dumps({
    "summary": summary_sizes, "fullText": full_text_sizes, "fullConservative": full_sizes,
    "summaryTotal": summary_total, "fullTextTotal": full_text_total, "fullConservativeTotal": full_total,
    "textReductionPercent": round(text_reduction * 100, 1),
    "conservativeReductionPercent": round(reduction * 100, 1)}, sort_keys=True))
process.stdin.close()
process.wait(timeout=10)
assert process.returncode == 0
assert process.stderr.read() == ""
after = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in artifacts}
assert before == after
PY

kill -INT "$pid"
for _ in {1..100}; do kill -0 "$pid" 2>/dev/null || break; sleep 0.1; done
if kill -0 "$pid" 2>/dev/null; then echo "Packaged Archie did not stop after SIGINT." >&2; exit 1; fi
set +e
wait "$pid"
exit_code=$?
set -e
pid=""
[[ "$exit_code" == 130 ]]

echo "Packaged Linux acceptance passed: scanner-free contents, offline no-scanner preservation, explicit local fixture installation, mixed-stack partial coverage, arbitrary CWD, external state/source context, static UI/API, read-only stdio MCP, loopback-only host, and cancellation."
