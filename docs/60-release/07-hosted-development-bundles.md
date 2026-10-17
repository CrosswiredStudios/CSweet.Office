# GitHub-hosted development bundles

**Audience:** developers preparing a release or installing without a source checkout.

`hosted-release.yml` runs when a push to `main` changes `Directory.Build.props`, the workflow,
`releases/**`, or `scripts/release/**`. Fixes to these build scripts
therefore retry an unpublished version automatically. Bump `VersionPrefix` and add
`releases/<version>.md` in the same push. The workflow reads
the version through `Get-OfficeReleaseMetadata.ps1`, skips already-published versions before
building, and creates `v<version>` at the triggering commit when publishing. No manual tag push
is needed. Changes to other files alone run ordinary CI without starting a release.

Manual dispatch on `main` and explicit `vMAJOR.MINOR.PATCH` tag pushes remain available. Tag runs
must match `VersionPrefix`. An unpublished tag pointing at another commit is rejected; dispatch
against that tag to release its original source, or bump the version for the new source. Missing
release notes and release-discovery errors fail before expensive builds. Release runs share one
concurrency group so branch and tag runs cannot publish simultaneously. Builds explicitly use the
published Office.Contracts pin. The current hosted release targets **Windows x64 only**:
the `binaries` job builds on `windows-2022`, and the independent `windows-image` job builds its
Hyper-V VHDX on `ubuntu-24.04`. Publishing waits for both. Hyper-V still uses a Linux guest OS;
the Ubuntu image-builder runner is required for the Windows bundle. Firecracker and Linux binary
builds are deferred and cannot consume the Windows image job's 90-minute timeout. No repository
secrets or signing identity are required.

`Publish-HostedOfficeAssets.ps1 -WindowsOnly` packages the downloaded `binaries-win-x64` and
`windows-guest` artifacts. Its manifest contains exactly the Windows support and full bundle;
the schema requires one of each, with the existing size/hash/development-certification fields.
The combined packaging path remains available without that switch for future Linux releases.
In that path, `Publish-HostedOfficeAssets.ps1` restores execute permission for the explicitly named Linux
Office apphosts, certification runner, probes, Firecracker tools, and shell scripts after artifact
download. `Test-HostedManifest.ps1` checks those modes after extracting the final tarball and
keeps configuration files non-executable. This corrects the Linux 0.6.0 archive, whose dotted
Office executable names were skipped by the former extension-based check.

Before packaging, the Windows binaries job runs independent Office tests against the
published Contracts package. It clears the inherited PowerShell 7 `PSModulePath` before
launching .NET and uploads `office-tests-win-x64` TRX diagnostics even on a test failure.
These are source tests, separate from destination-host guest certification. Source:
`.github/workflows/hosted-release.yml`; verified 2026-10-16.

The release contains:

- `office-bootstrap.json`: schema 1 for single-file assets, or schema 2 when an archive is multipart; Office/contracts/protocol versions, development signing label,
  mandatory target-host certification, and exact immutable asset URLs, sizes, and SHA-256 hashes.
- `csweet-office-<version>-windows-support.zip`: small preflight and installation scripts.
- `csweet-office-<version>-windows-x64.zip` when at most 1 GiB, otherwise numbered
  `.zip.part001`, `.zip.part002`, and subsequent parts: scripts, self-contained components,
  guest probe, certification runner, and a Hyper-V VHDX.
- `SHA256SUMS`: checksums for every published file above.

## Multipart transport

`OfficeBundleTransport.ps1` streams archives larger than 1 GiB into parts of at most
1 GiB. At most 16 parts and 16 GiB per logical archive are accepted. The small Windows
support ZIP stays a single file capped at 10 MiB. `PartSizeBytes` permits smaller release
transport fixtures in tests; production defaults to 1 GiB.

