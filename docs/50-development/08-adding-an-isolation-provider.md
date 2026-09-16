# Adding an isolation provider

**Audience:** an engineer adding a fourth backend (or a second backend for an already-supported host OS).

The Office selects providers fail-closed: a provider that is not registered, not probed, or not certified
is never returned, and nothing falls back to a weaker mechanism (`SEC-INV-10`). This page is the ordered
checklist. It is derived from `IsolationPorts.cs`, `IsolationModels.cs`, `IsolationProviderCatalog.cs`,
`ExternalPlatformIsolationBackend.cs`, `RuntimeHost/Program.cs`, `Node/Program.cs`, and
`IsolationProviderSelectorTests.cs`.

## What the contracts require

| Type | Requirement |
|---|---|
| `IAgentIsolationProvider` | `Descriptor`, `ProbeAsync`, `CreateAsync`, `StartAsync`, `InspectAsync`, `StopAsync`, `DestroyAsync`, `StreamLogsAsync`. `IPlatformIsolationBackend` extends it and adds nothing. |
| `IPlatformWorkloadReaper` | Optional fail-safe cleanup that must not depend on the control plane; RuntimeHost sweeps it every minute. |
| `IPlatformGuestChannelConnector` | `ProviderId` plus `OpenGuestChannelAsync`. The dispatcher requires a connector whose `ProviderId` equals the backend's. |
| `IsolationProviderDescriptor` | `ProviderId`, `DisplayName`, `ProviderVersion`, `HostOperatingSystem`, `HostArchitecture`, `Priority`, `Capabilities`. |
| `IsolationProviderProbeResult` | `IsAvailable` plus a non-null `Certification` for selection to succeed. |
| `IsolationProviderCertification` | `ProviderId`, `ProviderVersion`, `HostOperatingSystem`, `HostArchitecture`, `GuestImageDigest`, `BrokerProtocolVersion`, `CertificationSuiteVersion`, `EvidenceDigest`, `CertifiedAt`, optional `ExpiresAt` and `RevokedAt`. |

Two rules the selector enforces that are easy to miss:

1. Probe identity must equal the registered descriptor identity (`ProviderId`, `ProviderVersion` exact;
   operating system and architecture case-insensitive).
2. Certification must match the descriptor, the requested `GuestImageDigest` when one is pinned, and the
   requested `BrokerProtocolVersion`, and must be active at the current time (`RevokedAt is null` and not
   expired).

The assurance floor is `IsolationAssurance.CertifiedHardwareVirtualMachine`, applied to every request
regardless of trust level. The default `IsolationCapabilityRequirements` additionally require a dedicated
kernel, a broker socket, a read-only base disk, a read-only artifact device, an ephemeral writable disk,
CPU/memory/disk limits, and no network device. `SupportsSecureBoot` and `SupportsMeasuredOrVerifiedBoot`
are only required when a request asks for them.

## The checklist

### 1. Catalog entry — `src/CSweet.Office.Runtime.Abstractions/IsolationProviderCatalog.cs`

Add a `public static IsolationProviderDescriptor YourPlatform(string? architecture = null)` factory that
follows the shape of `HyperV()`, `Firecracker()`, and `AppleVirtualization()`. Choose:

- `ProviderId`: lowercase, hyphenated, stable — it is part of the signed authorization, the handle, and
  every log line.
- `ProviderVersion`: bump it when provider behavior changes; the certification and the payload manifest
  both bind to this exact string.
- `HostOperatingSystem` and `HostArchitecture` (lowercase, from `RuntimeInformation.ProcessArchitecture`).
- `Priority`: only breaks ties between equal assurance levels.
- `Capabilities`: the exact flags in `IsolationProviderCapabilities`. Set only what the platform really
  provides; the selector will reject a provider whose flags do not satisfy the request.

### 2. Options and backend class

Add a `PlatformIsolationBackendOptions` subclass next to the existing provider projects (for example
`src/CSweet.Office.Runtime.YourPlatform/YourPlatformIsolationBackend.cs` plus an options class), and
derive from `src/CSweet.Office.Runtime.Core/ExternalPlatformIsolationBackend.cs`:

