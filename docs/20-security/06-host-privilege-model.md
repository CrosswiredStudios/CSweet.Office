# Host privilege model

**Audience:** security reviewers and operators. This page explains what each Office process can do to the
host, and how that is enforced.

The principle: **the unprivileged component holds the identity and talks to the network; the privileged
component holds no network access and refuses to act without a signature.**

## Windows

### Two virtual service accounts

Both services run under Windows-managed virtual accounts, so no password is ever stored:

- `NT SERVICE\CSweet.Office.RuntimeHost` — display *"C-Sweet RuntimeHost"*, description *"Privileged C-Sweet
  virtual-machine lifecycle service. No network listener."*, start type Automatic.
- `NT SERVICE\CSweet.Office.Node` — display *"Unprivileged outbound C-Sweet office"*, start type Manual until
  an enrollment configuration exists, then Automatic.

Creation order matters and is enforced: services are registered **before** their SIDs are used in ACLs. The
installer creates them with `New-Service`, then calls the `Win32_Service.Change` method with
`StartName = "NT SERVICE\<name>"` and `StartPassword = $null`, then runs `sc.exe sidtype <name> unrestricted`.

> **Deliberate regression-tested choice:** the installer uses `Win32_Service.Change` plus `sc.exe sidtype`
> rather than `sc.exe config` with a quoted path. Test
> `RuntimeHostInstaller_DoesNotUseScForQuotedServiceExecutablePath` exists to keep it that way.

Both services get `sc.exe failure … reset= 86400 actions= restart/5000/restart/15000/none/0`, and an
Application Event Log source is **pre-created** — creating it lazily can race and terminate the host.

### Hyper-V access

- The RuntimeHost service SID is added to `Hyper-V Administrators` (`S-1-5-32-578`), idempotently. Uninstall
  removes the membership.
- The virtual machine group `S-1-5-83-0` (Virtual Machines) receives `RX` on the guest-image directory and
  `R` on the image, because Hyper-V opens the full differencing-disk chain as the VM worker identity, not as
  RuntimeHost.

> Never grant write access, or inherited access beyond the image, to `S-1-5-83-0`. The VM worker identity is
> not the Office and must not be able to modify the immutable package.

Because Windows grants Hyper-V administrators control over **every** VM on a host, use a dedicated Office
machine when unrelated or higher-trust Hyper-V workloads are present.

### Domain controllers are refused

`Assert-NotDomainController` reads `Win32_ComputerSystem.DomainRole` and throws when it is 4 or 5. Office
does not install on a domain controller.

Related invariant: `SEC-INV-17`.

### The immutable package

`Set-ProtectedPackageAcl` applied to the versioned install root:

```
icacls <versionRoot> /inheritance:r /grant:r "<rhSid>:RX" "<nodeSid>:RX" "S-1-5-18:F" "S-1-5-32-544:F" /T /C /Q
```

followed by a second pass over every directory granting `(OI)(CI)RX` to the service SIDs and `(OI)(CI)F` only
to `SYSTEM` and `Administrators`.

The second pass exists because files that already existed can lack an effective `RX` ACE. `Assert-FileReadExecuteAce`
then **fails the installation** if either service executable lacks an effective read-and-execute allow ACE or
has a deny ACE.

Payload files are verified against `runtime-manifest.json` (schema 1, per-file lowercase SHA-256, 1–1000
entries) before copying, and a pristine copy is retained at `$InstallRoot\recovery\<packageVersion>` for the
repair path.

Related invariant: `SEC-INV-16`.

### Protected state ACLs

All roots are written with `icacls /inheritance:r`, so no parent grant leaks in. See
[10-system/06-process-network-and-storage-surface.md](../10-system/06-process-network-and-storage-surface.md)
for the full table.

### Elevation

`WindowsRuntimeHostProvisioner.LaunchElevated` writes the transient enrollment token to
`%LocalAppData%\CSweet\Setup\execution-enrollment-<guid>.secret` using `FileMode.CreateNew`, `FileShare.None`,
and `FileOptions.WriteThrough`, then elevates with `Verb = "runas"` running
`powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File`. The installer reads the token, removes the
file, validates its length (32–256), and writes `node\enrollment.secret` with the Node-only ACL.

