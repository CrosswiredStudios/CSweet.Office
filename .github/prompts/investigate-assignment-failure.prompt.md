---
description: "Diagnose a failed or stuck Office workload: map the failure code to a cause, check the right logs, and identify whether the fault is local, provider-side, or Headquarters-side."
mode: agent
---

# Investigate a failed assignment

## 1. Start from the failure code

Ask for, or find, the `AssignmentStatusUpdate` failure code. It narrows the cause immediately.

| Code | Likely cause | Where to look |
|---|---|---|
| `assignment-envelope-invalid` | Version, key id, identifier, epoch, provider, time window, digest, or signature check failed **on the Node** | Node service log; the assignment came from Headquarters and was rejected, not executed |
| `isolation-provider-unavailable` | No certified provider was available, or the message is passed through verbatim (for example *"Hyper-V is not enabled."*) | Provider probe output, `/dev/kvm`, Hyper-V feature state, certification expiry |
| `headquarters-broker-rejected` | Headquarters rejected the guest broker exchange with `FailedPrecondition` | Headquarters; the Office relays the detail (control characters stripped, 1500-character cap) |
| `headquarters-authorization-rejected` | `PermissionDenied` or `Unauthenticated` from Headquarters | Enrollment, revocation state, certificate validity |
| `headquarters-unavailable` | `Unavailable` or `DeadlineExceeded` | Network path to Headquarters; Office reconnects on a fixed 5-second loop |
| `headquarters-rpc-error` | Any other gRPC status | Headquarters |
| `office-error` | A local exception. The original message is deliberately not leaked. | Node service log, using the assignment identifier |
| `workload-failed` | The workload exited non-zero or terminated abnormally | Provider status, guest `runtime.logs` stream |

See [`docs/80-reference/status-and-error-codes.md`](../../docs/80-reference/status-and-error-codes.md) for the
full catalogue.

## 2. Check the local gates in order

1. **Provider available?** Run the RuntimeHost's probe path and confirm the certification window is active. An
   expired certification stops work (`SEC-INV-10`).
2. **Authorization accepted?** Look for a replay or stale-epoch rejection. A fence or a re-dispatch with a lower
   epoch is rejected; the ledger is never pruned to unstick a workload (`SEC-INV-09`).
3. **Handle authorized?** `inspect` and `logs` are rejected after lease expiry; only `stop` and `destroy` are
   allowed (`SEC-INV-15`).
4. **Guest media mounted?** A guest that fails artifact validation exits early with a boot failure reason code.

## 3. Check the logs that actually exist

| Source | Where |
|---|---|
| Node failures | Windows Event Log source `CSweet.Office.Node`; `journalctl -u csweet-office-node` on Linux; launchd logs on macOS |
| RuntimeHost | Event Log source `CSweet.Office.RuntimeHost`; `journalctl -u csweet-office-runtime` |
| Provider logs | **Windows: none.** The Hyper-V helper's `Logs()` returns an empty array. Firecracker yields `console` and `firecracker` streams. |
| Workload output | Guest `runtime.logs` stream, surfaced through the tunnel — this is the only source of workload stdout on Windows |

If you find yourself looking for a Windows provider log, stop: it does not exist.

## 4. Distinguish a stuck workload from a failed one

- Long-running past the signed authorization window is **normal**; the authorization is not renewed for a running
  VM. What bounds the run is the broker lease.
- A `Fence` cancels the assignment in the Node. If the cancellation is not delivered, the workload continues
  until the lease expires.
- `ResourceLimits.MaximumDurationSeconds` is **not enforced anywhere**. Do not treat it as a timeout.

## 5. Before proposing a fix

State which side of the boundary owns the defect: Node, RuntimeHost, helper, guest, or Headquarters. Then read
[`docs/70-contributing/04-review-checklist.md`](../../docs/70-contributing/04-review-checklist.md) so the fix
does not weaken an invariant.
