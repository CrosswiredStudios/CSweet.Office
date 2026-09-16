# macOS installation

**Audience:** macOS administrators installing an Office on a host with Apple Virtualization.framework support.

macOS installs in two steps, like Linux: a signed and notarized pkg places the payload, and a root-only
configurator enrolls the machine. Apple Virtualization (`apple-virtualization`) is the only macOS provider, and
the helper that drives it must carry the `com.apple.security.virtualization` entitlement or the install is
refused.

## launchd labels

| Label | Plist | Identity | Contents |
|---|---|---|---|
| `com.csweet.office` | `/Library/LaunchDaemons/com.csweet.office.node.plist` | `UserName _csweetnode`, `GroupName _csweet` | `CSweet.Office.Node` from `/Library/Application Support/CSweet/Office/CSweet.Office.Node`. |
| `com.csweet.office.runtime` | `/Library/LaunchDaemons/com.csweet.office.runtime.plist` | No `UserName` key, so it runs as `root` | `CSweet.Office.RuntimeHost` from the same directory. |

Both plists set `RunAtLoad` and `KeepAlive` to true, `ProcessType` to `Background`, and `Umask` to 27. The two
labels (not the plist file names) are what `launchctl` addresses:

```
sudo launchctl bootstrap system /Library/LaunchDaemons/com.csweet.office.runtime.plist
sudo launchctl bootstrap system /Library/LaunchDaemons/com.csweet.office.node.plist
sudo launchctl bootout system/com.csweet.office.runtime
sudo launchctl bootout system/com.csweet.office
```

## Prerequisites

| Requirement | Evidence and failure mode |
|---|---|
| root | The configurator exits 1 with *"Run this command with sudo."*; the installer exits 1 with *"Run this installer with sudo."* |
| The installed payload | `csweet-configure-office` requires an executable `install-office.sh` under `/Library/Application Support/CSweet/Office/InstallerPayload`; otherwise it exits 2 with *"The signed C-Sweet Office payload is not installed."* |
| Valid code signatures | The installer runs `codesign --verify --strict --deep` on the RuntimeHost and Node, and `codesign --verify --strict` on the Swift helper. |
| The virtualization entitlement | `com.apple.security.virtualization` must read `true` from the helper's entitlements. A signed helper without it exits 2 with *"The signed helper is missing the Apple Virtualization entitlement."* |
| A complete payload | `runtime-manifest.json` and `apple-virtualization/vmlinux` must be present. |
| No symbolic links | Any symlink in the payload exits 2. |
| An HTTPS control plane | `https://…` is enforced by the configurator and the installer. |

## pkg install

`scripts/macos/new-installer-package.sh` builds, signs, notarizes, and staples a pkg with identifier
`com.csweet.office.payload`. It stages:

| pkg content | Path |
|---|---|
| Payload | `/Library/Application Support/CSweet/Office/InstallerPayload/` |
| Configurator | `/usr/local/sbin/csweet-configure-office` |
| Uninstaller | `/usr/local/sbin/csweet-uninstall-office` |

The build refuses to run unless `codesign`, `ditto`, `pkgbuild`, `pkgutil`, `plutil`, `productbuild`,
`spctl`, `xcrun`, and `python3` are available, and unless the helper's entitlement is present — the same check
the installer repeats on the target host. The finished pkg is verified with `pkgutil --check-signature`,
notarized through `xcrun notarytool`, stapled, and assessed with `spctl --assess --type install`.

> **Out of repo:** signing identities, the notary keychain profile, and the release signing workflow belong to
> the hardened platform workflows. Do not sign or notarize from an ordinary development runner.

## `csweet-configure-office`

```
sudo csweet-configure-office https://control-plane
```

The macOS configurator takes exactly one argument: the control-plane URL. It assumes the payload directory
above, re-validates the URL scheme, and executes the payload's `install-office.sh`. Because the Apple provider
has no equivalent of the Linux baseline notice, there is no `--dedicated-host` or `--accept-baseline-risk`
flag here — the same behaviour is reachable only by invoking `install-office.sh` directly from the payload
directory with `--security-profile` and `--dedicated-host`.

An install driven directly takes the same options as the Linux installer, including `--enrollment-token-file`,
`--result-job-id` (writing `/Library/Application Support/CSweet/Setup/local-provisioning-<jobId>.result`),
`--security-profile baseline|hardened|development`, `--dedicated-host`, and `--allow-development-assignments`.
Enrollment tokens are never accepted as command-line arguments: they arrive from a non-symlink file that is
deleted after reading, from an echo-off prompt, or from standard input.

## plist configuration

The installer renders the **node** plist from `scripts/macos/com.csweet.office.node.plist` by substituting four
placeholders, so the profile and URL are baked in at install time rather than read from an environment file:

| Placeholder | Source |
|---|---|
| `__CONTROL_PLANE_URL__` | The URL passed to the installer, escaped for `sed`. |
| `__SECURITY_PROFILE__` | `baseline`, `hardened`, or `development`. |
| `__MIXED_USE_HOST__` | `true`, or `false` when `--dedicated-host` was given. |
| `__ALLOW_DEVELOPMENT_ASSIGNMENTS__` | `true` only with `--allow-development-assignments`. |

