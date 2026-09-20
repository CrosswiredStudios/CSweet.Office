# Cross-repo contracts

**Audience:** anyone changing a shared type, bumping the contracts pin, or validating the package boundary
before a release.

C-Sweet Office is one of three repositories that share a wire contract. This page records what each side owns,
which concrete types cross the boundary, where the pin lives, and the exact commands that prove this
repository still builds and tests against the released package rather than against a local source checkout.

| Repository | Owns |
|---|---|
| **CSweet.Office** (this repository) | The Office execution plane: Node, RuntimeHost, providers and helpers, the in-guest runners, the installers, the payload tools, the certification smoke test, and this documentation. |
| **CSweet.Office.Contracts** | The versioned wire contract: control-plane JSON records and gRPC service, the guest broker protocol, the workload specification model, the Office v1 authorization envelope adapter, and the shared protobuf framing helper. |
| **CSweet** (Headquarters) | The gateway, scheduling, enrollment approval, certificate issuance, artifact authorization and storage, guest broker streaming, and the fleet UI. |
| **CSweet.Isolation** | The `CSweet.LinuxImage` PowerShell module that both products use to build Ubuntu guest images, plus the `CSweet.Isolation.Security` primitives that the contracts envelope is built on. |

> **Out of repo:** the type names in the tables below are declared in another repository. This page records
> how *this* repository consumes them and was verified against the sibling checkout at
> `..\CSweet.Office.Contracts` (the `CSweetOfficeContractsRepositoryRoot` default) and
> `..\CSweet.Isolation`. Nothing here is inferred from the Office code alone.

## How the contract enters this repository

`Directory.Build.props`:

| Property | Value | Effect |
|---|---|---|
| `CSweetOfficeContractsRepositoryRoot` | `$(MSBuildThisFileDirectory)..\CSweet.Office.Contracts` | Default location of the sibling checkout. |
| `UseLocalOfficeContracts` | `true` when `$(CSweetOfficeContractsRepositoryRoot)\src\CSweet.Office.Contracts\CSweet.Office.Contracts.csproj` exists, otherwise forced to `false` | **Silent auto-detection.** A developer with the sibling checked out always compiles against its source; a machine without it compiles against the package. |

`Directory.Build.targets` then adds, for every project whose name is not `CSweet.Office.Contracts`:

- a `ProjectReference` to the sibling project when `UseLocalOfficeContracts` is `true`, or
- a `PackageReference Include="CSweet.Office.Contracts"` (version from central package management) when it is
  not,

plus a compile item for `Office.GlobalUsings.cs`.

`Office.GlobalUsings.cs` puts all four contract namespaces in scope for every project in this repository:

```csharp
global using CSweet.Office.Contracts.ControlPlane;
global using CSweet.Office.Contracts.Guest;
global using CSweet.Office.Contracts.Security;
global using CSweet.Office.Contracts.Workloads;
```

## The pin

| Truth | Location | Current value |
|---|---|---|
| Contracts package version | `Directory.Packages.props` → `<PackageVersion Include="CSweet.Office.Contracts" Version="…" />` | `0.7.1` |
| Office runtime version | `Directory.Build.props` → `<VersionPrefix>` | `0.6.0` |
| Release tag | Git tag `vX.Y.Z` | must equal `VersionPrefix` |

`scripts/release/Get-OfficeReleaseMetadata.ps1` is the only script that compares these: it rejects a tag that
does not match `VersionPrefix` and rejects a contracts pin that is not exactly one `MAJOR.MINOR.PATCH`, and it
emits both values for the release pipeline. The emitted `contractsVersion` is what
`New-OfficeReleaseManifest.ps1` records in `office-release.json`.

## Namespace `ControlPlane`

Defined in `ControlPlane/office_control.proto` (`Option csharp_namespace = "CSweet.Office.Contracts.ControlPlane"`,
`GrpcServices="Both"`) and `ControlPlane/EnrollmentContracts.cs`.