Tokens are never passed as command-line arguments.

### The maintenance service

A third service, `CSweet.Office.Maintenance`, runs as `SYSTEM` with
`--maintenance-service "--settings=<maintenance-settings.json>"`. Its settings file lives in the
administrator-owned install root (`$InstallRoot\maintenance-settings.json`) and is restricted to `SYSTEM` and
`Administrators`. It opens no inbound listener.

Related invariant: `SEC-INV-19`.

## Linux

The same split as Windows, expressed with systemd sandboxing rather than a second service account.

### `csweet-office-node.service` — unprivileged

| Setting | Value |
|---|---|
| `User` / `Group` | `csweet-node` / `csweet-node` |
| `SupplementaryGroups` | `csweet-runtime` — the only reason the Node can read `runtime-host.key` |
| `EnvironmentFile` | `/etc/csweet/office.env` |
| `Requires` | `csweet-office-runtime.service` |
| `Restart` | `always`, `RestartSec=5` |

Hardening: `NoNewPrivileges=yes`, `ProtectSystem=strict`, `ProtectHome=yes`, `PrivateTmp=yes`,
`PrivateDevices=yes`, and a `ReadWritePaths` allow-list of `/var/lib/csweet/office`,
`/var/lib/csweet/artifact-media`, and `/run/csweet`.

`PrivateDevices=yes` matters: the Node cannot see `/dev/kvm` even though the host has it.

### `csweet-office-runtime.service` — privileged

| Setting | Value |
|---|---|
| `User` / `Group` | `root` / `csweet-runtime` |
| `EnvironmentFile` | `/etc/csweet/runtime-host.env` |
| `Restart` | `on-failure`, `RestartSec=5` |
| `RuntimeDirectory` | `csweet`, mode `0770` |
| `UMask` | `0007` |

Explicit `Environment=` overrides set the Firecracker artifact image root, the payload manifest path, the
shared key file, and the authorization state directory — so the authorization ledger and the key cannot be
redirected by an environment file alone.

Hardening and grants, all of which are deliberate:

- `NoNewPrivileges=yes`, `ProtectSystem=strict`, `ProtectHome=yes`, `PrivateTmp=yes`.
- `ProtectControlGroups=no` plus `Delegate=yes` — required so the jailer can create per-workload cgroups.
- `DevicePolicy=closed` with a single `DeviceAllow=/dev/kvm rw`.
- `RestrictAddressFamilies=AF_UNIX` — the RuntimeHost on Linux never opens an internet or vSocket; the guest
  channel is reached through the helper's Unix socket.
- `CapabilityBoundingSet=CAP_CHOWN CAP_DAC_OVERRIDE CAP_FOWNER CAP_SETGID CAP_SETUID CAP_SYS_ADMIN CAP_SYS_CHROOT CAP_MKNOD CAP_KILL`.
- `ReadWritePaths` includes `/sys/fs/cgroup` alongside the state and media roots.

### Host identities

| Identity | Purpose |
|---|---|
| `csweet-node` | System user, home `/var/lib/csweet/office`, shell `/usr/sbin/nologin`. Added to `csweet-runtime`. |
| `csweet-vm` | System user, home `/nonexistent`. Used for Firecracker jailer file ownership. |
| `csweet-runtime` | Group owning `runtime-host.key` (read) and `artifact-media` (read/write). |
| `csweet-workload` | Created **inside the guest image**, not on the host. Runs workloads. |

State and media permissions are in
[10-system/06-process-network-and-storage-surface.md](../10-system/06-process-network-and-storage-surface.md).

### Installation preconditions

`install-office.sh` refuses to proceed unless all of these hold:

- Running as root.
- Ubuntu **24.04 only**, checked from `/etc/os-release` (`ID=ubuntu`, `VERSION_ID=24.04`).
- Architecture is `x86_64`, `aarch64`, or `arm64`.
- The package root is owned by `root:root`, contains **no group- or world-writable paths**
  (`find -perm /022`), and contains **no symlinks**.
