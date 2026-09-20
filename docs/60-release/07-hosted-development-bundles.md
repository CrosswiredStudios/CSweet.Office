# GitHub-hosted development bundles

**Audience:** developers preparing a release or installing without a source checkout.

`hosted-release.yml` runs when a push to `main` changes `Directory.Build.props` or the workflow
itself. Bump `VersionPrefix` and add `releases/<version>.md` in the same push. The workflow reads
the version through `Get-OfficeReleaseMetadata.ps1`, skips already-published versions before
building, and creates `v<version>` at the triggering commit when publishing. No manual tag push
is needed. Changes to other files alone run ordinary CI without starting a release.

Manual dispatch on `main` and explicit `vMAJOR.MINOR.PATCH` tag pushes remain available. Tag runs
must match `VersionPrefix`. An unpublished tag pointing at another commit is rejected; dispatch
against that tag to release its original source, or bump the version for the new source. Missing
release notes and release-discovery errors fail before expensive builds. Release runs share one
concurrency group so branch and tag runs cannot publish simultaneously. Builds explicitly use the
published Office.Contracts pin. Windows x64 binaries build on `windows-2022`; Linux x64 binaries
and guest images build on `ubuntu-24.04`. No repository secrets or signing identity are required.

The release contains:

- `office-bootstrap.json`: schema 1, Office/contracts/protocol versions, development signing label,
  mandatory target-host certification, and exact immutable asset URLs, sizes, and SHA-256 hashes.
- `csweet-office-<version>-windows-support.zip`: small preflight and installation scripts.
- `csweet-office-<version>-windows-x64.zip`: scripts, self-contained components, guest probe,
  certification runner, and a Hyper-V VHDX.
- `csweet-office-<version>-linux-x64.tar.gz`: scripts, components, Firecracker/jailer tools,
  probe, kernel, initrd, and guest filesystem.
- `SHA256SUMS`: checksums for every published file above.

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
Debug/trace logging stays enabled; failed jobs upload `image-build-diagnostics` with
the preflight and image build logs for seven days. These changes affect only the build runner;
the shipped guest continues to use its own Ubuntu kernel and target-host certification.

The Ubuntu job customizes a checksum-verified Canonical cloud image with the existing Hyper-V
provisioner and converts it to VHDX. It does not execute Hyper-V certification. The Firecracker
guest uses `new-firecracker-guest.sh`. `Publish-HostedOfficeAssets.ps1` rejects assets at or above
GitHub's 2 GiB per-file limit. The workflow uploads a draft first and publishes only after upload
succeeds. Published releases are skipped; a release appearing during the build fails publication
rather than replacing assets. A failed draft needs inspection/removal before retrying the same
unpublished version. Deleting a draft does not remove its tag; retries must use the same tagged
commit, or a new version.

## Signing and installation

The archives carry release checksums, not an Authenticode production signature. Target-host
certification is mandatory (`SEC-INV-10`). Windows runs `Initialize-CSweetWindowsIsolationTest.ps1
-PrebuiltRoot <bundle>`; Linux runs `initialize-firecracker-test.sh --prebuilt-root <bundle>`.
Both paths use the supplied binaries and image, execute real no-network certification VMs, and
create development signing material locally. No production signing key is needed. Certification
failure prevents installation. Existing upgrade and identity rules still apply (`SEC-INV-18`).

> **Out of repo:** C-Sweet's `CSweet.OfficeRelease.ps1` discovers up to 20 recent GitHub releases,
> skips drafts/prereleases and releases without compatible bundles, verifies downloads, and rejects
> unsafe ZIP entries. `Start-CSweetDevelopmentOfficeSetup.ps1` uses the small support package for
> preflight, redeems the one-use handoff, then downloads the large bundle. Source fallback applies
> when discovery is unavailable; integrity failures never trigger fallback. Its enrollment-ready
> endpoint refreshes the short claim window after preparation using the machine-bound receipt.

For an Ubuntu 24.04 x64 Office, download the tarball and `SHA256SUMS` from the same tag, verify the
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

The signed production workflow `release.yml` is manual-only and retains its hardened runner and
signing requirements. `office-release.json` remains its separate production-installer manifest;
the development workflow does not label its archives as signed MSI/DEB installers.

## Sources

`.github/workflows/{hosted-release,release}.yml`,
`scripts/release/{Get-OfficeReleaseMetadata,Build-HostedOfficeBundle,Publish-HostedOfficeAssets}.ps1`,
`scripts/release/build-hosted-hyperv-image.sh`, `release/office-bootstrap.schema.json`,
`scripts/windows/{Initialize-CSweetWindowsIsolationTest,New-CSweetWindowsRuntimePayload}.ps1`,
`scripts/linux/{new-firecracker-guest,initialize-firecracker-test,new-runtime-payload}.sh`.

Verified: 2026-09-19. Target-host validation awaits the first hosted artifacts.
