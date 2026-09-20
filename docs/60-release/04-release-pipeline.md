# Release pipeline

**Audience:** release engineers, and reviewers of `.github/workflows/`.

The default tagged release path is now [GitHub-hosted development bundles](07-hosted-development-bundles.md).
It needs no signing secrets and certifies/signs locally on each destination host. `ci.yml` verifies
the published contracts boundary. The production `release.yml` workflow described below is
manual-only and still requires hardened runners and production signing inputs.

**Historical detail:** the production publish table below predates its current `gh release` commands.
Use `.github/workflows/release.yml` as the authority; it is not the hosted development pipeline.

## `ci.yml`

| Property | Value |
|---|---|
| Workflow name | `ci` |
| Triggers | `pull_request`; `push` to `main` |
| Permissions | `contents: read` |
| Runner | `ubuntu-latest` |
| .NET | `actions/setup-dotnet@v4` with `dotnet-version: '10.0.x'` |
| Job | `build-test` |

| Step | Command |
|---|---|
| Checkout | `actions/checkout@v4` |
| Restore | `dotnet restore CSweet.Office.Independent.slnx -p:UseLocalOfficeContracts=false` |
| Build | `dotnet build CSweet.Office.Independent.slnx -c Release --no-restore -p:UseLocalOfficeContracts=false` |
| Test | `dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release --no-build -p:UseLocalOfficeContracts=false` |

Every command passes `-p:UseLocalOfficeContracts=false`, so CI compiles against the pinned package rather than a
sibling checkout. The test step uses `--no-build`, which relies on the test project being part of the
independent solution — see `docs/10-system/04-solution-map.md`. CI runs no packaging, signing, or certification
step; those are release-only.

## `release.yml`

| Property | Value |
|---|---|
| Workflow name | `signed-release` |
| Trigger | `workflow_dispatch` on main or a matching release tag |
| Permissions | `contents: write`, `id-token: write`, `attestations: write` |
| Jobs | `certify-and-package` (matrix), `publish` |

### `certify-and-package`

`strategy.fail-fast: true`, one job per matrix entry, all in the `production-signing` environment:

| OS | Architecture | Runner label |
|---|---|---|
| `windows` | `x64` | `csweet-hardened-windows-signing` |
| `linux` | `x64` | `csweet-hardened-linux-x64` |
| `linux` | `arm64` | `csweet-hardened-linux-arm64` |
| `macos` | `x64` | `csweet-hardened-macos-x64` |
| `macos` | `arm64` | `csweet-hardened-macos-arm64` |

Steps:

1. `actions/checkout@v4`.
2. `actions/setup-dotnet@v4` with `dotnet-version: '10.0.x'`.
3. **Build, test, certify, package, sign, and emit SBOM** — `shell: pwsh`, running
   `./scripts/release/Invoke-PlatformRelease.ps1 -OperatingSystem '<os>' -Architecture '<arch>' -Tag '${{ github.ref_name }}'`
   with the secrets and variables below in the environment.
4. `actions/upload-artifact@v4` with name `release-<os>-<arch>`, path `artifacts/release/*`.

### `publish`

`needs: certify-and-package`, `runs-on: ubuntu-latest`, `environment: production-release`:

| Step | What it does |
|---|---|
| `actions/checkout@v4` | Checks out the tagged commit so the metadata scripts can read the build properties. |
| `actions/download-artifact@v4` | Downloads every matrix artifact into `artifacts/release` with `merge-multiple: true`. |
| Generate release manifest and consolidated checksums | Runs `Get-OfficeReleaseMetadata.ps1` for the tag, then `New-OfficeReleaseManifest.ps1 -Version … -ContractsVersion … -AssetDirectory artifacts/release -OutputPath artifacts/release/office-release.json`, then writes `SHA256SUMS` with lowercase digests and two spaces before each name, ASCII encoded. |
| `bash ./scripts/release/verify-release.sh artifacts/release` | Verifies `office-release.json`, `SHA256SUMS`, every checksum, and the SPDX SBOM. |
| `actions/attest-build-provenance@v2` | Attests `subject-path: 'artifacts/release/*'`. |
| `softprops/action-gh-release@v2` | Creates the GitHub Release with `make_latest: true` and `fail_on_unmatched_files: true`, uploading `*.msi`, `*.deb`, `*.pkg`, `*.img`, `*.sig`, `*.json`, `*.zip`, and `SHA256SUMS`. |

## Required secrets

Secrets are repository secrets referenced as `${{ secrets.… }}` and passed to the matrix step as environment
variables of the same name.

| Secret | Consumers |
|---|---|
| `CSWEET_WINDOWS_SIGNING_THUMBPRINT` | Windows: `New-CSweetOfficeMsi.ps1 -CertificateThumbprint` for the Authenticode signature. |
| `CSWEET_LINUX_SIGNING_KEY_ID` | Linux: `new-native-packages.sh --deb-signing-key` for the `dpkg-sig` signature. |
| `CSWEET_APPLE_SIGNING_IDENTITY` | macOS: `codesign --sign` for `RuntimeHost`, `Node`, and the Swift helper. |
| `CSWEET_APPLE_INSTALLER_SIGNING_IDENTITY` | macOS: `productbuild --sign` for the `.pkg`. |
| `CSWEET_APPLE_NOTARY_PROFILE` | macOS: `xcrun notarytool submit --keychain-profile`. |

