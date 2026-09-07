#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="$repo_root/.amp/runtime/book-retail"
mkdir -p "$runtime/bin" "$runtime/logs"
cd "$repo_root"

pg_bin="$(pg_config --bindir)"
pg_data="$runtime/postgres"
if [[ ! -s "$pg_data/PG_VERSION" ]]; then
  "$pg_bin/initdb" -D "$pg_data" --auth=trust --username=archie >/dev/null
fi

pnpm --dir demo/book-retail/storefront build >/dev/null
go -C demo/book-retail/catalogue build -o "$runtime/bin/catalogue" .
go -C demo/book-retail/kafka-broker build -o "$runtime/bin/kafka-broker" .
dotnet build demo/book-retail/BookRetail.slnx --no-restore >/dev/null

pids=()
cleanup() {
  trap - EXIT INT TERM
  if ((${#pids[@]})); then
    kill "${pids[@]}" 2>/dev/null || true
    wait "${pids[@]}" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

"$pg_bin/postgres" -D "$pg_data" -h 127.0.0.1 -p 4405 -k "$runtime" >"$runtime/logs/postgres.log" 2>&1 &
pids+=("$!")
for _ in {1..100}; do
  "$pg_bin/pg_isready" -h 127.0.0.1 -p 4405 -U archie -d postgres >/dev/null 2>&1 && break
  sleep 0.1
done
"$pg_bin/pg_isready" -h 127.0.0.1 -p 4405 -U archie -d postgres >/dev/null
if [[ "$("$pg_bin/psql" -X -Aqt -h 127.0.0.1 -p 4405 -U archie -d postgres -c "SELECT 1 FROM pg_database WHERE datname = 'bookretail'")" != "1" ]]; then
  "$pg_bin/createdb" -h 127.0.0.1 -p 4405 -U archie bookretail
fi
"$pg_bin/psql" -X -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 4405 -U archie -d bookretail \
  -f demo/book-retail/infra/postgres/init.sql >/dev/null

"$runtime/bin/kafka-broker" >"$runtime/logs/kafka-broker.log" 2>&1 &
pids+=("$!")
for _ in {1..100}; do
  (exec 3<>/dev/tcp/127.0.0.1/4403) 2>/dev/null && { exec 3>&-; break; }
  sleep 0.1
done
(exec 3<>/dev/tcp/127.0.0.1/4403) 2>/dev/null
exec 3>&-

export BOOK_RETAIL_KAFKA=127.0.0.1:4403
export BOOK_RETAIL_PSQL="$pg_bin/psql"
export BOOK_RETAIL_NOTIFICATION_FILE="$runtime/notification.json"

node demo/book-retail/payment-stub/server.mjs >"$runtime/logs/payment-stub.log" 2>&1 & pids+=("$!")
"$runtime/bin/catalogue" >"$runtime/logs/catalogue.log" 2>&1 & pids+=("$!")
dotnet demo/book-retail/Ordering/bin/Debug/net10.0/Ordering.dll >"$runtime/logs/ordering.log" 2>&1 & pids+=("$!")
dotnet demo/book-retail/InventoryWorker/bin/Debug/net10.0/InventoryWorker.dll >"$runtime/logs/inventory-worker.log" 2>&1 & pids+=("$!")
dotnet demo/book-retail/LegacyFulfillment/bin/Debug/net10.0/LegacyFulfillment.dll >"$runtime/logs/legacy-fulfilment.log" 2>&1 & pids+=("$!")
dotnet demo/book-retail/Notifications/bin/Debug/net10.0/Notifications.dll >"$runtime/logs/notifications.log" 2>&1 & pids+=("$!")
ASPNETCORE_URLS=http://127.0.0.1:4404 Payment__BaseUrl=http://127.0.0.1:4402 Kafka__BootstrapServers=127.0.0.1:4403 \
  dotnet demo/book-retail/Checkout/bin/Debug/net10.0/Checkout.dll >"$runtime/logs/checkout.log" 2>&1 & pids+=("$!")
PORT=4400 CATALOGUE_URL=http://127.0.0.1:4401 CHECKOUT_URL=http://127.0.0.1:4404 \
  node demo/book-retail/storefront/dist/server.js >"$runtime/logs/storefront.log" 2>&1 & pids+=("$!")

wait -n "${pids[@]}"
