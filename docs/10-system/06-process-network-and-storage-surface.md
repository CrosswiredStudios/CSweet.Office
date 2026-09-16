# Process, network, and storage surface

**Audience:** security reviewers, operators, and anyone writing a firewall rule or an ACL.

The complete inventory of what runs, what it opens, and what it writes. If a fact here is wrong, an operator
will get it wrong.

## Inbound network listeners

**Office opens no inbound network port.** There is no management listener, no remote procedure endpoint, and
no HTTP server reachable from off-host.

| Component | Binds a network port? | Evidence |
|---|---|---|
| `CSweet.Office.Node` | No | Worker service; the only HTTP client is `HttpClient("control-plane")`; the only server is the gRPC **client** stream. |
| `CSweet.Office.RuntimeHost` | No | Its service description is *"Privileged C-Sweet virtual-machine lifecycle service. No network listener."* It serves a named pipe or Unix socket only. |
| `CSweet.Office.Maintenance` | No | Described as *"Receives authorized C-Sweet Office repair requests. No inbound listener."* |
| Platform helpers | No | Typed JSON on stdin/stdout of a child process. |
| Guests | No NIC | Every provider configuration creates the VM with **no network device**. Hyper-V throws if any adapter exists. |

## Outbound connections

| Origin | Destination | Protocol | Notes |
|---|---|---|---|
| Node | Control plane | HTTPS (JSON) | Claim, heartbeat, certificate issue/renew/challenge/recover, assignment-trust probe. |
| Node | Control plane | gRPC over HTTP/2 | `OfficeGateway`: `Connect`, `OpenWorkloadTunnel`, `DownloadArtifact`. Only these three RPCs exist. |
| Node → RuntimeHost | Local host only | Named pipe / Unix socket | Never leaves the machine. |
| RuntimeHost → helper | Local host only | Anonymous pipe (stdin/stdout) | Child process. |
| Firecracker helper | Local host only | Unix socket | Firecracker API. Firecracker itself is configured with no NIC. |
| Apple helper → manager | Local host only | Unix socket | Descriptor passing. |

The Node's gRPC channel uses a rotating mTLS handler built from the current operational certificate with
`AllowTlsResume = false` and a per-handshake local certificate selection callback, and a 20 second connect
timeout.

## Local IPC inventory

| Transport | Platform | Name | Protection |
|---|---|---|---|
| Named pipe | Windows | `csweet-office-runtime-v1` | Explicit DACL: full control for the server identity, `LocalSystem`, and `BuiltinAdministrators`; each allowed client SID gets exactly `ReadWrite \| Synchronize \| CreateNewInstance`. Inheritance is disabled. |
| Unix socket | Linux, macOS | `/run/csweet/csweet-office-runtime-v1.sock` (default; the installer sets it explicitly) | Mode `0660`, `Listen(128)`, stale path deleted on bind, socket removed on shutdown. |
| Hyper-V guest socket | Windows | `AF_HYPERV` (address family 34), service GUID `00000ac9-facb-11e6-bd58-64006a7986d3` | Registered under `GuestCommunicationServices` with element name *"C-Sweet authenticated agent broker"*. |
| Guest vSock | Linux guest | `AF_VSOCK` (40), port `5000` (Firecracker) or `2761` (Linux `AF_HYPERV`) | Guest-side listener created with raw libc calls. |
| Firecracker API socket | Linux host | `<jail>/run/firecracker.socket` | Inside the jailer's chroot. |
| Firecracker guest vSock | Linux host | `<jail>/run/guest.vsock` | The helper sends `CONNECT <port>` and requires an `OK` response. |
| Apple manager socket | macOS host | `/var/run/csweet-av/<32-hex>.sock` | One per live instance, owned by the workload-host process. |
| Local broker socket | Linux guest | `/run/csweet/broker.sock` | Mode `0660`, group `csweet-workload`, backlog 16. This is what the agent SDK connects to. |
| Workload token file | Linux guest | `/run/csweet/workload-token` | Mode `0640`, group `csweet-workload`. |

The named pipe name is validated to ASCII alphanumerics, `-`, and `_`, at most 100 characters. The Unix
socket path must be absolute, at most 200 characters, with no `.` or `..` segments and no NUL.

## Windows registry surface

| Key | Written by | Purpose |
|---|---|---|
| `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\00000ac9-facb-11e6-bd58-64006a7986d3` | Installer | Registers the broker vSock service with element name *"C-Sweet authenticated agent broker"*. The legacy braced key is removed. |
| `HKLM\SYSTEM\CurrentControlSet\Services\CSweet.Office.RuntimeHost` → `Environment` (MultiString) | Installer | Service-scoped `CSWEET_HYPERV_BROKER_SERVICE_ID`, `CSWEET_HYPERV_DATA_ROOT`, `CSWEET_ARTIFACT_MEDIA_ROOT`. |
| `HKLM\Software\CSweet\Office\ProductCode` | MSI | Product code used by the recovery-removal path. |
| `HKLM\Software\Classes\csweet-office` | MSI | URL protocol registration pointing at the configurator. |
| Event Log source per service name | Installer | Pre-created deliberately: creating it lazily can race and terminate the host. |

## Machine-scope environment variables (Windows)

