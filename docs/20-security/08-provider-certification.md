# Provider certification and fail-closed selection

**Audience:** security reviewers; contributors to `Runtime.Abstractions`, `Runtime.Core`, and the provider
backends.

Office will not run work on a provider that is unprobed, uncertified, expired, revoked, or not the provider
that the Office thinks it is. There is no degraded path.

## The provider catalog

`IsolationProviderCatalog` declares three providers. All three declare
`IsolationAssurance.CertifiedHardwareVirtualMachine`.

| Provider | id | Host OS | Priority |
|---|---|---|---|
| Hyper-V Generation 2 | `hyperv-gen2` | `windows` | 300 |
| Firecracker on KVM | `firecracker-kvm` | `linux` | 200 |
| Apple Virtualization.framework | `apple-virtualization` | `macos` | 100 |

Provider version is `1.0.0` for all three. The host architecture is taken from
`RuntimeInformation.ProcessArchitecture` lower-cased unless supplied.

### Capabilities

| Capability | Hyper-V | Firecracker | Apple Virtualization |
|---|---|---|---|
| Dedicated kernel | yes | yes | yes |
| Broker socket | yes | yes | yes |
| Read-only base disk | yes | yes | yes |
| Read-only artifact | yes | yes | yes |
| Ephemeral writable disk | yes | yes | yes |
| CPU limits | yes | yes | yes |
| Memory limits | yes | yes | yes |
| Disk limits | yes | yes | yes |
| Process limits | **no** | yes | yes |
| No network device | yes | yes | yes |
| Secure Boot | **yes** | no | no |
| Measured or verified boot | no | yes | yes |

> In source, the Hyper-V descriptor uses named arguments while Firecracker and Apple pass the twelve
> capability flags positionally. Editing those two entries means counting arguments. Prefer adding names.

## Fail-closed selection

`FailClosedIsolationProviderSelector.SelectAsync` is the only selection path.

### 1. The floor is raised, never lowered

```csharp
// C-Sweet intentionally applies the same certified VM floor to every executable plugin.
// Trust classification remains part of policy so a stronger floor can be introduced later,
// but it can never lower this baseline.
```

Any requested `MinimumAssurance` below `CertifiedHardwareVirtualMachine` is raised to it. A caller cannot ask
for something weaker.

### 2. Candidates are ordered deterministically

Filtered by `PreferredProviderId` when supplied (ordinal comparison), then ordered by:

1. assurance descending,
2. priority descending,
3. provider id ascending (ordinal).

If no candidate is registered at all, it throws immediately with either *"No agent isolation provider is
registered."* or *"The requested isolation provider '<id>' is not registered."*

### 3. Every candidate must pass all four gates

| Gate | Failure message fragment |
|---|---|
| Capabilities satisfy the (raised) requirements | `required isolation capabilities are unavailable` |
| `ProbeAsync().IsAvailable` | the probe's `UnavailableReason`, or `provider probe failed` |
| Probe identity equals the registered descriptor — provider id, version, host OS, and host architecture | `probe identity did not match the registered provider` |
| Certification is non-null, active at `now`, and matches the descriptor plus the requested guest-image digest and broker protocol version | `no active matching certification` |

### 4. Zero survivors is an error, not a fallback

```
IsolationUnavailableException:
  "No certified hardware-backed agent isolation provider is available. " + per-provider failure list
```

The failure list is joined with `; ` and names every provider and why it lost. `RuntimeHostRequestDispatcher.CreateAsync`
surfaces this as `provider-unavailable`; it never substitutes a different provider.

Related invariant: `SEC-INV-10`.

## The certification record

```csharp
public sealed record IsolationProviderCertification(
    string ProviderId, string ProviderVersion, string HostOperatingSystem, string HostArchitecture,
    string GuestImageDigest, string BrokerProtocolVersion, string CertificationSuiteVersion,
    string EvidenceDigest, DateTimeOffset CertifiedAt, DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? RevokedAt = null)
{
    public bool IsActiveAt(DateTimeOffset instant) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > instant);
}
```

A certification with no expiry is valid indefinitely; `RevokedAt` overrides everything. The operational
consequence: **certification expiry is a hard stop, not a warning.** An Office whose certification lapses
stops accepting work until a new payload is installed.

## Evidence verification

