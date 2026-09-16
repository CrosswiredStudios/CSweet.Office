# CSweet.Office.Runtime.Artifacts

The two stores an Office needs for workload content: a content-addressed filesystem artifact store that
validates an imported bundle before publishing it under its digest, and a media store that renders a validated
artifact as a deterministic read-only ISO image for a virtual DVD. The Node uses both; the hypervisor helpers
consume the media. It sits above `Runtime.Abstractions` and `Runtime.Core`.

## Project facts

| Fact | Value |
|---|---|
| Output kind | library (`Microsoft.NET.Sdk`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | `Runtime.Abstractions`, `Runtime.Core` |
| Key package references | none of its own |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.Runtime.Artifacts`) |
| `InternalsVisibleTo` | not set |
| csproj `<Description>` | absent |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `ArtifactStoreOptions` | class | `SectionName = "CSweet:Office:Node:Artifacts"`: `RootPath`, `Provider` (`filesystem` or `s3`), `MaximumFileCount` (10 000), `MaximumPathLength` (512), `MaximumUncompressedBytes` (2 GiB), `MaximumManifestBytes` (1 MiB); `ValidatedRootPath()` and `ValidatedProvider()`. |
| `ArtifactMediaOptions` | class | `SectionName = "CSweet:Office:Node:ArtifactMedia"`: `RootPath` and `ValidatedRootPath()`. |
| `FileSystemAgentArtifactStore` | class, `IAgentArtifactStore` | `ExistsAsync`, `OpenReadAsync`, and `ImportAsync`, which streams the upload into a quarantine file while hashing, validates the bundle, then moves it to its content-addressed name and returns an `AgentArtifactReference` signed by the injected `IAgentArtifactSigner`. |
| `FileSystemAgentArtifactMediaStore` | class, `IAgentArtifactMediaStore` | `EnsureReadOnlyMediaAsync(digest)`: builds `{root}/{digest-without-prefix}.iso` when it is missing or fails verification, using a per-digest semaphore and a temporary file. |

## Entry points / composition

No executable. The Node composes both stores in `OfficeArtifactCache`, which derives the artifact root from
`OfficeOptions.ResolveArtifactCacheDirectory()`, constructs a `FileSystemAgentArtifactMediaStore` over
`OfficeOptions.ResolveArtifactMediaDirectory()` with itself as the artifact source, and calls
`EnsureReadOnlyMediaAsync` after every verified download. The Windows installer points the RuntimeHost
provider options at the same media directory through `CSWEET_ARTIFACT_MEDIA_ROOT`, so the helper can attach
the ISO it finds there.

## Behaviour worth knowing

- **Import is quarantine-then-publish.** The stream is copied with a byte ceiling and hashed in the same pass;
  a mismatch on the declared digest throws before anything touches the final path. A concurrent importer of
  the same digest simply deletes its quarantine copy and keeps the existing file.
- **The bundle format is validated, not trusted.** `ValidateBundleAsync` opens the tar and enforces: entry
  count under `MaximumFileCount`; unique, case-insensitive, relative, control-character-free paths; zero UID
  and GID; no set-uid, set-gid, or sticky bits; only regular files and directories; every file below
  `payload/` or the single `artifact.json`; uncompressed total under `MaximumUncompressedBytes`; a manifest
  that matches the import descriptor's format version, OS, and architecture; an entrypoint of 1-32 items; and
  an entrypoint file that exists on disk with the user-execute bit.
- **The media store is self-healing.** `EnsureReadOnlyMediaAsync` re-verifies an existing ISO and rebuilds it
  when verification fails, so a truncated or tampered image cannot survive a restart.
- **Digest names are the storage layout.** Artifacts live at `{root}/{digest[7..]}.artifact` and media at
  `{root}/{digest[7..]}.iso`; the media file name is what helpers re-derive and compare against the workload
  digest, so a renamed file cannot be attached.
- **`Provider = "s3"` is accepted by validation but has no implementation.** `ValidatedProvider()` allows the
  value; no code path constructs an S3 store, and `OfficeArtifactCache.ImportAsync` throws
  `NotSupportedException` outright because offices receive artifacts only through assignment-scoped grants.

## Related tests

| Test class | What it pins |
|---|---|
| `AgentArtifactMediaStoreTests` | That `EnsureReadOnlyMediaAsync` produces a verified content-addressed ISO, that a tampered image is rejected by `SingleFileIso9660.VerifyArtifactDigestAsync`, and that `FileSystemAgentArtifactStore` is constructed and used through the media store. |
| `OfficeTests` | The Node-side download-and-commit path that feeds this store. |

There is no dedicated test for `FileSystemAgentArtifactStore.ImportAsync` bundle validation in this repository.

## Related documentation

- [30-workloads/05-artifacts-and-media.md](../../30-workloads/05-artifacts-and-media.md) — the end-to-end
  artifact and media flow.
- [20-security/07-guest-isolation.md](../../20-security/07-guest-isolation.md) and
  [SEC-INV-22](../../20-security/11-security-invariants.md) — guest-side mounting and extraction limits that
  mirror these checks.
- [../configuration.md](../configuration.md) and [../file-layout.md](../file-layout.md).

## Sources

`src/CSweet.Office.Runtime.Artifacts/CSweet.Office.Runtime.Artifacts.csproj`,
`src/CSweet.Office.Runtime.Artifacts/{ArtifactStoreOptions,ArtifactMediaOptions,FileSystemAgentArtifactStore,FileSystemAgentArtifactMediaStore}.cs`,
`src/CSweet.Office.Node/OfficeArtifactCache.cs`, `tests/CSweet.Office.Tests/AgentArtifactMediaStoreTests.cs`.

Verified: 2026-09-15.
