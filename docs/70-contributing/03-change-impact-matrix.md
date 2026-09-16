# Change impact matrix

**Audience:** everyone changing code, scripts, or configuration. Read the row for your change before you open
the pull request.

Each row answers one question: if this changes, what else must move with it? "Certification" means the change
alters the provider's identity, the guest image, or the broker protocol, so the smoke path in
[../60-release/03-certification.md](../60-release/03-certification.md) must run again before a release can be
built. "Guest image" means the next Windows development run rebuilds the cached image, because the bundle
fingerprint covers the file.

| If you change | Guest image | Payload | Certification | Release notes | Contracts bump | Docs to update |
|---|---|---|---|---|---|---|
| `CSweet.Office.Contracts` (the package, or the pin in `Directory.Packages.props`) | Yes, when guest-visible types change | Yes | Yes, when guest binaries or the broker contract change | Yes | Yes — bump, pack, pin in both repositories | [../60-release/01-versioning-and-compatibility.md](../60-release/01-versioning-and-compatibility.md), `docs/50-development/03-contracts-dependency.md` |
| Anything under `src/CSweet.Office.Runtime.Protocol` | Yes — fingerprint root | Yes | Yes, when the broker contract changes | Only when behavior is user-visible | No | `docs/20-security/05-local-rpc-boundary.md`, [../60-release/03-certification.md](../60-release/03-certification.md), `docs/50-development/09-guest-image-changes.md` |
| `src/CSweet.Office.RuntimeGuest`, `BuilderGuest`, or `ToolchainGuest` | Yes — fingerprint roots | Yes | Yes | Yes when placement or isolation changes | No | `docs/30-workloads/06-guest-images.md`, `docs/20-security/07-guest-isolation.md` |
| `RuntimeHost`, `Node`, `Configurator`, or a helper executable | No | Yes | Only if the provider configuration or broker protocol version changed | Yes when operator-visible | No | `docs/10-system/03-components.md`, `docs/20-security/09-helper-protocol.md`, [../60-release/02-payload-and-manifest.md](../60-release/02-payload-and-manifest.md) |
| A provider capability or descriptor | No | No | Yes — certification identity includes provider id, version, OS, and architecture | Yes when placement changes | No | `docs/20-security/08-provider-certification.md`, `docs/30-workloads/07-provider-backends.md` |
| The broker protocol version | Yes | Yes | Yes | Yes | Maybe | `docs/30-workloads/04-guest-broker-protocol.md`, [../60-release/02-payload-and-manifest.md](../60-release/02-payload-and-manifest.md) |
| A helper's operation set | No | Yes — helpers ship inside the payload | No, but re-run the smoke path | Yes | No | `docs/20-security/09-helper-protocol.md` (`SEC-INV-20`), `docs/80-reference/scripts.md` |
| Installer scripts (`scripts/windows/*.ps1`, `scripts/linux/*.sh`, `scripts/macos/*.sh`) | No | Yes for the scripts the payload embeds; the MSI stages seven PowerShell files | No | Yes when installation changes | No | `docs/40-operations/*`, [../60-release/02-payload-and-manifest.md](../60-release/02-payload-and-manifest.md) |
| The runtime manifest schema | No | Yes — regenerate every platform manifest | No, but evidence fields feed it | Yes | No | [../60-release/02-payload-and-manifest.md](../60-release/02-payload-and-manifest.md) |
| `appsettings.json` shape or defaults | No | Yes — the file is part of the RuntimeHost publish output and its digest is listed | No | Yes when defaults change | No | `docs/80-reference/configuration.md`, [../60-release/02-payload-and-manifest.md](../60-release/02-payload-and-manifest.md), `SEC-INV-11` |
| `build/linux-firecracker/provision-guest.sh` | Yes — the Linux image is rebuilt from a staged root | Yes | Yes | Yes | No | `docs/30-workloads/06-guest-images.md`, `docs/20-security/07-guest-isolation.md` |
| `VersionPrefix` in `Directory.Build.props` | Yes — the file is fingerprinted, so a version bump invalidates the cached image | Yes | Yes, before the release | Yes — `releases/<version>.md` must be added | No | [../60-release/01-versioning-and-compatibility.md](../60-release/01-versioning-and-compatibility.md), [../60-release/06-release-notes-process.md](../60-release/06-release-notes-process.md) |
| `.github/workflows/release.yml` | No | No | No | Yes when runner, secret, or variable requirements change | No | [../60-release/04-release-pipeline.md](../60-release/04-release-pipeline.md), [../60-release/05-signing-and-provenance.md](../60-release/05-signing-and-provenance.md) |