The schema 2 `assets` list still describes logical archives. Each multipart record carries
the complete archive's name, byte count and SHA-256, plus ordered `parts` records containing
each numbered name, exact size, SHA-256 and immutable URL under the same release tag. It has
no logical archive download URL. The publisher removes the unsplit ZIP after producing all
parts, so the workflow uploads only transport files, the bootstrap manifest and `SHA256SUMS`.
The latter lists physical part checksums; the complete archive digest lives in the manifest.
Single-file-only releases retain schema 1.

> **Out of repo:** automatic reconstruction is implemented in Headquarters' multipart-capable
> `CSweet.OfficeRelease.ps1`. Upgrade Headquarters before installing schema 2 Office 0.11.0
> bundles. The protocol boundary is defined here by `release/office-bootstrap.schema.json`
> and `releases/0.11.0.md`. Host certification remains a destination-host operation.

`Test-HostedManifest.ps1` tests the actual publisher with small multipart fixtures, checks
the schema and physical checksums, and reconstructs the original ZIP. Supplying
`-InstallerScript ../csweet/src/CSweet.Api/Setup/CSweet.OfficeRelease.ps1` additionally exercises
Headquarters discovery and automatic installation-file extraction using local download
fixtures. `Test-OfficeBundleTransport.ps1` covers exact boundaries, short final parts and
the 16-part bound. These tests do not publish a release or install an Office service.

## Guest image construction

Linux tarballs are absent from current hosted releases. A future Linux image job should have its
own timeout, artifact upload and retry boundary before being added back to publication; merely
raising the combined job timeout does not isolate Firecracker failures.

The image job installs `linux-image-generic`, copies its kernel to a runner-readable temporary
file, and sets `SUPERMIN_KERNEL`, `SUPERMIN_KERNEL_VERSION`, and `SUPERMIN_MODULES` to that kernel
and its matching module tree. The runner removes the optional `passt` package so libguestfs uses
QEMU's built-in SLIRP networking for image construction. This avoids Ubuntu's `passt` AppArmor
profile rejecting libguestfs's temporary PID files, without disabling AppArmor. `libguestfs-test-tool`
checks the appliance, then `guestfish --network` launches an appliance with a scratch disk and
requires a nonempty `/etc/resolv.conf` and successful DNS lookups for `archive.ubuntu.com` and
`security.ubuntu.com`. The runner explicitly installs `isc-dhcp-client`, which Ubuntu 24.04's
supermin package list includes; its default host DHCP dependency can otherwise leave the
appliance without a DHCP executable. Both checks run before image download or guest compilation.
The network check verifies DNS, while the later package installation verifies package-server access.
This avoids relying on the runner's Azure kernel or root-only kernel file permissions.
Debug/trace logging is enabled for appliance preflight and disabled for the image build to avoid
logging every payload transfer chunk. Failed jobs upload `image-build-diagnostics` with
the preflight and image build logs for seven days. These logging settings affect only the build runner;
the shipped guest continues to use its own Ubuntu kernel and target-host certification.

The Ubuntu job customizes a checksum-verified Canonical cloud image with the existing Hyper-V
provisioner and converts it to VHDX. `build-hosted-hyperv-image.sh` uploads the provisioner,
runs it explicitly with `/bin/bash` through `--run-command`, and removes the temporary script.
This preserves Bash features such as `pipefail`; `virt-customize --run` passes script contents
through the guest's `/bin/sh`. `build/provision-development.sh` sets a fixed provisioning `PATH`
including `/usr/local/bin`, where it installs Node/npm and the .NET launcher. The non-login
customization shell can otherwise report `node: command not found` during version verification
after successful extraction. This provisioning environment is separate from the workload
environment allow-list (SEC-INV-21). It does not execute Hyper-V certification. The deferred Firecracker
guest builder is `new-firecracker-guest.sh`, which explicitly installs `initramfs-tools` and generates
an initrd matching the selected kernel. Its temporary `/proc` and `/sys` mounts are removed
before ext4 assembly. `Publish-HostedOfficeAssets.ps1` rejects assets at or above
GitHub's 2 GiB per-file limit. The workflow uploads a draft first and publishes only after upload
succeeds. Published releases are skipped; a release appearing during the build fails publication
rather than replacing assets. A failed draft needs inspection/removal before retrying the same
unpublished version. Deleting a draft does not remove its tag; retries must use the same tagged
commit, or a new version.

