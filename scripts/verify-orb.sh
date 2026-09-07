#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

bash -n .agents/setup .agents/resume scripts/*.sh
mkdir -p .amp/generated
"$HOME/.local/bin/mise" exec -- pnpm package:linux-x64 >/dev/null
"$HOME/.local/bin/mise" exec -- bash scripts/verify-linux-package.sh >/dev/null
"$HOME/.local/bin/mise" exec -- scripts/ensure-book-retail-graph.sh >/dev/null
"$HOME/.local/bin/mise" exec -- dotnet src/Archie.Cli/bin/Debug/net10.0/archie.dll validate .amp/generated/book-retail.observations.json >/dev/null
"$HOME/.local/bin/mise" exec -- dotnet src/Archie.Cli/bin/Debug/net10.0/archie.dll validate .amp/generated/book-retail.graph.json >/dev/null
"$HOME/.local/bin/mise" exec -- dotnet src/Archie.Cli/bin/Debug/net10.0/archie.dll validate .amp/generated/book-retail.source-context.json >/dev/null
jq -e '.scanners == [{"id":"archie.dotnet","version":"1.3.0"},{"id":"archie.php","version":"2.0.0"}]' .amp/generated/book-retail.observations.json >/dev/null
jq -e '.nodes[] | select(.id == "deployable:laravel-storefront" and .kind == "deployable" and .name == "Laravel Storefront" and .properties.environment == "current" and .properties.framework == "laravel" and .properties.language == "php")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.evidence[] | select(.scannerId == "archie.php" and .path == "laravel-storefront/composer.json" and .extractionMethod == "composer:locked-laravel-application")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.nodes[] | select(.kind == "component" and .name == "laravel/framework" and .resolution == "resolved" and .properties.dependencyType == "composer")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.edges[] | select(.kind == "exposes" and .from == "deployable:laravel-storefront" and .provenance == ["deterministic"] and .properties.httpMethod == "POST" and .properties.routeTemplate == "/storefront/orders" and .properties.controller == "App\\Http\\Controllers\\CheckoutController" and .properties.action == "store")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.evidence[] | select(.scannerId == "archie.php" and .path == "laravel-storefront/routes/web.php" and .range.startLine == 6 and .extractionMethod == "laravel:literal-controller-route")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.nodes[] | select(.kind == "http-endpoint" and .name == "POST /api/orders")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.evidence[] | select(.path == "Checkout/Program.cs" and .extractionMethod == "roslyn:semantic-minimal-api-route-host-dataflow")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '[.edges[] | select((.kind == "publishes" or .kind == "subscribes") and .provenance == ["deterministic"])] | length == 12' .amp/generated/book-retail.graph.json >/dev/null
jq -e '. as $root | .edges[] | select(.kind == "subscribes" and .from == "deployable:legacy-fulfilment" and .to == "channel:fulfilment-requested") as $edge | $root.evidence[] | select(.id == $edge.evidenceIds[0] and .path == "LegacyFulfillment/Functions.cs" and .extractionMethod == "roslyn:semantic-confluent-kafka-operation")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.edges[] | select(.kind == "calls" and .from == "deployable:checkout-service" and .to == "external:payment-provider" and .provenance == ["deterministic"] and .properties.configurationKey == "Payment:BaseUrl" and .properties.provider == "http" and .properties.host == "payment.local" and .properties.port == 4402)' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.edges[] | select(.kind == "depends-on" and .from == "deployable:ordering-service" and .to == "database:orders" and .provenance == ["manual"] and .properties.provider == "postgresql")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.edges[] | select(.kind == "depends-on" and .from == "deployable:inventory-worker" and .to == "database:inventory" and .provenance == ["manual"] and .properties.provider == "postgresql")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.edges[] | select(.kind == "deploys-to" and .provenance == ["manual"] and .properties.declarationOnly == true)' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.evidence[] | select(.path == "Checkout/Program.cs" and .extractionMethod == "roslyn:semantic-http-client-configured-base-address")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.evidence[] | select(.path == "demo/book-retail/infra/postgres/init.sql" and .extractionMethod == "governed architecture overlay" and .provenance == "manual")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.evidence[] | select(.path == "demo/book-retail/infra/kubernetes.yaml" and .extractionMethod == "governed architecture overlay" and .provenance == "manual")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.evidence[] | select(.path == "demo/book-retail/infra/legacy.bicep" and .extractionMethod == "governed architecture overlay" and .provenance == "manual")' .amp/generated/book-retail.graph.json >/dev/null
scripts/audit-generated-artifacts.sh .amp/generated/book-retail.observations.json .amp/generated/book-retail.graph.json .amp/generated/book-retail.source-context.json
curl -fsS http://127.0.0.1:4317/health | grep -q '"status":"ready"'
curl -fsS http://127.0.0.1:5173/api/v1/workspace | grep -q '"mode":"canonical-artifact"'
served_graph="$(curl -fsS http://127.0.0.1:5173/api/v1/graph)"
jq -e '.schemaVersion == "architecture/v1" and (.nodes[] | select(.kind == "http-endpoint" and .name == "POST /api/orders"))' <<<"$served_graph" >/dev/null
jq -e '.nodes[] | select(.id == "deployable:laravel-storefront" and .name == "Laravel Storefront" and .environment == "current" and .owner == "team:storefront")' <<<"$served_graph" >/dev/null
jq -e --argjson served "$served_graph" '. as $graph | ($served.nodes[] | select(.id == "deployable:laravel-storefront" and .resolution == "resolved" and .environment == "current" and .owner == "team:storefront")) as $servedNode | .nodes[] | select(.id == $servedNode.id) as $node | $graph.evidence[] | select(.id as $evidenceId | $node.evidenceIds | index($evidenceId)) | select(.path == "laravel-storefront/composer.json" and .extractionMethod == "composer:locked-laravel-application" and .provenance == "deterministic" and .scannerId == "archie.php")' .amp/generated/book-retail.graph.json >/dev/null
jq -e '.edges[] | select(.label == "exposes POST /storefront/orders") | .evidence[] | select(.path == "laravel-storefront/routes/web.php" and .startLine == 6 and .extractionMethod == "laravel:literal-controller-route" and .provenance == "scanner")' <<<"$served_graph" >/dev/null
jq -e '.edges[] | select(.label == "exposes POST /api/orders") | .evidence[] | select(.path == "Checkout/Program.cs" and .extractionMethod == "roslyn:semantic-minimal-api-route-host-dataflow" and .provenance == "scanner")' <<<"$served_graph" >/dev/null
jq -e '.edges[] | select(.label == "calls Payment external service" and .properties.configurationKey == "Payment:BaseUrl") | .evidence[] | select(.path == "Checkout/Program.cs" and .extractionMethod == "roslyn:semantic-http-client-configured-base-address" and .provenance == "scanner")' <<<"$served_graph" >/dev/null
jq -e '.edges[] | select(.label == "depends on Orders database" and .properties.provider == "postgresql") | .evidence[] | select(.path == "demo/book-retail/infra/postgres/init.sql" and .extractionMethod == "governed architecture overlay" and .provenance == "manual")' <<<"$served_graph" >/dev/null
jq -e '.edges[] | select(.label == "declared in Kubernetes") | .evidence[] | select(.path == "demo/book-retail/infra/kubernetes.yaml" and .provenance == "manual")' <<<"$served_graph" >/dev/null
journey="$(curl -fsS 'http://127.0.0.1:5173/api/v1/paths?from=deployable%3Astorefront&to=deployable%3Anotification-service&direction=downstream&maxDepth=8&edgeKind=calls%2Cpublishes%2Csubscribes')"
jq -e 'length == 1 and .[0].nodeIds == ["deployable:storefront","deployable:checkout-service","channel:order-submitted","deployable:ordering-service","channel:fulfilment-requested","deployable:legacy-fulfilment","channel:dispatch-requested","deployable:notification-service"]' <<<"$journey" >/dev/null
scripts/verify-book-retail.sh

limit_response="$(mktemp)"
trap 'rm -f "$limit_response"' EXIT
status="$(curl -sS -o "$limit_response" -w '%{http_code}' 'http://127.0.0.1:5173/api/v1/graph?nodeLimit=2&edgeLimit=2')"
[[ "$status" == "422" ]]
grep -q '"code":"QUERY_LIMIT_EXCEEDED"' "$limit_response"
! grep -q '"nodes":\[' "$limit_response"

echo "Real combined scan, served graph, and runnable demo preserve scanner-derived .NET plus PHP/Composer/Laravel route evidence and visibly manual PostgreSQL, Go/TypeScript, Kubernetes, and Bicep provenance."
