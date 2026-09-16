# Provider backends

**Audience:** contributors to a provider backend; operators diagnosing a VM that will not start.

All three backends share `ExternalPlatformIsolationBackend` and the helper protocol
([20-security/09-helper-protocol.md](../20-security/09-helper-protocol.md)). What differs is what the helper
is asked to do, how the guest channel is established, and when instances get reaped.

## Side by side

| | Hyper-V | Firecracker | Apple Virtualization |
|---|---|---|---|
| Provider id | `hyperv-gen2` | `firecracker-kvm` | `apple-virtualization` |
| Host OS | Windows | Linux | macOS |
| Helper | C# EXE, `Runtime.HyperV.Helper` | C# EXE, `Runtime.Firecracker.Helper` | Swift executable |
| Isolation primitive | `New-VM -Generation 2`, differencing disk, Secure Boot | `jailer` + Firecracker API, cgroups | `VZVirtualMachine` |
| Guest channel owner | **RuntimeHost** (`AF_HYPERV` socket) | helper (stdio bridge + `CONNECT`) | helper (manager socket, descriptor passing) |
| Network device | none (throws if any exists) | none | `networkDevices = []` |
| Artifact media | DVD at SCSI 0:2 | virtio-blk, read-only | virtio-blk, read-only |
| Logs | **empty stub** | `console.log` + `firecracker.log` | manager logs |
| Reaps | Runtime, ToolchainBuild | Runtime, ToolchainBuild | **Runtime only** |

## Hyper-V

### Probe

Windows edition and hardware checks, then `HyperVSocketRegistration.IsConfigured()` — the registry key under
`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\{serviceId:D}`
derived from `CSWEET_HYPERV_BROKER_SERVICE_ID` — then a bounded `Get-VMHost` / `Get-Command New-VM`
effective-access check.

That last check exists because `WindowsPrincipal.IsInRole` is unreliable for virtual service accounts. The
effective check is the helper's `Get-VMHost` call, not the advisory probe.

### Create

Per-instance directory under `HyperVHelperPaths.InstancesRoot` (`<CSWEET_HYPERV_DATA_ROOT>\instances\<32-hex>`):

1. `New-VM -Generation 2 -MemoryStartupBytes <bytes> -NoVHD`.
2. `os-diff.vhdx` — a differencing disk on the certified base VHDX.
3. `scratch.vhdx` — sized to `WritableDiskMegabytes`.
4. `artifact.iso` — for artifact workloads, a **private copy** of the shared media ISO, re-verified.
5. `Add-VMHardDiskDrive` at SCSI 0:0 and 0:1; `Add-VMDvdDrive` at 0:2.
6. `Set-VMProcessor -Count … -Maximum …`.
7. `Set-VMFirmware -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority`.

An `instance.json` (`HyperVInstanceMetadata`: instance id, workload id, kind, VM name, created/started/finished
timestamps, lease expiry) is written per instance. Start is `Start-VM`.

### Guest channel

Not the helper. `WindowsHyperVSocketTransport` (also `IPlatformGuestChannelConnector`) opens a raw `AF_HYPERV`
socket — address family **34** — to the guest's broker service GUID `00000ac9-facb-11e6-bd58-64006a7986d3`.

Because the managed `ConnectAsync`/`ConnectEx` path rejects `AF_HYPERV` with `WSAEINVAL (10022)`, the transport
sets `Blocking = false`, calls blocking `Connect`, and polls with `Socket.Poll` in a retry loop bounded by
`ConnectTimeoutSeconds` (60 seconds). The endpoint serialization is a hand-built 36-byte `SOCKADDR_HV` with
GUIDs at byte offsets 4 and 20.

This is why the broker vSock service is registered in the registry with element name
*"C-Sweet authenticated agent broker"*.

### Logs

`Logs()` returns an **empty array**. The interface exists; the implementation is a stub. On Windows, provider
logs are not available through the Office.

### Reaping

`ShouldReap` requires the kind to be `Runtime` or `ToolchainBuild` **and** one of:

- `LeaseExpiresAt` is null, or the lease has expired,
- `FinishedAt` is set,
- the VM state is unknown,
- the state is `Off` and the instance has either started or aged past a 5-minute creation grace period.

`Builder` VMs are **explicitly skipped**. An Office that crashes during a builder run leaves an orphaned VM.

## Firecracker

### Defaults

| Setting | Default |
|---|---|
| Data root (`CSWEET_FIRECRACKER_DATA_ROOT`) | `/var/lib/csweet/office/firecracker` |
| Package root (`CSWEET_FIRECRACKER_PACKAGE_ROOT`) | `/opt/csweet/office/firecracker` |
| Artifact root (`CSWEET_ARTIFACT_MEDIA_ROOT`) | `/var/lib/csweet/artifact-media` |
| Workload uid/gid (`CSWEET_FIRECRACKER_WORKLOAD_UID`) | `65534` |
| Guest vSock port | `5000` |
| Parent cgroup | read from `/proc/self/cgroup` |

### Probe

cgroup v2 present; the pinned executables and files are trusted (no group or other write permission); the data
root is protected; `/dev/kvm` is openable; and **`firecracker --version` and `jailer --version` must be equal**.

### Create

```
jailer --id csweet-<32hex> --exec-file <firecracker> --uid <u> --gid <g>
       --chroot-base-dir <dir> --cgroup-version 2 --parent-cgroup <cgroup>
       --cgroup memory.max=<mem + 128 MiB>
       --cgroup pids.max=<MaximumProcessCount>
       --cgroup cpu.max=<cpuPercent * 1000> 100000
       --resource-limit no-file=1024
       --daemonize --new-pid-ns -- --api-sock /run/firecracker.socket
```

