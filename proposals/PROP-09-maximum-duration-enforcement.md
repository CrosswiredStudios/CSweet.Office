# PROP-09 — Decide the fate of `ResourceLimits.MaximumDurationSeconds`

**Status:** proposal — not implemented. **Area:** security and contract honesty. **Effort:** small as a
decision, medium to enforce.

**Tooling cost:** enforcing in the guest is a guest change (fingerprint root, Packer rebuild, re-certification).
Enforcing in the Node or RuntimeHost is a payload rebuild. Removing or renaming the field is a
cross-repository `CSweet.Office.Contracts` change.

**Invariant interactions:** none directly. `SEC-INV-11` covers the numeric clamps, and this field is not one
of them; the point of the proposal is that the field currently means nothing.

## Problem

`ResourceLimits.MaximumDurationSeconds` is part of the signed workload specification, but nothing enforces it:

- The deliberate behaviour is recorded in [`AGENTS.md`](../AGENTS.md) ("do not fix") and in
  [`docs/20-security/12-known-limits-and-tradeoffs.md`](../docs/20-security/12-known-limits-and-tradeoffs.md):
  "Nothing in Office or the helpers enforces it… If you are relying on this field for enforcement: it is not
  doing what you think."
- Runtime length is bounded by the broker lease expiry that Headquarters places in `BrokerLease.ExpiresAt`.
  The guest enforces that with `CancelAfter` in `GuestBrokerSession.RunAsync`, and the Node renews the lease
  every 20 seconds while the assignment is active — so the effective bound is "as long as Headquarters keeps
  renewing", not the signed duration.
- The maximum authorization lifetime (10 minutes) bounds the authorization window, not the run.

This is a specification-honesty problem. A workload author who reads the contract, a reviewer who checks the
limits, and a customer reading a runbook can all reach the same wrong conclusion: that a workload will be
stopped after the number in the spec.

## User story

**Scenario.** A workload author signs a plugin specification with
`ResourceLimits.MaximumDurationSeconds = 3600` and tells stakeholders that a runaway analysis will be cut off
after an hour. It is not. The workload keeps running for nine hours because the lease keeps being renewed,
and the only reason it stops is that someone notices the bill.

> As a workload author, I want `MaximumDurationSeconds` either enforced or removed from the contract, so that
> the limits I am handed in the specification are limits I can actually rely on.

## Recommended fix

Write the decision down first, then implement one of the three consistent outcomes.

### Outcome A — Enforce it (recommended if any policy depends on it)

Enforce **at the Node or RuntimeHost boundary** and document it as a policy bound, not an isolation
guarantee: when a workload has been `Running` for longer than the signed duration, cancel the assignment
(the local cancellation path already tears the workload down in `ExecuteAssignmentAsync`'s `finally`) and
report a deterministic terminal status. This needs no contract change because the specification JSON is
already available on both sides. It must respect the status semantics in
[`docs/80-reference/status-and-error-codes.md`](../docs/80-reference/status-and-error-codes.md).

A hard in-guest stop is stronger but needs the value to reach the guest. The boot configuration is authored
by Headquarters, and `StartCommand` carries only entrypoint, log budget, and environment — so a guest-side
enforcement is a `CSweet.Office.Contracts` change plus out-of-repo work. If that path is chosen, the guest
already has the right mechanism (`CancelAfter`, exit `137` for `resource-limit-exceeded`) and the change is
mechanical once the value arrives.

### Outcome B — Remove it from the contract

At the next Contracts major, remove the field and note the removal in the release notes and the
compatibility page. This is the honest answer if no policy wants the bound.

### Outcome C — Keep it, but say what it is

If the field stays advisory, rename it at the next Contracts major to make that unmistakable (for example an
`Advisory` prefix) and update the specification documentation. This is the weakest option: it still invites
the wrong conclusion.

Whichever outcome is chosen, record it in the known-limits page so the current entry stops being a warning
about a trap and becomes a statement of policy.

## Changes required

| Area | Change | Notes |
|---|---|---|
| Contract (`CSweet.Office.Contracts`) | Outcome B or C: field removal/rename, package bump, pin both repositories | Cross-repository release flow |
| `src/CSweet.Office.Node/OfficeWorker.cs` | Outcome A: running-time timer and teardown; deterministic failure code | Reuses the existing cancellation/teardown path |
| `src/CSweet.Office.RuntimeGuest/*` | Outcome A (guest variant): apply the duration from the boot configuration; exit `137` | Guest rebuild and re-certification |
| Tests | Outcome A: a fake provider that reports `Running` past the bound; assert the teardown and the reported status | Hand-written fakes |
| Docs | [`docs/20-security/12-known-limits-and-tradeoffs.md`](../docs/20-security/12-known-limits-and-tradeoffs.md), [`docs/30-workloads/01-assignment-and-lease-semantics.md`](../docs/30-workloads/01-assignment-and-lease-semantics.md), [`docs/80-reference/status-and-error-codes.md`](../docs/80-reference/status-and-error-codes.md), [`docs/80-reference/wire-protocols.md`](../docs/80-reference/wire-protocols.md) | Whichever outcome is chosen |

## What must not change

- The lease-expiry cancellation and the guest's own power-off behaviour (`SEC-INV-23`).
- The relationship between the lease and the boot configuration expiry ("equal verbatim", `SEC-INV-23`).
- The numeric clamps in `SEC-INV-11`.

## Verification

1. Outcome A: a workload that exceeds the bound terminates with the documented code and status, and the
   resource is destroyed — not merely reported.
2. Outcome A: a workload inside the bound is unaffected; lease renewal and fencing still behave as before.
3. Outcome B/C: the contract tests and both repositories build with the new package pin.

## Sources

`AGENTS.md`, `docs/20-security/{11-security-invariants.md,12-known-limits-and-tradeoffs.md}`,
`docs/30-workloads/01-assignment-and-lease-semantics.md`, `docs/80-reference/status-and-error-codes.md`,
`src/CSweet.Office.Node/OfficeWorker.cs`, `src/CSweet.Office.RuntimeGuest/{GuestBrokerSession.cs,GuestWorkloadSupervisor.cs}`,
`src/CSweet.Office.Runtime.Core/ExternalPlatformIsolationBackend.cs`.

Verified: 2026-09-15.
