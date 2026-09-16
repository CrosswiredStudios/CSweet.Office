# Signing and provenance

**Audience:** release engineers, and anyone verifying a downloaded Office asset.

Every published asset is signed, hashed, and attested, and the release carries a machine-readable manifest that
names the contracts version, the payload digest, and the certification validity window. This page records what
each platform script actually does, and which checks are enforced before a release can be created.

## Windows — Authenticode on the MSI

`scripts/windows/New-CSweetOfficeMsi.ps1` signs the MSI and then verifies its own work:

| Step | Command |
|---|---|
| Sign | `signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $OutputPath` |
| Timestamp URL | Default parameter `http://timestamp.digicert.com` |
| Verify | `signtool verify /pa /all $OutputPath` |
| Confirm | `Get-AuthenticodeSignature -LiteralPath $OutputPath` must report `SignatureStatus.Valid`; otherwise *"The MSI signature is not valid: …"* |

The thumbprint must match `^[0-9A-Fa-f]{40,128}$`, and `wix.exe` plus `signtool` must be on the runner. The
payload generator does **not** sign the payload executables — the Authenticode signature produced by the
pipeline covers the MSI package.

## Linux — OpenPGP on the package

`scripts/linux/new-native-packages.sh` requires a signing key id for any signed output and verifies the result:

| Format | Sign | Verify |
|---|---|---|
| `deb` | `dpkg-sig --sign builder -k "$deb_signing_key" "$deb_path"` | `dpkg-sig --verify "$deb_path"` after `dpkg-deb --info` |
| `rpm` | `rpmsign --define "_gpg_name $rpm_signing_key" --addsign "$output_path"` | `rpm --checksig` must end with `digests signatures OK` |

`Invoke-LinuxRelease.ps1` requests `--format deb` only, so the released Linux asset set is the
`csweet-office_<version>_<arch>.deb` built and signed with `CSWEET_LINUX_SIGNING_KEY_ID`. Packaging also
refuses a payload containing symbolic links, and requires `runtime-manifest.json` and the installed script set
to be present.

## macOS — Developer ID, notarization, and stapling

Two scripts split the work.

`scripts/macos/new-runtime-payload.sh` signs the payload binaries before packaging:

```
codesign --force --timestamp --options runtime --sign "$signing_identity" \
  --entitlements "$helper_root/CSweet.AppleVirtualization.entitlements" \
  "$output_root/CSweet.Office.Runtime.AppleVirtualization.Helper"
codesign --force --timestamp --options runtime --sign "$signing_identity" "$output_root/CSweet.Office.RuntimeHost"
codesign --force --timestamp --options runtime --sign "$signing_identity" "$output_root/CSweet.Office.Node"
codesign --verify --strict "$output_root/CSweet.Office.Runtime.AppleVirtualization.Helper"
```

It then extracts the helper's entitlements with `codesign -d --entitlements :-` piped through
`plutil -extract com.apple.security.virtualization raw` and fails unless the value is `true`. The signing
identity is `CSWEET_APPLE_SIGNING_IDENTITY`.

`scripts/macos/new-installer-package.sh` re-verifies the three executables
(`codesign --verify --strict --deep` for the two .NET binaries, `--strict` for the helper), re-checks the
virtualization entitlement, builds a component package with `pkgbuild`, signs the product with
`productbuild --sign "$INSTALLER_SIGNING_IDENTITY"`, and then:

| Step | Command |
|---|---|
| Signature check | `pkgutil --check-signature "$output_pkg"` |
| Notarization | `xcrun notarytool submit "$output_pkg" --keychain-profile "$NOTARY_KEYCHAIN_PROFILE" --wait` |
| Stapling | `xcrun stapler staple` then `xcrun stapler validate` |
| Gatekeeper assessment | `spctl --assess --type install --verbose "$output_pkg"` |

The installer identity and notary profile come from `CSWEET_APPLE_INSTALLER_SIGNING_IDENTITY` and
`CSWEET_APPLE_NOTARY_PROFILE`.

## Software bill of materials

`Invoke-PlatformRelease.ps1` scans the whole release output directory with Syft:

```powershell
if (Get-Command syft -ErrorAction SilentlyContinue) { syft scan "dir:$output" -o "spdx-json=$(Join-Path $output "$OperatingSystem-$Architecture.spdx.json")" }
else { throw 'syft is required to produce the release SBOM.' }
```

Each matrix job therefore contributes `<os>-<arch>.spdx.json`. `verify-release.sh` refuses a release directory
that contains no top-level `*.spdx.json`:

```sh
find "$root" -maxdepth 1 -name '*.spdx.json' -type f -print -quit | grep -q . || {
  echo "No SPDX SBOM was produced." >&2; exit 2;
}
```

## Provenance attestation

The `publish` job requests `id-token: write` and `attestations: write`, and runs
`actions/attest-build-provenance@v2` with `subject-path: 'artifacts/release/*'` after verification succeeds. The
attestation is produced for exactly the files that are about to be uploaded, so any later copy can be checked
against the build that produced it.

