# What C-Sweet Office is

**Audience:** everyone. Read this before anything else.

C-Sweet Office is the independently installed execution plane for C-Sweet agents. It runs two services and
one optional maintenance service on a Windows, Linux, or macOS host, plus the platform-specific code needed
to boot certified hardware virtual machines and supervise work inside them.

Office is a *deliverable*, not a component of the C-Sweet application. It has its own version, its own
`vX.Y.Z` tags, and its own release assets. C-Sweet AppHost does not launch Office, and Office never
self-updates.

## What runs on a host

| Component | Identity | Role |
|---|---|---|
| `CSweet.Office.Node` | `NT SERVICE\CSweet.Office.Node` / `csweet-node` | Unprivileged, outbound-only control client. Holds the Office identity, enrolls, heartbeats, receives assignments, relays guest traffic. |
| `CSweet.Office.RuntimeHost` | `NT SERVICE\CSweet.Office.RuntimeHost` / `root` | Privileged virtualization service. Owns the authorization gate, the local RPC server, the platform helpers, and (on Windows) the guest channel. No network listener. |
| `CSweet.Office.Maintenance` | Windows `SYSTEM` | Runs `CSweet.Office.Configurator.exe --maintenance-service`. Accepts administrator-authorized repair requests over an outbound connection. No inbound listener. |
| Platform helpers | Child of RuntimeHost | Narrow, digest-pinned executables that perform VM lifecycle operations. Typed JSON over stdio. |
| Guests | VM | `CSweet.Office.RuntimeGuest` (and the builder and toolchain runners) inside a certified image. |

See [03-components.md](03-components.md) for the detailed map and
[06-process-network-and-storage-surface.md](06-process-network-and-storage-surface.md) for ports, sockets,
and directories.

## The security shape in one paragraph

The installer pins Headquarters assignment trust over a verified TLS connection before enrollment. The
Node cannot replace that pinned key. Every workload must arrive as a complete, signed Headquarters
authorization binding the office, assignment, workload, provider, canonical specification digest, issue
time, expiry, and fencing epoch. The RuntimeHost independently verifies that signature against the pinned
key and commits the fencing epoch to a protected replay ledger before it asks a provider to create a
virtual machine. A replayed or stale authorization is rejected. An unavailable or uncertified provider
always fails closed — there is no process-level or shared-kernel-container fallback.

The full treatment is in [20-security/](../20-security/README.md).

## What is deliberately not in this repository

> **Out of repo:** These live in the C-Sweet repository and are reached over the network or through the
> `CSweet.Office.Contracts` package.

- The Headquarters gateway and its Kestrel endpoints.
- Scheduling and assignment placement decisions.
- Enrollment approval and certificate issuance.
- Artifact authorization, storage, signing, and validation.
- Guest broker session handling on the host side — the challenge/lease exchange, boot configuration and
  `StartCommand` construction, and purpose authorization for proxied requests.
- Result artifact ingestion: the Office never sets `result_artifact_*` on a status update.
- The fleet UI.

The practical consequence: Office is a **relay** for guest traffic. It moves bytes between the guest and
Headquarters and does not interpret the broker protocol. The only in-repo implementation of the host side is
the certification harness in `src/CSweet.Office.WindowsSmokeTest/CertificationBrokerHost.cs`, which is
test-only and says so in its header comment. Treat it as documentation of the protocol, not as production
behavior. See [30-workloads/04-guest-broker-protocol.md](../30-workloads/04-guest-broker-protocol.md).

Two more repositories are involved:

- `CSweet.Office.Contracts` — the versioned cross-repository contract package. It supplies the control-plane
  protos and gRPC clients, the guest envelope and handshake, `LengthDelimitedProtobuf`, and the security
  primitives (`AssignmentEnvelope`, `WorkloadAuthorizationEnvelope`, `OfficeCertificateRecoveryProof`).
- `CSweet.Isolation` — shared tooling. `scripts/windows/New-CSweetHyperVTestGuest.ps1` imports the
  `CSweet.LinuxImage` PowerShell module from it, and the Windows guest build fingerprint hashes it.

See [80-reference/cross-repo-contracts.md](../80-reference/cross-repo-contracts.md) and
[50-development/03-contracts-dependency.md](../50-development/03-contracts-dependency.md).

## Provenance

This repository was extracted from C-Sweet commit `a85a19be588c82b1d7a6b9b4ed174bf3cd204409`. The import
contains the former execution node, RuntimeHost, runtime abstractions and providers, native helpers,
builder/runtime guests, payload tools, installer scripts, and certification utilities. Earlier history
remains authoritative in the C-Sweet repository.

## Posture, not assumption

Office reports an explicit `baseline`, `hardened`, or `development` posture rather than assuming a
security level. Development placement requires two independent approvals: the Office installer must opt in
and the signed workload request from Headquarters must opt in. It still requires an available certified
hardware-virtualization provider.

On Windows, the two services run under separate Windows-managed virtual accounts with protected state
directories. Only the RuntimeHost virtual account is added to `Hyper-V Administrators`; the Node cannot
manage Hyper-V or modify RuntimeHost state. Neither account has write access to the immutable application
package. Because Windows grants Hyper-V administrators control over every VM on a host, use a dedicated
Office machine when unrelated or higher-trust Hyper-V workloads are present. Installation is refused on
domain controllers.

Mixed-use personal machines are supported, but the host administrator and the host operating system remain
trusted. A dedicated, patched host provides better assurance. Development-only execution is never an
automatic fallback and must never use production credentials or sensitive data. See
[40-operations/08-security-postures.md](../40-operations/08-security-postures.md).

## Next

- [02-trust-model.md](02-trust-model.md) — the principals and the evidence each one requires.
- [04-solution-map.md](04-solution-map.md) — how the code is organized.

## Sources

`README.md`, `AGENTS.md`, `CSweet.Office.slnx`, `CSweet.Office.Independent.slnx`,
`src/CSweet.Office.Node/Program.cs`, `src/CSweet.Office.RuntimeHost/Program.cs`,
`src/CSweet.Office.WindowsSmokeTest/CertificationBrokerHost.cs`,
`scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`,
`scripts/windows/New-CSweetHyperVTestGuest.ps1`, `scripts/linux/install-office.sh`.

Verified: 2026-09-15.