## Notes per row

### `CSweet.Office.Contracts`

The package is the shared cross-repository surface, so a change has two halves. Bump the package with semantic
versioning, pack it, then update both this repository and C-Sweet to the same released pin, and verify both with
local project references disabled (`AGENTS.md`). The tests that pin the mapping between contracts and this
repository's workload model are `OfficeTests.WorkloadMapperRoundTripsSharedContract` and
`RuntimeHostProtocolMapperTests` (`RuntimeRoundTrip_PreservesBoundedSpec`,
`ToProtocol_RejectsMismatchedArtifactBinding`, `FromProtocol_RejectsRepositoryCredentials`,
`ToolchainRoundTrip_PreservesExactBuildIdentityAndBounds`). A guest-visible change also means new guest
binaries, so rebuild the image and re-certify before rebuilding the payload.

### `src/CSweet.Office.Runtime.Protocol`

This directory is part of the bundle fingerprint, so any edit forces a guest rebuild even when the guest
binaries would not have changed. The HMAC authenticator lives here; its behavior is pinned by
`RuntimeHostAuthenticationTests` (`Validate_AcceptsSignedEnvelopeOnce`, `Validate_RejectsChangedBody`,
`Validate_RejectsExpiredEnvelope`) and `OfficeTests.RuntimeHostAuthenticationAcceptsSignedEnvelopeOnlyOnce`.
`SEC-INV-13` and `SEC-INV-14` apply.

### Guest projects

All three guest executables are embedded in the same image, so a change to any of them invalidates the image,
the certification, and the payload, in that order. Pinned by `WorkspaceEnvironmentTests` (`SEC-INV-21`),
`GuestArtifactMaterializerTests` (`SEC-INV-22`), `ToolchainGuestSecurityTests`, and `GuestLocalBrokerProxyTests`.

### Host projects and helpers