Set with `SetEnvironmentVariable(..., 'Machine')` **and** duplicated on the RuntimeHost service:

- `CSWEET_HYPERV_BROKER_SERVICE_ID`
- `CSWEET_HYPERV_DATA_ROOT`
- `CSWEET_ARTIFACT_MEDIA_ROOT`

Uninstall removes all three.

## Identity and state directories

### Windows

Install root: `%ProgramFiles%\CSweet\Office`.

- `<version>\` — the immutable, versioned package: `node\`, `runtime\`, `helper\`, `configurator\`, `images\`,
  `certificates\`, `certification\`, `runtime-manifest.json`, `appsettings.json`, plus the installer scripts
  staged by the MSI.
- `recovery\<version>\` — a pristine copy of the same payload used by the repair path.
- `maintenance-settings.json` — SYSTEM/Administrators only.

Data root: `%ProgramData%\CSweet\Office`.

| Path | Grantees |
|---|---|
| `node\` | Node service SID `(OI)(CI)M`; SYSTEM and Administrators `(OI)(CI)F` |
| `authorization\` | RuntimeHost service SID `(OI)(CI)M`; SYSTEM and Administrators `(OI)(CI)F` |
| `hyperv\` | RuntimeHost service SID `(OI)(CI)M`; SYSTEM and Administrators `(OI)(CI)F` |
| `artifacts\` | Node `(OI)(CI)M`, RuntimeHost `(OI)(CI)R` |
| `artifact-media\` | Node `(OI)(CI)M`, RuntimeHost `(OI)(CI)R` |
| `runtime-host.key` (file) | interactive `ControlPlaneUserSid` `R`, Node `R`, RuntimeHost `R`, SYSTEM and Administrators `F` |
| `node\control-plane-trust.json` (file) | Node `R` |

All of these are written with `icacls /inheritance:r` so no parent grant leaks in. The immutable package uses
`/inheritance:r` with `RX` for both service SIDs, plus a second pass granting `(OI)(CI)RX` on directories and
`(OI)(CI)F` only to SYSTEM and Administrators; `Assert-FileReadExecuteAce` then fails the installation if
either service executable lacks an effective read-and-execute allow ACE or has a deny ACE.

### Linux

| Path | Owner and mode |
|---|---|
| `/opt/csweet/office/` | `root:root`; executables `0755`, kernels and manifest `0644` |
| `/usr/lib/csweet/office-installer/` | full payload tree |
| `/etc/csweet/office.env`, `/etc/csweet/runtime-host.env` | `0600` |
| `/var/lib/csweet/office/node/` | `csweet-node:csweet-node 0700` |
| `/var/lib/csweet/office/authorization/` | `root:root 0700`; `headquarters-trust.json` `0600` |
| `/var/lib/csweet/office/firecracker/` | `root:root 0700` |
| `/var/lib/csweet/office/runtime-host.key` | `root:csweet-runtime 0640`, 32 random bytes base64 |
| `/var/lib/csweet/artifact-media/` | `csweet-node:csweet-runtime 0770` |
| `/run/csweet/csweet-office-runtime-v1.sock` | `0660` |
| `/usr/lib/csweet/{guest,builder,toolchain}/` (in image) | `root:root 0755` |

System users and groups created: `csweet-node` (home `/var/lib/csweet/office`, shell `/usr/sbin/nologin`),
`csweet-vm` (home `/nonexistent`), and the group `csweet-runtime`, with `csweet-node` added to it. Inside the
guest image, the separate user and group `csweet-workload` are created with `/nonexistent` and `nologin`.

### macOS

`/Library/Application Support/CSweet/Office/` with `node/`, `authorization/`, `artifact-media/`,
`AppleVirtualization/`, `runtime-host.key`, and `runtime-manifest.json`. Manager sockets live under
`/var/run/csweet-av/`.

## Guest storage layout

Device order is identical for Firecracker and Apple Virtualization:

| Device | Role | Mode |
|---|---|---|
| `/dev/vda` | Certified guest image (root) | read-only, mounted `ro,nosuid,nodev` |
| `/dev/vdb` | Scratch | read-write, wiped, `mkfs.ext4 -L CSWEET_SCRATCH`, mounted `rw,nosuid,nodev` at `/run/csweet` |
| `/dev/vdc` | Artifact media | read-only, enforced by `prepare-runtime.sh` |

Hyper-V presents artifact media as an optical disc, so the materializer's default device is `/dev/sr0` and the
Linux image path uses `/dev/vdc`.

## Sources

`scripts/windows/Install-CSweetOfficeRuntimeHost.ps1` (config block, ACL block, registry block),
`src/CSweet.Office.Runtime.HyperV.Helper/HyperVHelperPaths.cs`,
`src/CSweet.Office.Runtime.LocalRpc/{RuntimeHostEndpointOptions.cs,RuntimeHostRpcServer.cs}`,
`src/CSweet.Office.RuntimeHost/appsettings.json`, `scripts/linux/{install-office.sh,csweet-office-runtime.service,csweet-office-node.service}`,
`scripts/macos/{com.csweet.office.node.plist,com.csweet.office.runtime.plist,install-office.sh}`,
`build/linux-firecracker/provision-guest.sh`, `scripts/linux/uninstall-office.sh`.

Verified: 2026-09-15.
