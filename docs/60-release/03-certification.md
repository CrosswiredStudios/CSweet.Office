# Certification

**Audience:** release engineers and contributors changing guest code, provider configuration, or the
certification runners.

Certification is evidence that a specific provider, host operating system, host architecture, guest image, and
broker protocol version were tested together and are currently valid. `SEC-INV-10` in
[../20-security/11-security-invariants.md](../20-security/11-security-invariants.md) makes it load-bearing: the
assurance floor never drops, and no provider is ever returned from selection without a non-null, active,
identity-matching certification and a live probe. Mechanism detail is in
[../20-security/08-provider-certification.md](../20-security/08-provider-certification.md).

## What the smoke path actually runs

Both platform paths run the same runner, `src/CSweet.Office.WindowsSmokeTest` (the name is historical; on Linux
it is invoked with `--provider firecracker`). The runner refuses to start without the right privilege: Windows
Hyper-V demands an administrator and Windows, Firecracker demands Linux and `root`.

For each run it:

1. creates an artifact-media root, an ISO containing the certified probe bundle, and a workload through the
   platform helper (`create`, then `start`) using the helper binary under test;
2. boots a real guest with no network and hosts the guest channel, waiting for the guest broker's report;
3. fails the run when the report did not pass or when any reported check is `false`;
4. stops and destroys the workload through the helper, and fails when cleanup does not complete;
5. runs a second, builder workload — a real build inside the guest that streams an artifact back — and merges
   its checks into the same evidence;
6. writes the evidence JSON and prints the evidence path and suite version.

There is no simulated or in-process substitute in the evidence path: the checks are produced by the guest, and
the host only aggregates them.

| Platform path | Entry point | Host requirements | Guest image |
|---|---|---|---|
| Windows / Hyper-V | `scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1` | Administrator, Hyper-V, a cached or freshly built `.vhdx` | `New-CSweetHyperVTestGuest.ps1`, cached by the bundle fingerprint |
| Linux / Firecracker | `scripts/linux/initialize-firecracker-test.sh` | `root`, cgroup v2 with `/dev/kvm`, a `csweet-vm` account | `new-firecracker-guest.sh` builds `rootfs.ext4` locally |

The Linux path also pins the hypervisor: it downloads the Firecracker release archive named by
`--firecracker-version` (default `v1.16.1`), verifies it against the release's published `.sha256.txt`, installs
`firecracker` and `jailer`, and fails if the two do not report the same version. The payload generator applies
its own floor of `1.14.0` or later to the same pair.

> **Out of repo:** the macOS smoke path is not in this repository. `scripts/macos/new-runtime-payload.sh` takes
> a pre-built guest image, its signature, the signer's certificate, and an evidence file as inputs. Production
> evidence for every platform is produced on the hardened runner images and supplied to the release pipeline as
> protected environment variables.

## The evidence file

`CSweet.Office.WindowsSmokeTest` writes a single JSON document. Every field below is present in
`artifacts/windows-test/certification-20260915-134026/windows-hyperv.json`, the run of 2026-09-15:

| Field | Meaning |
|---|---|
| `providerId`, `providerVersion`, `hostOperatingSystem`, `hostArchitecture` | The provider identity the evidence is valid for. Selection compares these against both the registered descriptor and the certification record. |
| `guestImageDigest` | `sha256:` digest of the exact image that was booted. |
| `brokerProtocolVersion` | `1.0`. |
| `certificationSuiteVersion` | The suite identifier reported by the guest probe; the probe currently reports `csweet-hardware-vm-smoke-v14`. |
| `certifiedAt` | The moment the run started. |
| `certificationExpiresAt` | `certifiedAt` plus seven days. Development evidence is deliberately short-lived. |
| `checks` | A flat object of `name → boolean`. Every value must be `true`. |
| `guestOperatingSystem` | The guest kernel string the probe reported (for example `Unix 6.8.0.139`). |
| `completedAt` | When the guest finished its checks. |

The `checks` object from that run, grouped by what it covers:

| Group | Checks |
|---|---|
| Guest integrity and network isolation | `linux-guest`, `no-network-interface`, `no-default-route`, `no-host-filesystem-mount`, `ephemeral-scratch-mounted`, `outbound-network-denied` |
| Artifact and broker plumbing | `artifact-root`, `broker-socket-present`, `workload-unprivileged` |
| Toolchain contents | `builder-executable-present`, `toolchain-executable-present`, `dotnet-sdk-present` |
| Builder result pipeline | `builder-source-brokered`, `builder-package-trust-metadata-brokered`, `builder-progress-reported`, `builder-artifact-streamed`, `builder-artifact-digest-verified`, `builder-artifact-entrypoint-executable` |

The key set is produced by the probe and the builder smoke; treat the list above as the current evidence
surface, not as a frozen contract. Adding a check changes the evidence file, not the schema of the run.

### How the scripts assert it

Windows, after the runner exits, reads the file back and requires every property to be `true`:

