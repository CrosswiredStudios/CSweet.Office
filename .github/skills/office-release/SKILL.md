---
name: office-release
description: "Use when preparing, validating, or troubleshooting a C-Sweet Office release — reconciling the three version truths, running the independent-solution boundary check, understanding the hardened runner matrix and required secrets, the office-release.json manifest and its schema, signing and SBOM verification, or writing releases/*.md notes."
---

# Office releases

Office is an independently versioned installable deliverable. It has its own `vX.Y.Z` tags, its own release
assets, and its own signing requirements. It never self-updates.

**This repository never publishes or signs from a development runner.** Signing and certification happen only
on the hardened platform workflows.

## The three version truths

| Value | Location |
|---|---|
| Office version | `VersionPrefix` in `Directory.Build.props` |
| Contracts pin | `CSweet.Office.Contracts` `PackageVersion` in `Directory.Packages.props` |
| Release tag | the git tag |

Only `scripts/release/Get-OfficeReleaseMetadata.ps1` enforces agreement: the tag must equal `VersionPrefix`, and
it reports the pinned contracts version alongside the version.

Office tags are deliberately **not** coupled to C-Sweet headquarters tags. Do not introduce that coupling.

## The boundary check

CI and every release job build with the switch forcing the published package:

```powershell
dotnet restore CSweet.Office.Independent.slnx -p:UseLocalOfficeContracts=false
dotnet build   CSweet.Office.Independent.slnx -c Release --no-restore -p:UseLocalOfficeContracts=false
dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release --no-build -p:UseLocalOfficeContracts=false
```

`UseLocalOfficeContracts` auto-defaults to `true` whenever a sibling `../CSweet.Office.Contracts` checkout
exists on disk. Omitting the switch means you validated a local working tree, not the released artifact.

## The pipeline

`.github/workflows/ci.yml` runs on pull requests and pushes to `main`: build the independent solution, then
test.

`.github/workflows/release.yml` runs on `v*.*.*` tags:

1. A matrix of hardened self-hosted runners in the `production-signing` environment, one job per OS and
   architecture, each calling `scripts/release/Invoke-PlatformRelease.ps1`.
2. A `publish` job in the `production-release` environment that generates `office-release.json` and
   `SHA256SUMS`, runs `scripts/release/verify-release.sh`, attests build provenance, and creates the GitHub
   Release.

Required inputs come from repository secrets and variables, including `CSWEET_GUEST_IMAGE`,
`CSWEET_GUEST_IMAGE_SIGNATURE`, `CSWEET_GUEST_SIGNING_CERTIFICATE`, `CSWEET_GUEST_SIGNING_THUMBPRINT`,
`CSWEET_CERTIFICATION_EVIDENCE`, `CSWEET_CERTIFICATION_VALID_UNTIL`, the signing identities, and the platform
toolchain variables (`CSWEET_FIRECRACKER`, `CSWEET_JAILER`, `CSWEET_GUEST_KERNEL`, `CSWEET_GUEST_INITRD`).

Full tables are in
[`../../docs/60-release/04-release-pipeline.md`](../../docs/60-release/04-release-pipeline.md).

## The release manifest

`New-OfficeReleaseManifest.ps1` produces `office-release.json`, validated against
`release/office-release.schema.json`. Every asset records:

| Field | Requirement |
|---|---|
| `operatingSystem` | `windows`, `linux`, or `macos` |
| `architecture` | `x64` or `arm64` |
| `packageType` | `msi`, `deb`, `rpm`, or `pkg` |
| `url` | must match the GitHub Releases download URL for the same tag |
| `size`, `sha256` | of the asset |
| `signature` | `{ kind, keyId, timestamp }` |
| `guestImageDigest` | `sha256:<64 lowercase hex>` |
| `certificationValidUntil` | from the certification evidence |

GitHub Releases is the canonical immutable asset origin; c-sweet.com links to those assets rather than hosting
its own copies.

## Release notes

One file per version in `releases/`, named for the version. The observed style is short: the user-visible
change, any deployment ordering requirement (for example "deploy the Headquarters endpoints and
Office.Contracts X before upgrading Office"), and any operator prerequisite (drain first, rebuild guest images).
See [`../../docs/60-release/06-release-notes-process.md`](../../docs/60-release/06-release-notes-process.md) for
the 0.4.0–0.5.3 examples.

## Prerequisites an operator must satisfy

- **Drain before upgrade.** Identity is preserved on upgrade only when the office is draining with zero active
  assignments (`SEC-INV-18`).
- **Contracts first.** If the release requires a newer `CSweet.Office.Contracts`, Headquarters must be deployed
  before Office is upgraded.
- **Certification must be current.** An expired certification window stops work rather than degrading
  (`SEC-INV-10`).
- **Never reuse a stale certification.** A guest-image or helper change invalidates the certified
  configuration.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `NU1101` for `CSweet.Office.Contracts` | The pinned version is not on the feed the runner can reach. |
| Release job refuses to start | The tag does not equal `VersionPrefix`. |
| `verify-release.sh` fails | Checksums, JSON validity, a missing SPDX SBOM, or an asset missing from the manifest. |
| macOS job fails | The signing identity or notary profile is missing, or the Swift helper could not be built. |

Do not weaken a verification step to make a release succeed. If a check fails, the artifact is wrong.

## Where the detail lives

- [`../../docs/60-release/01-versioning-and-compatibility.md`](../../docs/60-release/01-versioning-and-compatibility.md)
- [`../../docs/60-release/02-payload-and-manifest.md`](../../docs/60-release/02-payload-and-manifest.md)
- [`../../docs/60-release/03-certification.md`](../../docs/60-release/03-certification.md)
- [`../../docs/60-release/05-signing-and-provenance.md`](../../docs/60-release/05-signing-and-provenance.md)
- [`../../docs/50-development/03-contracts-dependency.md`](../../docs/50-development/03-contracts-dependency.md)
