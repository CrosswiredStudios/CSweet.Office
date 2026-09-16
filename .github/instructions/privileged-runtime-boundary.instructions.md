---
description: "Rules for the privileged runtime boundary: RuntimeHost, Runtime.LocalRpc, and Runtime.Protocol. Covers the authorization gate, the replay and handle ledgers, the HMAC envelope, transport ACLs, and the never-reorder validation order."
applyTo: "src/CSweet.Office.Runtime.LocalRpc/**,src/CSweet.Office.RuntimeHost/**,src/CSweet.Office.Runtime.Protocol/**"
---

# The privileged runtime boundary

This directory set is where unprivileged code crosses into privileged code. Everything here is either a
security control or a parser sitting next to one.

Read [`docs/20-security/05-local-rpc-boundary.md`](../../docs/20-security/05-local-rpc-boundary.md) and
[`docs/20-security/04-workload-authorization.md`](../../docs/20-security/04-workload-authorization.md) before
editing. The relevant invariants are `SEC-INV-07` through `SEC-INV-15`.

## Never change these

| Rule | Invariant |
|---|---|
| `RuntimeHostProviderClient.CreateAsync` throws. Do not add an overload, a fast path, or a reachable bypass. | `SEC-INV-07` |
| Validation order inside `RuntimeHostAuthorizationGate.ValidateAndCommit` stays trust → ids → window → digest → signature → ledger commit. | `SEC-INV-08` |
| Reject when `previousEpoch >= authorization.FencingEpoch`; never prune `accepted-assignments.json`. | `SEC-INV-09` |
| The pipe DACL stays protected and grants clients only `ReadWrite \| Synchronize \| CreateNewInstance`; the Unix socket stays `0660`. | `SEC-INV-12` |
| Frames that fail length, protocol, or authentication get **no response**. Signatures are validated **before** nonces are consumed. | `SEC-INV-13` |
| Responses stay signed and request-id-bound, and the client keeps validating them. | `SEC-INV-14` |
| Handle authorization stays bound to provider id, provider instance id, and workload kind; only `Destroy` removes it. | `SEC-INV-15` |

## Protocol changes are cross-repository

`runtime_host.proto` is compiled with `GrpcServices="None"`, and `RuntimeHostRequestAuthenticator` canonicalizes
by clearing `AuthenticationSignature` and re-serializing with the generated protobuf code. That means:

- Adding map fields, renaming fields, or changing proto options can silently change the canonical byte sequence
  and break interoperability between the two sides.
- The envelope is a frozen wire format. Prefer adding a new oneof arm over changing an existing message.
- Anything in `CSweet.Office.Contracts` is a separate repository with its own release process. See
  [`docs/50-development/03-contracts-dependency.md`](../../docs/50-development/03-contracts-dependency.md).

## Ledgers

Three JSON ledgers live under the authorization state directory. All three are written atomically and read and
written under a lock.

| File | Shape | Rule |
|---|---|---|
| `headquarters-trust.json` | office id, key id, SPKI | Write-once. Only the `Guid.Empty` → real-office transition is permitted. |
| `accepted-assignments.json` | assignment id → highest epoch | Monotonic. Never pruned. |
| `authorized-workload-handles.json` | workload id → handle record | Registered **after** `backend.CreateAsync`; if persisting fails, destroy the workload you just created. |

If you are tempted to add a fourth file, check whether the information belongs in a handle record instead.

## Failure handling

- Return typed, sanitized errors. The dispatcher's convention is a short code plus
  `"Diagnostic request: {requestId}."`, so support can correlate without exposing internals.
- Do not let a helper's raw exception message reach the caller. `Probe` replaces it with a generic diagnostic
  on purpose.
- A failure that was a security decision must never be caught and continued. Fail closed.

## Tests that will catch you

`tests/CSweet.Office.Tests/RuntimeHostAuthenticationTests.cs`,
`RuntimeHostRpcIntegrationTests.cs`, `RuntimeHostProtocolMapperTests.cs`. Several early-return on non-Windows
because they exercise named pipes — do not read a green run on Linux as Windows coverage.
