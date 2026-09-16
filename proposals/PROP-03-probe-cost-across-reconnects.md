# PROP-03 — Bound probe cost across reconnects

**Status:** proposal — not implemented. **Area:** speed. **Effort:** small to medium.

**Tooling cost:** payload rebuild (`Runtime.Core`). No guest or certification impact.

**Invariant interactions:** `SEC-INV-10` — a provider is never returned from **selection** without an active,
identity-matching certification and a live probe. This proposal does not touch selection: in this repository
the selector has no production call site
([`docs/20-security/08-provider-certification.md`](../docs/20-security/08-provider-certification.md)), and any
cache must be short-lived, must fail closed on doubt, and must never let a create succeed that would otherwise
fail (create re-validates independently in `ExternalPlatformIsolationBackend.ValidateWorkload`).

## Problem

A failed control session is retried on a flat five-second loop (`OfficeWorker.ExecuteAsync` catches, logs, and
`Task.Delay(TimeSpan.FromSeconds(5))` — deliberately simple, per
[`docs/20-security/12-known-limits-and-tradeoffs.md`](../docs/20-security/12-known-limits-and-tradeoffs.md)).
Every attempt restarts `RunControlSessionAsync`, whose first act is `inventory.ProbeAsync`, and each provider
probe is expensive:

- `RuntimeHostProviderClient.ProbeAsync` retries the probe RPC up to four times (`ProbeAttempts = 4`).
- Each attempt reaches `ExternalPlatformIsolationBackend.ProbeAsync`, which SHA-256s the helper executable,
  the guest image, and the certification evidence, verifies the guest-image signature (another full hash of
  the image), parses the evidence, and then **launches the helper** for its `probe` operation.

Nothing here is cached. On a host with a multi-gigabyte guest image, an unreachable Headquarters costs several
gigabytes of hashing per attempt and up to twenty gigabytes per five-second retry cycle — while the Office is
offline and doing nothing else useful. The same cost is paid once during enrollment.

## User story

**Scenario.** A customer's Headquarters gateway is down for maintenance for an hour. During that hour, every
registered Office host pins a CPU and its disk verifying an unchanged guest image and spawning a probe helper
every five seconds. When the gateway returns, the hosts are hot, the disk has performed hundreds of gigabytes
of pointless I/O, and nothing about the host has actually changed.

> As an operator, I want an unreachable Headquarters to cost the Office almost nothing, so that a control-plane
> outage does not degrade every registered host at once.

## Recommended fix

Cache the expensive, deterministic part of the probe for a short bound (for example 60 seconds), and keep the
live behavioural part every time:

1. Cache helper-digest, guest-image-digest, evidence-digest, and signature verification results keyed on the
   file metadata the verification already reads (path, length, last-write time). Verification still happens
   on the first probe and after any observed change.
2. Continue to run the helper `probe` operation on every probe: it is the part that asks the hypervisor, and
   `SEC-INV-10`'s "live" language is about exactly that.
3. Invalidate the cache whenever the payload manifest is re-applied at startup, whenever a verification fails,
   and whenever a create fails closed with `provider-unavailable`.
4. Optionally, make the four-attempt retry budget in `RuntimeHostProviderClient` apply to transient transport
   failures only, and not re-drive file verification four times within one burst.

If a reviewer judges any caching unacceptable, the fallback is documentation plus measurement: record the
probe cost in the diagnostic guidance so operators understand why an outage is loud on disk.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `src/CSweet.Office.Runtime.Core/ExternalPlatformIsolationBackend.cs` | Short-TTL, metadata-keyed verification cache around `ProbeAsync`'s file checks | Keep every failure a failure; never cache `Unavailable` as available |
| `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostProviderClient.cs` | Optional: narrow the 4-attempt budget to transport faults | Probe is the only request with retries |
| Tests | `PlatformIsolationBackendTests` and `PlatformRuntimePayloadManifestTests` — add "second probe does not re-hash an unchanged file" and "a changed file is re-hashed" | Hand-written fakes and temporary directories |
| Docs | [`docs/20-security/08-provider-certification.md`](../docs/20-security/08-provider-certification.md) (probe caching semantics), [`docs/40-operations/07-diagnostics-and-troubleshooting.md`](../docs/40-operations/07-diagnostics-and-troubleshooting.md) | Payload rebuild required |

## What must not change

- The flat five-second reconnect loop itself — it is a recorded deliberate behaviour.
- The fail-closed selector and the create-time validations.
- The probe's identity comparison and the certification activity window.
- The probe's helper launch: it is the live half of the check.

## Verification

1. With a local gateway stub refusing connections, measure hashing volume before and after with a filesystem
   counter or `Process` I/O counters on the RuntimeHost.
2. Prove that modifying the guest image file (touch a byte) invalidates the cache and fails a probe.
3. Prove that a provider whose helper `probe` operation fails is still reported unavailable on every probe.

## Sources

`src/CSweet.Office.Node/{OfficeWorker.cs,ProviderInventory.cs}`,
`src/CSweet.Office.Runtime.Core/ExternalPlatformIsolationBackend.cs`,
`src/CSweet.Office.Runtime.LocalRpc/RuntimeHostProviderClient.cs`,
`docs/20-security/{08-provider-certification.md,10-threat-model.md,12-known-limits-and-tradeoffs.md}`,
`tests/CSweet.Office.Tests/{PlatformIsolationBackendTests.cs,PlatformRuntimePayloadManifestTests.cs}`.

Verified: 2026-09-15.
