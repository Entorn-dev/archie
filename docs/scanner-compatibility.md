# Scanner compatibility

The JSON Schemas and conformance fixtures in the public [Entorn scanner contracts repository](https://github.com/Entorn-dev/entorn-scanner-contracts) define compatibility. The optional [`Entorn.Scanner.Contracts`](https://www.nuget.org/packages/Entorn.Scanner.Contracts) .NET package follows them and does not independently extend the wire contract.

## Version rules

- Manifest and protocol versions are exact identifiers, currently `scanner-manifest/v1` and `scanner/v1`.
- A worker must emit the same protocol version requested by the Archie version that launches it. An unknown version is incompatible, not a request for best-effort parsing.
- Objects reject unknown fields unless a schema explicitly provides an `extensions` member. Implementations must not depend on undeclared fields being ignored.
- Required fields may not be omitted. Enum and discriminator values are closed to those in the schema.
- Message ordering, count, byte, time, and process-lifecycle limits are part of the runner contract even where JSON Schema cannot express them.

## Change policy

A change is compatible within `scanner/v1` only when every previously valid document retains the same meaning and every supported implementation can safely ignore or consume the change under the existing schemas. Because current objects are closed, adding a field normally requires a new protocol version. Tightening a bound or validation rule that rejects previously valid output also requires a new version unless it only enforces an already documented runtime invariant.

A new protocol version must ship with updated schemas, protocol documentation, valid and invalid fixtures, runner validation, and binding tests. Archie may support multiple versions during a migration, but each worker exchange uses one exact version. Catalog compatibility metadata will prevent installation or activation of unsupported scanner versions.

## Conformance

An implementation is conformant when:

1. its manifest validates against `scanner-manifest.schema.json`;
2. every emitted line validates against `protocol-message.schema.json`;
3. all valid fixtures are accepted and all invalid fixtures are rejected;
4. lifecycle ordering and `observationCount` are correct; and
5. equivalent input produces deterministic observations, diagnostics, and source-ownership claims.

Passing schema validation does not permit a scanner to emit canonical graph or reconciliation decisions. Those remain outside the scanner contract.
