# Payload and runtime manifest

**Audience:** release engineers building or inspecting a payload, and contributors changing the payload
generators or `PlatformRuntimePayloadManifest`.

A payload is a versioned, manifest-verified directory of Office binaries plus the certified guest image, the
helper, its signing certificate, and the certification evidence. Per the glossary it is installed to
`$InstallRoot\<packageVersion>`. It is produced by three scripts, one per platform, and it is the only thing an
installer consumes.

| Platform | Generator | Manifest | Consumer |
|---|---|---|---|
| Windows x64 | `scripts/windows/New-CSweetWindowsRuntimePayload.ps1` | Installer manifest | `scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`, `New-CSweetOfficeMsi.ps1` |
| Linux x64 / arm64 | `scripts/linux/new-runtime-payload.sh` | Provider manifest | `PlatformRuntimePayloadManifest` via `PayloadManifestPath` |
| macOS x64 / arm64 | `scripts/macos/new-runtime-payload.sh` | Provider manifest | `PlatformRuntimePayloadManifest` via `PayloadManifestPath` |

## Windows payload layout

`New-CSweetWindowsRuntimePayload.ps1` publishes four self-contained applications and copies the four
certification inputs into fixed locations:

| Path | Content | Produced by |
|---|---|---|
| `runtime/` | Self-contained `RuntimeHost` publish (includes `appsettings.json` from the project) | `dotnet publish … / CSweet.Office.RuntimeHost.csproj -r win-x64 --self-contained` |
| `helper/` | Hyper-V helper executable | `dotnet publish … / CSweet.Office.Runtime.HyperV.Helper.csproj` |
| `node/` | `CSweet.Office.Node.exe` — the binary whose file version becomes `officeVersion` | `dotnet publish … / CSweet.Office.Node.csproj` |
| `configurator/` | `CSweet.Office.Configurator.exe` | `dotnet publish … / CSweet.Office.Configurator.csproj` |
| `images/csweet-agent-guest.vhdx` and `images/csweet-agent-guest.vhdx.sig` | Certified guest image and detached signature | `-GuestImage`, `-GuestImageSignature` |
| `certificates/guest-image-signing.cer` | Guest image signing certificate | `-GuestImageSigningCertificate` |
| `certification/windows-hyperv.json` | Certification evidence | `-CertificationEvidence` |
| `runtime-manifest.json` | Installer manifest (below) | The script itself |

The MSI build refuses a payload that is missing `runtime-manifest.json`, `runtime\CSweet.Office.RuntimeHost.exe`,
`node\CSweet.Office.Node.exe`, `configurator\CSweet.Office.Configurator.exe`, or
`helper\CSweet.Office.Runtime.HyperV.Helper.exe`, and refuses any payload containing a reparse point.

## Linux payload layout

`new-runtime-payload.sh` (`OUTPUT_ROOT RID FIRECRACKER JAILER VMLINUX INITRD GUEST_EXT4 GUEST_SIG SIGNING_CERT
CERT_THUMBPRINT EVIDENCE SUITE_VERSION CERTIFIED_AT [EXPIRES_AT]`) requires `RID` to be `linux-x64` or
`linux-arm64` and an empty output directory:

| Path | Content |
|---|---|
| `CSweet.Office.RuntimeHost`, `CSweet.Office.Node` | Self-contained single-file publishes, `0755` |
| `CSweet.Office.Runtime.Firecracker.Helper` | Self-contained single-file publish, `0755` |
| `firecracker/firecracker`, `firecracker/jailer` | The supplied binaries, `0755`; the script requires them to report the same version and that version to be `1.14.0` or later |
| `firecracker/vmlinux`, `firecracker/initrd.img` | Kernel and initrd, `0644` |
| `images/csweet-agent-guest.ext4` and `.sig` | Certified guest image and detached signature |
| `certificates/guest-image-signing.cer` | Guest image signing certificate |
| `certification/linux-firecracker.json` | Certification evidence |
| `install-office.sh`, `uninstall-office.sh`, `csweet-office-runtime.service`, `csweet-office-node.service` | Copied from `scripts/linux/` |
| `runtime-manifest.json` | Provider manifest, written with `jq` and `chmod 0644` |

The `csweet-office-runtime.service` unit points the provider at the manifest:

```
Environment=CSweet__Office__Providers__Firecracker__PayloadManifestPath=/opt/csweet/office/runtime-manifest.json
```

## macOS payload layout

`new-runtime-payload.sh` (`OUTPUT_ROOT RID SIGNING_IDENTITY VMLINUX GUEST_IMG GUEST_SIG SIGNING_CERT
CERT_THUMBPRINT EVIDENCE SUITE_VERSION CERTIFIED_AT [EXPIRES_AT]`) requires `RID` to be `osx-x64` or
`osx-arm64`. It publishes `RuntimeHost` and `Node` with .NET, builds the Swift helper with
`swift build --package-path src/CSweet.Office.Runtime.AppleVirtualization.Helper -c release --arch <arch>`, and
then signs all three executables with `codesign --force --timestamp --options runtime --sign <identity>`,
verifying the virtualization entitlement on the helper. Layout:

| Path | Content |
|---|---|
| `CSweet.Office.RuntimeHost`, `CSweet.Office.Node`, `CSweet.Office.Runtime.AppleVirtualization.Helper` | Signed executables, `0755` |
| `apple-virtualization/vmlinux` | Kernel |
| `images/csweet-agent-guest.img` and `.sig` | Certified guest image and detached signature |
| `certificates/guest-image-signing.cer`, `certification/macos-apple-virtualization.json` | Signing certificate and evidence |
| `install-office.sh`, `uninstall-office.sh`, `com.csweet.office.runtime.plist`, `com.csweet.office.node.plist` | Copied from `scripts/macos/` |
| `runtime-manifest.json` | Provider manifest |

The launch agent points at the manifest the same way:

```
<key>CSweet__Office__Providers__AppleVirtualization__PayloadManifestPath</key><string>/Library/Application Support/CSweet/Office/runtime-manifest.json</string>
```

## The per-file SHA-256 list

Every generator writes a `files` array in which each entry is `{ "path": "<relative path>", "sha256": "<digest>" }`.
The path is relative to the payload root, uses `/` separators, and never names `runtime-manifest.json` itself —
Windows excludes the file by name while enumerating, the shell generators use
`find … ! -name runtime-manifest.json`. The Linux and macOS generators sort the records (`sort -z`); Windows
writes them in `Get-ChildItem -File -Recurse` order. File digests are bare lowercase hex in all three
generators; `guestImageDigest` and `certificationEvidenceDigest` carry the `sha256:` prefix.

The Linux and macOS generators also publish the broker protocol version and the provider identity that the
manifest will be validated against: `providerId` is `firecracker-kvm` or `apple-virtualization`,
`providerVersion` is `1.0.0`, and `brokerProtocolVersion` is `1.0`.

## Provider manifest, schema 1

This is the manifest `PlatformRuntimePayloadManifest` consumes. Field names are camelCase on the wire
(`JsonSerializerDefaults.Web`).

| Field | Binds |
|---|---|
| `schemaVersion` | Must be `1`. |
| `providerId`, `providerVersion`, `hostOperatingSystem`, `hostArchitecture` | Must equal the registered `IsolationProviderDescriptor` exactly (the string comparison for OS and architecture is case-insensitive, the others are ordinal). |
| `helperExecutable` | Relative path of the helper; must appear in `files`; its digest becomes `HelperExecutableDigest`. |
| `guestImage`, `guestImageDigest` | Image path and the digest it must hash to. Both the declared file and the field must agree. |
| `guestImageSignature` | Detached signature path; must appear in `files`. |
| `guestImageSigningCertificate`, `guestImageSigningCertificateThumbprint` | Certificate path and pinned thumbprint. The thumbprint must be non-empty. |
| `brokerProtocolVersion` | The broker protocol the payload was certified against; becomes `BrokerProtocolVersion`. |
| `certificationSuiteVersion`, `certificationEvidence`, `certificationEvidenceDigest` | Suite identifier, evidence path, and the digest the evidence file must hash to. |
| `certifiedAt`, `certificationExpiresAt` | Certification window; `certifiedAt` is required, `certificationExpiresAt` may be `null`. |
| `files` | The per-file digest list; 5 to 1000 entries. |

Validation rules applied in order by `PlatformRuntimePayloadManifest.ApplyIfConfigured`:

| Rule | Failure |
|---|---|
| The manifest path must be fully qualified. | `InvalidDataException` |
| The file must exist and be 2 bytes to 1 MiB. | `InvalidDataException` |
| No symbolic link or reparse point anywhere on the path. | `InvalidDataException` |
| 5 to 1000 `files` entries, no duplicate relative path. | `InvalidDataException` |
| Every relative path must be relative, with no `.`, `..`, empty segment, or control character, and must stay inside the package directory. | `InvalidDataException` |
| Every digest must be lowercase SHA-256, with or without the `sha256:` prefix. | `InvalidDataException` |
| Every declared file must exist and hash to its declared digest, compared in fixed time. | `InvalidDataException` |
| The helper, guest image, signature, certificate, and evidence paths must all be declared. | `InvalidDataException` |
| Guest image and evidence digests in the header must equal the declared file digests. | `InvalidDataException` |
| `guestImageSigningCertificateThumbprint`, `certificationSuiteVersion`, and `certifiedAt` must be present. | `InvalidDataException` |

`PlatformRuntimePayloadManifest` records the constraint on itself: *"Loads the immutable platform payload
installed beside RuntimeHost. The manifest never grants additional behavior: it only binds a known provider to
fixed package files and certification metadata after every declared file has passed its SHA-256 check."* In
other words the manifest cannot add a provider, relax a requirement, or introduce a path that the provider
code does not already understand. `PlatformRuntimePayloadManifestTests` pins that behaviour.