## Required variables

Variables are repository variables referenced as `${{ vars.… }}` and passed as environment variables of the
same name.

| Variable | Consumers |
|---|---|
| `CSWEET_FIRECRACKER` | Linux: path to the `firecracker` binary. |
| `CSWEET_JAILER` | Linux: path to the `jailer` binary. |
| `CSWEET_GUEST_KERNEL` | Linux and macOS: path to `vmlinux`. |
| `CSWEET_GUEST_INITRD` | Linux: path to the initrd. |
| `CSWEET_GUEST_IMAGE` | All platforms: the certified guest image. |
| `CSWEET_GUEST_IMAGE_SIGNATURE` | All platforms: the detached image signature. |
| `CSWEET_GUEST_SIGNING_CERTIFICATE` | All platforms: the image signing certificate. |
| `CSWEET_GUEST_SIGNING_THUMBPRINT` | All platforms: the pinned signing certificate thumbprint. |
| `CSWEET_CERTIFICATION_EVIDENCE` | All platforms: the certification evidence file. |
| `CSWEET_CERTIFICATION_VALID_UNTIL` | All platforms: the expiry written into the payload manifest. |

`Invoke-PlatformRelease.ps1` pre-checks six of these directly — `CSWEET_GUEST_IMAGE`,
`CSWEET_GUEST_IMAGE_SIGNATURE`, `CSWEET_GUEST_SIGNING_CERTIFICATE`, `CSWEET_GUEST_SIGNING_THUMBPRINT`,
`CSWEET_CERTIFICATION_EVIDENCE`, `CSWEET_CERTIFICATION_VALID_UNTIL` — and throws *"Missing protected release
input $name."* when any is empty. The remaining platform inputs are read from the environment by the platform
entry points without an explicit pre-check, so a missing value surfaces as a failed payload or package build on
that platform.

## Pipeline stages

| Stage | Where | What happens | If it fails |
|---|---|---|---|
| Tag validation | Every matrix job, and again in `publish` | `Get-OfficeReleaseMetadata.ps1` requires `vMAJOR.MINOR.PATCH`, equality with `VersionPrefix`, and exactly one contracts `PackageVersion`. | Nothing is built or published. |
| Unit tests | Matrix job | `dotnet test CSweet.Office.Independent.slnx -c Release -p:UseLocalOfficeContracts=false`. | *"Release tests failed."* |
| Symbol archive | Matrix job | Every `src/**/*.pdb` under a `\Release\` path is compressed to `csweet-office-<version>-<os>-<arch>-symbols.zip` when any exist. | Skipped when there are no symbols; otherwise a failure stops the job. |
| Protected input check | Matrix job | The six required environment variables must be non-empty. | *"Missing protected release input $name."* |
| Payload build | Matrix job | Windows: `New-CSweetWindowsRuntimePayload.ps1` with suite `production-v1`. Linux/macOS: `new-runtime-payload.sh` with the Firecracker or Apple inputs. | The platform entry point throws (`'Linux payload creation failed.'`, `'macOS payload creation failed.'`). |
| Installer build and signing | Matrix job | Windows: `New-CSweetOfficeMsi.ps1`. Linux: `new-native-packages.sh … --format deb`. macOS: `new-installer-package.sh` (sign, notarize, staple, assess). | The job throws at the first failed step. |
| SBOM | Matrix job | `syft scan "dir:$output" -o spdx-json=<os>-<arch>.spdx.json`. | `'syft is required to produce the release SBOM.'` |
| Manifest and checksums | `publish` | `New-OfficeReleaseManifest.ps1` plus `SHA256SUMS`. | No release is created. |
| Verification | `publish` | `verify-release.sh` re-checks the checksums, the JSON, the SBOM, and that every package appears in the manifest. | No release is created. |
| Attestation and publish | `publish` | Build provenance attestation, then the GitHub Release. | No release, or a partial release that must be deleted. |

Platform limits observed by the scripts: `Invoke-PlatformRelease.ps1` accepts only `windows`, `linux`, or
`macos` with `x64` or `arm64`, and rejects Windows `arm64` with *"Windows release currently supports x64
only."*; `Invoke-LinuxRelease.ps1` builds `deb` output only; `new-runtime-payload.sh` (Linux) requires a
Firecracker and jailer pair from the same release at `1.14.0` or later.

## Sources

`.github/workflows/ci.yml`, `.github/workflows/release.yml`, `scripts/release/Invoke-PlatformRelease.ps1`,
`scripts/release/Invoke-LinuxRelease.ps1`, `scripts/release/Invoke-MacRelease.ps1`,
`scripts/release/Get-OfficeReleaseMetadata.ps1`, `scripts/release/New-OfficeReleaseManifest.ps1`,
`scripts/release/verify-release.sh`, `scripts/windows/New-CSweetOfficeMsi.ps1`, `scripts/linux/new-native-packages.sh`,
`scripts/macos/new-installer-package.sh`, `docs/10-system/04-solution-map.md`.

Verified: 2026-09-19.
