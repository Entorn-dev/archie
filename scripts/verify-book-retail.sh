#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="$repo_root/.amp/runtime/book-retail"
pg_bin="$(pg_config --bindir)"
order_id="slice-9-acceptance"

books="$(curl -fsS http://127.0.0.1:4400/api/books)"
jq -e '.[] | select(.id == "book-1984" and .available == 7)' <<<"$books" >/dev/null
rm -f "$runtime/notification.json"
"$pg_bin/psql" -X -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 4405 -U archie -d bookretail -q \
  -c "DELETE FROM inventory_reservations WHERE order_id = '$order_id'; DELETE FROM orders WHERE id = '$order_id';"

response="$(curl -fsS -X POST http://127.0.0.1:4400/api/orders \
  -H 'content-type: application/json' \
  --data '{"id":"slice-9-acceptance","bookId":"book-1984","quantity":1,"paymentToken":"local-demo-token"}')"
jq -e '.id == "slice-9-acceptance" and .status == "submitted"' <<<"$response" >/dev/null

for _ in {1..200}; do
  if [[ -s "$runtime/notification.json" ]] &&
    jq -e '.orderId == "slice-9-acceptance" and .status == "dispatch-notified"' "$runtime/notification.json" >/dev/null 2>&1; then
    break
  fi
  sleep 0.1
done
jq -e '.orderId == "slice-9-acceptance" and .status == "dispatch-notified"' "$runtime/notification.json" >/dev/null

order_status="$("$pg_bin/psql" -X -Aqt -h 127.0.0.1 -p 4405 -U archie -d bookretail -c "SELECT status FROM orders WHERE id = '$order_id'")"
inventory_status="$("$pg_bin/psql" -X -Aqt -h 127.0.0.1 -p 4405 -U archie -d bookretail -c "SELECT status FROM inventory_reservations WHERE order_id = '$order_id'")"
[[ "$order_status" == "fulfilment-requested" ]]
[[ "$inventory_status" == "reserved" ]]

echo "Place order reached PostgreSQL order=$order_status inventory=$inventory_status and current notification=dispatch-notified."
