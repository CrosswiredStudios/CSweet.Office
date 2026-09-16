# Review checklist

**Audience:** reviewers of pull requests, and authors checking their own change before requesting review.

The checklist is organised around the security invariants in
[../20-security/11-security-invariants.md](../20-security/11-security-invariants.md). A diff that touches one of
those properties is not reviewed until the reviewer has looked at the invariant, not just at the code. Where the
invariants page carries a `Pinned by` reference, that test is named here; otherwise the relevant test class is
named, and where no test covers the property at all the cell says so.

## Invariant review table

| Invariant | What to look for in a diff | Pinned by |
|---|---|---|
| [SEC-INV-01](../20-security/11-security-invariants.md#sec-inv-01) — trust pin is write-once | A new or moved pin path; removal of the `Guid.Empty → real OfficeId` transition; a second key id or key byte accepted for an existing Office. | `RuntimeHostRpcIntegrationTests.PrivilegedAuthorizationRejectsTamperingExpiryAndTrustReplacement` |
| [SEC-INV-02](../20-security/11-security-invariants.md#sec-inv-02) — certificate cannot replace identity without validation | Reordering or dropping the expiry, optional thumbprint, or `CopyWithPrivateKey` steps in `OfficeStateStore.InstallOperationalCertificate`; a non-atomic write. | `OfficeCertificateRecoveryTests.RejectedCertificateDoesNotOverwriteDurableIdentity` |
| [SEC-INV-03](../20-security/11-security-invariants.md#sec-inv-03) — recovery sends no expired certificate and no reusable receipt | `certificate/recover` gaining a client-certificate requirement; the enrollment receipt appearing on the recovery call; weakened server validation on challenge or recovery. | `OfficeCertificateRecoveryTests.ExpiredOrSupersededCertificateRecoversWithKeyProofAndPersistsReplacement` |
| [SEC-INV-04](../20-security/11-security-invariants.md#sec-inv-04) — bootstrap detection and TLS resumption unchanged | The `Subject == Issuer` bootstrap test replaced by a flag; the bootstrap/renew/recover split collapsed; `AllowTlsResume` set to true. | `OfficeCertificateTlsTests.NewTlsConnectionsUseRenewedCertificateAndStillValidateServerPin` |
| [SEC-INV-05](../20-security/11-security-invariants.md#sec-inv-05) — retired-certificate retention longer than the handshake timeout | A retention period below two minutes, or a `ConnectTimeout` above it. | `OfficeCertificateTlsTests` exercises the rotating handler; the retention constant itself is not asserted by a test. |
| [SEC-INV-06](../20-security/11-security-invariants.md#sec-inv-06) — control messages bound to Office id and session epoch | A control message that no longer carries both values, or validation that accepts a mismatch. | No automated test; review `src/CSweet.Office.Node/OfficeWorker.cs`. |
| [SEC-INV-07](../20-security/11-security-invariants.md#sec-inv-07) — no workload-create path bypasses the authorization gate | A convenience overload, test-only bypass, or internal fast path on `RuntimeHostProviderClient.CreateAsync`; a caller switched from `CreateAuthorizedAsync`. | No automated test; grep `CreateAsync(` call sites and confirm only `CreateAuthorizedAsync` reaches creation. |
| [SEC-INV-08](../20-security/11-security-invariants.md#sec-inv-08) — validation order | The order inside `RuntimeHostAuthorizationGate.ValidateAndCommit` changing from trust → ids → window → digest → signature → commit; the replay ledger committed before signature verification. | `RuntimeHostRpcIntegrationTests.PrivilegedAuthorizationRejectsReplayAndProviderSubstitution` |
| [SEC-INV-09](../20-security/11-security-invariants.md#sec-inv-09) — strict fencing rule, unpruned ledger | A comparison relaxed from `previousEpoch >= FencingEpoch`; any code that deletes, truncates, or expires `accepted-assignments.json`. | `RuntimeHostRpcIntegrationTests.PrivilegedAuthorizationRejectsReplayAndProviderSubstitution` |
| [SEC-INV-10](../20-security/11-security-invariants.md#sec-inv-10) — assurance floor, active certification required | `EnforcePlatformMinimum` lowering a request; a selection path returning a provider without a non-null, active, identity-matching certification and a live probe. | `IsolationProviderSelectorTests.SelectAsync_RejectsAvailableProviderWithoutCertification`; `CertifiedGuestImageRegistryTests` |
| [SEC-INV-11](../20-security/11-security-invariants.md#sec-inv-11) — numeric clamps | Any changed bound or default in the clamp table: shared key length, RPC clock skew, nonce retention, authorization lifetime and skew, assignment window, `MaximumFrameBytes`, helper timeout, stop grace period. | No automated test; review `RuntimeHostEndpointOptions.Validate()` (frame size), the `RuntimeHostAuthorizationGate` option checks (lifetime and skew), and the authentication and helper request validation. |
| [SEC-INV-12](../20-security/11-security-invariants.md#sec-inv-12) — local RPC transport protections | Removed pipe protection or inheritance settings; client rights beyond `ReadWrite \| Synchronize \| CreateNewInstance`; Unix socket mode other than `0660`; an unbounded frame size. | `RuntimeHostRpcIntegrationTests.WindowsPipeSecurity_GrantsExactDuplexClientConnectionRights` |
| [SEC-INV-13](../20-security/11-security-invariants.md#sec-inv-13) — no oracle, signature before nonce | A response written for a frame that failed authentication; the nonce consumed before the signature is verified. | `RuntimeHostAuthenticationTests.Validate_AcceptsSignedEnvelopeOnce` |
| [SEC-INV-14](../20-security/11-security-invariants.md#sec-inv-14) — signed, request-id-bound responses | A response signed without a fresh nonce; a client that stops validating protocol version, request-id echo, or body case. | `RuntimeHostRpcIntegrationTests.ClientAndServer_AuthenticateAndDispatchTypedLifecycle` |
| [SEC-INV-15](../20-security/11-security-invariants.md#sec-inv-15) — handle authorization binding | `IsHandleAuthorized` no longer matching provider id, instance id, and kind; expiry enforced for termination; removal of the handle authorization anywhere except `Destroy`; a failed persistence that leaves the created workload alive. | `RuntimeHostRpcIntegrationTests.PrivilegedLifecycleRejectsForgedAndExpiredProviderHandles` |
| [SEC-INV-16](../20-security/11-security-invariants.md#sec-inv-16) — package and image ACLs read-only for services | A write ACE or a broadened inherited ACE for `S-1-5-83-0`; removal of `Assert-FileReadExecuteAce` as an installation gate; inherited instead of explicit ACEs on the package. | `WindowsHyperVOnboardingTests`; `LinuxInstallationTests` |
| [SEC-INV-17](../20-security/11-security-invariants.md#sec-inv-17) — installation refused on a domain controller | Removal of `Assert-NotDomainController`, or a check that no longer covers `DomainRole` 4 and 5. | `WindowsHyperVOnboardingTests` asserts the installer text keeps `Assert-NotDomainController`. |
| [SEC-INV-18](../20-security/11-security-invariants.md#sec-inv-18) — identity never migrated | `reconnect` no longer deleting `node`, `authorization`, `artifact-media`, `hyperv`, and `runtime-host.key`; recovery allowed while state is `active`; an upgrade path that preserves identity without a drained office and zero active assignments. | `WindowsHyperVOnboardingTests` upgrade-probe scenarios; `scripts/tests/Test-OfficeUpgradeProbe.ps1` |
| [SEC-INV-19](../20-security/11-security-invariants.md#sec-inv-19) — maintenance service bound to the install tree | `maintenance-settings.json` moving out of the install root or losing its `SYSTEM`/`Administrators`-only access; a new inbound listener; any path that executes remote commands. | `MaintenanceHandoffTests.OnlyPredefinedMaintenanceActionsAreAccepted`; `OfficeMaintenanceStateTests` |
| [SEC-INV-20](../20-security/11-security-invariants.md#sec-inv-20) — fixed helper surface, digest re-verified per invocation | A new operation in a `HelperArguments` allow-list; a dropped `VerifyFileDigestAsync`; a device, host path, mount option, or command taken from the request; request data interpolated into script text; a helper returning non-zero for a typed failure. | `WindowsHyperVOnboardingTests.HelperArguments_RejectUnknownOperations`; `FirecrackerHelperSecurityTests.ArgumentsAllowOnlyTheFixedTypedProtocolSurface` |
| [SEC-INV-21](../20-security/11-security-invariants.md#sec-inv-21) — guest environment cleared and rebuilt | A missing `start.Environment.Clear()`; a new allow-list key; `PATH` or any loader-affecting variable; a removed `CSweet__Agent__ManifestPath` override. | `WorkspaceEnvironmentTests.AcceptsPlatformWorkspaceLimits`; `WorkspaceEnvironmentTests.RejectsUnapprovedEnvironmentKeys` |
| [SEC-INV-22](../20-security/11-security-invariants.md#sec-inv-22) — media mounting and extraction limits | A third artifact device; changed mount flags (`RDONLY`, `NOSUID`, `NODEV`, `NOEXEC`); the whole-stream digest check removed; raised extraction limits (10,000 entries, 2 GiB); links or special files accepted; path confinement or mode sanitization weakened. | `GuestArtifactMaterializerTests`; `AgentArtifactMediaStoreTests`; `ToolchainGuestSecurityTests` |
| [SEC-INV-23](../20-security/11-security-invariants.md#sec-inv-23) — guest handshake proofs | The boot-token HMAC payload changed; the one-shot challenge losing its ≤ 60-second expiry; lease expiry no longer equal to the boot-configuration expiry verbatim; the session not cancelled at lease expiry. | `ExternalPlatformStdioGuestChannelConnectorTests.HandshakeMustConfirmCertifiedTransport`; the real-guest smoke path in [../60-release/03-certification.md](../60-release/03-certification.md) is the end-to-end check. |

## General review items

- **Documentation moved with the behaviour.** The affected page under `docs/` is updated in the same change,
  and its `Verified:` date is refreshed. The change-impact matrix in
  [03-change-impact-matrix.md](03-change-impact-matrix.md) names the pages per area.
- **No new dependency without a central pin.** Every package version lives in `Directory.Packages.props`
  (central package management with transitive pinning); the project file references the package without a
  version.
- **No file added under a fingerprint root.** `src/CSweet.Office.RuntimeGuest`, `src/CSweet.Office.BuilderGuest`,
  `src/CSweet.Office.ToolchainGuest`, `src/CSweet.Office.Runtime.Protocol`, `build/windows-hyperv`, and the
  individual files `scripts/windows/New-CSweetHyperVTestGuest.ps1`, `Directory.Build.props`,
  `Directory.Packages.props`, and `global.json` when present. Adding anything there forces a full guest-image
  rebuild. See [01-repository-conventions.md](01-repository-conventions.md).
- **`Verified:` dates on the pages you touched**, and on the pages whose sources you changed — not on the whole
  directory.
- **No secrets committed.** No keys, passwords, tokens, thumbprints, enrollment material, or customer data in
  source, tests, documentation, or workflow files. Release inputs are protected environment variables
  ([../60-release/04-release-pipeline.md](../60-release/04-release-pipeline.md)).
- **`releases/<version>.md` exists** when `VersionPrefix` moves, and states any deployment ordering or
  prerequisite ([../60-release/06-release-notes-process.md](../60-release/06-release-notes-process.md)).
- **CI passes with `-p:UseLocalOfficeContracts=false`.** A change verified only against a sibling contracts
  checkout is not verified.
- **Test coverage.** A behavior change on a security-critical path needs a test, or a note in the pull request
  explaining why the existing tests suffice. Script-content assertions read repository files by path; renaming
  a script breaks them.

## Changes that need an explicit reviewer confirmation

These paths carry the privileged boundary. A reviewer must confirm the change against the invariant table above
— a general approval does not count as review:

| Path | Why |
|---|---|
| `src/CSweet.Office.Runtime.LocalRpc` | Authorization gate, replay ledger, RPC server, dispatcher, handle authorization. |
| `src/CSweet.Office.RuntimeHost` | The privileged service, provider wiring, configuration surface. |
| `src/CSweet.Office.Runtime.Core` | Payload manifest validation, fail-closed selection, guest image registry, helper protocol client. |
| `src/CSweet.Office.Runtime.HyperV.Helper`, `src/CSweet.Office.Runtime.Firecracker.Helper`, `src/CSweet.Office.Runtime.AppleVirtualization.Helper` | The privileged helper surface. |
| `src/CSweet.Office.RuntimeGuest`, `src/CSweet.Office.BuilderGuest`, `src/CSweet.Office.ToolchainGuest` | Guest isolation, environment allow-list, artifact materialization, handshake. |
| `scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`, `scripts/linux/install-office.sh`, `scripts/macos/install-office.sh` | Installation ACLs, service identity, and the gates that refuse an unsafe host. |
| `.github/workflows/release.yml` and `scripts/release/*` | Signing and publication. Per `AGENTS.md`, release signing and certification require the hardened platform workflows. |

## Sources

`docs/20-security/11-security-invariants.md`, `AGENTS.md`, `Directory.Packages.props`,
`scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1`, `.github/workflows/ci.yml`,
`tests/CSweet.Office.Tests/*`, `scripts/tests/Test-OfficeUpgradeProbe.ps1`.

Verified: 2026-09-15.
