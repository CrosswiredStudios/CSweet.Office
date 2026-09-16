# CSweet.Office.Runtime.Abstractions

The provider-neutral contract layer: the isolation models, ports, and provider catalog that every other runtime project implements or consumes. Nothing here knows about Hyper-V, Firecracker, Apple Virtualization, or the control plane, and the project sits at the bottom of the dependency graph.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/runtime-abstractions.md`](../../docs/80-reference/projects/runtime-abstractions.md).

**Output:** library
**References:** none

## What it owns

- `IsolationModels.cs` — the shared records and enums: `IsolationAssurance`, `IsolationWorkloadHandle`, `IsolationWorkloadStatus`, `IsolationProviderDescriptor`, `IsolationProviderCertification`, `IsolationProviderProbeResult`, and `SignedWorkloadAuthorization`.
- `IsolationPorts.cs` — the composition surface: `IAgentIsolationProvider`, `IAgentGuestChannelProvider`, `IPlatformGuestChannelConnector`, `IAgentIsolationProviderSelector`, `IRuntimeHostClient`, `IPlatformIsolationBackend`, `IPlatformWorkloadReaper`, and `IGuestImageRegistry`.
- `IsolationProviderCatalog.cs` — the single place provider identity is declared: `HyperV()`, `Firecracker()`, and `AppleVirtualization()`.
- Artifact and result contracts: `IAgentArtifactStore`, `IAgentArtifactMediaStore`, `IAgentArtifactSigner`, and the `IBuilderArtifactResultStore` / `IBuilderArtifactResultPublisher` pair.

## Notes for contributors

- The workload types these contracts reference — `WorkloadKind`, `WorkloadSpecification`, `BrokerChannelLease`, `GuestImageReference`, and the rest — come from the `CSweet.Office.Contracts` package, not from this repository.
- `IRemoteSecureRunnerClient` is a reserved contract boundary with no in-repo implementation; do not add one to "complete" it.
- `IsolationCapabilityRequirements` defaults to the strict set, and only secure boot and measured boot are opt-in. Changing a default changes what every selection request demands.
- This project must not gain a project reference of its own; providers and hosts depend on it, not the other way round.

## Tests

No dedicated test class. `IsolationProviderSelectorTests`, `InMemoryIsolationProviderTests`, and `RuntimeHostProtocolMapperTests` exercise the catalog and the models indirectly.

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
