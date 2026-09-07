# Security policy

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability. Use GitHub's private vulnerability reporting for `Entorn-dev/archie`. Do not include production credentials, private repository source, or customer artifacts in a report.

## Security boundary

Archie keeps analyzed source local, writes artifacts to user-local state, and serves its viewer only on loopback. It does not restore, build, or execute the analyzed repository. Scanner inputs and outputs are bounded and validated, and failed scans do not replace a prior successful snapshot.

Scanners are executable code. Signature and package verification establish origin and integrity; worker lifecycle containment limits descendant lifetime but is not a complete filesystem or network sandbox. Installing a catalog or local scanner is therefore an explicit trust decision.

Reports about credential leakage, unsafe archive extraction or symlinks, scanner containment escape, artifact/source disclosure, remote catalog trust, loopback host exposure, or MCP data boundaries are in scope. Hosted SaaS and cloud infrastructure are owned and reported separately.
