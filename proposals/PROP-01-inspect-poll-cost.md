# PROP-01 — Reduce the cost of the workload inspect loop

**Status:** proposal — not implemented. **Area:** speed. **Effort:** medium.

**Tooling cost:** host-only stages need a payload rebuild. The optional helper stage additionally needs the
certification smoke path re-run, and a change to the helper's process model needs an explicit reading of
`SEC-INV-20` before any code is written.

**Invariant interactions:** `SEC-INV-08` (validation order), `SEC-INV-09` (the replay ledger is never pruned),
`SEC-INV-13` and `SEC-INV-14` (authentication ordering and signed responses), `SEC-INV-20` (helper surface).
None of the recommended stages requires an invariant change; several constrain what a fix may do.

## Problem

While a workload runs, the Node polls the RuntimeHost for its state every two seconds, and every poll pays the
full cost of the local RPC boundary plus a helper process launch:

- `OfficeWorker.ExecuteAssignmentAsync` calls `provider.InspectAsync(handle, …)` and then
  `Task.Delay(TimeSpan.FromSeconds(2))`, for the whole lifetime of the assignment, per workload.
- Each call opens a **new** connection: `LocalRuntimeHostTransport.ConnectAsync` creates a named pipe or Unix
  socket per request, and `RuntimeHostRpcServer.HandleOwnedStreamAsync` reads exactly one request per
  connection before the stream is disposed.
- The frame is HMAC-authenticated in both directions. `RuntimeHostRequestAuthenticator.ComputeSignature` calls
  `RuntimeHostAuthenticationOptions.ResolveSharedKeyBase64`, which **re-reads the key file on every call** —
  deliberate, and pinned by `RuntimeHostAuthenticationTests.Authenticator_LoadsKeyCreatedAfterControlPlaneStartup`
  (see [`docs/20-security/05-local-rpc-boundary.md`](../docs/20-security/05-local-rpc-boundary.md), "re-reads on
  every call, so a key created or rotated after startup is picked up without a restart").
- Inside the privileged process, `RuntimeHostRequestDispatcher.InspectAsync` authorises the handle through
  `RuntimeHostAuthorizationGate.IsHandleAuthorized`, which re-reads and re-parses
  `authorized-workload-handles.json` from disk on every call (`ReadHandles()`).
- The backend then launches a new helper process: `ExternalPlatformIsolationBackend.InvokeAsync` re-hashes the
  helper executable (`VerifyFileDigestAsync`), creates a `ProcessStartInfo`, and starts it.
- On Windows the helper starts `powershell.exe -EncodedCommand` for
  `Import-Module Hyper-V; $vm = Get-VM -Name …` (`PowerShellHyperV.GetStateAsync`). A PowerShell 5.1 cold
  start plus a Hyper-V module import is typically the single most expensive step in the poll.

For one running workload the bill is: one pipe instance, four HMAC operations with four key-file reads, one JSON
ledger read, one helper process launch with a full file hash, and one PowerShell cold start — every two
seconds.

## User story

**Scenario.** Dana runs a dedicated Hyper-V Office with eight concurrent runtime workloads. Each workload is
small and mostly idle between model calls, but the host shows the helper executable and `powershell.exe`
cycling continuously, and CPU never drops below a noticeable baseline even when every workload is waiting.

> As an Office operator, I want the per-workload liveness check to cost milliseconds rather than seconds of
> process and PowerShell startup, so that the host's CPU, process table, and disk are spent on customer work
> instead of on monitoring overhead.

## Recommended fix

Stage the work so each stage is independently valuable and independently reviewable.

### Stage 1 — Keep the ledgers in memory (host only)

Make the authorization gate's working copy of `authorized-workload-handles.json` (and optionally
`accepted-assignments.json`) authoritative in memory: load once at startup, mutate under the existing lock,
and persist with the existing `WriteAtomic` on every change. The RuntimeHost is the only writer of these files,
so the file remains the durable record while the per-operation disk read disappears. The
[privileged-boundary rules](../.github/instructions/privileged-runtime-boundary.instructions.md) require the
atomic write and the lock to stay — a cache must not replace them.

### Stage 2 — Make the signature key read cheap without changing its semantics (host only)

`ResolveSharedKeyBase64` re-reads the file on every signature for a good reason. A short-lived cache keyed on
(length, last-write time, file identity) would keep the pinned `Authenticator_LoadsKeyCreatedAfterControlPlaneStartup`
behaviour (a key created after startup is still picked up) while removing four small file reads per poll.
This stage is optional and low value on its own; fold it into stage 1 or leave it alone.

