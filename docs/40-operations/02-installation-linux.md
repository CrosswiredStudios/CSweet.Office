# Linux installation

**Audience:** Linux administrators installing an Office on an Ubuntu 24.04 host.

Linux installs in two steps, deliberately separated: a signed package places the payload, and a root-only
configurator enrolls the machine. Both steps fail closed — an unsupported host, an unverifiable package tree, or
a missing `/dev/kvm` aborts rather than degrading.

## Prerequisites

| Requirement | Evidence and failure mode |
|---|---|
| Ubuntu 24.04 LTS only | `install-office.sh` sources `/etc/os-release` and requires `ID=ubuntu` and `VERSION_ID=24.04`. Any other distribution or release exits 2. |
| x86-64 or arm64 | `uname -m` must be `x86_64`, `aarch64`, or `arm64`. |
| root | The configurator exits 1 unless `id -u` is 0; the installer exits 1 with *"Run this installer as root."* |
| cgroup v2 and `/dev/kvm` | `/sys/fs/cgroup/cgroup.controllers` must be readable and `/dev/kvm` readable **and** writable. Failure message: *"Firecracker requires cgroup v2 and read/write access to /dev/kvm."* `firecracker-kvm` is the only Linux provider, so hardware virtualization is not optional. |
| A complete package tree | The package root must contain the RuntimeHost, Node, and Firecracker helper executables, `firecracker/{firecracker,jailer}`, `firecracker/{vmlinux,initrd.img}`, and `runtime-manifest.json`. |
| A safe package tree | Owned `root:root`, no group- or world-writable paths (`find -perm /022`), and no symbolic links. Each violation exits 2. |
| An HTTPS control plane | `https://…` is enforced by the configurator and the installer. |

## Package install

`scripts/linux/new-native-packages.sh` builds a signed `csweet-office_<version>_<arch>.deb` (and, with
`--format rpm`, an RPM from the same staged tree). The package itself contains no secrets:

| Package content | Path |
|---|---|
| Payload | `/usr/lib/csweet/office-installer/` |
| Configurator | `/usr/sbin/csweet-configure-office` |
| Uninstaller | `/usr/sbin/csweet-uninstall-office` |

The `postinst` script only prints the enrollment instruction; nothing is installed as a service until the
configurator runs. The `prerm` script invokes `/usr/sbin/csweet-uninstall-office` when an Office is present, so
removing the package is subject to the same drain gate as any other removal (see
[06-uninstall-and-removal.md](06-uninstall-and-removal.md)).

## `csweet-configure-office`

```
sudo csweet-configure-office https://control-plane [--dedicated-host] [--accept-baseline-risk]
```

| Option | Effect |
|---|---|
| *(none)* | Selects `--security-profile baseline` and prints the baseline notice: C-Sweet and Office may share the machine, agents still run in isolated Firecracker VMs, and a host-level flaw in the OS, KVM, or Firecracker could reach other data on that machine. |
| `--dedicated-host` | Selects `--security-profile hardened` and passes `--dedicated-host`, which sets `MixedUseHost=false`. It also prints the patches the operator owes the host: Ubuntu, KVM, Firecracker, firmware, and CPU microcode. |
| `--accept-baseline-risk` | Acknowledges the baseline notice and skips the confirmation prompt. Required when stdin is not a terminal. |

Unknown options exit 2. There is **no** `--enrollment-token` option, and there is deliberately no way to pass a
token on a command line: process arguments on a multi-user host are readable by other local users and land in
shell history and audit logs. `--security-profile development` is likewise not reachable from the configurator;
it must be requested by invoking `install-office.sh` directly with `--security-profile development
--allow-development-assignments`.

### Supplying the enrollment token

The installer accepts the one-use token in exactly one of two ways, both of which remove the value from disk
after reading it.

| Method | Behaviour |
|---|---|
| `--enrollment-token-file <path>` | Reads the file, then deletes it. The path must exist, must not be a symbolic link, and must not be a directory. |
| Standard input | When no file is given: an interactive terminal is prompted with echo off; a non-interactive stdin is read directly. |

Non-interactive provisioning therefore looks like a pipe, not an argument:

```
printf '%s' "$ENROLLMENT_TOKEN" | sudo csweet-configure-office https://control-plane --accept-baseline-risk
```

