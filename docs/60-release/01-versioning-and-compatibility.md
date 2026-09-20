# Versioning and compatibility

**Audience:** release engineers, and contributors changing a version number or the `CSweet.Office.Contracts`
pin.

Office follows semantic versioning, carries its own `vX.Y.Z` tags, and never updates itself. A release is cut
from `VersionPrefix`: pushing its change to `main` starts the hosted workflow, which creates the matching
tag during publication. Explicit tag runs also verify that the tag matches the source version.

## The three places version truth lives

| Truth | File and property | Value at the time of writing | Changed by |
|---|---|---|---|
| Office version | `Directory.Build.props` → `VersionPrefix` | `0.6.0` | The contributor cutting the release |
| Contracts version | `Directory.Packages.props` → `PackageVersion Include="CSweet.Office.Contracts"` | `0.7.0` | The contributor taking a new contracts package |
| Release identity | The git tag | `vMAJOR.MINOR.PATCH` | The hosted workflow, or an explicit tag push |

`VersionPrefix` feeds the assembly and file version of every project. The payload generator reads the published
`node/CSweet.Office.Node.exe` file version and publishes it as `officeVersion` in the payload manifest, so the
number an operator sees in a payload is the number the assemblies were built with.

## Only one script enforces agreement

`scripts/release/Get-OfficeReleaseMetadata.ps1` is the single place where the three truths are compared. It:

1. requires the tag to match `^v(?<version>[0-9]+[.][0-9]+[.][0-9]+)$`, otherwise
   *"Release tag must be vMAJOR.MINOR.PATCH."*;
2. reads `VersionPrefix` out of `Directory.Build.props` and throws when the tag and the version differ under
   a case-sensitive comparison: *"Release tag $Tag does not match the runtime version $runtimeVersion in
   Directory.Build.props."*;
3. requires exactly one `CSweet.Office.Contracts` `PackageVersion` entry whose version is
   `MAJOR.MINOR.PATCH`, otherwise *"A single Office contracts package version is required."*;
4. returns `{ Version, ContractsVersion }` for the rest of the pipeline.

The production workflow calls the same script in every `certify-and-package` matrix job (through
`Invoke-PlatformRelease.ps1`) and once in the `publish` job that generates the release manifest. Nothing else
in the repository compares a tag with a build property, and no other file validates the contracts pin.
The hosted development workflow validates `VersionPrefix` through this script before building bundles;
explicit tag runs use its tag validation path.

Downstream, three packaging surfaces also require the version format:

| Script | Requirement |
|---|---|
| `scripts/windows/New-CSweetOfficeMsi.ps1` | `Version` must match `^\d+\.\d+\.\d+$`. |
| `scripts/linux/new-native-packages.sh` | `VERSION` must be numeric dot-separated components of the form `MAJOR.MINOR.PATCH`. |
| `scripts/macos/new-installer-package.sh` | `VERSION` must match `^\d+\.\d+\.\d+$`. |
| `scripts/release/New-OfficeReleaseManifest.ps1` | `officeVersion` and `contractsVersion` must both match `^\d+\.\d+\.\d+$`. |

## Independent versioning

The repository is an independently versioned installable deliverable; its tags are not coupled to C-Sweet
headquarters tags (`AGENTS.md`). The root `README.md` states the same rule for operators: *"Office follows
semantic versioning and has independent `vX.Y.Z` tags."* A C-Sweet release therefore does not imply an Office
release, and an Office release does not imply a C-Sweet release. Compatibility between them is carried by the
contracts version, not by the tag number.

## The contracts compatibility rule

`AGENTS.md` states the rule for changes in the contracts package:

> If `CSweet.Office.Contracts` changes, bump that package using semantic versioning, pack it, update this
> repository and C-Sweet to the same released pin, and verify both with local project references disabled.

In this repository the rule lands in three concrete places:

| Surface | What it records |
|---|---|
| `Directory.Packages.props` | The pinned `CSweet.Office.Contracts` version every project consumes. |
| CI and the release entry point | Every command runs with `-p:UseLocalOfficeContracts=false`, so the package — not a sibling checkout — is what compiles. |
| `office-release.json` | `contractsVersion` is copied from the pin by `New-OfficeReleaseManifest.ps1`, so a published release states which contracts it was built against. |

