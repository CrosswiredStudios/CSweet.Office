# CSweet.Office.Runtime.Core

Shared plumbing that every platform backend needs and none of them should re-implement: the typed JSON helper protocol, the payload manifest verifier, the certification-driven guest image registry and fail-closed selector, the deterministic ISO-9660 media writer, and the in-memory test doubles. It holds the fail-closed selection path that runs before any workload starts.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/runtime-core.md`](../../docs/80-reference/projects/runtime-core.md).

**Output:** library
**References:** `CSweet.Office.Runtime.Abstractions`

## What it owns

- `ExternalPlatformIsolationBackend` — the fail-closed backend skeleton: the probe verifies helper digest, guest image, evidence binding, and detached signature before the helper is invoked.
- `ExternalPlatformStdioGuestChannelConnector` — starts the helper with `--operation open-guest-channel`, reads one bounded JSON handshake line, and returns the same stdio pair as a duplex stream (`stdio-duplex-v1`).
- `PlatformHelperRequest`, `PlatformHelperResponse`, and `PlatformRuntimePayloadManifest` — the typed helper contract and the manifest that binds a provider to verified package files.
- `FailClosedIsolationProviderSelector` and `CertifiedGuestImageRegistry` — selection that requires a live probe with an active, identity-matching certification, and image resolution pinned to the certified digest.
- `SingleFileIso9660` — the deterministic ISO-9660 writer plus `VerifyArtifactDigestAsync`, shared by the artifact media store and re-implemented independently by the Swift helper.
- `InMemoryAgentIsolationProvider` and `InMemoryBuilderArtifactResultStore` — deterministic test doubles; production registration is intentionally absent.

## Notes for contributors

- The helper operation surface and framing are fixed (SEC-INV-20); read [docs/20-security/09-helper-protocol.md](../../docs/20-security/09-helper-protocol.md) before changing requests, responses, or the handshake reader.
- Selection raises every request to certified hardware assurance and never falls back to another provider (SEC-INV-10).
- `ReadHandshakeAsync` deliberately does not consume bytes after the newline — those are the first guest broker bytes. The tests assert this byte-exact behaviour.
- Payload manifests are read-only metadata; they bind known providers to fixed package files and never grant additional behaviour.
- `ReapAbandonedWorkloadsAsync` is `protected` on the base class and re-exposed per backend; keep reaping independent of control-plane state.

## Tests

`IsolationProviderSelectorTests`, `PlatformIsolationBackendTests`, `PlatformRuntimePayloadManifestTests`, `CertifiedGuestImageRegistryTests`, `InMemoryIsolationProviderTests`, `AgentArtifactMediaStoreTests`, and `ExternalPlatformStdioGuestChannelConnectorTests`; `FirecrackerHelperSecurityTests` also loads the helper contracts.

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
