# CSweet.Office.Runtime.LocalRpc

The complete Node-to-RuntimeHost boundary in one library: the client the unprivileged Node uses, the server and dispatcher the privileged service runs, the transport, the contracts-to-protocol mapper, and the authorization gate that is the privileged authorization boundary — the only code that turns a signed Headquarters assignment into a workload. Treat every change here as a change to the trust boundary.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/runtime-localrpc.md`](../../docs/80-reference/projects/runtime-localrpc.md).

**Output:** library
**References:** `CSweet.Office.Runtime.Abstractions`, `CSweet.Office.Runtime.Protocol`

## What it owns

- `RuntimeHostAuthorizationGate` — the privileged authorization boundary: the write-once Headquarters trust pin, the fencing-epoch replay ledger, and the authorized-handle ledger.
- `RuntimeHostRpcServer` — the listener: a named pipe with an exact security descriptor on Windows, a mode `0660` Unix socket elsewhere; exactly one request per connection, and a rejected frame is dropped without writing a byte.
- `RuntimeHostRequestDispatcher` — routes pin, probe, create, start, inspect, stop, destroy, and log requests, and refuses a provider that cannot hand back a guest-channel stream.
- `RuntimeHostProviderClient` — the Node-side `IRuntimeHostClient`, including `CreateAuthorizedAsync`, probe retry, lifecycle calls, and the guest-channel relay surface.
- `RuntimeHostProtocolMapper` — converts between the contracts workload types and the wire messages with every bound re-checked in both directions.
- `LocalRuntimeHostTransport` and `RuntimeHostEndpointOptions` — connect over `csweet-office-runtime-v1` (named pipe or Unix socket) under the `CSweet:Office:RuntimeHost` section.

## Notes for contributors

- Never reorder the validation sequence in `ValidateAndCommit`, and never commit the replay ledger before signature, digest, and time validation succeed (SEC-INV-08); a replay is rejected on fencing epoch (SEC-INV-09).
- `CreateAsync` throws unconditionally by design (SEC-INV-07). Do not add an overload, a fast path, or a reachable bypass.
- The Windows pipe ACL grants allowed clients exactly `ReadWrite | Synchronize | CreateNewInstance` (SEC-INV-12); do not widen it.
- Response validation on the client is deliberately symmetric (SEC-INV-14), and handles are re-checked against the ledger on every call (SEC-INV-15).
- Accepted assignments are never pruned; see [docs/20-security/12-known-limits-and-tradeoffs.md](../../docs/20-security/12-known-limits-and-tradeoffs.md).

## Tests

`RuntimeHostRpcIntegrationTests` (the largest class; real named pipe or Unix socket), `RuntimeHostAuthenticationTests`, `RuntimeHostProtocolMapperTests`, and the Office-level single-use assertions in `OfficeTests`.

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
