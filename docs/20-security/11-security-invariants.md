# Security invariants

**Audience:** everyone changing code. **This page is normative.**

These are the properties the security model depends on. Each has a stable identifier. Other pages, `AGENTS.md`,
and the agent customization files cite them by identifier rather than restating them. If you change code in a
way that breaks one of these, you have introduced a vulnerability, not a refactor — even if every test passes.

| # | Invariant |
|---|---|
| [SEC-INV-01](#sec-inv-01) | The Headquarters assignment trust pin is write-once, with one permitted upgrade |
| [SEC-INV-02](#sec-inv-02) | An operational certificate cannot replace the durable identity without validation |
| [SEC-INV-03](#sec-inv-03) | Recovery never sends an expired client certificate or a reusable receipt |
| [SEC-INV-04](#sec-inv-04) | Bootstrap detection and TLS session resumption stay as they are |
| [SEC-INV-05](#sec-inv-05) | Retired-certificate retention stays longer than the handshake timeout |
| [SEC-INV-06](#sec-inv-06) | Control messages stay bound to both Office id and session epoch |
| [SEC-INV-07](#sec-inv-07) | No workload-create path bypasses the authorization gate |
| [SEC-INV-08](#sec-inv-08) | Authorization validation order stays trust → ids → window → digest → signature → commit |
| [SEC-INV-09](#sec-inv-09) | The fencing-epoch rule stays strict and the ledger is never pruned |
| [SEC-INV-10](#sec-inv-10) | The assurance floor never drops and no provider passes without an active certification |
| [SEC-INV-11](#sec-inv-11) | The numeric clamps stay within their documented ranges |
| [SEC-INV-12](#sec-inv-12) | Local RPC transport protections stay exact |
| [SEC-INV-13](#sec-inv-13) | Unauthenticated frames get no response, and signatures are checked before nonces |
| [SEC-INV-14](#sec-inv-14) | Responses stay signed and request-id-bound |
| [SEC-INV-15](#sec-inv-15) | Handle authorization stays bound to provider, instance, and kind |
| [SEC-INV-16](#sec-inv-16) | Package and image ACLs stay read-only for the services |
| [SEC-INV-17](#sec-inv-17) | Installation is refused on a domain controller |
| [SEC-INV-18](#sec-inv-18) | Identity is never migrated; reconnect wipes mutable trust |
| [SEC-INV-19](#sec-inv-19) | The maintenance service stays bound to the administrator-owned install tree |
| [SEC-INV-20](#sec-inv-20) | The helper surface stays fixed and the helper digest is re-verified per invocation |
| [SEC-INV-21](#sec-inv-21) | The guest environment is cleared and rebuilt from the allow-list |
| [SEC-INV-22](#sec-inv-22) | Artifact media mounting and extraction limits stay in place |
| [SEC-INV-23](#sec-inv-23) | The guest handshake keeps its HMAC proof, one-shot challenge, and lease cancellation |

---

## SEC-INV-01

**The Headquarters assignment trust pin is write-once, with exactly one permitted upgrade.**

Never accept a pin whose key id or key bytes differ from an existing pin. Never allow two different non-empty
office ids. **Keep** the `Guid.Empty → real OfficeId` transition — the installer necessarily pins before the
Office has an identity, so removing it breaks every installation.

*Enforced in:* `RuntimeHostAuthorizationGate.Pin` / `ValidateTrust`.
*Detail:* [03-headquarters-trust-pinning.md](03-headquarters-trust-pinning.md).

## SEC-INV-02

**An operational certificate cannot replace the durable identity without validation.**

`OfficeStateStore.InstallOperationalCertificate` must keep, in order: expiry and optional thumbprint checks,
then `CopyWithPrivateKey` (which rejects a key-binding mismatch), then an atomic write. Dropping the
key-binding check lets a server replace an Office identity with a certificate whose private key it holds.

*Pinned by:* `OfficeCertificateRecoveryTests.RejectedCertificateDoesNotOverwriteDurableIdentity`.
*Detail:* [02-certificate-lifecycle.md](02-certificate-lifecycle.md).

## SEC-INV-03

**Recovery never sends an expired client certificate or a reusable receipt.**

`certificate/recover` must remain callable without a client certificate, must never carry the enrollment
receipt, and must keep mandatory server certificate validation (including any configured pin) on both the
challenge and the recovery call. The one-use property is enforced server-side; do not weaken the client-side
shape checks that mirror it.

*Detail:* [02-certificate-lifecycle.md](02-certificate-lifecycle.md).

## SEC-INV-04

**Bootstrap detection and TLS session resumption stay as they are.**

Keep `Subject == Issuer` as the bootstrap test and keep the bootstrap / renew / recover three-way split. Keep
`AllowTlsResume = false` on the rotating handler — TLS resumption would let a session outlive the certificate
decision that authorized it.

*Detail:* [02-certificate-lifecycle.md](02-certificate-lifecycle.md).

## SEC-INV-05

**Retired-certificate retention stays longer than the handshake timeout.**

`OfficeCertificateLease` retains a retired certificate for at least two minutes, which must stay ≥
`CreateRotatingHttpHandler`'s 20-second `ConnectTimeout`. A shorter retention breaks in-flight handshakes
against a rotating TLS callback.

*Detail:* [02-certificate-lifecycle.md](02-certificate-lifecycle.md).

## SEC-INV-06

**Control messages stay bound to both Office id and session epoch.**

Every `HeadquartersControlMessage` must match the local `OfficeId` and `SessionEpoch` or the session is
terminated. Epoch reuse across reconnects inside one process is intentional — Headquarters fences per epoch,
not per connection.

*Detail:* [01-identity-and-enrollment.md](01-identity-and-enrollment.md).

## SEC-INV-07

**No workload-create path bypasses the authorization gate.**

`RuntimeHostProviderClient.CreateAsync` must keep throwing
`IsolationUnavailableException("RuntimeHost creation requires a signed Headquarters workload authorization.")`.
Do not add a convenience overload, an internal fast path, or a test-only bypass that is reachable in
production. Only `CreateAuthorizedAsync` may create a workload.

*Detail:* [04-workload-authorization.md](04-workload-authorization.md).

## SEC-INV-08

**Authorization validation order stays trust → ids → window → digest → signature → commit.**

The order inside `RuntimeHostAuthorizationGate.ValidateAndCommit` is load-bearing. Committing the replay
ledger before signature verification would let unauthenticated traffic burn fencing epochs — a
denial-of-service primitive with no compensating benefit.

*Detail:* [04-workload-authorization.md](04-workload-authorization.md).

## SEC-INV-09

**The fencing-epoch rule stays strict and the ledger is never pruned.**

Reject when `previousEpoch >= authorization.FencingEpoch`. Never delete or prune `accepted-assignments.json`
to unstick a workload. The unbounded growth is the price of the replay guarantee.

*Detail:* [04-workload-authorization.md](04-workload-authorization.md) and
[12-known-limits-and-tradeoffs.md](12-known-limits-and-tradeoffs.md).

## SEC-INV-10

**The assurance floor never drops and no provider passes without an active certification.**

`EnforcePlatformMinimum` raises any request below `CertifiedHardwareVirtualMachine`; it must never lower the
baseline. A provider must never be returned from selection without a non-null, active, identity-matching
certification and a live probe.

*Detail:* [08-provider-certification.md](08-provider-certification.md).

## SEC-INV-11

**The numeric clamps stay within their documented ranges.**

| Value | Range | Default |
|---|---|---|
| Shared key length | ≥ 32 bytes | — |
| RPC clock skew | 1–300 s | 60 s |
| Nonce replay retention | 60–3600 s | 300 s |
| Authorization lifetime | 30–3600 s | 600 s |
| Authorization clock skew | 0–600 s | 120 s |
| Assignment window | lifetime ≤ 10 min, future skew ≤ 2 min | — |
| `MaximumFrameBytes` | 4096–16 MiB | 1 MiB |
| Helper timeout | 5–600 s | 120 s |
| Stop grace period | 0–300 s | — |

Widening any of these silently is a security change, not a tuning change.

## SEC-INV-12

**Local RPC transport protections stay exact.**

Keep `SetAccessRuleProtection(isProtected: true, preserveInheritance: false)` on the Windows pipe, and grant
clients nothing beyond `ReadWrite | Synchronize | CreateNewInstance`. Keep the Unix socket at `0660`. Keep
`MaximumFrameBytes` bounded.

*Pinned by:* `WindowsPipeSecurity_GrantsExactDuplexClientConnectionRights`.
*Detail:* [05-local-rpc-boundary.md](05-local-rpc-boundary.md).

## SEC-INV-13

**Unauthenticated frames get no response, and signatures are checked before nonces.**

A frame that fails the length, protocol, or authentication gate is logged and the connection is closed with no
response — there is no oracle. Validation must check the signature *before* consuming the nonce; otherwise
unsigned traffic can burn nonces and deny service to legitimate callers.

*Detail:* [05-local-rpc-boundary.md](05-local-rpc-boundary.md).

## SEC-INV-14

**Responses stay signed and request-id-bound.**

The server signs every response with a fresh nonce, and the client validates authentication, protocol version,
request-id echo, and body case. Without this, a local attacker able to write to the transport could inject
forged handles or statuses.

*Detail:* [05-local-rpc-boundary.md](05-local-rpc-boundary.md).

## SEC-INV-15

**Handle authorization stays bound to provider, instance, and kind.**

`IsHandleAuthorized` matches provider id, provider instance id, and workload kind, and enforces expiry unless
the caller asked for termination. Termination deliberately bypasses expiry so a stuck workload can always be
torn down; `Destroy` remains the only operation that removes the handle authorization. If persisting a handle
fails after `backend.CreateAsync`, the just-created workload must still be destroyed.

*Detail:* [04-workload-authorization.md](04-workload-authorization.md).

## SEC-INV-16

**Package and image ACLs stay read-only for the services.**

Keep explicit, non-inherited `RX` ACEs on the immutable package and keep `Assert-FileReadExecuteAce` as an
installation gate. Keep the virtual machine group `S-1-5-83-0` read-only on the guest image. **Never** grant
write access, or broadened inherited access, to `S-1-5-83-0` — Hyper-V opens the whole differencing chain as
the VM worker identity.

*Detail:* [06-host-privilege-model.md](06-host-privilege-model.md).

## SEC-INV-17

**Installation is refused on a domain controller.**

Keep `Assert-NotDomainController` (`Win32_ComputerSystem.DomainRole` of 4 or 5).

*Detail:* [06-host-privilege-model.md](06-host-privilege-model.md).

## SEC-INV-18

**Identity is never migrated; reconnect wipes mutable trust.**

A first install removes legacy services and enrolls fresh. `reconnect` must keep deleting `node`,
`authorization`, `artifact-media`, `hyperv`, and `runtime-host.key`, and must keep refusing while the recovery
state is `active`. Legacy identities are never carried across the privileged signed-assignment cutover.
Identity-preserving upgrade is permitted only when the office is draining with zero active assignments.

*Pinned by:* `WindowsHyperVOnboardingTests` upgrade-probe scenarios, `scripts/tests/Test-OfficeUpgradeProbe.ps1`.
*Detail:* [40-operations/04-upgrade-and-drain.md](../40-operations/04-upgrade-and-drain.md).

## SEC-INV-19

**The maintenance service stays bound to the administrator-owned install tree.**

Keep `maintenance-settings.json` inside the install root with `SYSTEM`/`Administrators`-only access. The
maintenance service must never gain an inbound listener or execute arbitrary remote commands.

*Detail:* [40-operations/05-repair-and-recovery.md](../40-operations/05-repair-and-recovery.md).

## SEC-INV-20

**The helper surface stays fixed and the helper digest is re-verified per invocation.**

Keep protocol `1.0`, the eight-operation allow-list, typed JSON on stdio, and the per-invocation
`VerifyFileDigestAsync`. Never accept a device, host path, mount option, or command from a request. Never
interpolate request data into PowerShell script text. Never make a helper exit non-zero for a typed failure,
because RuntimeHost would discard the typed error.

*Detail:* [09-helper-protocol.md](09-helper-protocol.md).

## SEC-INV-21

**The guest environment is cleared and rebuilt from the allow-list.**

Keep `start.Environment.Clear()`, the 17-key allow-list, and the fixed injections. Never allow `PATH` or any
loader-affecting variable. Keep the forced `CSweet__Agent__ManifestPath = csweet-plugin.json` override so a
stale packaged configuration cannot redirect the SDK.

*Pinned by:* `WorkspaceEnvironmentTests`.
*Detail:* [07-guest-isolation.md](07-guest-isolation.md).

## SEC-INV-22

**Artifact media mounting and extraction limits stay in place.**

Keep the two-device allow-list, the `RDONLY | NOSUID | NODEV | NOEXEC` mount flags, the whole-stream digest
check, the extraction limits (10,000 entries, 2 GiB expanded), the rejection of links and special files, the
path confinement, and the mode sanitization.

*Detail:* [07-guest-isolation.md](07-guest-isolation.md).

## SEC-INV-23

**The guest handshake keeps its HMAC proof, one-shot challenge, and lease cancellation.**

Keep the boot-token HMAC over the identity-bound payload, the one-shot ECDSA challenge with its ≤ 60-second
expiry, the requirement that the lease expiry equal the boot-configuration expiry verbatim, and cancellation
of the guest session at lease expiry.

*Detail:* [07-guest-isolation.md](07-guest-isolation.md).

---

## Using these identifiers

- In `AGENTS.md` and `.github/copilot-instructions.md`: cite the identifier, never restate the rule.
- In `.github/instructions/*.instructions.md`: each boundary-scoped instruction file names the invariants that
  apply to its paths.
- In [70-contributing/04-review-checklist.md](../70-contributing/04-review-checklist.md): the checklist is
  organized around them.
- When adding an invariant: append `SEC-INV-24` and later. **Never renumber** — the identifiers are cited from
  other files and from review comments.

## Sources

`src/CSweet.Office.Runtime.LocalRpc/{RuntimeHostAuthorizationGate.cs,RuntimeHostRpcServer.cs,RuntimeHostRequestDispatcher.cs,RuntimeHostProviderClient.cs,RuntimeHostEndpointOptions.cs}`,
`src/CSweet.Office.Runtime.Protocol/RuntimeHostAuthentication.cs`,
`src/CSweet.Office.Node/{OfficeWorker.cs,OfficeStateStore.cs,OfficeCertificateLease.cs}`,
`src/CSweet.Office.Runtime.Core/{FailClosedIsolationProviderSelector.cs,ExternalPlatformIsolationBackend.cs,PlatformRuntimePayloadManifest.cs}`,
`src/CSweet.Office.RuntimeGuest/{GuestWorkloadSupervisor.cs,GuestArtifactMaterializer.cs,GuestBrokerSession.cs}`,
`src/CSweet.Office.Runtime.HyperV.Helper/{HelperArguments.cs,PowerShellHyperV.cs}`,
`scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`, `tests/CSweet.Office.Tests/*`.

Verified: 2026-09-15.