- Pass `descriptor`, your options, and `TimeProvider` to the base constructor.
- Implement `IsHostPlatform(out string unavailableReason)`.
- Implement `IPlatformWorkloadReaper` and call the protected `ReapAbandonedWorkloadsAsync` if the helper
  can decide what to reap (both existing C# backends do; the Apple backend reaps only runtime workloads).
- Do not re-implement the helper invocation, digest re-verification, or the response size limits; the base
  class owns them, including the `MaximumHelperResponseBytes` bound and the `HelperTimeoutSeconds` clamp.

### 3. Helper executable

Create a new project following the existing helper pattern (`CSweet.Office.Runtime.HyperV.Helper`,
`CSweet.Office.Runtime.Firecracker.Helper`, or the Swift helper). The helper is the privileged surface
that `SEC-INV-20` constrains:

- Speak protocol `1.0` over stdio: `--protocol 1.0 --operation <operation>`, one typed JSON request in,
  one typed JSON response out.
- Support the fixed operation set: `probe`, `create`, `start`, `inspect`, `stop`, `destroy`, `reap`,
  `logs`. Add `open-guest-channel` only if you use the shared stdio connector.
- Never accept a device, host path, mount option, or command from the request; never exit non-zero for a
  typed failure; never interpolate request data into shell text.
- Advertise `guestChannelTransport` in the probe response only with the transport the certification
  covers, and match `RequiredGuestChannelTransport` exactly.

### 4. Guest channel connector

Two supported shapes:

- Reuse `src/CSweet.Office.Runtime.Core/ExternalPlatformStdioGuestChannelConnector.cs` by constructing it
  with your provider id and settings, and set
  `RequiredGuestChannelTransport = ExternalPlatformStdioGuestChannelConnector.TransportName`
  (`stdio-duplex-v1`). The helper then owns the vSock/virtio handshake.
- Write a bespoke connector like `WindowsHyperVSocketTransport` if the host owns the guest channel
  (`AF_HYPERV` is the reason that one exists).

The probe fails when no connector with the same `ProviderId` is registered, so this is not optional.

### 5. Registration in both executables

| File | Change |
|---|---|
| `src/CSweet.Office.RuntimeHost/Program.cs` | Read `CSweet:Office:Providers:<Name>`, call `PlatformRuntimePayloadManifest.ApplyIfConfigured(yourOptions, IsolationProviderCatalog.YourPlatform())`, register the options, the `IPlatformIsolationBackend`, and the `IPlatformGuestChannelConnector` in the right operating-system branch. |
| `src/CSweet.Office.RuntimeHost/HostPlatformProvider.cs` | Resolve your descriptor for the platform. |
| `src/CSweet.Office.Node/Program.cs` and `src/CSweet.Office.Node/HostPlatformProvider.cs` | The unprivileged client resolves the same descriptor and registers a `RuntimeHostProviderClient` for it. |

Both `HostPlatformProvider` classes currently return exactly **one** provider per host operating system.
Adding a second provider for an already-supported OS is a registration change in four files, not a
one-line catalog addition.

### 6. Configuration section — `src/CSweet.Office.RuntimeHost/appsettings.json`

Add a `CSweet:Office:Providers:<Name>` object shaped like the existing ones. On an installed system the
file paths, digests, and certification window are filled from `runtime-manifest.json` when
`PayloadManifestPath` is configured; the shipped `appsettings.json` keeps them empty. See
[80-reference/configuration.md](../80-reference/configuration.md) for the full key list.

### 7. Release scripts and the runtime manifest

Extend or copy a payload script: `scripts/windows/New-CSweetWindowsRuntimePayload.ps1`,
`scripts/linux/new-runtime-payload.sh`, `scripts/macos/new-runtime-payload.sh`. The manifest they emit is
validated by `src/CSweet.Office.Runtime.Core/PlatformRuntimePayloadManifest.cs`:

- `schemaVersion` must be `1`, and `providerId`, `providerVersion`, `hostOperatingSystem`, and
  `hostArchitecture` must match the descriptor exactly.
- `helperExecutable`, `guestImage`, `guestImageSignature`, `guestImageSigningCertificate`, and
  `certificationEvidence` must all appear in `files`.
- `files` needs at least 5 entries; every path is relative, control-character-free, inside the package
  root, and free of symbolic links; every digest is lowercase SHA-256 (`sha256:` prefix or bare).
- `guestImageDigest` and `certificationEvidenceDigest` must match the declared files, and
  `guestImageSigningCertificateThumbprint`, `certificationSuiteVersion`, and `certifiedAt` are mandatory.

### 8. Certification evidence

Nothing may be selected without an active certification, so the provider needs a real isolation run that
emits evidence. `src/CSweet.Office.WindowsSmokeTest/Program.cs` is the only in-repo producer, and it
accepts exactly two providers (`--provider must be hyperv or firecracker.`). Adding a third means adding a
provider arm there plus the guest-side probe path, and then following
[60-release/03-certification.md](../60-release/03-certification.md) for the evidence format and window.

### 9. Tests

| Test | Change |
|---|---|
| `tests/CSweet.Office.Tests/IsolationProviderSelectorTests.cs` | Add cases for your provider: certified and available selects; certification for a different guest image is rejected; unavailable preferred provider does not fall back. |
| `tests/CSweet.Office.Tests/PlatformIsolationBackendTests.cs` | Add a fail-closed probe test (nothing installed ⇒ `IsAvailable == false`, `Certification == null`). |
| `tests/CSweet.Office.Tests/ExternalPlatformStdioGuestChannelConnectorTests.cs` | Extend if you use the shared connector; the framing and transport assertions are transport-level and usually reusable. |
| `tests/CSweet.Office.Tests/RuntimeHostRpcIntegrationTests.cs` | Extend the dispatcher cases if your provider needs lifecycle coverage at the RPC layer. |

### 10. Documentation

- `docs/30-workloads/07-provider-backends.md` — add a column to the side-by-side table and a section.
- `docs/20-security/06-host-privilege-model.md`, `07-guest-isolation.md`, and
  `docs/50-development/09-guest-image-changes.md` — the host privilege additions, the in-guest device or
  mount changes, and the new guest build inputs.
- `docs/60-release/04-release-pipeline.md` if the payload or asset set changes.

## Summary table

| Step | File |
|---|---|
| Catalog entry and capabilities | `src/CSweet.Office.Runtime.Abstractions/IsolationProviderCatalog.cs` |
| Options + backend | new `src/CSweet.Office.Runtime.<Name>/*.cs` |
| Helper | new `src/CSweet.Office.Runtime.<Name>.Helper/**` |
| Guest channel | reuse `ExternalPlatformStdioGuestChannelConnector` or add a connector project |
| Service registrations | `src/CSweet.Office.RuntimeHost/{Program,HostPlatformProvider}.cs`, `src/CSweet.Office.Node/{Program,HostPlatformProvider}.cs` |
| Provider configuration section | `src/CSweet.Office.RuntimeHost/appsettings.json` |
| Payload and manifest | `scripts/{windows,linux,macos}/…` payload script |
| Certification | `src/CSweet.Office.WindowsSmokeTest/Program.cs` plus the guest probe |
| Tests | `tests/CSweet.Office.Tests/*` |

> **Out of repo:** the provider id must also be understood by Headquarters placement. The Office only
> reports what it has: `src/CSweet.Office.Node/ProviderInventory.cs` builds `RegisterOfficeProviderRequest`
> (a `CSweet.Office.Contracts.ControlPlane` type) from the probe, and Headquarters decides which office and
> provider a workload goes to. In addition, `CSweet.Office.Contracts` may need to change — the signed
> workload specification is deserialized into `CSweet.Office.Contracts.Workloads` types by
> `RuntimeHostProtocolMapper`, so a provider that needs new resource limits, device parameters, or
> specification fields requires a package change and the pin workflow in
> [03-contracts-dependency.md](03-contracts-dependency.md). Provider ids themselves are strings on the wire
> and need no contract change.

## Sources

`src/CSweet.Office.Runtime.Abstractions/{IsolationPorts.cs,IsolationModels.cs,IsolationProviderCatalog.cs}`,
`src/CSweet.Office.Runtime.Core/{ExternalPlatformIsolationBackend.cs,ExternalPlatformStdioGuestChannelConnector.cs,FailClosedIsolationProviderSelector.cs,PlatformRuntimePayloadManifest.cs}`,
`src/CSweet.Office.RuntimeHost/{Program.cs,HostPlatformProvider.cs,RuntimeHostWorkloadReaper.cs}`,
`src/CSweet.Office.Node/{Program.cs,HostPlatformProvider.cs,ProviderInventory.cs}`,
`src/CSweet.Office.Runtime.LocalRpc/RuntimeHostRequestDispatcher.cs`,
`src/CSweet.Office.WindowsSmokeTest/Program.cs`, `tests/CSweet.Office.Tests/IsolationProviderSelectorTests.cs`,
`tests/CSweet.Office.Tests/PlatformIsolationBackendTests.cs`, `docs/30-workloads/07-provider-backends.md`.

Verified: 2026-09-15.