| Type | Kind | Used in this repository |
|---|---|---|
| `OfficeGateway` (+ `OfficeGatewayClient`) | gRPC service / generated client | `src/CSweet.Office.Node/OfficeWorker.cs` (the `Connect` and `OpenWorkloadTunnel` calls), `src/CSweet.Office.Node/OfficeArtifactCache.cs` (`DownloadArtifact`) |
| `OfficeControlMessage` | message | `OfficeWorker.cs` (status updates, lease renewals), `src/CSweet.Office.Node/ProviderInventory.cs` (heartbeat inventory) |
| `HeadquartersControlMessage` | message | `OfficeWorker.ReadControlMessagesAsync` |
| `WorkloadAssignment`, `FenceAssignment`, `DrainOffice`, `GatewayHello` | messages | `OfficeWorker.cs` |
| `AssignmentLeaseRenewal`, `AssignmentStatusUpdate` | messages | `OfficeWorker.cs` |
| `WorkloadTunnelFrame` | message | `OfficeWorker.RelayGuestChannelAsync` |
| `ArtifactDownloadRequest`, `ArtifactChunk` | messages | `OfficeArtifactCache.cs` |
| `ClaimOfficeRequest`, `ClaimOfficeResponse` | record | `OfficeWorker.EnrollAsync` |
| `OfficeHeartbeatRequest` | record | `OfficeWorker.RunControlSessionAsync` |
| `RegisterOfficeProviderRequest` | record | `src/CSweet.Office.Node/ProviderInventory.cs` |
| `OfficeSecurityPostureReport` | record | `src/CSweet.Office.Node/OfficeOptions.cs` (`SecurityPosture()`), reported in enrollment and heartbeat |
| `OfficeCertificateRequest`, `OfficeCertificateResponse` | record | `OfficeWorker.RefreshOperationalCertificateAsync`, `RecoverOperationalCertificateAsync` |
| `OfficeCertificateChallengeResponse`, `OfficeCertificateRecoveryRequest` | record | `OfficeWorker.RecoverOperationalCertificateAsync` |
| `OfficeCertificateRecoveryProof` | static helper | `OfficeWorker.RecoverOperationalCertificateAsync`, `tests/CSweet.Office.Tests/OfficeCertificateRecoveryTests.cs` |
| `HeadquartersAssignmentTrustResponse` | record | `src/CSweet.Office.Node/ControlPlaneCertificateProbe.cs` (`api/offices/assignment-trust`) |
| `AssistedOfficePreflightRequest/Response`, `RedeemAssistedOfficeSetupRequest/Response`, `ReportAssistedOfficeSetupResultRequest`, `CompleteAssistedOfficeRemovalRequest` | record | `src/CSweet.Office.Configurator/Program.cs`, `src/CSweet.Office.Configurator/OfficeMaintenanceService.cs` |

## Namespace `Guest`

Defined in `Guest/guest_broker.proto` (`GrpcServices="None"`), `Guest/GuestHandshake.cs`, and
`Guest/LengthDelimitedProtobuf.cs`.

| Type | Kind | Used in this repository |
|---|---|---|
| `GuestEnvelope` | message | `src/CSweet.Office.RuntimeGuest/{GuestBrokerSession.cs,Program.cs}` |
| `GuestBootConfiguration`, `GuestBootFailure` | messages | `src/CSweet.Office.RuntimeGuest/GuestBrokerTransport.cs` (`GuestBootConfigurationReader`), `Program.cs` |
| `GuestHello`, `HostChallenge`, `GuestProof`, `GuestLease` | messages | `GuestBrokerSession.cs` |
| `GuestHealth`, `StartCommand`, `ShutdownCommand`, `GuestExit` | messages | `GuestBrokerSession.cs` |
| `ProxyRequest`, `ProxyResponse`, `StreamChunk` | messages | `GuestBrokerSession.cs`, `src/CSweet.Office.RuntimeGuest/GuestLocalBrokerProxy.cs` |
| `GuestHandshakeClient`, `GuestHandshakeVerifier`, `ExpectedGuestIdentity` | classes | `GuestBrokerSession.cs` (client), `src/CSweet.Office.WindowsSmokeTest/CertificationBrokerHost.cs` (verifier) |
| `LengthDelimitedProtobuf` | static class | `GuestBrokerSession.cs`, `src/CSweet.Office.Runtime.Protocol/RuntimeHostRpcServer.cs` — **the local RPC and the guest broker share this framing helper even though it is declared in the guest namespace** |

## Namespace `Security`

Defined in `Security/AssignmentEnvelope.cs`.

| Type | Kind | Used in this repository |
|---|---|---|
| `AssignmentEnvelope` | static class with `CurrentAuthorizationVersion = 1`, `Digest(string)`, and `Payload(...)` | `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostAuthorizationGate.cs` (version check, digest check, signature verification), `tests/CSweet.Office.Tests/RuntimeHostRpcIntegrationTests.cs` (builds authorizations) |

`AssignmentEnvelope` is a thin adapter: it forwards to
`CSweet.Isolation.Security.WorkloadAuthorizationEnvelope.Encode("CSweet.Office.WorkloadAuthorization", 1, …)`.
The purpose string and byte layout are owned by the Isolation library; do not reimplement them here.