`ProviderCertificationEvidence` mirrors the identity fields plus `CertifiedAt` and `CertificationExpiresAt`.
`VerifyCertificationEvidenceAsync` requires:

- The on-disk JSON to equal the configured options **field for field**, including both timestamps and every
  digest.
- File size between 2 bytes and 1 MiB.
- `CertificationEvidenceDigest` to match, canonical lowercase `sha256:`.

`ProbeAsync` additionally fails when `CertifiedAt` or `CertificationSuiteVersion` is unset, and when the
resulting certification is not active — *"The configured provider certification is expired."*

Evidence is not a capability grant. It binds a known provider identity to fixed files; it cannot introduce
new behavior.

## Guest image resolution

`CertifiedGuestImageRegistry` resolves image references **from the selected provider's certification**, not
from control-plane input:

- digest `= certification.GuestImageDigest` (must be canonical lowercase `sha256:` or it is rejected),
- version `= certification.CertificationSuiteVersion` when the request supplies none,
- and it fails with a user-facing message when `RequiredCertificationSuiteVersion` differs:

> *"The installed secure agent runtime is out of date (installed: X; required: Y). Open Agent Execution setup
> and prepare the secure agent runtime before retrying."*

This registry has no call site in this repository. It is the Headquarters-side placement library, covered by
`CertifiedGuestImageRegistryTests`.

## The payload manifest

`PlatformRuntimePayloadManifest` binds a provider to the exact files on disk. `ApplyIfConfigured` runs at
RuntimeHost startup on Linux and macOS (Windows options are written by the installer into `appsettings.json`).

Validation:

| Rule | Detail |
|---|---|
| Path | Absolute, existing, 2 bytes–1 MiB. |
| Link rejection | The manifest and **every path segment** are rejected if they are a reparse point or symlink. |
| Schema | `schemaVersion == 1`. |
| Identity | The declared provider id, version, OS, and architecture must equal the expected descriptor. |
| File list | 5–1000 declared files, each verified by SHA-256 using `FixedTimeEquals`. |
| Declared digests | The guest image digest and evidence digest must match the declared files. |
| Mandatory fields | Signing certificate thumbprint, suite version, and `CertifiedAt`. |

`HelperExecutableDigest` is **computed** during verification, never trusted from the manifest.

## Helper digest and guest image verification at create time

`ExternalPlatformIsolationBackend.ValidateWorkload` forces, on every create:

- `workload.GuestImage.Digest == options.GuestImageDigest`, and
- `workload.BrokerLease.ExpectedGuestImageDigest == options.GuestImageDigest`,
- `BrokerLease.ProtocolVersion == BrokerProtocolVersion`,
- `BrokerLease.ExpiresAt > now`.

The artifact ISO path is derived, not supplied: `ArtifactImageRoot + digest[7..] + ".iso"`, and
`SingleFileIso9660.VerifyArtifactDigestAsync` re-verifies it before the helper is invoked.

The probe additionally requires `GuestChannelTransport == RequiredGuestChannelTransport`, so a provider whose
guest channel is not the certified mechanism fails even if everything else passes.

## Fail-safe cleanup

`RuntimeHostWorkloadReaper` runs a one-minute `PeriodicTimer` and calls
`IPlatformWorkloadReaper.ReapAbandonedWorkloadsAsync`. The interface is deliberately documented as not
depending on the control-plane database, *"which may be unavailable or have been recreated."*

Reaping rules are asymmetric across backends and are documented in
[30-workloads/07-provider-backends.md](../30-workloads/07-provider-backends.md). The asymmetry is a known
limitation — see
[12-known-limits-and-tradeoffs.md](12-known-limits-and-tradeoffs.md).

## Sources

`src/CSweet.Office.Runtime.Abstractions/{IsolationProviderCatalog.cs,IsolationModels.cs,IsolationPorts.cs}`,
`src/CSweet.Office.Runtime.Core/{FailClosedIsolationProviderSelector.cs,CertifiedGuestImageRegistry.cs,PlatformRuntimePayloadManifest.cs,ExternalPlatformIsolationBackend.cs}`,
`src/CSweet.Office.RuntimeHost/{RuntimeHostWorkloadReaper.cs,appsettings.json}`,
`tests/CSweet.Office.Tests/{IsolationProviderSelectorTests.cs,CertifiedGuestImageRegistryTests.cs,PlatformRuntimePayloadManifestTests.cs}`.

Verified: 2026-09-15.