Release notes carry the operator-facing half of the rule. 0.4.0 says *"Require Office.Contracts 0.6.0. Upgrade
headquarters first, then drain Office and use an identity-preserving upgrade after its active assignments reach
zero."* 0.5.2 says *"Use Office contracts 0.7.0."* When a release needs a specific contracts version on the
headquarters side, the release note is where the ordering is stated. See
[06-release-notes-process.md](06-release-notes-process.md).

> **Out of repo:** the contracts package itself, and the headquarters-side compatibility matrix, live in the
> C-Sweet repository. This repository can only state the pin it compiled against.

## What "Office never self-updates" means for operators

The root `README.md` records the operational model: *"GitHub Releases is the canonical immutable asset origin;
c-sweet.com links to those assets. Office never self-updates. Administrators drain an office to zero active
assignments before running an upgrade."*

Consequences:

| Consequence | Evidence |
|---|---|
| There is no update channel, background updater, or rollback service. Upgrading means installing a newer signed package on the machine. | No updater exists in `src/`; the Windows upgrade path is an MSI install, Linux is `apt install` of the new `.deb`, macOS is the new `.pkg`. |
| Identity is preserved only after a drain with zero active assignments. | `SEC-INV-18` in [../20-security/11-security-invariants.md](../20-security/11-security-invariants.md), and `AGENTS.md`: *"Preserve Office identity on upgrades only after the office is drained and has zero active assignments."* |
| A first install never preserves anything: it removes legacy Execution Node services and enrolls a fresh identity. | `AGENTS.md`; [../20-security/01-identity-and-enrollment.md](../20-security/01-identity-and-enrollment.md). |
| Headquarters still notices an in-place update without re-enrollment, because the running assembly version rides on every heartbeat. | `releases/0.5.2.md`. |
| 0.5.3 narrows the upgrade gate: identity-preserving upgrade is allowed when only saved authorization records and powered-off VMs remain, and stays blocked for active assignment markers, running or saved VMs, unrecognized providers, or unverifiable state. | `releases/0.5.3.md`. |

## Where a version number can appear

| Surface | Source |
|---|---|
| Assembly and file version of every built project | `VersionPrefix` in `Directory.Build.props`. |
| `officeVersion` in the payload `runtime-manifest.json` | `[Version]` file version of the published `node/CSweet.Office.Node.exe`, formatted with `ToString(3)`. |
| MSI product version | `-Version` passed by `Invoke-PlatformRelease.ps1` into `New-CSweetOfficeMsi.ps1`. |
| `csweet-office_<version>_<arch>.deb` file name and package `Version` field | `new-native-packages.sh`. |
| `csweet-office-<version>-macos-<arch>.pkg` | `Invoke-MacRelease.ps1`. |
| `officeVersion` and `contractsVersion` in `office-release.json` | `Get-OfficeReleaseMetadata.ps1` and `New-OfficeReleaseManifest.ps1`. |
| Tag | The hosted workflow creates `vX.Y.Z` at the triggering commit; explicit tag pushes are also supported. |
| Release notes | `releases/<version>.md`. |

## Cutting a version

1. Land the behaviour changes, with tests, on `main`.
2. Set `VersionPrefix` in `Directory.Build.props` to the new version.
3. Add `releases/<version>.md` following the convention in
   [06-release-notes-process.md](06-release-notes-process.md).
4. Confirm the contracts pin in `Directory.Packages.props` is the released package the release requires.
5. Build and test with `-p:UseLocalOfficeContracts=false`.
6. Push the version bump and notes to `main`. The hosted workflow skips published versions, otherwise
   builds and creates `v<version>` and its release automatically. Manual dispatch on `main` can retry
   an unpublished version. See [07-hosted-development-bundles.md](07-hosted-development-bundles.md).

## Sources

`AGENTS.md`, `README.md`, `Directory.Build.props`, `Directory.Packages.props`, `scripts/release/Get-OfficeReleaseMetadata.ps1`,
`scripts/release/Invoke-PlatformRelease.ps1`, `scripts/release/New-OfficeReleaseManifest.ps1`,
`scripts/windows/New-CSweetWindowsRuntimePayload.ps1`, `scripts/windows/New-CSweetOfficeMsi.ps1`,
`scripts/linux/new-native-packages.sh`, `scripts/macos/new-installer-package.sh`, `releases/{0.4.0,0.5.2,0.5.3}.md`,
`.github/workflows/{ci,hosted-release}.yml`, `docs/20-security/11-security-invariants.md`.

Verified: 2026-09-19.