## Namespace `Workloads`

Defined in `Workloads/WorkloadModels.cs`.

| Type | Kind | Used in this repository |
|---|---|---|
| `WorkloadSpecification` (abstract) and the three concrete records `BuilderWorkloadSpecification`, `RuntimeWorkloadSpecification`, `ToolchainBuildWorkloadSpecification` | records | `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostProtocolMapper.cs`, `RuntimeHostAuthorizationGate.cs`, `RuntimeHostRequestDispatcher.cs`, `RuntimeHostProviderClient.cs`, `src/CSweet.Office.Node/OfficeWorker.cs` (`DeserializeSpecification`) |
| `WorkloadKind` (`Builder` 0, `Runtime` 1, `ToolchainBuild` 2) | enum | everywhere a kind is compared |
| `AgentTrustLevel` | enum | `src/CSweet.Office.Runtime.Core/FailClosedIsolationProviderSelector.cs`, `CertifiedGuestImageRegistry` |
| `WorkloadResourceLimits` | record with `Validate()` | `RuntimeHostProtocolMapper.cs`, provider helpers |
| `GuestImageReference`, `AgentArtifactReference` | records | `RuntimeHostProtocolMapper.cs`, `CertifiedGuestImageRegistry.cs` |
| `BrokerChannelLease` | record | `RuntimeHostProtocolMapper.cs`, `RuntimeHostAuthorizationGate.RegisterHandle` (lease expiry) |
| `RepositoryDescriptor`, `RuntimeAgentIdentity` | records | `RuntimeHostProtocolMapper.cs` |

The in-repo `src/CSweet.Office.Runtime.Abstractions` layer defines the provider-facing mirrors
(`IsolationWorkloadHandle`, `IsolationWorkloadStatus`, `IsolationProviderCertification`,
`IsolationSelectionRequest`, `PinnedHeadquartersTrust`). Those types are **not** part of the shared contract and
must not be moved into it: they are the Office-internal view, and `RuntimeHostProtocolMapper` is the only place
that translates between the two.

## Transitive dependency: `CSweet.Isolation.Security`

| Fact | Value |
|---|---|
| Declared by | `CSweet.Office.Contracts.csproj` |
| Version | `0.1.0` (package), or the sibling project when its own `UseLocalIsolation` auto-detection resolves |
| How it reaches this repository | Transitively, through the contracts package or project reference. It is not declared in `Directory.Packages.props`. |
| Where it is visible | `CSweet.Isolation.Security.WorkloadAuthorizationEnvelope` is reachable from `AssignmentEnvelope`; the assembly ships in the payload (`CSweet.Isolation.Security.dll` in every `runtime/`, `node/`, `helper/`, and `configurator/` publish output and in `runtime-manifest.json`). |
| Consequence | A change to the envelope encoding is a change to `CSweet.Isolation`, which must be released before `CSweet.Office.Contracts` can adopt it. `SEC-INV-08` and `SEC-INV-09` depend on this encoding staying byte-compatible. |

`CentralPackageTransitivePinningEnabled` is `true` in `Directory.Packages.props`, so the transitive package
version cannot float silently: it resolves to whatever the contracts pin declares.

## What lives in C-Sweet instead

From the root `README.md`: *"The headquarters gateway, scheduling, enrollment approval, certificates, artifact
authorization/storage, guest broker streaming, and fleet UI remain in C-Sweet."*

| Area | Owner | Consequence for this repository |
|---|---|---|
| Headquarters gateway and the `OfficeGateway` server side | C-Sweet | The Office only ever connects out; it never listens (`SEC-INV-12`, and the listener inventory in [10-system/06](../10-system/06-process-network-and-storage-surface.md)). |
| Scheduling and placement decisions | C-Sweet | Assignments arrive as signed envelopes; the Office never chooses work (`SEC-INV-07`). |
| Enrollment approval and one-use enrollment codes | C-Sweet | The Office can claim and wait; approval happens in the fleet UI. |
| Certificate issuance, challenge, and recovery endpoints | C-Sweet | The Office implements the client half (`SEC-INV-03`, `SEC-INV-04`). |
| Artifact authorization and the bytes themselves | C-Sweet | The Office receives artifacts only through assignment-scoped grants; `ImportAsync` throws `NotSupportedException("Offices receive artifacts only through assignment-scoped grants.")`. |
| Guest broker streaming (broker session, boot configuration, handshake verification, result-artifact ingestion) | C-Sweet | The Office relays bytes; `src/CSweet.Office.WindowsSmokeTest/CertificationBrokerHost.cs` is an in-repo *test* implementation of the host side, not a product component. |
| Fleet UI, setup progress, assisted setup, and maintenance requests | C-Sweet | The installers report progress and redemptions to endpoints served by C-Sweet. |
| Release hosting | GitHub Releases (canonical), linked from c-sweet.com | Office never self-updates. |