### Stage 3 — Measure, then reduce the poll cadence (host only)

`RuntimeHostRpcServer` already logs `"RuntimeHost completed {Operation} request {RuntimeHostRequestId} in
{ElapsedMilliseconds} ms."` Use those logs to establish the real cost before changing cadence. If the numbers
justify it, hold the two-second interval until the workload reports `Running`, then back off to a slower
steady-state interval and inspect immediately when the guest tunnel ends. Completion detection must remain
correct: the Node currently learns the terminal state and exit code through `InspectAsync`, so any back-off
must be paired with a completion signal that is at least as timely.

### Stage 4 — A cheaper state read inside the helper (helper change; review required)

The PowerShell cold start can be avoided by reading Hyper-V VM state through the platform APIs directly
(for example `Msvm_ComputerSystem` via WMI) instead of spawning `powershell.exe`. This keeps the eight typed
operations and their message shapes unchanged, so it is not a protocol change — but it is helper behaviour, so
the payload must be rebuilt, the certification smoke path re-run, and the change reviewed against
[the isolation-provider rules](../.github/instructions/isolation-providers.instructions.md). Any long-lived
helper process instead of one process per invocation needs an explicit reading of `SEC-INV-20`'s
"digest re-verified per invocation" wording before it is attempted.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostAuthorizationGate.cs` | In-memory write-through working copy of the handle ledger (and optionally the replay ledger) | Keep the lock, the atomic write, and the never-prune rule (`SEC-INV-09`) |
| `src/CSweet.Office.Runtime.Protocol/RuntimeHostAuthentication.cs` | Optional: change-detecting key cache | Must keep `Authenticator_LoadsKeyCreatedAfterControlPlaneStartup` green |
| `src/CSweet.Office.Node/OfficeWorker.cs` | Optional: adaptive inspect cadence with an immediate terminal inspect | Only with measurements from the existing logs |
| `src/CSweet.Office.Runtime.HyperV.Helper/PowerShellHyperV.cs` | Stage 4 only: direct state read instead of a PowerShell cold start | Payload rebuild; smoke path re-run; `SEC-INV-20` review |
| Tests | Add a gate test that counts file reads across repeated authorisations using a temporary state directory; keep `RuntimeHostRpcIntegrationTests` green | Follow [test conventions](../.github/instructions/tests.instructions.md) — hand-written fakes, no mocking library |
| Docs | [`docs/10-system/05-request-and-data-flow.md`](../docs/10-system/05-request-and-data-flow.md) if cadence changes; [`docs/20-security/04-workload-authorization.md`](../docs/20-security/04-workload-authorization.md) for ledger semantics; [`docs/20-security/05-local-rpc-boundary.md`](../docs/20-security/05-local-rpc-boundary.md) for the key-read note | Per the change-impact matrix row for host projects |

## What must not change

- The validation order in `ValidateAndCommit` (`SEC-INV-08`) and the handle-authorization binding
  (`SEC-INV-15`).
- The atomic ledger write, the lock, and the rule that `accepted-assignments.json` is never pruned
  (`SEC-INV-09`).
- The signature-before-nonce rule and the no-response-on-unauthenticated-frames rule (`SEC-INV-13`).
- The helper operation allow-list and exit-`0`-on-typed-failure behaviour (`SEC-INV-20`).

## Verification

1. Capture `RuntimeHost ... in {ElapsedMilliseconds} ms` log lines for a running workload before and after.
2. Add the gate-level test for stage 1 and keep the existing RPC integration tests green.
3. For stage 3, re-run the latency observation that a workload's terminal status still arrives promptly.

## Sources

`src/CSweet.Office.Node/OfficeWorker.cs`, `src/CSweet.Office.Runtime.LocalRpc/{LocalRuntimeHostTransport.cs,RuntimeHostRpcServer.cs,RuntimeHostRequestDispatcher.cs,RuntimeHostAuthorizationGate.cs}`,
`src/CSweet.Office.Runtime.Protocol/RuntimeHostAuthentication.cs`,
`src/CSweet.Office.Runtime.Core/ExternalPlatformIsolationBackend.cs`,
`src/CSweet.Office.Runtime.HyperV.Helper/PowerShellHyperV.cs`,
`docs/20-security/{04-workload-authorization.md,05-local-rpc-boundary.md,11-security-invariants.md}`,
`docs/10-system/05-request-and-data-flow.md`, `tests/CSweet.Office.Tests/*`.

Verified: 2026-09-15.
