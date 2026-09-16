# PROP-02 — Stop hashing the same payload on every launch

**Status:** proposal — not implemented. **Area:** speed. **Effort:** small to medium.

**Tooling cost:** payload rebuild (Node and artifacts code). No guest or certification impact.

**Invariant interactions:** `SEC-INV-22` (artifact media mounting and extraction limits) and the documented
deliberate behaviour that `SingleFileIso9660.VerifyArtifactDigestAsync` hashes only the payload extent
([`docs/20-security/12-known-limits-and-tradeoffs.md`](../docs/20-security/12-known-limits-and-tradeoffs.md)).
The proposal preserves both; it removes duplicate work above the verified artifact, not verification itself.

## Problem

A repeat launch of an already-cached artifact hashes the payload more than once before the VM starts:

1. `OfficeArtifactCache.EnsureAsync` calls `VerifyAsync(path, digest)`, which reads the whole artifact file and
   computes SHA-256, to decide whether a download is needed.
2. `FileSystemAgentArtifactMediaStore.EnsureReadOnlyMediaAsync` checks the shared media ISO with
   `SingleFileIso9660.VerifyArtifactDigestAsync` — a second full pass over the same bytes — and rebuilds the
   ISO only if that check fails.
3. `ExternalPlatformIsolationBackend.CreateAsync` re-verifies the ISO payload extent in the privileged
   process, by design.
4. On Hyper-V, the helper additionally writes a **private copy** of the shared ISO into the instance directory
   and re-verifies it ([`docs/30-workloads/07-provider-backends.md`](../docs/30-workloads/07-provider-backends.md),
   "artifact.iso — for artifact workloads, a private copy of the shared media ISO, re-verified").

The artifact file itself is never mounted: it is only an input to the ISO build. Once the ISO verifies, the
Node's artifact hash adds nothing that the ISO check has not already established, and both are repeated on
every launch of every workload that uses the artifact.

## User story

**Scenario.** Priya schedules eight concurrent workloads that all consume the same 1.5 GiB artifact. The
artifact is already in the cache and the media ISO is already built, but each launch still reads and hashes
the artifact and the ISO before the first VM starts. Start latency scales with the number of launches, not
with the number of new artifacts, and the disk is saturated hashing bytes that were verified minutes ago.

> As an Office operator, I want a repeat launch to reuse already-verified media, so that start latency and
> disk I/O scale with the number of new artifacts rather than the number of launches.

## Recommended fix

Reorder `OfficeArtifactCache.EnsureAsync` so the media store is asked first:

1. If `IAgentArtifactMediaStore.EnsureReadOnlyMediaAsync` reports that a verifying ISO exists, skip the
   artifact re-hash and return.
2. If the ISO must be built (or fails verification), run the artifact verification, download when the cache is
   missing, then rebuild the ISO. The ISO build already verifies its own output before the atomic move.

Optionally, add a verification cache for the artifact file keyed on (length, last-write time, file identity)
so a rebuild after a deliberate media refresh does not re-hash a file whose metadata is unchanged. The trust
argument for relaxing the Node-side hash: the artifact cache directory is Node-writable, and the security
boundary that protects the VM is the privileged create-time check in `ExternalPlatformIsolationBackend`
plus the in-guest extraction checks — not the Node's own hash. A reviewer who disagrees should keep the
Node-side hash and implement only the reordering, which is the main win.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `src/CSweet.Office.Node/OfficeArtifactCache.cs` | Ask the media store first; verify or download only when the media must be (re)built | Keep `ValidateDigest`, the download sequence checks, and the post-download digest verification unchanged |
| `src/CSweet.Office.Runtime.Artifacts/FileSystemAgentArtifactMediaStore.cs` | No behaviour change expected; confirm the return contract makes "verified ISO exists" observable to the caller | It already verifies before returning early |
| Tests | Add a counting `IAgentArtifactStore` fake proving the artifact is not hashed when a verifying ISO exists; keep `AgentArtifactMediaStoreTests` green | Hand-written fakes only |
| Docs | [`docs/30-workloads/05-artifacts-and-media.md`](../docs/30-workloads/05-artifacts-and-media.md) if it describes the verification sequence | Payload rebuild required for the Node change |

## What must not change

- The privileged create-time ISO verification and the deliberate payload-extent-only digest.
- The download-time verification (offset/size checks, digest comparison, atomic move).
- The in-guest whole-stream digest check and extraction limits (`SEC-INV-22`).

## Verification

1. Instrument a repeat launch (timestamps around `EnsureAsync` and media ensure) and compare before/after.
2. Prove the negative case: corrupt the media ISO and confirm the artifact is re-verified and the media is
   rebuilt, not silently reused.

## Sources

`src/CSweet.Office.Node/OfficeArtifactCache.cs`, `src/CSweet.Office.Runtime.Artifacts/FileSystemAgentArtifactMediaStore.cs`,
`src/CSweet.Office.Runtime.Core/{ExternalPlatformIsolationBackend.cs,SingleFileIso9660.cs}`,
`docs/30-workloads/{05-artifacts-and-media.md,07-provider-backends.md}`,
`docs/20-security/{11-security-invariants.md,12-known-limits-and-tradeoffs.md}`.

Verified: 2026-09-15.
