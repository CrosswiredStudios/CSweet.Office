# Artifacts and media

**Audience:** contributors to the Node or to `Runtime.Artifacts`; reviewers of the guest-media path.

An artifact travels through four distinct forms on its way into a guest, and each form is verified
independently.

## The four forms

| # | Form | Location | Verified by |
|---|---|---|---|
| 1 | `.csab` tar stream | in transit from Headquarters | Headquarters, and the guest on extraction |
| 2 | Cached file `<hex>.artifact` | Node artifact cache | Node, on download and again on every cache hit |
| 3 | Single-file ISO `<hex>.iso` | artifact media directory | Provider, at every create; Hyper-V also on its private copy |
| 4 | Extracted tree | `/run/csweet/artifact/payload` in the guest | Guest, per entry |

## 1. Authorization

An assignment carries an `ArtifactReadToken` (32–256 characters) whenever its specification has an artifact
digest. `DownloadArtifact` is bound to:

```
OfficeId + SessionEpoch + AssignmentId + FencingEpoch + ArtifactDigest + ArtifactReadToken + TransferId
```

Tokens are assignment-scoped and are **never persisted** by the Office.

`OfficeArtifactCache.ImportAsync` throws `NotSupportedException`:

> *"Offices receive artifacts only through assignment-scoped grants."*

There is no path by which an artifact can be pushed to an Office or fetched without a grant.

## 2. Node download and cache

`OfficeArtifactCache` (`IAgentArtifactStore`), cache root `ArtifactCacheDirectory`
(default `<state>/artifact-cache`), file name `<hex>.artifact`.

`EnsureAsync`:

1. Re-hashes the cached file (fixed-time comparison) and returns early on a match.
2. Otherwise streams `DownloadArtifact` with up to **3 attempts** sharing one `TransferId`, so Headquarters
   can resume across attempts.
3. Enforces, per chunk: contiguous `chunk.Offset`, at most 64 KiB per chunk, at most 2 GiB total,
   `chunk.TotalSize == bytes`, and `chunk.Sha256 == digest`.
4. Writes to `.{guid}.download` and then `File.Move(..., overwrite: true)`.
5. Retries only for `Unavailable`, `Internal`, and `DeadlineExceeded`, with a `250 ms × attempt` delay.
6. Then calls `_media.EnsureReadOnlyMediaAsync(digest)`.

> **Re-verification on every use** is what makes a corrupted or evicted cache file safe rather than silently
> dangerous.

## 3. Media (the ISO)

`FileSystemAgentArtifactMediaStore` writes `<mediaRoot>/<hex>.iso`, but **only** when an existing ISO does not
already verify. A per-digest `SemaphoreSlim` serializes writers, and the file is written to a temporary path
and moved into place.

`SingleFileIso9660` produces a deterministic image:

| Element | Value |
|---|---|
| Volume identifiers | `CSWEET` / `CSWEET_AGENT_ARTIFACT` |
| Primary volume descriptor | sector 16 |
| Path tables | sectors 18 and 19 |
| Root directory | sector 20 |
| Payload | sector 21 |
| Directory entry | `ARTIFACT.CSAB;1` |

`VerifyArtifactDigestAsync` re-parses the primary volume descriptor and the root directory record and hashes
**only the file extent**.

> **Consequence worth knowing:** ISO padding and metadata are not covered by the digest. The payload bytes are,
> and the payload digest is independently bound into the signed assignment — so the guarantee that matters is
> intact, but do not assume a whole-file hash.

## 4. Hand-off to the VM

Every backend requires `ArtifactImagePath` to be exactly `<ArtifactImageRoot>/<hex>.iso` for the requested
digest, and re-verifies it (`ExternalPlatformIsolationBackend.CreateAsync`). Hyper-V goes further: it copies
the ISO into the instance directory as `artifact.iso` and re-verifies the private copy.

> **The Node's `ArtifactMediaDirectory` and the RuntimeHost's `ArtifactImageRoot` must point at the same
> physical directory.** On Linux and macOS the service files set `CSWEET_ARTIFACT_MEDIA_ROOT`; the Firecracker
> default is `/var/lib/csweet/artifact-media`.

Attachment differs per platform:

| Platform | Mechanism | Guest device |
|---|---|---|
| Hyper-V | DVD drive at SCSI 0:2 | `/dev/sr0` |
| Firecracker | `artifact.iso` drive, read-only | `/dev/vdc` |
| Apple Virtualization | storage entry, read-only | `/dev/vdc` |

**Builder workloads may never attach artifact media.** Setting `ArtifactImagePath` with a builder spec
produces `invalid-artifact-media`.

## 5. Materialization in the guest

Covered in [20-security/07-guest-isolation.md](../20-security/07-guest-isolation.md): device allow-list,
`RDONLY | NOSUID | NODEV | NOEXEC` mount, whole-stream digest check, tar limits, entry-type rejection, path
confinement, mode rewriting, and `chgrp csweet-workload`.

## The importer (library, no production call sites here)

`FileSystemAgentArtifactStore.ImportAsync` in `Runtime.Artifacts` is the **import** path:

- quarantines the incoming file,
- hashes it,
- validates the `.csab` tar — size limits, no links or special files, uid/gid 0, no setuid/setgid/sticky bits,
  an `artifact.json` manifest matching the descriptor's format, OS, and architecture, and an executable
  entrypoint,
- stores it at `<root>/sha256/<xx>/<hex>.csab`,
- returns an `AgentArtifactReference` and signs it with `IAgentArtifactSigner.Sign(digest, provenanceJson)`.

It has no call sites in this repository. It is the Headquarters-side storage library, covered by
`AgentArtifactMediaStoreTests`.

> **Out of repo:** artifact authorization, storage, signing, and the decision to grant a read token all live in
> Headquarters. Office only consumes a grant.

## Sources

`src/CSweet.Office.Node/OfficeArtifactCache.cs`,
`src/CSweet.Office.Runtime.Artifacts/{ArtifactStoreOptions.cs,ArtifactMediaOptions.cs,FileSystemAgentArtifactStore.cs,FileSystemAgentArtifactMediaStore.cs}`,
`src/CSweet.Office.Runtime.Core/{SingleFileIso9660.cs,ExternalPlatformIsolationBackend.cs}`,
`src/CSweet.Office.RuntimeGuest/GuestArtifactMaterializer.cs`,
`tests/CSweet.Office.Tests/{AgentArtifactMediaStoreTests.cs,GuestArtifactMaterializerTests.cs,PlatformIsolationBackendTests.cs}`.

Verified: 2026-09-15.