## Installer manifest, schema 1 (Windows)

The Windows generator writes the same key set the installer consumes, plus the payload's own version fields.
Windows does not use `PayloadManifestPath`; the installer reads this file directly and writes the Hyper-V
provider settings into `appsettings.json` in the installed version directory.

| Field | Example / source |
|---|---|
| `schemaVersion` | `1`; the installer rejects anything else. |
| `packageVersion` | The `-PackageVersion` argument, checked against `^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$`. |
| `officeVersion` | The published Node executable's file version, formatted with `ToString(3)`. |
| `runtimeHostExecutable`, `helperExecutable`, `officeExecutable`, `configuratorExecutable` | Fixed relative paths (`runtime/…`, `helper/…`, `node/…`, `configurator/…`). |
| `guestImage`, `guestImageDigest`, `guestImageSignature` | `images/csweet-agent-guest.vhdx` and its digest. |
| `guestImageSigningCertificate`, `guestImageSigningCertificateThumbprint` | `certificates/guest-image-signing.cer` and the supplied thumbprint. |
| `certificationSuiteVersion`, `certificationEvidence`, `certificationEvidenceDigest` | Suite, `certification/windows-hyperv.json`, and its digest. |
| `certifiedAt`, `certificationExpiresAt` | Passed in by the certifying caller. |
| `files` | The per-file digest list. |

The installer checks `schemaVersion`, `packageVersion`, both `sha256:` digests, and a `files` count of 1 to
1000, resolves the declared `officeExecutable`, `runtimeHostExecutable`, `helperExecutable`, guest image,
signature, signing certificate, and evidence paths relative to the installed version directory, and copies
declared files whose digest does not already match on disk.

Observation, recorded rather than resolved: `configuratorExecutable` is written by the generator (the MSI build
separately requires `configurator\CSweet.Office.Configurator.exe` to exist), but no other script reads the
field. It is a declared-but-unconsumed manifest key.

## The published-version guard, and one discrepancy

Before it will write a manifest, the Windows generator reads the file version of the published Office
executable and refuses payloads below a fixed floor:

```powershell
if ($publishedNodeVersion -lt [Version]'0.1.0.0') {
    throw "The payload generator published Office $publishedNodeVersion. Version 1.0.2 or later is required for privileged signed-assignment enforcement."
}
```

Two observations, recorded rather than resolved:

- The comparison floor is `0.1.0.0`, while the message names `1.0.2`. The message therefore does not describe
  the range the guard actually accepts. The root `README.md` presents the same floor to operators with the
  wording *"predates reliable signed-assignment delivery"*, and
  `WindowsHyperVOnboardingTests.PayloadGeneratorRejectsPublishedNodeBeforeSignedAssignmentFix` pins the guard
  text and the `officeVersion = $publishedNodeVersion.ToString(3)` derivation.
- `officeVersion` is built from the same file version with three components, so a payload built from
  `0.5.3.0` reports `0.5.3`.

## Where the inputs come from

`Invoke-PlatformRelease.ps1` requires six protected inputs — `CSWEET_GUEST_IMAGE`, `CSWEET_GUEST_IMAGE_SIGNATURE`,
`CSWEET_GUEST_SIGNING_CERTIFICATE`, `CSWEET_GUEST_SIGNING_THUMBPRINT`, `CSWEET_CERTIFICATION_EVIDENCE`, and
`CSWEET_CERTIFICATION_VALID_UNTIL` — and throws *"Missing protected release input $name."* when any is empty.
It passes the guest image, its signature, the certificate, and the evidence through to the platform generator
unchanged, and passes the certification suite version `production-v1` with `certifiedAt` set to the moment the
payload is built. See [04-release-pipeline.md](04-release-pipeline.md).

> **Out of repo:** the hardened runner produces the certified guest image, its signature, and the evidence
> before this entry point runs. Nothing in this repository creates a production guest image; the development
> paths that do create one are described in [03-certification.md](03-certification.md).

## Sources

`scripts/windows/New-CSweetWindowsRuntimePayload.ps1`, `scripts/windows/New-CSweetOfficeMsi.ps1`,
`scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`, `scripts/windows/runtime-manifest.example.json`,
`scripts/linux/new-runtime-payload.sh`, `scripts/linux/new-native-packages.sh`, `scripts/linux/csweet-office-runtime.service`,
`scripts/macos/new-runtime-payload.sh`, `scripts/macos/new-installer-package.sh`, `scripts/macos/com.csweet.office.runtime.plist`,
`src/CSweet.Office.Runtime.Core/PlatformRuntimePayloadManifest.cs`, `src/CSweet.Office.RuntimeHost/appsettings.json`,
`tests/CSweet.Office.Tests/{PlatformRuntimePayloadManifestTests.cs,WindowsHyperVOnboardingTests.cs}`, `docs/GLOSSARY.md`.

Verified: 2026-09-15.
