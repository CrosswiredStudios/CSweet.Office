# release

Release metadata for the independently versioned Office deliverable. This folder holds the JSON Schema that describes the release manifest published with every release.

Part of the C-Sweet Office repository. Full documentation: [`docs/60-release/`](../docs/60-release/README.md).

## office-release.schema.json

JSON Schema (draft 2020-12, id `https://c-sweet.com/schemas/office-release-v1.json`) for `office-release.json`, the manifest that describes every published installer asset.

| Field | Required | Notes |
|---|---|---|
| `schemaVersion` | yes | Const `1`. |
| `officeVersion`, `contractsVersion` | yes | `MAJOR.MINOR.PATCH`; the contracts version is copied from the package pin. |
| `protocolVersion` | yes | 1-32 characters. |
| `assets` | yes, at least one | One entry per installer. |
| `assets[].operatingSystem` | yes | `windows`, `linux`, or `macos`. |
| `assets[].architecture` | yes | `x64` or `arm64`. |
| `assets[].packageType` | yes | `msi`, `deb`, `rpm`, or `pkg`. |
| `assets[].url` | yes | Must match the repository's GitHub Releases download URL pattern. |
| `assets[].size`, `assets[].sha256` | yes | Byte count and 64-character lowercase SHA-256. |
| `assets[].signature` | yes | `kind`, `keyId`, and an optional `timestamp`. |
| `assets[].guestImageDigest` | yes | `sha256:<hex>` of the certified guest image the release carries. |
| `assets[].certificationValidUntil` | yes | Date-time certification expiry. |

## Who writes and reads it

- Written by `scripts/release/New-OfficeReleaseManifest.ps1` during a platform release run.
- Validated by `scripts/release/verify-release.sh` along with `SHA256SUMS`, the SPDX SBOM, and the asset listing.
- Read by anyone verifying or staging a published release: the manifest is what ties an installer download to its digest, signature kind, guest image digest, and certification expiry.

Release signing and certification run only on the hardened platform workflows. Nothing in this repository publishes or signs from a development machine.