## What lives in `CSweet.Isolation`

| Item | Consumers here |
|---|---|
| `tools/LinuxImage/CSweet.LinuxImage.psd1` (PowerShell module) with `New-CSweetLinuxHyperVImage` | `scripts/windows/New-CSweetHyperVTestGuest.ps1` imports it as its first statement after the parameter block, so a missing sibling checkout fails immediately. The module is also used to build generic compute images in CSweet.Isolation itself. |
| `src/CSweet.Isolation.Security` | Transitively through the contracts package; see above. |
| `%IsolationRoot%\artifacts\linux-images` | Default `-ArtifactDirectory` for the Windows image builder. |

The module's directory is inside the Hyper-V guest build fingerprint, so changing the shared builder
invalidates the cached Windows guest image. See
[30-workloads/06-guest-images.md](../30-workloads/06-guest-images.md) for the full fingerprint definition and
[50-development/09-guest-image-changes.md](../50-development/09-guest-image-changes.md) for what that means
when you are editing documentation.

## Boundary verification

`-p:UseLocalOfficeContracts=false` is the switch that proves this repository works against the **released
package** rather than a sibling checkout. Run it from the repository root.

| Command | What it proves |
|---|---|
| `dotnet restore CSweet.Office.Independent.slnx -p:UseLocalOfficeContracts=false` | The pinned package version exists on the configured feed and restores every transitive dependency. |
| `dotnet build CSweet.Office.Independent.slnx -c Release --no-restore -p:UseLocalOfficeContracts=false` | The independent solution compiles with no project reference to the sibling checkout. It excludes `CSweet.Office.ToolchainGuest` and the contracts project. |
| `dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release --no-build -p:UseLocalOfficeContracts=false` | The full suite passes against the package. |

These are exactly the three steps `.github/workflows/ci.yml` runs, in that order, and they are why CI forces
the switch: on a CI runner the sibling repository does not exist, so auto-detection would otherwise decide the
build's meaning. Forcing the value makes the package boundary explicit and reproducible, and it is what
prevents a local-only `ProjectReference` from masking a missing package update.

The release path forces the same value: `scripts/release/Invoke-PlatformRelease.ps1` runs
`dotnet test CSweet.Office.Independent.slnx -c Release -p:UseLocalOfficeContracts=false` before it produces
any artifact, and throws `Release tests failed.` when it does not pass.

For day-to-day work you can also build the primary solution, which does include the sibling contracts project:

```powershell
dotnet build CSweet.Office.slnx -c Release
```

> **Contributor rule (from `AGENTS.md`, verbatim intent):** if `CSweet.Office.Contracts` changes, bump that
> package using semantic versioning, pack it, update **both** this repository and C-Sweet to the same released
> pin, and verify both with local project references disabled. This repository is an independently versioned
> installable deliverable: do not couple its tags to C-Sweet headquarters tags, and never publish or sign from
> an ordinary development runner.

A contract change that is not yet released cannot be validated by CI, because CI has no sibling checkout — so
the sequence is always: change in `CSweet.Office.Contracts` → bump, pack, publish → update
`Directory.Packages.props` here and in C-Sweet → run the three commands above.

## Sources

`Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `Office.GlobalUsings.cs`,
`CSweet.Office.slnx`, `CSweet.Office.Independent.slnx`, `README.md`, `AGENTS.md`,
`src/**/*.cs` usages listed in the tables above, `.github/workflows/ci.yml`,
`scripts/release/{Get-OfficeReleaseMetadata.ps1,Invoke-PlatformRelease.ps1,New-OfficeReleaseManifest.ps1}`,
`scripts/windows/New-CSweetHyperVTestGuest.ps1`, `artifacts/windows-test/certification-20260915-134026/payload/runtime-manifest.json`,
and the sibling checkouts `..\CSweet.Office.Contracts\src\CSweet.Office.Contracts\{CSweet.Office.Contracts.csproj,ProtocolVersions.cs,ControlPlane\*.cs,Guest\*.cs,Security\AssignmentEnvelope.cs,Workloads\WorkloadModels.cs}`
and `..\CSweet.Isolation\` (directory layout).

Verified: 2026-09-19.
