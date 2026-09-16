# PROP-06 — Make workload reaping lease-driven, not kind-driven

**Status:** proposal — not implemented. **Area:** security (residue and resource exhaustion).
**Effort:** medium.

**Tooling cost:** the change is in the three platform helpers, so it requires a payload rebuild and a re-run
of the certification smoke path (the helper digest is recorded in the payload manifest and in the
certification evidence). It is **not** a protocol change: no operation is added and no message shape changes,
so the eight-operation allow-list and `SEC-INV-20` are untouched. Helper sources are not guest-image
fingerprint roots, so no Packer rebuild is implied.

**Invariant interactions:** `SEC-INV-20` (helper surface stays fixed) — preserved. The reaping decision must
keep failing safe: reap only what is provably abandoned.

## Problem

All three helpers refuse to reap builder instances:

- `HyperVHelperController.ReapAsync` skips `metadata.Kind == WorkloadKind.Builder`, and `ShouldReap` requires
  `Runtime` or `ToolchainBuild`.
- `FirecrackerHelperController.ReapAsync` carries the same kind restriction, with the mirrored predicate.
- The Apple helper's `reap` accepts `metadata.kind == 1` only.

The documented rationale is that builders are long-running and have "no reliable lease to key on"
([`docs/20-security/12-known-limits-and-tradeoffs.md`](../docs/20-security/12-known-limits-and-tradeoffs.md),
"Reaping is asymmetric…"). That rationale is stale: the Hyper-V helper records
`workload.BrokerLease.ExpiresAt` in `instance.json` for **every** workload kind, and the Firecracker instance
metadata carries `LeaseExpiresAt` as well. `ShouldReap` already treats an expired or missing lease, a
`FinishedAt` timestamp, an unknown VM state, or an `Off` VM past the creation grace period as reap-eligible.

The consequence of the skip is recorded in
[`docs/30-workloads/07-provider-backends.md`](../docs/30-workloads/07-provider-backends.md): "An Office that
crashes during a builder run leaves an orphaned VM" — and, on Hyper-V, its differencing disk and scratch VHDX
sit in the instance directory with it. Recovery is manual.

The missing safety argument is already present in the design: the guest's broker session is lease-bound.
`GuestBrokerSession.RunAsync` creates `leaseCancellation` with `CancelAfter(leaseDelay)`, the Node requests a
lease renewal every 20 seconds while the assignment is live, and the guest powers its own VM off when the
session ends. A builder whose lease has expired therefore has no live guest session to protect.

## User story

**Scenario.** At 02:00 the Office VM that was running a long builder workload is restarted by the host's
patch management. The builder's lease is never renewed again, but the guest VM, its differencing disk, and its
scratch disk remain on the host. Nobody notices until capacity planning three weeks later reveals a shelf of
`CSweet-<id>` VMs that only a human can clean up.

> As an operator, I want an abandoned builder VM to be reclaimed automatically once its lease has expired, so
> that a host crash does not leave VMs and disks accumulating until someone thinks to look for them.

## Recommended fix

Make the reap predicate depend on abandonment evidence (expired lease, `FinishedAt`, VM missing or `Off` past
the grace period) and remove the kind-based skip in all three helpers:

1. **Hyper-V and Firecracker.** Remove the `Kind.Builder` short-circuit and the kind requirement in
   `ShouldReap`; the existing lease/state/grace logic then applies to builders unchanged.
2. **Apple.** Apply the same predicate to kinds 0 and 2 in place of the `kind == 1` gate, so all three
   backends agree.
3. **Verify the lease-renewal premise first.** Before removing the skip, confirm that Headquarters honours
   `AssignmentLeaseRenewal` for builder assignments. The Node sends renewals for every active assignment, but
   the accepting side is out of this repository. If builder leases are never extended, the predicate must key
   on `FinishedAt` or a grace period alone, and the missing renewal is a separate cross-repository issue.

A defensive additional signal is a "finishing" marker written by the guest before a long final upload, so a
grace-window reap cannot race a legitimate final phase. If the lease premise holds, this is optional.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `src/CSweet.Office.Runtime.HyperV.Helper/HyperVHelperController.cs` | Drop the `Builder` skip; generalise `ShouldReap` | Keep the `CSweet-` name prefix check and the instance-directory validation |
| `src/CSweet.Office.Runtime.Firecracker.Helper/FirecrackerHelperController.cs` | Mirror the change | The two predicates are intentionally mirrored; keep them in step |
| `src/CSweet.Office.Runtime.AppleVirtualization.Helper/Sources/CSweetAppleVirtualizationHelper/HelperController.swift` | Replace the `kind == 1` gate with the same predicate | |
| Tests | `HyperVInstanceReapingTests` and `FirecrackerHelperSecurityTests` (the mirrored predicate); add builder cases and an explicit "active builder with live lease is never reaped" case | Hand-written fakes |
| Docs | [`docs/30-workloads/07-provider-backends.md`](../docs/30-workloads/07-provider-backends.md) (reaping rows and the orphan sentence), [`docs/20-security/12-known-limits-and-tradeoffs.md`](../docs/20-security/12-known-limits-and-tradeoffs.md) (rewrite or remove the entry), [`docs/40-operations/07-diagnostics-and-troubleshooting.md`](../docs/40-operations/07-diagnostics-and-troubleshooting.md) (manual cleanup note) | Also a release note: operator-visible behaviour change |
| Release | Payload rebuild; certification smoke path re-run because the helper digest is part of the certified configuration | Per the change-impact matrix row for helper executables |

## What must not change

- The eight-operation allow-list, typed JSON over stdio, and exit-`0`-on-typed-failure behaviour
  (`SEC-INV-20`).
- The reap predicate's fail-safe direction: never destroy an instance that shows signs of being active.
- The `reap` operation's signature (`ReapAbandonedWorkloadsAsync` takes no arguments and cannot depend on
  control-plane state).

## Verification

1. Unit-test the shared predicate against builder metadata in all states: live lease, expired lease,
   `FinishedAt` set, VM missing, VM `Off` past grace.
2. On a Windows test host, create a builder instance, stop renewing, let the lease lapse, and observe the
   reaper reclaim it within a few intervals.
3. Confirm a running builder with an actively renewed lease survives multiple reap sweeps.

## Sources

`src/CSweet.Office.Runtime.HyperV.Helper/{HyperVHelperController.cs,PowerShellHyperV.cs}`,
`src/CSweet.Office.Runtime.Firecracker.Helper/FirecrackerHelperController.cs`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Sources/CSweetAppleVirtualizationHelper/HelperController.swift`,
`src/CSweet.Office.RuntimeGuest/{GuestBrokerSession.cs,GuestSystemPower.cs}`,
`src/CSweet.Office.Node/OfficeWorker.cs` (`AssignmentLeaseRenewal`),
`docs/30-workloads/07-provider-backends.md`, `docs/20-security/12-known-limits-and-tradeoffs.md`,
`tests/CSweet.Office.Tests/{HyperVInstanceReapingTests.cs,FirecrackerHelperSecurityTests.cs}`.

Verified: 2026-09-15.