```powershell
if (-not $evidence.checks -or @($evidence.checks.PSObject.Properties | Where-Object { $_.Value -ne $true }).Count -ne 0) {
    throw 'Certification evidence contains a failed isolation check.'
}
```

Linux uses `jq` and requires a non-empty object in which every value is `true`:

```bash
jq -e '.checks | length > 0 and all(.[]; . == true)' "$evidence_path" >/dev/null || {
  echo "Firecracker certification evidence contains a failed check." >&2; exit 2;
}
```

Both paths also require the runner process itself to exit zero and, on Windows, the evidence file to exist.

## What the numbers become

| Caller | Suite version passed | `certifiedAt` | Expiry |
|---|---|---|---|
| `Initialize-CSweetWindowsIsolationTest.ps1` | The evidence's `certificationSuiteVersion` | The evidence's `certifiedAt` | The evidence's `certificationExpiresAt` |
| `initialize-firecracker-test.sh` | Same, read with `jq` | Same | Same, or empty for `null` |
| `Invoke-PlatformRelease.ps1` (hardened runner) | The literal `production-v1` | `[DateTimeOffset]::UtcNow` at payload build time | `CSWEET_CERTIFICATION_VALID_UNTIL` |

Those values are written into the payload manifest's `certificationSuiteVersion`, `certifiedAt`, and
`certificationExpiresAt`, which `PlatformRuntimePayloadManifest` then binds into the provider options.
`New-OfficeReleaseManifest.ps1` copies the payload's `certificationExpiresAt` into the release manifest as
`certificationValidUntil` and throws *"Release evidence is missing for $($file.Name)."* when either the guest
image digest or the validity date is absent. See [02-payload-and-manifest.md](02-payload-and-manifest.md).

## The development signer certificates

Development runs sign the guest image with a throwaway key so the payload path can be exercised end to end.
These identities are explicitly development-only and are never used by the release pipeline.

| Property | Windows | Linux |
|---|---|---|
| Subject | `CN=C-Sweet Windows Hyper-V Development Guest Image Signer` | `CN=C-Sweet Linux Firecracker Development Guest Signer` |
| Created by | `Initialize-CSweetWindowsIsolationTest.ps1`, `New-SelfSignedCertificate -Type Custom` in `Cert:\CurrentUser\My` | `initialize-firecracker-test.sh`, `openssl req -x509` |
| Algorithm | RSA 3072, SHA-256, `DigitalSignature` key usage | RSA 3072, SHA-256 |
| Validity | `-NotAfter (Get-Date).AddYears(1)` | `-days 365`, key file `chmod 0600` |
| Reuse | Reused only when a matching certificate has a private key and expires more than 30 days from now; otherwise a new one is created | A new key and certificate every run, under the run root |
| Signature | `RSA.SignHash` with `Pkcs1` padding over the image SHA-256 | `openssl dgst -sha256 -sign` |
| Thumbprint | `$certificate.Thumbprint` (SHA-1 of the certificate) | `openssl x509 -fingerprint -sha1` with the colons stripped |

The generated certificate (DER) and detached signature are handed to the payload generator, so a development
payload carries exactly what a production payload carries: an image digest, a signature, a signing certificate,
and its thumbprint.

## The operational consequence of expiry

A certification is active when `RevokedAt` is null and `ExpiresAt` is null or in the future
(`IsolationProviderCertification.IsActiveAt`). Selection requires an active, identity-matching certification
*and* a live probe; if neither is available the selector throws rather than degrading:

```
No certified hardware-backed agent isolation provider is available. <provider>: no active matching certification
```

Because the smoke evidence expires after seven days and the release pipeline pins
`CSWEET_CERTIFICATION_VALID_UNTIL`, an expired or unrenewed certification stops placement. Work does not
continue on an unverified image; it fails closed, per `SEC-INV-10`. The same applies to a suite mismatch: the
guest image registry refuses a certification whose suite does not match the required one and reports the
user-facing *"installed secure agent runtime is out of date"* message.

## Sources

`src/CSweet.Office.WindowsSmokeTest/Program.cs`, `src/CSweet.Office.GuestProbe/Program.cs`,
`scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1`, `scripts/linux/initialize-firecracker-test.sh`,
`scripts/linux/new-firecracker-guest.sh`, `scripts/linux/new-runtime-payload.sh`, `scripts/macos/new-runtime-payload.sh`,
`scripts/release/{Invoke-PlatformRelease.ps1,Invoke-LinuxRelease.ps1,Invoke-MacRelease.ps1,New-OfficeReleaseManifest.ps1}`,
`artifacts/windows-test/certification-20260915-134026/windows-hyperv.json`,
`src/CSweet.Office.Runtime.Core/{FailClosedIsolationProviderSelector.cs,CertifiedGuestImageRegistry.cs,PlatformRuntimePayloadManifest.cs}`,
`src/CSweet.Office.Runtime.Abstractions/IsolationModels.cs`, `docs/20-security/{08-provider-certification.md,11-security-invariants.md}`,
`docs/30-workloads/06-guest-images.md`.

Verified: 2026-09-15.
