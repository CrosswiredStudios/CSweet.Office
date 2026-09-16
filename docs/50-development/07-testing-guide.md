# Testing guide

**Audience:** anyone writing or relying on a test in `tests/CSweet.Office.Tests`, or asking "is this
covered?".

One flat xUnit project, 25 test classes, no external services. That is enough to pin the security-critical
managed behavior and almost nothing about what actually runs on a host. This page says which is which.

## Conventions

- One class per subject, `sealed class`, no fixtures beyond `IDisposable` for temporary directories.
- Test names are `Thing_Behaviour` (for example `SelectAsync_DoesNotFallbackWhenPreferredProviderIsUnavailable`)
  or `Thing_Condition` for theories.
- Fakes are hand-rolled nested classes (`FakeProvider`, `TestGuestConnector`, `FixedTimeProvider`,
  `FailingBackend`) rather than a mocking library; no mocking package is referenced.
- Time is injected. `TimeProvider` appears as a constructor dependency of the selector, the backends, and
  the certificate validator, and tests substitute a fixed clock.
- `InternalsVisibleTo("CSweet.Office.Tests")` is declared by the projects whose internals the suite asserts
  on, so tests do not force public API growth.

## Tests that assert on script text

`LinuxInstallationTests` and `WindowsHyperVOnboardingTests` read repository scripts with `File.ReadAllText`
and assert on substrings. This is intentional: the scripts are the deliverable, and CI runs on Linux with
no Hyper-V, no Administrator token, and no package manager. The price is important:

- A text assertion passes against a *broken but present* script. If `Install-CSweetOfficeRuntimeHost.ps1`
  still contains `StartName = "NT SERVICE\$runtimeHostServiceName"` but stops setting it at runtime, the
  test is green.
- A refusals-and-gates test (for example `LinuxInstallationTests.Installer_IsBoundToSupportedUbuntuAndHardwareVirtualization`)
  proves the check string exists in the file, not that the check executes.
- Any rewording of an asserted literal breaks the test even when behavior is unchanged. Change the script
  and the assertion together, in one commit.

`WindowsHyperVOnboardingTests` is mixed. It also exercises managed code directly — `PowerShellHyperV`
output sanitizing, helper argument parsing, host-probe edition and feature-state parsing, vsock address
layout, and the provisioning progress store — and those are real behavior tests.

## Tests that return early off Windows

Three tests are effectively no-ops on Linux and macOS:

| Test | Guard |
|---|---|
| `RuntimeHostRpcIntegrationTests.WindowsPipeSecurity_GrantsExactDuplexClientConnectionRights` | `[SupportedOSPlatform("windows")]` plus `if (!OperatingSystem.IsWindows()) return;` |
| `WindowsHyperVOnboardingTests.HyperVPowerShellDiagnostic_UsesPlainTextForRedirectedErrors` | `if (!OperatingSystem.IsWindows()) return;` |
| `WindowsHyperVOnboardingTests.UpgradeProbeDistinguishesPreservedStateFromExecutingWork` | `if (!OperatingSystem.IsWindows()) return;` |

There is no `SkippableFact`, no `[Trait]`, and no `Skip =` anywhere, so these show up as passed in the
Linux CI run. Do not read a green CI build as evidence that the Windows pipe ACL or the upgrade-probe
harness was executed.

## The upgrade-probe harness

`scripts/tests/Test-OfficeUpgradeProbe.ps1` is the only PowerShell test harness in the repository. It
tests the recovery/upgrade state machine of `scripts/windows/Get-CSweetOfficeRecoveryState.ps1` without a
Hyper-V host.

What it stubs, in its own script scope:

| Stub | Replaces |
|---|---|
| `Get-Service` | Returns a synthetic service object. |
| `Get-CimInstance` | Returns a `PathName` pointing inside the temporary install root. |
| `Get-Module`, `Import-Module` | Make Hyper-V module availability a no-op. |
| `Get-VM`, `Get-VMHardDiskDrive` | Return `$global:officeProbeTestVms`, a mutable fake VM list. |

Fixtures it builds in a temporary root: an `appsettings.json` with the Node state directory and the
RuntimeHost authorization state directory, `node\maintenance\drain-state`, and
`authorization\authorized-workload-handles.json` containing one workload-handle record.

The eight scenarios, in order:

| # | State | Expected |
|---|---|---|
| 1 | VM `Off`, drain state `draining`, recognized provider handle | `clean` (upgrade allowed) |
| 2 | Same state, probed with `-ForUpgrade:$false` | `active` |
| 3 | VM `Running` | `active` |
| 4 | VM `Saved` | `active` |
| 5 | No VMs registered | `clean` |
| 6 | `active-assignments\work.active` present | `active` |
| 7 | Marker removed, drain state `ready` | `active` |
| 8 | Drain state `draining` again, handle record for an unrecognized provider id | `unsafe` |

Run it directly:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\tests\Test-OfficeUpgradeProbe.ps1
```

Success prints `Passed 8 upgrade probe scenarios.` and exits `0`; a mismatch throws with the expected and
actual values. The temp root is always removed, and the harness refuses to clean up anything outside the
system temp directory. On Windows it is also executed by
`WindowsHyperVOnboardingTests.UpgradeProbeDistinguishesPreservedStateFromExecutingWork` with a 30-second
timeout.

## Scenarios that can only be tested manually

| Scenario | Why | How |
|---|---|---|
| Hyper-V VM lifecycle end to end | Needs Windows, Hyper-V, a build switch, and Administrator rights; the script self-elevates and cannot run in CI. | [04-windows-dev-loop.md](04-windows-dev-loop.md) |
| Firecracker VM lifecycle end to end | Needs Linux root, `/dev/kvm` read/write, cgroup v2, and the jailer. | [05-linux-dev-loop.md](05-linux-dev-loop.md) |
| Apple Virtualization | Needs macOS 14, a signed helper with the virtualization entitlement, and a guest image and evidence that this repository does not build. | [06-macos-dev-loop.md](06-macos-dev-loop.md) |
| Service installation and removal | Windows services, systemd units, and launchd plists all need Administrator/root and mutate the host. | `Install-CSweetOffice*.ps1`, `scripts/linux/install-office.sh`, `scripts/macos/install-office.sh` |
| MSI production | Needs WiX Toolset v4 and Windows SDK `signtool`. | `scripts/windows/New-CSweetOfficeMsi.ps1` |
| Identity-preserving upgrade and drain | Needs an installed, enrolled Office and Headquarters state. | [40-operations/04-upgrade-and-drain.md](../40-operations/04-upgrade-and-drain.md); the state *classification* is the part the harness above does cover. |
| Enrollment, assignment delivery, revocation | Needs a real Headquarters; cross-repository messages are not tested here. | C-Sweet test environments |
| End-to-end agent import and build | Needs a live C-Sweet instance; the helper script drives HTTP APIs that do not exist in this repository. | `scripts/office-e2e.ps1 -RepositoryUrl <repo>` |

## What each test class pins

| Test class | Pins |
|---|---|
| `AgentArtifactMediaStoreTests` | Content-addressed artifact ISO creation and digest re-verification; tampered media rejected (`SEC-INV-22`). |
| `CertifiedGuestImageRegistryTests` | Image identity resolved from the active certification; optional configured digest pin honoured; certification from a stale guest contract rejected (`SEC-INV-10`). |
| `ControlPlaneServerCertificateValidatorTests` | Headquarters pin acceptance, hostname enforcement, and expiry; a matching pin never overrides a hostname mismatch. |
| `ExternalPlatformStdioGuestChannelConnectorTests` | Handshake reads stop at the newline without consuming broker bytes; oversized or ambiguous framing rejected; wrong provider and control characters rejected; only the certified transport is accepted. |
| `FirecrackerHelperSecurityTests` | Fixed argument surface; jailer namespaces and hard limits without networking; protected-path traversal rejected; vsock handshake bounds and byte preservation; reaper selects only expired runtime instances; strict tool version parsing (`SEC-INV-20`). |
| `GuestArtifactMaterializerTests` | Only provider-owned fixed devices are mountable; arbitrary guest paths rejected; artifact root stays root-owned and group-readable (`SEC-INV-22`). |
| `GuestLocalBrokerProxyTests` | Bounded chunked JSON accepted; conflicting body framing rejected; responses reframed without hop-by-hop headers. |
| `HyperVInstanceReapingTests` | Reaping retains a running VM with an active lease, reaps expired or powered-off runtime instances, gives new instances a grace period, reaps legacy instances without a lease, and never reaps a builder VM. |
| `InMemoryIsolationProviderTests` | The test double's lifecycle stays deterministic and `Destroy` is final; duplicate workload identity rejected. |
| `IsolationProviderSelectorTests` | Shared-kernel providers rejected at every trust level; providers without certification rejected; certifications bound to a different image rejected; highest assurance wins; digest discovery without a pin; no fallback when a preferred provider is unavailable (`SEC-INV-10`). |
| `LinuxInstallationTests` | Script text: Ubuntu/cgroup/`/dev/kvm` gates, writable-path rejection, symlink-free packages, informational baseline labels, and no enrollment token on the command line. |
| `MaintenanceHandoffTests` | Only predefined maintenance actions are accepted; malformed handoff values fail closed (`SEC-INV-19`). |
| `OfficeCertificateRecoveryTests` | Expired or superseded certificates recover with key proof and persist the replacement; a rejected certificate never overwrites the durable identity (`SEC-INV-02`, `SEC-INV-03`). |
| `OfficeCertificateTlsTests` | New TLS connections use the renewed certificate while the server pin is still validated (`SEC-INV-04`, `SEC-INV-05`). |
| `OfficeMaintenanceStateTests` | Drain state is durable and resumable; assignment markers track work and are cleared for a new process session. |
| `OfficeTests` | Node advertises canonical assignment digest support; development posture requires explicit Office consent; branded endpoint identity; signed envelope accepted once; workload mapping round-trips; drain and assignment markers durable; enrollment token file deleted only after state is saved; artifacts closed before hash verification and commit; workload tunnel opened before waiting for the guest payload. |
| `OfficeWorkerFailureTests` | Headquarters broker failures stay bounded and actionable; unexpected exception text is not exposed; isolation failures stay actionable. |
| `PlatformIsolationBackendTests` | All three backends fail closed — unavailable with no certification — when nothing is installed (`SEC-INV-10`). |
| `PlatformRuntimePayloadManifestTests` | Only declared, digest-verified files are applied; a file changed after packaging is rejected; wrong provider and traversal paths rejected. |
| `RuntimeHostAuthenticationTests` | Signed envelope accepted once; changed body and expired envelope rejected; bounded, non-reparse shared-key file loading; a key created after startup is loaded. |
| `RuntimeHostProtocolMapperTests` | Runtime and toolchain specifications round-trip with bounds; mismatched artifact binding rejected; repository credentials rejected; registry schemes and ports rejected. |
| `RuntimeHostRpcIntegrationTests` | Windows pipe ACL grants exactly `ReadWrite \| Synchronize \| CreateNewInstance` (`SEC-INV-12`); client and server authenticate and dispatch a typed lifecycle; backend failures return correlated typed errors without closing the pipe; replayed, tampered, expired, and trust-replacing authorizations rejected (`SEC-INV-07`, `SEC-INV-08`, `SEC-INV-09`); forged and expired provider handles rejected (`SEC-INV-15`); a provider without a guest-channel connector fails the probe. |
| `ToolchainGuestSecurityTests` | Offline sources use only the exact broker build reference; existing GitHub sources stay exact archives; extraction rejects traversal and expanded-byte overflow; trusted flat archives preserve repository root files. |
| `WindowsHyperVOnboardingTests` | Helper argument surface and reaping operation; host-probe edition and feature-state parsing; Linux vsock service-id and address layout; guest service keeps the scratch mount in the broker process; installer and configurator script text for service registration, ACL and privilege rules, reconnect and removal staging, artifact-media copying and re-verification, and uninstaller privilege removal (`SEC-INV-16`, `SEC-INV-17`, `SEC-INV-18`, `SEC-INV-19`); plus the upgrade-probe harness. |
| `WorkspaceEnvironmentTests` | Platform workspace limit keys are allowed; `PATH`, `LD_PRELOAD`, and arbitrary keys are rejected (`SEC-INV-21`). |

## Adding a test

- Put behavior tests in the class that already owns the subject; do not create a second class for the same
  type.
- If the behavior is a security invariant, name the invariant in the test or the class so the mapping above
  can stay correct, and update this table in the same change.
- Do not add a mocking package; extend the existing hand-rolled fakes.
- Keep `Verified:` and table edits in one commit with the test — a stale mapping is worse than no mapping.

## Sources

`tests/CSweet.Office.Tests/*.cs` (all 25 classes), `tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj`,
`scripts/tests/Test-OfficeUpgradeProbe.ps1`, `scripts/windows/Get-CSweetOfficeRecoveryState.ps1`,
`scripts/office-e2e.ps1`, `scripts/windows/New-CSweetOfficeMsi.ps1`, `.github/workflows/ci.yml`,
`docs/20-security/11-security-invariants.md`.

Verified: 2026-09-15.
