# CSweet.Office.Runtime.Core

Shared plumbing that every platform backend needs and none of them should re-implement: the typed JSON
helper protocol, the immutable payload manifest, the guest-image registry, the deterministic ISO-9660 writer,
the fail-closed provider selector, and the in-memory doubles the test suite uses. It sits directly above
`Runtime.Abstractions` and is referenced by all three provider libraries, both C# helpers, `Runtime.Artifacts`,
and `WindowsSmokeTest`.

## Project facts

| Fact | Value |
|---|---|
| Output kind | library (`Microsoft.NET.Sdk`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | `CSweet.Office.Runtime.Abstractions` |
| Key package references | none of its own |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.Runtime.Core`) |
| `InternalsVisibleTo` | `CSweet.Office.Tests` |
| csproj `<Description>` | absent |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `PlatformIsolationBackendOptions` | class | Configuration base for external-platform backends: payload manifest path, helper path and digest, guest image path, digest, detached signature, pinned signing certificate and thumbprint, artifact media root, broker protocol version, certification suite version, certification evidence path and digest, certification window, `HelperTimeoutSeconds` (120), `GuestChannelConnectTimeoutSeconds` (30), `RequiredGuestChannelTransport`. |
| `ExternalPlatformIsolationBackend` | abstract class | The fail-closed backend skeleton: probe verifies helper digest, image digest, evidence digest, evidence binding, and detached signature before invoking the helper; workload methods verify the guest image, artifact media, and handle, then invoke the helper over stdio. |
| `ProviderCertificationEvidence` | record | The evidence file shape: provider id/version, host OS/architecture, guest image digest, broker protocol version, certification suite version, `CertifiedAt`, optional `CertificationExpiresAt`. |
| `PlatformHelperRequest` | class | The typed request: `BuilderWorkload`, `RuntimeWorkload`, `ToolchainBuildWorkload`, `Handle`, `GuestImagePath`, `ArtifactImagePath`, `GracePeriodSeconds`, `MaximumBytes`. |
| `PlatformHelperResponse` | class | `Success`, `ErrorCode`, `SanitizedError`, `ProviderInstanceId`, `Status`, `Logs`, `WorkloadsRemoved`, `GuestChannelTransport`. |
| `ExternalPlatformStdioGuestChannelConnector` | class | Opens the guest channel by starting the helper with `--operation open-guest-channel`, reading exactly one bounded JSON line, then returning the same stdio pair as a duplex `Stream`. Publishes `TransportName = "stdio-duplex-v1"`. |
| `PlatformRuntimePayloadManifest` | static class | `ApplyIfConfigured` loads `PayloadManifestPath`, rejects links and traversal, verifies every declared file's SHA-256, checks the two identity digests, and only then writes the resolved helper/image/signature/certificate/evidence settings into a `PlatformIsolationBackendOptions`. |
| `PayloadManifest` (`internal`) | class | Manifest document: schema version 1, provider identity, the five required file paths, the two digests, the signing certificate thumbprint, broker protocol version, certification suite version, certification window, and the `Files` list. |
| `PayloadFile` (`internal`) | class | One declared file: `Path`, `Sha256`. |
| `CertifiedGuestImageRegistry` | class | `IGuestImageRegistry` implementation: selects a provider through `IAgentIsolationProviderSelector`, requires a non-null certification, uses the certified guest image digest, and rejects a stale `RequiredCertificationSuiteVersion`. |
| `FailClosedIsolationProviderSelector` | class | `IAgentIsolationProviderSelector` implementation: raises every request to `CertifiedHardwareVirtualMachine`, orders candidates by assurance then priority then id, and returns only a provider whose live probe carries an active, identity-matching certification. |
| `InMemoryAgentIsolationProvider` | class | Deterministic orchestration test double with a real state machine; explicitly not a security boundary and never registered in production. |
| `InMemoryBuilderArtifactResultStore` | class | Implements `IBuilderArtifactResultStore` and `IBuilderArtifactResultPublisher` with a `TaskCompletionSource` per workload; rejects a duplicate publish and a malformed digest. |
| `SingleFileIso9660` | static class | Deterministic minimal ISO-9660 media containing exactly one `ARTIFACT.CSAB;1` file, plus `VerifyArtifactDigestAsync`, which re-parses the filesystem structures and hashes the payload extent. |

## Entry points / composition

No executable. The library is consumed in four composition paths:

- **Backends**: `HyperVIsolationBackend`, `FirecrackerIsolationBackend`, and
  `AppleVirtualizationIsolationBackend` all derive from `ExternalPlatformIsolationBackend` and publish the
  helper-protocol transport through `ExternalPlatformStdioGuestChannelConnector` subclasses.
- **RuntimeHost startup**: `CSweet.Office.RuntimeHost.Program.cs` calls
  `PlatformRuntimePayloadManifest.ApplyIfConfigured(firecracker, …)` and again for Apple before registering
  the backend, so the manifest wins over appsettings values when it is present.
- **Image resolution**: `CertifiedGuestImageRegistry` and `FailClosedIsolationProviderSelector` are the
  certification-driven selection pair.
- **Media**: `CSweet.Office.Runtime.Artifacts.FileSystemAgentArtifactMediaStore` calls
  `SingleFileIso9660.WriteAsync` / `VerifyArtifactDigestAsync`, and every helper calls the same verifier
  before attaching media.

## Behaviour worth knowing

- **The probe order in `ExternalPlatformIsolationBackend.ProbeAsync` is the fail-closed contract.** Platform
  check, helper existence, helper digest, guest image existence, signature and certificate existence,
  evidence existence, digest syntax, certification metadata presence, image digest, evidence digest,
  evidence binding, image signature, then the helper's own `probe` operation and transport check. Any failure
  returns an unavailable probe with a reason and a null certification — there is no partial success path.
- **`ReapAbandonedWorkloadsAsync` is `protected`** on the base class; each concrete backend re-exposes it by
  explicitly implementing `IPlatformWorkloadReaper`. RuntimeHost's reaper only sees the interface.
- **Hand-rolled protocol framing.** The helper is started as a child process with `--protocol 1.0` and
  `--operation <op>`; the request is a single JSON document written to stdin, stdin is closed, stdout and
  stderr are read with byte ceilings (1 MiB and 16 KiB), and a non-zero exit code becomes an `IOException`
  carrying sanitized stderr. Responses larger than the ceiling throw instead of truncating.
- **Guest-channel framing is stricter than request framing.** `ReadHandshakeAsync` reads byte-by-byte, stops
  at `\n`, rejects `\r`, NUL, empty, and over-4 KiB responses, and deliberately does not consume the bytes
  after the newline — those are the first guest broker bytes.
- **Hand-rolled ISO-9660 writer.** `SingleFileIso9660` writes sectors 0-15 as zeros, a primary volume
  descriptor at sector 16, a terminator at 17, path tables at 18 and 19, the root directory at 20, and the
  artifact from sector 21, padding to `SectorSize` 2048. `VerifyArtifactDigestAsync` re-reads those positions,
  locates `ARTIFACT.CSAB;1`, and requires its extent to be sector 21 and its length to match the directory
  record.
- **The selector never falls back.** If `PreferredProviderId` is set and that provider is not registered,
  selection throws rather than choosing another provider. `EnforcePlatformMinimum` raises a weaker minimum to
  `CertifiedHardwareVirtualMachine` and never lowers it ([SEC-INV-10](../../20-security/11-security-invariants.md)).
- **Payload manifests are read-only metadata.** The doc comment on `PlatformRuntimePayloadManifest` states
  the rule: the manifest never grants additional behaviour, it only binds a known provider to fixed package
  files. Paths must be relative, segment-clean, free of control characters, and every prefix directory is
  checked for symbolic links.
- **`InMemoryAgentIsolationProvider` is a test double.** Production registration is intentionally absent; a
  workload created through it never runs anything.

## Related tests

| Test class | What it pins |
|---|---|
| `IsolationProviderSelectorTests` | The assurance floor, the certification requirement, digest binding, ordering, and the no-fallback rule. |
| `PlatformIsolationBackendTests` | That Hyper-V and Firecracker probes fail closed without an installed helper or certification. |
| `PlatformRuntimePayloadManifestTests` | That a valid manifest applies only its declared verified files, that a file changed after packaging is rejected, and that wrong-provider and traversal paths are rejected. |
| `CertifiedGuestImageRegistryTests` | Active-certification resolution, the optional digest pin, and rejection of a stale guest contract. |
| `InMemoryIsolationProviderTests` | That the in-memory lifecycle is deterministic and that destroy is final. |
| `AgentArtifactMediaStoreTests` | ISO generation, digest verification, and tamper rejection (exercises `SingleFileIso9660` through the media store). |
| `ExternalPlatformStdioGuestChannelConnectorTests` | Handshake framing stops at the newline without consuming broker bytes, and oversized or ambiguous framing is rejected. |
| `FirecrackerHelperSecurityTests` | Shared helper-contract assertions that also load `PlatformHelperRequest`/`PlatformHelperResponse`. |

## Related documentation

- [20-security/09-helper-protocol.md](../../20-security/09-helper-protocol.md) — the stdio protocol these
  types implement.
- [20-security/08-provider-certification.md](../../20-security/08-provider-certification.md) — evidence,
  expiry, and the fail-closed selector.
- [30-workloads/05-artifacts-and-media.md](../../30-workloads/05-artifacts-and-media.md) — the ISO media path.
- [30-workloads/06-guest-images.md](../../30-workloads/06-guest-images.md) — certified images and the
  payload manifest.
- [../configuration.md](../configuration.md) and [../status-and-error-codes.md](../status-and-error-codes.md).

## Sources

`src/CSweet.Office.Runtime.Core/CSweet.Office.Runtime.Core.csproj`,
`src/CSweet.Office.Runtime.Core/{ExternalPlatformIsolationBackend,ExternalPlatformStdioGuestChannelConnector,FailClosedIsolationProviderSelector,CertifiedGuestImageRegistry,InMemoryAgentIsolationProvider,InMemoryBuilderArtifactResultStore,PlatformHelperContracts,PlatformRuntimePayloadManifest,SingleFileIso9660}.cs`,
`src/CSweet.Office.RuntimeHost/Program.cs`, `tests/CSweet.Office.Tests/*.cs`.

Verified: 2026-09-15.
