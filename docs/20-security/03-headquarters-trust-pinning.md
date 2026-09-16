# Headquarters trust pinning

**Audience:** security reviewers; anyone changing enrollment, the installer, or `RuntimeHostAuthorizationGate`.

The Office pins the Headquarters *assignment signing key* — not just a TLS certificate — so that a
compromised or substituted control plane cannot direct work to an Office that already belongs to someone
else. The pin is established before enrollment and is write-once afterwards.

## The record

```csharp
public sealed record PinnedHeadquartersTrust(
    Guid OfficeId,
    string AssignmentSigningKeyId,
    byte[] AssignmentVerificationPublicKey);
```

Defined in `src/CSweet.Office.Runtime.Abstractions/IsolationPorts.cs`.

## Where it is pinned

### 1. Install time, before the Office has an identity

The installer fetches the assignment trust document over the verified TLS connection and writes it with
`OfficeId = Guid.Empty`:

```
GET api/offices/assignment-trust
```

- **Windows** — `Install-CSweetOfficeRuntimeHost.ps1` calls the Node executable with
  `--probe-headquarters-assignment-trust <url> [fingerprint]`, then writes
  `%ProgramData%\CSweet\Office\authorization\headquarters-trust.json` and re-applies an `/inheritance:r` ACL
  granting the RuntimeHost service SID `(OI)(CI)M` and `SYSTEM`/`Administrators` full control.
- **Linux and macOS** — the installers call
  `--initialize-headquarters-assignment-trust <url> <absolute path> [fingerprint]`, which writes a
  **pre-pinned** file with `OfficeId = Guid.Empty`, rejecting a reparse-point destination. The file is then
  `chown root:root` and `chmod 0600`.

The probe is strict about the key document: `ImportSubjectPublicKeyInfo` must consume every byte of the
public key and the key id must be non-empty.

This pre-pin exists so that even the very first pin is anchored to the TLS-verified control plane rather than
to whatever a later connection claims.

### 2. Runtime pin, on every control-session cycle

`OfficeWorker.PinRuntimeHostTrustAsync` sends the pin to each provider that implements `IRuntimeHostClient`.
The request travels as a `PinHeadquartersTrustRequest` in the authenticated local RPC envelope and ends at
`RuntimeHostAuthorizationGate.Pin`.

Re-pinning every session is deliberate: the RuntimeHost's pin is idempotent for a matching value, so the
Node can safely re-assert it, and a mismatched value is fatal rather than silent.

## Pin validation

`RuntimeHostAuthorizationGate.Pin` applies, in order:

1. `ValidateTrust` — key id non-empty and at most 200 characters; public key between 64 and 1024 bytes;
   `ImportSubjectPublicKeyInfo` must consume exactly the buffer, rejecting trailing bytes.
2. Under a lock:
   - an existing pin with a **different key id** or **different key bytes** (compared with
     `CryptographicOperations.FixedTimeEquals`) throws
     `InvalidDataException("… does not match this enrollment.")`;
   - an existing `OfficeId == Guid.Empty` with a requested **non-empty** id is **overwritten** — this is the
     legitimate installer-pre-pin to enrollment upgrade;
   - an existing different **non-empty** `OfficeId` throws `"belongs to another Office"`.
3. The write is atomic: a temp file created with `FileMode.CreateNew` and `FileOptions.WriteThrough`, then
   `File.Move(..., overwrite: true)`.

The empty-to-real office id transition is the **only** permitted mutation. Removing it breaks every install,
because the installer necessarily pins before the Office has an identity.

Related invariant: `SEC-INV-01`.

## Where the pin is enforced

Three independent places, all of which reject on mismatch:

| Location | Check |
|---|---|
| `RuntimeHostAuthorizationGate.ValidateAndCommit` | `authorization.SignatureKeyId` must equal the pinned key id, and `authorization.OfficeId` must equal the pinned office id. |
| `OfficeWorker.ReadControlMessagesAsync` (the `Hello` frame) | The key id must be string-equal **and** `CryptographicOperations.FixedTimeEquals(message.Hello.AssignmentVerificationPublicKey, pinnedBytes)` over a 64–1024 byte key, or the session dies with `InvalidDataException` (*"does not match the identity pinned during enrollment"*). |
| `RuntimeHostAuthorizationGate.Pin` | Re-pinning with different material is rejected. |

Note the asymmetry: the `Hello` check is a *stricter* re-confirmation of what the Node already believes,
while the gate check is the enforcement point that actually authorizes work.

## What invalidates the pin

There is **no unpin or rotation API**. A changed assignment signing key therefore requires one of:

| Action | Effect |
|---|---|
| Uninstall and reinstall | Data root removed; a fresh install pre-pins the new key. |
| `Install-CSweetOffice.ps1 -ExistingInstallationAction reconnect` | Deletes the mutable roots `node`, `authorization`, `artifact-media`, `hyperv`, and `runtime-host.key`, forcing fresh enrollment. Guarded: requires an assisted setup session and a recovery state of `clean`, never `active`. |
| Any key-id, key, or office-id mismatch at runtime | Permanent `InvalidDataException` for that session. The Office loops with a five-second backoff and never executes work. |

Per `AGENTS.md`, Office identity is preserved on upgrade **only** after the office is drained and has zero
active assignments.

## Verifying a pin by hand

The probe subcommand is the canonical way to see what a control plane would pin:

```
CSweet.Office.Node --probe-headquarters-assignment-trust <https url> [expected fingerprint]
```

It performs `GET api/offices/assignment-trust`, validates the TLS fingerprint in fixed time when one is
supplied (otherwise OS trust), validates the public key strictly, and prints the trust document as JSON.

## Sources

`src/CSweet.Office.Runtime.Abstractions/IsolationPorts.cs`,
`src/CSweet.Office.Runtime.LocalRpc/RuntimeHostAuthorizationGate.cs`,
`src/CSweet.Office.Node/{OfficeWorker.cs,ControlPlaneCertificateProbe.cs}`,
`scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`, `scripts/linux/install-office.sh`,
`scripts/macos/install-office.sh`, `AGENTS.md`.

Verified: 2026-09-15.
