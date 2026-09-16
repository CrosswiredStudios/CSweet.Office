# Assignment and lease semantics

**Audience:** contributors and reviewers; read before touching `OfficeWorker.ExecuteAssignmentAsync`.

Four different notions of "time remaining" apply to a running workload. They are not interchangeable, and
conflating them is the most likely source of a subtle bug.

## The four clocks

| Clock | Set by | Enforced by | Bound |
|---|---|---|---|
| **Signed authorization window** | Headquarters, in the signature | Node (`ValidateAssignment`) and RuntimeHost gate (`ValidateAndCommit`) | Lifetime ≤ 10 minutes. Never extended after issuance. |
| **Assignment lease renewal** | Node, every 20 seconds | Headquarters | `RequestedExpiry = now + 60 seconds`. It updates Headquarters' view only. |
| **Broker lease** | Headquarters, in the boot configuration | The **guest**, with `CancellationTokenSource.CancelAfter(leaseExpiry − now)` | Provider also requires `BrokerLease.ExpiresAt > now` at create and at handle registration. |
| **`ResourceLimits.MaximumDurationSeconds`** | Headquarters, in the specification | **Nothing** | Not enforced anywhere in Office or the helpers. |

A workload that runs for an hour is legal: its *authorization* expired after ten minutes, but nothing needs a
fresh authorization to keep an already-created VM running. What actually bounds the run is the broker lease.

## Validation windows

Both verification stages apply time checks, with slightly different parameters.

### Node — `ValidateAssignment`

| Check | Limit |
|---|---|
| `IssuedAt` not in the future | ≤ now + 2 minutes |
| `LeaseExpiresAt` | > now |
| Authorization lifetime | `ExpiresAt − IssuedAt` ≤ 10 minutes |

### RuntimeHost gate — `ValidateAndCommit`

| Check | Limit |
|---|---|
| Skew on `IssuedAt` | ≤ `MaximumClockSkewSeconds` (default 120, valid 0–600) |
| `ExpiresAt` | > now |
| Authorization lifetime | ≤ `MaximumAuthorizationLifetimeSeconds` (default 600, valid 30–3600) |

The Windows installer writes `MaximumAuthorizationLifetimeSeconds = 600` and `MaximumClockSkewSeconds = 120`.

## The renewal loop

While an assignment runs, `ExecuteAssignmentAsync` does two things on timers:

| Interval | Action |
|---|---|
| 2 seconds | `InspectAsync` against the provider — state, termination reason, exit code, timestamps. |
| 20 seconds | Send `AssignmentLeaseRenewal { AssignmentId, FencingEpoch, RequestedExpiry = now + 60s }`. |

The loop exits when the inspected status is null or reaches `Destroyed`, `Failed`, or `Stopped`. It also
re-throws the guest-tunnel task's exception if that task faults, so a broken relay ends the assignment rather
than leaving it silently running.

### Why the renewal does not extend the authorization

The signed authorization is a Headquarters decision, made once, with a hard ten-minute cap at both ends of the
local RPC boundary. The renewal tells Headquarters "this assignment is still alive" so it can make scheduling
decisions and so it knows whether to extend the *broker lease* on its side. Treating the renewal as an
authorization extension would require the RuntimeHost to hold open-ended trust in the message stream rather
than in a verified signature — which is the property the design refuses to give up.

## Admission control

The only backpressure in the Node is a semaphore:

```csharp
_workloadSlots = new SemaphoreSlim(Math.Max(1, options.MaximumConcurrentWorkloads));
```

`MaximumConcurrentWorkloads` defaults to `ProcessorCount / 2` and is reported to Headquarters at enrollment
and in the heartbeat, so Headquarters can avoid over-dispatching. An assignment that cannot get a slot waits —
it is not rejected.

## Fencing and cancellation

| Mechanism | Effect |
|---|---|
| `Fence` control message | Cancels the assignment's `CancellationTokenSource` in the active-assignment dictionary. Only a matching assignment id is affected. |
| Cancellation | Propagates into the inspect loop, the renewal loop, and the tunnel relay. Teardown happens in the handler's `finally`. |
| Gate epoch check | **Rejects** stale epochs on create and on every handle operation. It does not stop a running workload. |

So the gate protects against *starting* replayed or superseded work, and the Node's cancellation is what
*stops* a running workload. Both are required; neither substitutes for the other.

## Drain

`Drain` sets a marker file (`maintenance/drain-state` containing `draining`). Nothing in the Node reacts to it
operationally beyond recording it — draining is a *reporting* state that gates the installer, the upgrade
probe, and the uninstallers. An Office continues to run its existing assignments and will still accept new
ones; Headquarters is expected to stop dispatching (see [40-operations/04-upgrade-and-drain.md](../40-operations/04-upgrade-and-drain.md)).

## Teardown is unconditional

Regardless of how the assignment ended, the `finally` block:

1. cancels and awaits the tunnel task,
2. calls `provider.DestroyAsync(handle, CancellationToken.None)` **unconditionally** — a failure only logs a
   warning,
3. releases the workload slot,
4. removes the assignment from the active set and deletes its local activity marker,
5. disposes the cancellation token source.

Destroy is also the only operation that removes the handle from the RuntimeHost authorization ledger.

## Sources

`src/CSweet.Office.Node/OfficeWorker.cs`,
`src/CSweet.Office.Runtime.LocalRpc/RuntimeHostAuthorizationGate.cs`,
`src/CSweet.Office.RuntimeGuest/GuestBrokerSession.cs`,
`src/CSweet.Office.Runtime.Core/ExternalPlatformIsolationBackend.cs`,
`scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`.

Verified: 2026-09-15.