## Signing and installation

Office 0.9.3 finalizes the runtime DNS link in
`scripts/release/build-hosted-hyperv-image.sh` with guestfish filesystem operations
after `virt-customize`, and verifies it before VHDX export. libguestfs temporarily
substitutes and restores `/etc/resolv.conf` around chroot commands; setting the link
inside `provision-development.sh` therefore did not survive hosted customization.
Offline inspection of the published 0.9.2 image found Ubuntu's
`../run/systemd/resolve/stub-resolv.conf` link. Its real Hyper-V probe passed direct
public TCP/UDP and destination-denial checks but npm failed lookup with `EAI_AGAIN`.
The source/Packer path does not use that chroot substitution lifecycle.

Office 0.9.2 corrects the SDK launcher path in `build/provision-development.sh`.
The hosted cloud image has no distro SDK at `/usr/bin/dotnet`; 0.9.1 only created
`/usr/local/bin/dotnet`, while `GuestProbe.DotNetSdkPresentAsync` checks the former.
Both launchers now resolve to the pinned SDK, and provisioning verifies `/usr/bin/dotnet`
directly. `scripts/tests/test-development-dotnet-path.sh` executes this provisioning block
inside a disposable SDK container and checks both launchers as an unprivileged user.
This regression is not destination-host certification; a rebuilt image still requires it.

Office 0.9.1 stages `Prepare-CSweetDevelopmentPayload.ps1` before publishing the hosted Windows
guest. The existing `provision-guest.sh` runs `provision-development.sh` when that checksum-pinned
payload is present, installing Node/npm, .NET, Python and the fixed TUN adapter. The completed
VHDX is paired with `guest-image-profile.json`. `Publish-HostedOfficeAssets.ps1` checks the image
hash before copying it into a fresh binary artifact directory, then carries `environmentProfiles`
and `guestBrokerProtocolVersion` into the bootstrap manifest and support/full bundle metadata.
Missing evidence or a substituted image prevents packaging. These declarations select an image;
they do not supply certification authority (SEC-INV-10).

The 0.9.0 hosted image omitted the development payload, despite Office binaries supporting
protocol 1.1. A destination requesting internet certification could complete the restricted
and builder checks, then lose the guest channel during internet startup. Updating only the
binary version or certification switches cannot add missing guest tools.

The archives carry release checksums, not an Authenticode production signature. Target-host
certification is mandatory (`SEC-INV-10`). Windows runs `Initialize-CSweetWindowsIsolationTest.ps1
-PrebuiltRoot <bundle>`; Linux runs `initialize-firecracker-test.sh --prebuilt-root <bundle>`.
Both paths use the supplied binaries and image, execute real no-network certification VMs, and
create development signing material locally. No production signing key is needed. Certification
failure prevents installation. The Windows prebuilt path uses its unique extraction and certification
directories directly and does not wait on the source-build mutex; only explicit source fallback is
serialized with other source builds. Existing upgrade and identity rules still apply (`SEC-INV-18`).

> **Out of repo:** C-Sweet's `CSweet.OfficeRelease.ps1` discovers up to 20 recent GitHub releases,
> skips drafts/prereleases and releases without compatible bundles, verifies downloads, and rejects
> unsafe ZIP entries. `Start-CSweetDevelopmentOfficeSetup.ps1` uses the small support package for
> preflight, redeems the one-use handoff, then downloads the large bundle. Source fallback applies
> when discovery is unavailable; integrity failures never trigger fallback. Its enrollment-ready
> endpoint refreshes the short claim window after preparation using the machine-bound receipt.