The rendered node plist contains `CSweet__Office__Node__{ControlPlaneUrl,StateDirectory,ArtifactCacheDirectory,
ArtifactMediaDirectory,EnrollmentTokenFilePath,SecurityProfile,MixedUseHost,AllowDevelopmentAssignments}` and
`CSweet__Office__RuntimeHost__Authentication__SharedKeyFilePath`. The runtime plist is installed verbatim and
carries the Apple Virtualization roots
(`CSweet__Office__Providers__AppleVirtualization__{ArtifactImageRoot,PayloadManifestPath}`), the shared key
path, the authorization state directory, and `CSWEET_APPLE_VIRTUALIZATION_{DATA_ROOT,PACKAGE_ROOT,GUEST_PORT,
SOCKET_ROOT}` — socket root `/var/run/csweet-av`, guest port `5000`.

## Install paths

| Path | Owner and mode | Contents |
|---|---|---|
| `/Library/Application Support/CSweet/Office/` | `root:wheel` | Payload, `runtime-manifest.json`, `runtime-host.key` (`root:_csweet`, `0640`). |
| `/Library/Application Support/CSweet/Office/node/` | `_csweetnode:_csweet`, `0700` | `node-state.json`, `node-identity.pfx`, `enrollment.secret`, `maintenance/`. |
| `/Library/Application Support/CSweet/Office/authorization/` | `root:wheel`, `0700` | `headquarters-trust.json` (`0600`), the authorization ledger. |
| `/Library/Application Support/CSweet/Office/artifact-media/` | `_csweetnode:_csweet`, `0770` | ISO images attached to guests read-only. |
| `/Library/Application Support/CSweet/Office/AppleVirtualization/instances/` | `root:wheel`, `0700` | Per-instance workload-host state. |
| `/var/run/csweet-av/` | `root:wheel`, `0700` | The helper's per-instance Unix sockets, `/var/run/csweet-av/<32-hex>.sock`. |
| `/Library/LaunchDaemons/com.csweet.office{,.runtime}.plist` | `0644` | The two daemons. |

The installer creates a hidden service group `_csweet` and user `_csweetnode` (shell `/usr/bin/false`, home
`/var/empty`, `IsHidden 1`), choosing the first free uid/gid at or below 498 and not below 350. If an existing
`_csweetnode` is not in `_csweet`, the install exits 2 rather than adopting it.

Headquarters assignment trust is pinned by running the installed Node binary with
`--initialize-headquarters-assignment-trust <url> /Library/Application Support/CSweet/Office/authorization/
headquarters-trust.json`, owned `root:wheel` at `0600`.

## The virtualization entitlement

`CSweet.Office.Runtime.AppleVirtualization.Helper` is a Swift executable. Both
`new-installer-package.sh` and `install-office.sh` verify that its signature carries
`com.apple.security.virtualization = true`, which is what allows it to create and run a
`VZVirtualMachine`. The helper does not run the VM in its own process: it spawns a separate workload-host
process per instance, and that process owns the `VZVirtualMachine` and listens on
`/var/run/csweet-av/<32-hex>.sock`. The RuntimeHost reaches it there; the helper itself is reached by the
RuntimeHost over the stdio helper protocol (see
[20-security/09-helper-protocol.md](../20-security/09-helper-protocol.md)).

## Upgrade gating and first-install cutover

An upgrade is detected when `/Library/LaunchDaemons/com.csweet.office.node.plist` exists or
`/Library/Application Support/CSweet/Office/CSweet.Office.Node` is present. The installer then requires
`/Library/Application Support/CSweet/Office/maintenance/drain-state` to contain `draining` and the
`active-assignments` directory to hold no `*.active` files, exiting **3** otherwise, and only then boots the two
daemons out.

With no existing Office, the installer retires the legacy daemons `com.csweet.executionnode` and
`com.csweet.runtimehost`, deletes their plists, and removes
`/Library/Application Support/CSweet/Execution` and `/Library/Application Support/CSweet/AgentRuntime`. Legacy
identity and enrollment are discarded rather than migrated (`SEC-INV-18`).

## Known asymmetry

The Apple provider reaps **runtime** instances only (`kind == 1`). Toolchain-build and builder instances are not
reaped, so a crash during a build can leave an orphaned VM to clean up by hand. See
[20-security/12-known-limits-and-tradeoffs.md](../20-security/12-known-limits-and-tradeoffs.md) and
[30-workloads/07-provider-backends.md](../30-workloads/07-provider-backends.md).

## Sources

`scripts/macos/{install-office.sh,configure-office.sh,uninstall-office.sh,new-installer-package.sh,com.csweet.office.node.plist,com.csweet.office.runtime.plist}`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/` (entitlement and socket contract),
`README.md`, `docs/20-security/06-host-privilege-model.md`, `docs/20-security/12-known-limits-and-tradeoffs.md`.

Verified: 2026-09-15.
