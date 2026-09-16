# PROP-07 — Harden Office identity key storage

**Status:** proposal — not implemented. **Area:** security (credential protection). **Effort:** large.
**Tooling cost:** payload rebuild (Node code).

**Invariant interactions:** `SEC-INV-02` — `OfficeStateStore.InstallOperationalCertificate` must keep its
order of expiry/thumbprint checks, then `CopyWithPrivateKey` (which rejects a key-binding mismatch), then the
atomic write. Any storage change must keep that validation path intact and green in
`OfficeCertificateRecoveryTests.RejectedCertificateDoesNotOverwriteDurableIdentity`.

## Problem

The Office identity is a passwordless, exportable PFX:

- `OfficeStateStore` writes `node-identity.pfx` and loads it with
  `X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet`.
- All protection is filesystem ACLs. The documented rationale
  ([`docs/20-security/12-known-limits-and-tradeoffs.md`](../docs/20-security/12-known-limits-and-tradeoffs.md),
  "The Office private key is a passwordless, exportable PFX") is that an unattended service has no human to
  supply a password, and that a host administrator can decrypt anything anyway.

The residual risk is real and different in kind: the key is the only credential that can drive the
certificate-recovery endpoint (`SEC-INV-03`, `SEC-INV-18` in
[`docs/20-security/02-certificate-lifecycle.md`](../docs/20-security/02-certificate-lifecycle.md)). It does not
need to be attacked on the host — it leaks through backups, VM snapshot copies, support bundles, or antivirus
quarantine, and those copies leave the ACL's protection behind. The identity is used for TLS client
authentication and for the recovery proof, both of which need a private key *handle*, not an exportable key.

## User story

**Scenario.** Ewan's Office VM is backed up nightly by a host-level agent that copies `%ProgramData%` without
regard for its ACLs. Six months later a backup tape is lost in transit. The PFX on that tape is still
sufficient to impersonate the Office at the recovery endpoint from anywhere.

> As a security reviewer, I want the Office identity key to be non-exportable wherever the platform provides
> a key store, so that an accidental file copy does not yield a credential that can drive the recovery
> endpoint.

## Recommended fix

Stage it, and prototype before committing:

### Stage 1 — Bind the key to the machine (medium effort)

Wrap the PFX bytes with a machine-scope protection (Windows DPAPI `CryptProtectData` machine scope; a
platform equivalent elsewhere) so a copied file is useless off the machine. This is the cheapest meaningful
step and does not change the certificate object model. It removes the offline-copy exposure and keeps the
existing file layout.

### Stage 2 — Move the key into a key store (large effort; prototype required)

Hold the private key in a CNG/Keychain/keystore container with an export policy of *none*, and let
`node-identity.pfx` become a public certificate plus a key reference. Two prototype risks must be retired
before committing:

1. `InstallOperationalCertificate` relies on `CopyWithPrivateKey` to combine the issued certificate with the
   durable key and to reject a key-binding mismatch. Whether that call works with a non-exportable CNG key
   from a machine key store — and how the service identity's ACL on the key container interacts with the
   Node's virtual account — must be tested first. If it cannot work as-is, the binding check needs an
   equivalent replacement, and that is an `SEC-INV-02` review.
2. `OfficeCertificateTlsTests` already shows the code path differs by platform (`RSACng` on Windows,
   `RSA.Create` elsewhere); the rotating TLS handler and the recovery proof signing both use the key handle,
   so both need coverage under the new storage.

### Stage 3 — TPM-backed keys where available (optional, largest effort)

Where a TPM 2.0 (including a Hyper-V virtual TPM) is present, hold the key there. Migration to another host
then means re-enrollment, which is already the documented path for key loss and requires administrator
intervention — acceptable, but it must be documented as an explicit operational consequence.

Every stage needs an explicit migration story for identity-preserving upgrades: either a one-time,
installer-gated export during a drained upgrade, or a documented "re-enroll on host replacement" flow.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `src/CSweet.Office.Node/OfficeStateStore.cs` | Key storage and loading; keep `InstallOperationalCertificate` order | `SEC-INV-02` must stay pinned by its test |
| `src/CSweet.Office.Node/OfficeCertificateLease.cs` and the recovery proof path | Use key handles, not exported key material | No behavioural change to renewal or recovery semantics |
| Installer scripts | Any new key-container ACLs; document the service identity requirement | Linux/macOS equivalents if the mechanism is not Windows-only |
| Tests | Extend `OfficeCertificateRecoveryTests` and `OfficeCertificateTlsTests`; add a "copied file is unusable off-machine" case for stage 1 | Hand-written fakes, no mocking library |
| Docs | [`docs/20-security/01-identity-and-enrollment.md`](../docs/20-security/01-identity-and-enrollment.md), [`docs/20-security/02-certificate-lifecycle.md`](../docs/20-security/02-certificate-lifecycle.md), [`docs/20-security/12-known-limits-and-tradeoffs.md`](../docs/20-security/12-known-limits-and-tradeoffs.md) (rewrite the entry deliberately), [`docs/40-operations/01-installation-windows.md`](../docs/40-operations/01-installation-windows.md) for migration | The known-limits entry must change on purpose, not drift |

## What must not change

- The validation order and key-binding check in `InstallOperationalCertificate` (`SEC-INV-02`).
- The bootstrap / renew / recover split and `AllowTlsResume = false` (`SEC-INV-04`).
- Recovery refusing to send an expired certificate or a reusable receipt (`SEC-INV-03`).
- The ACL model: file protection stays at least as strong as today.

## Verification

1. Stage 1: copy `node-identity.pfx` to another machine and prove the Node cannot use it.
2. Every stage: run the recovery and renewal test suites on all three platforms' code paths.
3. Prove the rejected-certificate case still does not overwrite the durable identity.

## Sources

`src/CSweet.Office.Node/{OfficeStateStore.cs,OfficeCertificateLease.cs}`,
`docs/20-security/{01-identity-and-enrollment.md,02-certificate-lifecycle.md,11-security-invariants.md,12-known-limits-and-tradeoffs.md}`,
`tests/CSweet.Office.Tests/{OfficeCertificateRecoveryTests.cs,OfficeCertificateTlsTests.cs,ControlPlaneServerCertificateValidatorTests.cs}`.

Verified: 2026-09-15.