> **Out of repo:** The development setup consumer skips ordinary-only manifests without the
> required profile/protocol declaration before download. When none is compatible, it uses the
> already configured local source preparation path. Integrity failures still prevent fallback.
> The source path stages the pinned development payload and performs real destination certification.

For a previously published Ubuntu 24.04 x64 Office bundle, download the tarball and `SHA256SUMS` from the same tag, verify the
selected tarball with `sha256sum`, and extract into a new directory. Run as root on a cgroup v2 host
with working KVM. `jq`, `openssl`, `tar`, and normal system installation tools remain prerequisites;
.NET, a compiler, and an Office source checkout are not required.

```sh
# From the extracted bundle directory; the one-use enrollment code is requested securely.
sudo bash scripts/linux/initialize-firecracker-test.sh \
  --prebuilt-root "$PWD" --control-plane https://headquarters.example
```

For disconnected source development, retain the existing checkout and all cached build dependencies.
Air-gapped fallback does not provision missing SDKs, Ubuntu media, or packages.

## Validation and handoff

Build/test the independent solution with `-p:UseLocalOfficeContracts=false`. Run the PowerShell
release consumer checks in C-Sweet (`tests/OfficeRelease.Tests.ps1`) and Office's prebuilt payload
checks (`scripts/tests/Test-PrebuiltPayload.ps1` and `scripts/tests/Test-HostedManifest.ps1`). After publishing, validate both image boot paths
on actual target hosts. Hosted-image construction is not a substitute for those checks.

`Test-PrebuiltPayload.ps1` supplies JSON certification fixtures for guest broker protocols 1.0 and 1.1.
It checks protocol propagation into `runtime-manifest.json`, executable version, copied files and hashes,
and throws if packaging invokes `dotnet`. The fixtures test packaging only; they are not hardware
certification evidence. The hosted workflow runs this check with Windows PowerShell.

The signed production workflow `release.yml` is manual-only and retains its hardened runner and
signing requirements. `office-release.json` remains its separate production-installer manifest;
the development workflow does not label its archives as signed MSI/DEB installers.

## Sources

`.github/workflows/{hosted-release,release}.yml`,
`scripts/release/{Get-OfficeReleaseMetadata,Build-HostedOfficeBundle,Publish-HostedOfficeAssets,OfficeBundleTransport}.ps1`,
`scripts/release/build-hosted-hyperv-image.sh`, `release/office-bootstrap.schema.json`,
`build/provision-development.sh`,
`src/CSweet.Office.GuestProbe/Program.cs`, `scripts/tests/test-development-dotnet-path.sh`,
`src/CSweet.Office.GuestProbe/CertificationCommand.cs`,
`scripts/windows/{Initialize-CSweetWindowsIsolationTest,New-CSweetWindowsRuntimePayload}.ps1`,
`scripts/tests/{Test-PrebuiltPayload,Test-HostedManifest,Test-OfficeBundleTransport}.ps1`, `releases/0.11.0.md`,
`scripts/linux/{new-firecracker-guest,initialize-firecracker-test,new-runtime-payload}.sh`.

Verified: 2026-10-16. Multipart fixtures and the cross-repository publisher-to-installer check
passed locally; real hosted publication and installed-host certification were not run for
this transport change. The DNS restoration mechanism is in upstream libguestfs
[`daemon/sh.c`](https://github.com/libguestfs/libguestfs/blob/v1.52.0/daemon/sh.c),
`set_up_etc_resolv_conf` / `free_resolver_state`. `Test-PrebuiltPayload.ps1` passed under Windows PowerShell and PowerShell 7
for both broker protocol fixtures. `Test-HostedManifest.ps1` exercises Windows-only and combined packaging,
required Windows assets, profile propagation, image-bound evidence, rejected missing/substituted
images, hashes and archive image placement. `Test-DevelopmentBuildCoordination.ps1`
covers prebuilt bypass and source-build exclusion; target-host certification remains mandatory.
