# CSweet.Office.Runtime.Artifacts

The two stores an Office needs for workload content: a content-addressed filesystem artifact store that validates an imported bundle before publishing it under its digest, and a media store that renders a validated artifact as a deterministic read-only ISO image for a virtual DVD. The Node uses both; the hypervisor helpers consume the media.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/runtime-artifacts.md`](../../docs/80-reference/projects/runtime-artifacts.md).

**Output:** library
**References:** `CSweet.Office.Runtime.Abstractions`, `CSweet.Office.Runtime.Core`

## What it owns

- `FileSystemAgentArtifactStore` — `ExistsAsync`, `OpenReadAsync`, and `ImportAsync`, which streams the upload into a quarantine file while hashing, validates the bundle, then publishes it at its digest.
- `FileSystemAgentArtifactMediaStore` — `EnsureReadOnlyMediaAsync(digest)`: builds `{root}/{digest}.iso` when it is missing or fails verification, using a per-digest semaphore and a temporary file.
- `ArtifactStoreOptions` — section `CSweet:Office:Node:Artifacts`: root path, provider, and the bundle limits (`MaximumFileCount`, `MaximumPathLength`, `MaximumUncompressedBytes`, `MaximumManifestBytes`).
- `ArtifactMediaOptions` — section `CSweet:Office:Node:ArtifactMedia`: the media root the helpers are pointed at.

## Notes for contributors

- Import is quarantine-then-publish: nothing touches the final digest-named path until the size ceiling, digest, and bundle validation pass.
- Digest-derived file names are the storage layout — helpers re-derive `{digest}.iso` from the workload digest, so a renamed file cannot be attached.
- The media store re-verifies an existing ISO and rebuilds it on failure; do not "optimise" the re-verification away.
- `Provider = "s3"` is accepted by validation but has no implementation, and `OfficeArtifactCache.ImportAsync` throws `NotSupportedException` by design — artifacts arrive only through assignment-scoped grants.
- There is no test for the full `ImportAsync` bundle validator; changes there need manual verification against the format rules.

## Tests

`AgentArtifactMediaStoreTests` covers ISO generation, digest verification, and tamper rejection; `OfficeTests` covers the Node download-and-commit path that feeds the store. No dedicated test for `ImportAsync` bundle validation in `tests/CSweet.Office.Tests`.

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