The token must be 32–256 characters; anything else exits 2. `--result-job-id <32 hex characters>` additionally
writes the outcome to `/var/lib/csweet/setup/local-provisioning-<id>.result` for a provisioning driver to pick
up.

> **Out of repo:** C-Sweet issues the one-use connection code and approves the resulting enrollment. The
> configurator never contacts Headquarters directly.

## Install paths

| Path | Owner and mode | Contents |
|---|---|---|
| `/opt/csweet/office/` | `root:root`, `0755` executables, `0644` kernel and manifest | The installed payload copy, including `firecracker/` and `runtime-manifest.json`. |
| `/etc/csweet/office.env` | `0600` | Node configuration: control-plane URL, state directory, artifact cache and media directories, enrollment token path, security profile, mixed-use flag, development opt-in, runtime socket path, shared key path, authorization state directory. |
| `/etc/csweet/runtime-host.env` | `0600` | Firecracker roots, workload uid/gid, guest vsock port `5000`, artifact media root. |
| `/var/lib/csweet/office/` | mixed | `node/` (`csweet-node`, `0700`), `authorization/` (`root`, `0700`), `firecracker/` (`root`, `0700`), `runtime-host.key` (`root:csweet-runtime`, `0640`). |
| `/var/lib/csweet/artifact-media/` | `csweet-node:csweet-runtime`, `0770` | ISO images attached to guests read-only. |
| `/etc/systemd/system/csweet-office-{runtime,node}.service` | `0644` | The two units, enabled and started at the end of the install. |
| `/var/lib/csweet/setup/local-provisioning-<jobId>.result` | `0644` | Only when `--result-job-id` is supplied. |

Identities are created if absent: system users `csweet-node` (home `/var/lib/csweet/office`, shell
`/usr/sbin/nologin`) and `csweet-vm` (home `/nonexistent`), and the group `csweet-runtime`. `csweet-node` is
added to `csweet-runtime`.

Headquarters assignment trust is pinned during the install by running the installed Node binary:

```
/opt/csweet/office/CSweet.Office.Node \
  --initialize-headquarters-assignment-trust "$control_plane" \
  /var/lib/csweet/office/authorization/headquarters-trust.json
```

The result is owned by `root` with mode `0600`. Unlike Windows, the Linux installer writes no control-plane TLS
pin file; the Node validates the control-plane certificate against the host trust store unless
`ControlPlaneCertificateSha256` or `ControlPlaneTrustFilePath` is configured separately.

## Upgrade gating

The installer treats an existing Office as an upgrade when `/etc/systemd/system/csweet-office-node.service`
exists or `/opt/csweet/office/CSweet.Office.Node` is present. An upgrade requires:

- `/var/lib/csweet/office/maintenance/drain-state` containing `draining`, and
- zero files matching `/var/lib/csweet/office/maintenance/active-assignments/*.active`.

Either condition failing prints *"Drain this node in C-Sweet and wait for active assignments to reach zero before
upgrading."* and exits **3**. Only then are the two units stopped. See
[04-upgrade-and-drain.md](04-upgrade-and-drain.md).

## First-install cutover

With no existing Office, the installer retires the pre-cutover daemons instead of migrating them: it disables
and removes `csweet-execution-node.service` and `csweet-runtime-host.service`, deletes their unit files and
reloads systemd, and removes `/opt/csweet/execution-node`, `/opt/csweet/runtime-host`,
`/var/lib/csweet/execution-node`, and `/var/lib/csweet/agent-runtime`. Legacy identities, state, and enrollment
are discarded and a fresh identity is enrolled (`SEC-INV-18`).

## Result codes

| Exit code | Meaning |
|---|---|
| 0 | Installed and enrolled. |
| 1 | Not run as root. |
| 2 | Usage error, unsupported host or package, invalid option, invalid token, or a non-interactive baseline run without `--accept-baseline-risk`. |
| 3 | Draining and zero active assignments are required before an upgrade. |

## Sources

`scripts/linux/{install-office.sh,configure-office.sh,uninstall-office.sh,new-native-packages.sh,csweet-office-node.service,csweet-office-runtime.service}`,
`README.md`, `docs/20-security/06-host-privilege-model.md`.

Verified: 2026-09-15.