## The release manifest

`New-OfficeReleaseManifest.ps1` writes `office-release.json` (UTF-8 without BOM, `ConvertTo-Json -Depth 8`).
`release/office-release.schema.json` describes it; the schema id is `https://c-sweet.com/schemas/office-release-v1.json`.

Top-level fields: `schemaVersion` (`1`), `officeVersion`, `contractsVersion`, `protocolVersion` (default `1.0`),
and `assets` (at least one).

Each asset carries:

| Field | Requirement | Produced from |
|---|---|---|
| `operatingSystem` | `windows`, `linux`, or `macos` | The package extension (`*.msi`, `*.deb`, `*.pkg`). |
| `architecture` | `x64` or `arm64` | The file name (`arm64`/`aarch64` in the name means `arm64`, otherwise `x64`). |
| `packageType` | `msi`, `deb`, `rpm`, or `pkg` | The package extension. |
| `url` | Must match `^https://github\.com/CrosswiredStudios/CSweet\.Office/releases/download/v[0-9]+\.[0-9]+\.[0-9]+/` | The literal `https://github.com/CrosswiredStudios/CSweet.Office/releases/download/v<version>/<file name>`. |
| `size` | Integer ≥ 1 | The file length. |
| `sha256` | `^[0-9a-f]{64}$` | `Get-FileHash -Algorithm SHA256`, lowercased. |
| `signature` | Object with `kind` and `keyId`; `timestamp` may be `null` | `authenticode` for `msi`, `openpgp` for `deb`, `developer-id` for `pkg`; `keyId` is the literal `c-sweet-release`; `timestamp` is written as `null`. |
| `guestImageDigest` | `^sha256:[0-9a-f]{64}$` | The matching payload's `runtime-manifest.json`, found by directory name (`*-payload` with the OS and architecture in it). |
| `certificationValidUntil` | RFC 3339 date-time | The payload manifest's `certificationExpiresAt`. |

Generation fails when a package has no payload evidence behind it: *"Release evidence is missing for
$($file.Name)."*, and *"No installer assets were found."* when nothing matched.

The `url` pattern is the load-bearing part: a release manifest can only point at the canonical GitHub Releases
download location for a `vX.Y.Z` tag. An asset served from anywhere else cannot be described by this schema.

## Verification before publication

`scripts/release/verify-release.sh RELEASE_DIRECTORY` is the gate between the matrix jobs and the GitHub
Release. It fails unless:

1. `office-release.json` and `SHA256SUMS` both exist and are non-empty;
2. `sha256sum --check SHA256SUMS` passes for every listed file;
3. `office-release.json` parses (`python3 -m json.tool`);
4. at least one top-level `*.spdx.json` exists;
5. every `*.msi`, `*.deb`, `*.rpm`, and `*.pkg` in the directory appears by name in `office-release.json`.

`SHA256SUMS` is written by the `publish` job from every file except `SHA256SUMS` itself, with lowercase digests,
two spaces before the name, and ASCII encoding.

## The canonical-origin rule

The root `README.md` states the operator-facing rule under *Releases*: *"Office follows semantic versioning and
has independent `vX.Y.Z` tags. GitHub Releases is the canonical immutable asset origin; c-sweet.com links to
those assets. Office never self-updates. Administrators drain an office to zero active assignments before
running an upgrade."*

`AGENTS.md` adds the process half of the same rule: this repository is an independently versioned installable
deliverable whose tags are not coupled to C-Sweet headquarters tags, and *"Never publish or sign from an
ordinary development runner. Release signing and certification require the hardened platform workflows."*

Consequences for verification work:

| If you have | You can check |
|---|---|
| A downloaded `.msi` | `signtool verify /pa /all`, and compare its SHA-256 with the `sha256` in `office-release.json`. |
| A downloaded `.deb` | `dpkg-sig --verify`, and the same hash comparison. |
| A downloaded `.pkg` | `pkgutil --check-signature`, `stapler validate`, `spctl --assess --type install`, and the same hash comparison. |
| An `office-release.json` | That its `url` values resolve to GitHub Releases for its own `officeVersion`, and that its `guestImageDigest` and `certificationValidUntil` match the payload evidence. |
| A release directory | Re-run `verify-release.sh`. |

## Sources

`scripts/windows/New-CSweetOfficeMsi.ps1`, `scripts/windows/New-CSweetWindowsRuntimePayload.ps1`,
`scripts/linux/new-native-packages.sh`, `scripts/macos/new-runtime-payload.sh`, `scripts/macos/new-installer-package.sh`,
`scripts/release/Invoke-PlatformRelease.ps1`, `scripts/release/Invoke-LinuxRelease.ps1`, `scripts/release/Invoke-MacRelease.ps1`,
`scripts/release/New-OfficeReleaseManifest.ps1`, `scripts/release/verify-release.sh`, `release/office-release.schema.json`,
`.github/workflows/release.yml`, `README.md`, `AGENTS.md`.

Verified: 2026-09-15.