Then the helper stages `rootfs.ext4` (read-only), `vmlinux`, `initrd.img`, `scratch.raw` (read-write,
truncated to `WritableDiskMegabytes`, chowned to the workload uid/gid), and `artifact.iso` when applicable.

The Firecracker API is configured over its Unix socket:

| Endpoint | Content |
|---|---|
| `/logger` | Log configuration |
| `/serial` | `console.log` |
| `/machine-config` | vCPU, memory |
| `/boot-source` | Kernel, initrd, `console=ttyS0 reboot=k panic=1 root=/dev/vda ro systemd.volatile=state` |
| `/drives/rootfs`, `/drives/scratch`, `/drives/artifact` | Read-only, read-write, read-only |
| `/vsock` | `guest_cid = GuestCid(instanceId)`, `uds_path = /run/guest.vsock` |

Start is `PUT /actions {"action_type":"InstanceStart"}`.

### Guest channel

The helper connects to `<jail>/run/guest.vsock`, sends `CONNECT 5000\n`, requires `OK <port>`, writes exactly
one newline-terminated JSON handshake line, and then bridges its own stdin/stdout to the guest
(`BridgeAsync`). It requires the metadata process to still be alive **and** owned by the jail root.

### Logs

`console.log` receives three quarters of the byte budget (stream `"console"`); `firecracker.log` receives one
quarter (stream `"provider"`). Reads are tail-biased, with a `Truncated` flag when the budget is exceeded.

### Reaping

Same kind restriction as Hyper-V, plus `!IsProcessRunning(processId)` or
`StartedAt == null && CreatedAt <= now − 5 minutes`.

## Apple Virtualization

### Defaults

| Setting | Default |
|---|---|
| Data root (`CSWEET_APPLE_VIRTUALIZATION_DATA_ROOT`) | `/Library/Application Support/CSweet/Office/AppleVirtualization` |
| Package root | `…/apple-virtualization/vmlinux` |
| Artifact root (`CSWEET_ARTIFACT_MEDIA_ROOT`) | `…/CSweet/Office/artifact-media` |
| Manager sockets | `/var/run/csweet-av` |
| Guest broker port | `5000` |

### Probe

`VZVirtualMachine.isSupported`, `geteuid() == 0`, protected directories, and a readable pinned kernel.

### Create

`create` spawns a **separate workload-host process** — `<helper> --workload-host <instance.json>` — that owns
the `VZVirtualMachine` and listens on `/var/run/csweet-av/<32-hex>.sock`. Every other helper operation sends a
newline-terminated JSON request carrying a manager token to that socket.

VM configuration:

| Element | Value |
|---|---|
| Boot loader | `VZLinuxBootLoader` with `console=hvc0 panic=-1 reboot=t root=/dev/vda ro csweet.broker_port=<port>` |
| Platform | `VZGenericPlatformConfiguration` |
| Entropy | entropy device attached |
| Sockets | one `VZVirtioSocketDeviceConfiguration` |
| Network | `networkDevices = []` |
| Storage order | `guestImage` (ro → `/dev/vda`), `scratch.raw` (rw → `/dev/vdb`), `artifact.iso` (ro → `/dev/vdc`) |

### Guest channel

`openGuestChannel` connects the manager socket and sends `open-guest-channel` **passing a descriptor**. The
manager connects `VZVirtioSocketDevice.connect(toPort: brokerPort)` and hands the connected socket back by
descriptor passing (for example `SCM_RIGHTS`).

### Reaping

`HelperController.reap` considers **only** `metadata.kind == 1` (Runtime). Toolchain-build and builder VMs are
never reaped on macOS.

## Choosing a backend

Placement is upstream of the Office; the Office only reports what it has. Selection is described in
[20-security/08-provider-certification.md](../20-security/08-provider-certification.md).

Practical differences that matter operationally:

| Want | Choose |
|---|---|
| Secure Boot with the Microsoft UEFI CA template | Hyper-V (`SupportsSecureBoot: true`) |
| Enforced process-count limits | Firecracker or Apple (`SupportsProcessLimits: true`) |
| Provider logs visible through the Office | Firecracker (Hyper-V's implementation is a stub) |
| Reaping for build workloads | Hyper-V or Firecracker (Apple reaps Runtime only) |

## Sources

`src/CSweet.Office.Runtime.HyperV/{HyperVIsolationBackend.cs,HyperVSocketTransport.cs,WindowsHyperVHostProbe.cs}`,
`src/CSweet.Office.Runtime.HyperV.Helper/{HyperVHelperController.cs,PowerShellHyperV.cs,HyperVHelperPaths.cs}`,
`src/CSweet.Office.Runtime.Firecracker/FirecrackerIsolationBackend.cs` (which also declares
`FirecrackerGuestChannelConnector`),
`src/CSweet.Office.Runtime.Firecracker.Helper/{FirecrackerHelperController.cs,FirecrackerApiClient.cs,FirecrackerHelperPaths.cs}`,
`src/CSweet.Office.Runtime.AppleVirtualization/{AppleVirtualizationIsolationBackend.cs,AppleVirtualizationGuestChannelConnector.cs}`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Sources/CSweetAppleVirtualizationHelper/*.swift`,
`tests/CSweet.Office.Tests/{HyperVInstanceReapingTests.cs,FirecrackerHelperSecurityTests.cs}`.

Verified: 2026-09-15.