- A fixed set of binaries and files is present: the RuntimeHost, Node, and Firecracker helper executables,
  `firecracker/{firecracker,jailer}`, `firecracker/{vmlinux,initrd.img}`, and `runtime-manifest.json`.
- cgroup v2 is available (`/sys/fs/cgroup/cgroup.controllers` is readable) and `/dev/kvm` is readable and
  writable.

Hardware virtualization is not optional. Without `/dev/kvm` the install fails rather than degrading.

### Upgrade and uninstall gates

Both the upgrade path and the uninstall path require `maintenance/drain-state == draining` and zero
`maintenance/active-assignments/*.active` markers, exiting with code 3 otherwise. `--force` bypasses the
uninstall gate **after revocation** — it is not a convenience flag.

## macOS

launchd runs the two services under different identities, mirroring the Windows split:

| Service | Identity | Source |
|---|---|---|
| `com.csweet.office.node` | `_csweetnode` / group `_csweet` | `UserName` and `GroupName` in the plist |
| `com.csweet.office.runtime` | `root` (no `UserName` set) | `com.csweet.office.runtime.plist` |

The installer creates the identities with `dscl` when they are absent:

| Attribute | Value |
|---|---|
| Group `_csweet` | RealName *"C-Sweet execution services"*, allocated `PrimaryGroupID` |
| User `_csweetnode` | RealName *"C-Sweet Office"*, allocated `UniqueID`, primary group `_csweet`, shell `/usr/bin/false`, home `/var/empty`, `IsHidden 1` |

If `_csweetnode` already exists but does not belong to `_csweet`, the installer exits with code 2 rather than reusing it.

| Path | Owner and mode |
|---|---|
| `node/` | `_csweetnode:_csweet 0700` |
| `artifact-media/` | `_csweetnode:_csweet` |
| `runtime-host.key` | `root:_csweet` |
| `authorization/headquarters-trust.json` | `root:wheel` |
| `AppleVirtualization/` | `root:wheel` |
| `/var/run/csweet-av/` | `root:wheel` |
| The rest of `/Library/Application Support/CSweet/Office` | `root:wheel` |

Uninstall deletes both `_csweetnode` and the `_csweet` group.

The Apple Virtualization helper is a Swift executable that requires the
`com.apple.security.virtualization` entitlement. It spawns a **separate workload-host process** per instance
that owns the `VZVirtualMachine` and listens on `/var/run/csweet-av/<32-hex>.sock`.

## Comparison

| Property | Windows | Linux | macOS |
|---|---|---|---|
| Node identity | `NT SERVICE\CSweet.Office.Node` | `csweet-node` (systemd) | `_csweetnode:_csweet` (launchd) |
| RuntimeHost identity | `NT SERVICE\CSweet.Office.RuntimeHost` | `root` (systemd), group `csweet-runtime` | `root` (launchd) |
| Node can see hypervisor device | No (not in the Hyper-V admin group) | No (`PrivateDevices=yes`) | No |
| RuntimeHost in a hypervisor admin group | Yes — `Hyper-V Administrators` | n/a — `/dev/kvm` via `DeviceAllow` | n/a |
| Immutable package enforcement | Explicit non-inherited `RX` ACEs, verified at install | `root:root`, no group/world write, no symlinks | `root`-owned payload |
| Domain controllers | Refused | n/a | n/a |
| Inbound listener | None | None | None |
| Third service | `CSweet.Office.Maintenance` (`SYSTEM`) | None | None |

## Sources

`scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`,
`src/CSweet.Office.Runtime.HyperV/{WindowsRuntimeHostProvisioner.cs,WindowsHyperVHostProbe.cs,WindowsHyperVFeatureProvisioner.cs}`,
`scripts/linux/{install-office.sh,uninstall-office.sh,csweet-office-node.service,csweet-office-runtime.service}`,
`scripts/macos/{install-office.sh,uninstall-office.sh,com.csweet.office.node.plist,com.csweet.office.runtime.plist}`,
`README.md`, `tests/CSweet.Office.Tests/WindowsHyperVOnboardingTests.cs`.

Verified: 2026-09-15.
