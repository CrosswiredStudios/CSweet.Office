# PROP-04 — Remove `WriteThrough` from bulk artifact and media writes

**Status:** proposal — not implemented. **Area:** speed. **Effort:** small.

**Tooling cost:** payload rebuild (Node and artifacts code). No guest or certification impact.

**Invariant interactions:** none. Integrity comes from post-write digest verification and atomic renames; this
proposal does not touch either. It only removes a durability hint from two bulk writers.

## Problem

Two bulk writers open files with `FileOptions.WriteThrough`, which bypasses the operating-system cache and
pushes every write to the device:

- `OfficeArtifactCache.DownloadAndCommitAsync` writes the downloaded artifact into a temporary file with
  `FileOptions.WriteThrough` (64 KiB buffer) before verifying and renaming it.
- `FileSystemAgentArtifactMediaStore.EnsureReadOnlyMediaAsync` writes the temporary ISO with
  `FileOptions.WriteThrough` before verifying and renaming it.

Both paths verify the finished file with SHA-256 and then `File.Move` it into place. If a crash produces a
truncated or torn file, the next reader's digest check fails and the file is rebuilt or re-downloaded — the
durability hint buys nothing the verification does not already provide, and it caps write throughput at
device-write speed for files that can reach 2 GiB (the artifact transfer ceiling in
`DownloadAndCommitAsync`).

The privileged ledger writes in `RuntimeHostAuthorizationGate.WriteAtomic` also use `WriteThrough`. Those are
small, security-relevant, crash-visible records and should keep it.

## User story

**Scenario.** A 1.8 GiB artifact is downloaded to an Office on a host with a fast NVMe disk. The transfer
takes far longer than the network or the disk's cached write speed should allow, because every 64 KiB written
is pushed through to the device before the next arrives.

> As an operator, I want artifact download and media creation to run at cached disk speed, so that workload
> start latency reflects the network and the CPU, not a durability guarantee that verification already covers.

## Recommended fix

Remove `FileOptions.WriteThrough` from the two bulk writers and keep the existing structure: buffered
asynchronous writes, `FlushAsync`, dispose/close, verify, atomic move. Leave `WriteAtomic` in the authorization
gate untouched. If a reviewer wants explicit crash semantics, document that the verified-digest-plus-rename
protocol is what makes a torn file safe, not the write flag.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `src/CSweet.Office.Node/OfficeArtifactCache.cs` | Drop `WriteThrough` from the download stream | Keep the offset/size checks, digest verification, and overwrite move |
| `src/CSweet.Office.Runtime.Artifacts/FileSystemAgentArtifactMediaStore.cs` | Drop `WriteThrough` from the temporary ISO stream | Keep the verify-before-move |
| Tests | Confirm the existing media-store and download tests still pass; optionally add a "truncated temporary file is rejected" case | The verification path is already the test surface |
| Docs | [`docs/30-workloads/05-artifacts-and-media.md`](../docs/30-workloads/05-artifacts-and-media.md) if the write flags are described | Payload rebuild required |

## What must not change

- Post-write digest verification and the atomic rename in both writers.
- `WriteThrough` in `RuntimeHostAuthorizationGate.WriteAtomic` — the ledgers are small and their crash
  semantics are part of the authorization story.

## Verification

1. Time a large artifact download before and after on the same host.
2. Interrupt a download mid-write, restart, and confirm the artifact is re-downloaded rather than accepted.

## Sources

`src/CSweet.Office.Node/OfficeArtifactCache.cs`,
`src/CSweet.Office.Runtime.Artifacts/FileSystemAgentArtifactMediaStore.cs`,
`src/CSweet.Office.Runtime.LocalRpc/RuntimeHostAuthorizationGate.cs`,
`docs/30-workloads/05-artifacts-and-media.md`.

Verified: 2026-09-15.