Host code is deliberately absent from the fingerprint, so the cached guest image stays valid; the payload must
still be regenerated before any installation, because the installer consumes published executables rather
than build output. Pinned by `WindowsHyperVOnboardingTests` (installer and helper surface),
`RuntimeHostRpcIntegrationTests` (RPC boundary), `MaintenanceHandoffTests` and `OfficeMaintenanceStateTests`
(maintenance service), `OfficeWorkerFailureTests` (bounded failure reporting), and `FirecrackerHelperSecurityTests`
(the Firecracker helper's jailer arguments and reaper).

### Provider capability or descriptor

`IsolationProviderCatalog` builds the descriptors; `FailClosedIsolationProviderSelector` matches a probe's
descriptor against the registered one and then matches the certification against the probe, comparing
`providerId`, `providerVersion`, `hostOperatingSystem`, and `hostArchitecture`. Changing any of those four
values makes every existing certification record inapplicable, so the provider must be re-certified. A
capability change can also change whether a request is satisfiable at all, which is why the release note covers
placement. Pinned by `IsolationProviderSelectorTests` and `PlatformIsolationBackendTests`.

### Broker protocol version

The version appears in the payload manifests (`brokerProtocolVersion`), in the provider options
(`BrokerProtocolVersion`), in the certification record, and in the selection request, and
`ExternalPlatformIsolationBackend` refuses a workload whose expected image digest does not match the
configured one. Moving the version re-opens all four surfaces, so re-certify. Pinned by
`CertifiedGuestImageRegistryTests.ResolveAsync_RejectsCertifiedImageFromAStaleGuestContract` and
`RuntimeHostProtocolMapperTests`.

### A helper's operation set

`args[--operation]` is checked against a fixed allow-list: eight operations in
`src/CSweet.Office.Runtime.HyperV.Helper/HelperArguments.cs`, and the same eight plus `open-guest-channel` in
the Firecracker helper. Adding an operation widens a privileged surface, so `SEC-INV-20` applies and the change
needs the review described in [04-review-checklist.md](04-review-checklist.md). Pinned by
`WindowsHyperVOnboardingTests.HelperArguments_RejectUnknownOperations` and
`FirecrackerHelperSecurityTests.ArgumentsAllowOnlyTheFixedTypedProtocolSurface`.

### Installer scripts

The MSI build refuses to run unless all seven staged scripts exist (`Install-CSweetOffice.ps1`,
`Install-CSweetOfficeRuntimeHost.ps1`, `CSweet.WindowsSetupProgress.ps1`, `Get-CSweetOfficeRecoveryState.ps1`,
`Enter-CSweetOfficeMaintenance.ps1`, `Remove-CSweetOfficeForRecovery.ps1`, `Uninstall-CSweetOffice.ps1`), and
the Linux and macOS packagers require `configure-office.sh` plus the install and uninstall scripts. Renaming or
moving a script breaks packaging and the script-content tests that read it by path. Pinned by
`WindowsHyperVOnboardingTests` and `LinuxInstallationTests`.

### Runtime manifest schema

Changing a field means changing all four producers and consumers together:
`src/CSweet.Office.Runtime.Core/PlatformRuntimePayloadManifest.cs`,
`scripts/windows/New-CSweetWindowsRuntimePayload.ps1`, both `new-runtime-payload.sh` scripts, and
`scripts/windows/runtime-manifest.example.json`, plus the installer that reads the Windows variant. The
schema version constant is `1` on both the producer and the validator; treat any change as a coordinated
release. Pinned by `PlatformRuntimePayloadManifestTests`.

### `appsettings.json` shape

`src/CSweet.Office.RuntimeHost/appsettings.json` is the template that ships in the payload;
`Install-CSweetOfficeRuntimeHost.ps1` writes the operational file into the installed version directory on
Windows, and the Linux and macOS units inject configuration through environment variables
(`CSweet__Office__Providers__Firecracker__PayloadManifestPath`,
`CSweet__Office__Providers__AppleVirtualization__PayloadManifestPath`). Numeric clamps are covered by
`SEC-INV-11`: widening a range silently is a security change. Pinned by `RuntimeHostAuthenticationTests`,
`OfficeTests.LocalEndpointUsesBrandedV1Identity`, and `WindowsHyperVOnboardingTests`.

### `build/linux-firecracker/provision-guest.sh`

This script is the Linux guest's provisioning contract: workload user, boot entry point, masked network
services, `fstab`, and the `NoNewPrivileges` decision. There is no fingerprint cache on the Linux path, so a
change takes effect on the next image build — which is exactly why it still needs a re-certification before
release. Detail in `docs/30-workloads/06-guest-images.md`; isolation behavior in
`docs/20-security/07-guest-isolation.md`.

### `VersionPrefix`

A version bump is a release action: the tag must match, `releases/<version>.md` must exist, and the payload's
`officeVersion` follows the published Node executable, so the payload must be rebuilt for the number to change.
The Windows guest cache keys on `Directory.Build.props`, so the next development run rebuilds the image. See
[../60-release/01-versioning-and-compatibility.md](../60-release/01-versioning-and-compatibility.md).

### The release workflow

Changes here alter which runners, environments, secrets, and variables a release needs, and which assets end up
on a GitHub Release. Anything touching signing inputs is covered by `AGENTS.md`: never publish or sign from an
ordinary development runner.

## Using this matrix

- If a row says "Yes" for a column you have not touched, the change is not finished.
- Rows compose. A change to the broker protocol version on a guest project is three "Yes" cells in two rows,
  and the ordering is image, then certification, then payload.
- When you add a row: name the file or directory precisely, and state the consequence, not the category.

## Sources

`AGENTS.md`, `Directory.Build.props`, `Directory.Packages.props`, `src/CSweet.Office.Runtime.Abstractions/{IsolationModels.cs,IsolationProviderCatalog.cs}`,
`src/CSweet.Office.Runtime.Core/{FailClosedIsolationProviderSelector.cs,ExternalPlatformIsolationBackend.cs,PlatformRuntimePayloadManifest.cs}`,
`src/CSweet.Office.Runtime.HyperV.Helper/HelperArguments.cs`, `src/CSweet.Office.Runtime.Firecracker.Helper/HelperArguments.cs`,
`scripts/windows/{New-CSweetWindowsRuntimePayload.ps1,New-CSweetOfficeMsi.ps1,Install-CSweetOfficeRuntimeHost.ps1}`,
`scripts/linux/new-runtime-payload.sh`, `scripts/macos/new-runtime-payload.sh`, `build/linux-firecracker/provision-guest.sh`,
`.github/workflows/release.yml`, `tests/CSweet.Office.Tests/*`, `docs/20-security/11-security-invariants.md`.

Verified: 2026-09-15.
