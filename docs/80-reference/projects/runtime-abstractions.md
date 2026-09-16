# CSweet.Office.Runtime.Abstractions

The provider-neutral contract layer. Every other runtime project either implements these types or consumes
them; nothing here knows about Hyper-V, Firecracker, Apple Virtualization, or the control plane, so a provider
backend and the Node-side client that talks to it can be compiled from the same shape. It sits at the bottom
of the dependency graph with no project references of its own.

## Project facts

| Fact | Value |
|---|---|
| Output kind | library (`Microsoft.NET.Sdk`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | none |
| Key package references | none of its own; `CSweet.Office.Contracts` arrives through `Directory.Build.targets` |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.Runtime.Abstractions`) |
| `InternalsVisibleTo` | not set |
| csproj `<Description>` | "Provider-neutral contracts for hardware-isolated C-Sweet agent workloads." |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `IsolationAssurance` | enum | Assurance ladder: `None` 0, `Process` 10, `SharedKernelContainer` 20, `HardwareVirtualMachine` 30, `CertifiedHardwareVirtualMachine` 40, `RemoteCertifiedHardwareVirtualMachine` 50. |
| `IsolationWorkloadState` | enum | `Creating`, `Created`, `Starting`, `BootstrappingGuest`, `Running`, `Stopping`, `Stopped`, `Destroying`, `Destroyed`, `Failed`. |
| `IsolationTerminationReason` | enum | `None`, `Completed`, `Cancelled`, `StartFailed`, `GuestBootstrapFailed`, `LeaseExpired`, `RuntimeLimitExceeded`, `ResourceLimitExceeded`, `PolicyDenied`, `SecurityViolation`, `ProviderFailure`, `HostShutdown`. |
| `IsolationProviderCapabilities` | record | Thirteen capability flags plus `Satisfies(IsolationCapabilityRequirements)`, which requires the assurance floor and every requested flag. |
| `IsolationCapabilityRequirements` | record | The request side of the same check; only secure boot and measured/verified boot default to `false`. |
| `IsolationProviderDescriptor` | record | `ProviderId`, `DisplayName`, `ProviderVersion`, host OS and architecture, `Priority`, capabilities. |
| `IsolationProviderCertification` | record | Certification identity and window; `IsActiveAt` is false when revoked or expired. |
| `IsolationProviderProbeResult` | record | Descriptor, availability, reason, and the optional certification that selection requires. |
| `IsolationWorkloadHandle` | record | `ProviderId`, `WorkloadId`, `ProviderInstanceId`, `WorkloadKind`. This is what every lifecycle call takes. |
| `IsolationWorkloadStatus` | record | Handle, state, termination reason, optional exit code, timestamps, error code, sanitized error. |
| `IsolationLogChunk` | record | Timestamp, stream name, bounded content, truncation flag. |
| `IsolationSelectionRequest` | record | Trust level, capability requirements, optional digest pin, broker protocol version, optional preferred provider. |
| `IsolationProviderSelection` | record | The chosen provider plus its probe result. |
| `IsolationProviderCatalog` | static class | The three built-in descriptors: `HyperV()`, `Firecracker()`, `AppleVirtualization()`. |
| `IAgentIsolationProvider` | interface | The provider surface: `Descriptor`, `ProbeAsync`, `CreateAsync`, `StartAsync`, `InspectAsync`, `StopAsync`, `DestroyAsync`, `StreamLogsAsync`. |
| `IAgentGuestChannelProvider` | interface | `OpenGuestChannelAsync` — the provider-neutral authenticated guest broker byte stream. |
| `IPlatformGuestChannelConnector` | interface | `ProviderId` plus the same open call, implemented by the privileged RuntimeHost side. |
| `IAgentIsolationProviderSelector` | interface | `SelectAsync(IsolationSelectionRequest)`. |
| `IRuntimeHostClient` | interface | `IRuntimeHostClient : IAgentIsolationProvider, IAgentGuestChannelProvider`; adds `PinHeadquartersTrustAsync` and `CreateAuthorizedAsync`. |
| `IPlatformIsolationBackend` | interface | Marker: a provider that runs inside RuntimeHost. |
| `IPlatformWorkloadReaper` | interface | `ReapAbandonedWorkloadsAsync` — cleanup that deliberately does not depend on the control plane. |
| `IRemoteSecureRunnerClient` | interface | Reserved contract boundary for provider implementations; no in-repo implementation. |
| `PinnedHeadquartersTrust` | record | `OfficeId`, `AssignmentSigningKeyId`, `AssignmentVerificationPublicKey`. |
| `SignedWorkloadAuthorization` | record | The Office-side view of a signed assignment: ids, fencing epoch, provider, specification JSON and digest, signature, issued/expiry timestamps. |
| `IAgentArtifactStore` | interface | `ExistsAsync`, `OpenReadAsync`, `ImportAsync`. |
| `IAgentArtifactMediaStore` | interface | `EnsureReadOnlyMediaAsync(digest)`. |
| `IAgentArtifactSigner` | interface | `Sign` / `Verify` over an artifact digest and its provenance JSON. |
| `ArtifactImportDescriptor` | record | Expected digest, byte ceiling, format version, OS, architecture, provenance JSON. |
| `IBuildProfileRegistry` / `BuildProfileDescriptor` | interface / record | Resolves a runtime type and target framework to a profile id, guest image id, allowed package hosts, and maximum duration. |
| `IGuestImageRegistry` / `GuestImageResolutionRequest` | interface / record | Resolves a logical image to a `GuestImageReference`, optionally pinned by digest and required certification suite version. |
| `BuilderArtifactResult` | record | `WorkloadId`, `AgentArtifactReference`, `OpaqueLocator`. |
| `IBuilderArtifactResultStore` / `IBuilderArtifactResultPublisher` | interfaces | Wait-side and publish-side of the builder result handoff. |
| `IsolationUnavailableException` | exception | The single failure type for "no certified provider can serve this request". |

## Entry points / composition

No executable, no host, no `Program.cs`. The library composes in two directions:

- Providers implement `IAgentIsolationProvider` (and `IPlatformIsolationBackend`, `IPlatformWorkloadReaper`,
  and `IPlatformGuestChannelConnector` when they run inside RuntimeHost).
- Hosts resolve descriptors from `IsolationProviderCatalog`, select a provider through
  `IAgentIsolationProviderSelector`, and drive lifecycle calls with `IsolationWorkloadHandle`.

The Node-side composition is the interesting one: `CSweet.Office.Node.Program.cs` asks
`HostPlatformProvider.Resolve()` for the descriptor for the current OS and builds exactly one
`RuntimeHostProviderClient` typed as `IAgentIsolationProvider`. The client implements `IRuntimeHostClient`,
so the same object satisfies RuntimeHost's remote surface and every local provider abstraction.

## Behaviour worth knowing

- **The catalog is the only place provider identity is declared.** `hyperv-gen2` ("Hyper-V Generation 2",
  priority 300), `firecracker-kvm` ("Firecracker on KVM", priority 200), and `apple-virtualization`
  ("Apple Virtualization.framework", priority 100) all claim `CertifiedHardwareVirtualMachine`, version
  `1.0.0`, and the host OS matching their platform. The architecture argument defaults to the running
  process architecture.
- **Capability differences that matter.** Hyper-V declares `SupportsSecureBoot: true`,
  `SupportsMeasuredOrVerifiedBoot: false`, and `SupportsProcessLimits: false`. Firecracker and Apple
  Virtualization declare `SupportsSecureBoot: false` and `SupportsMeasuredOrVerifiedBoot: true`, and claim
  every other flag. Selection requires the flags a caller asks for, so these differences are observable.
- **`IPlatformWorkloadReaper` is deliberately control-plane independent.** Its doc comment states the reason:
  the control-plane database may be unavailable or may have been recreated, so a backend that can only reap
  with database help cannot reap at all.
- **`IRemoteSecureRunnerClient` has no implementation in this repository.** It exists as a contract boundary
  so a fleet-level runner could be modelled without pretending that a provider is a machine.
- **`IsolationCapabilityRequirements` defaults to the strict set.** A caller that constructs one with only a
  minimum assurance still asks for a dedicated kernel, broker socket, read-only base disk and artifact,
  ephemeral writable disk, CPU, memory, disk and process limits, and no network device. Secure boot and
  measured boot are opt-in.

> **Out of repo:** the workload types these contracts reference — `WorkloadKind`, `WorkloadSpecification`,
> `BuilderWorkloadSpecification`, `RuntimeWorkloadSpecification`, `ToolchainBuildWorkloadSpecification`,
> `WorkloadResourceLimits`, `BrokerChannelLease`, `GuestImageReference`, `RuntimeAgentIdentity`,
> `RepositoryDescriptor`, `AgentArtifactReference`, and `AgentTrustLevel` — come
> from the `CSweet.Office.Contracts` package, not from this repository.

## Related tests

There is no test class dedicated to this project; the contracts are exercised indirectly. The closest
coverage is `IsolationProviderSelectorTests` (selects over providers built from these descriptors),
`InMemoryIsolationProviderTests` (the deterministic double's lifecycle), and
`RuntimeHostProtocolMapperTests` (round-trips the contracts workload types through the protocol mapper).

## Related documentation

- [10-system/04-solution-map.md](../../10-system/04-solution-map.md) — where this project sits in both
  solutions.
- [10-system/03-components.md](../../10-system/03-components.md) — the `RuntimeHostProviderClient` seam.
- [20-security/04-workload-authorization.md](../../20-security/04-workload-authorization.md) — the signed
  authorization record.
- [20-security/08-provider-certification.md](../../20-security/08-provider-certification.md) — certification
  records and capability gating.
- [../wire-protocols.md](../wire-protocols.md) and [../status-and-error-codes.md](../status-and-error-codes.md).

## Sources

`src/CSweet.Office.Runtime.Abstractions/CSweet.Office.Runtime.Abstractions.csproj`,
`src/CSweet.Office.Runtime.Abstractions/IsolationModels.cs`,
`src/CSweet.Office.Runtime.Abstractions/IsolationPorts.cs`,
`src/CSweet.Office.Runtime.Abstractions/IsolationProviderCatalog.cs`,
`src/CSweet.Office.Node/Program.cs`, `Directory.Build.props`.

Verified: 2026-09-15.
