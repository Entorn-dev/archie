# Runnable book-retail fixture

This intentionally small mixed-stack application proves the approved place-order architecture locally. `BookRetail.slnx` contains Checkout, Ordering, InventoryWorker, Notifications, LegacyFulfillment, and Contracts projects; `storefront` is TypeScript and `catalogue` is Go. `laravel-storefront` is a static PHP/Laravel scanner fixture that appears with its locked framework dependency and controller-backed `POST /storefront/orders` route, but is deliberately not installed or run by the demo service.

- Storefront calls the Go availability API and .NET checkout API.
- Checkout calls a local payment stub that discards the synthetic token, then publishes `order.submitted`.
- Ordering persists the order in PostgreSQL and requests inventory; InventoryWorker persists a reservation and publishes `inventory.reserved`.
- Ordering publishes `fulfilment.requested`; the legacy Azure Functions source adapter handles it and publishes `dispatch.requested`; Notifications writes current notification state.
- A Kafka-compatible in-memory broker and loopback-only PostgreSQL server provide deterministic local dependencies.
- `architecture.overlay.json` binds scanner candidates to approved IDs. Supported .NET HTTP/messaging and PHP Composer/Laravel route evidence is scanner-derived; TypeScript/Go calls, PostgreSQL relationships, Kubernetes YAML, and Bicep remain governed manual evidence.

Run all services through the repository's supervised orb declaration:

```bash
amp orb services ensure
scripts/verify-book-retail.sh
```

The fixture requires no cloud account and contains no credential. `infra/kubernetes.yaml` and `infra/legacy.bicep` are authored declaration evidence only: nothing applies, deploys, or live-validates them.

Scan and reconcile it as part of the containing Git repository:

```bash
dotnet run --project src/Archie.Cli -- scan demo/book-retail \
  --plugin-path .amp/runtime/scanners/dotnet-1.3.0 \
  --plugin-path .amp/runtime/scanners/php-2.0.0 \
  --observations .amp/generated/book-retail.observations.json \
  --overlay demo/book-retail/architecture.overlay.json \
  --graph .amp/generated/book-retail.graph.json \
  --source-context .amp/generated/book-retail.source-context.json \
  --as-of 2026-09-02
```

`.agents/setup` downloads these exact public release archives and verifies their pinned SHA-256 digests. The scan itself remains offline.
