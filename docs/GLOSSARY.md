# Glossary

Vocabulary used across the Office documentation, the codebase, and the C-Sweet control plane. Terms are
listed alphabetically. Where a term is defined by a type, the type is named.

**Accepted assignment ledger** — See *replay ledger*.

**Adapter** — An executable supplied inside a toolchain artifact package that performs a build and signals
completion by creating `<output>/.csweet-adapter-complete`. Run by `CSweet.Office.ToolchainGuest`.

**AppHost** — The C-Sweet desktop application. It does not launch Office.

**Artifact** — A signed, hashed payload that a workload runs. In transit it is a `.csab` archive
(`artifact.json` plus `payload/**`). At rest on an Office it is a cache file plus a single-file ISO.
See [30-workloads/05-artifacts-and-media.md](30-workloads/05-artifacts-and-media.md).

**Artifact media** — The directory (`ArtifactImageRoot` / `ArtifactMediaDirectory`) where the Office writes
`<hex digest>.iso` images that providers attach to guests read-only. The Node and the RuntimeHost must
point at the same physical directory.

**Artifact read token** — A 32–256 character, assignment-scoped credential that authorizes a single
artifact download. Never persisted by the Office.

**Assisted setup session** — A `Guid` correlating an interactive C-Sweet-driven installation with the
Office installer. Required for the `reconnect` installation action.

**Assignment** — A Headquarters-issued, signed unit of work: a workload specification plus the identity,
provider, fencing epoch, time window, and signature that bind it. Type `WorkloadAssignment`.

**Assignment envelope** — The canonical signed byte layout for an assignment, produced by
`AssignmentEnvelope.Payload(...)`. Defined in `CSweet.Office.Contracts`.

**Assurance** — `IsolationAssurance.CertifiedHardwareVirtualMachine` is the only value Office will place
work on. The fail-closed selector raises any lower request back to it.

**Authorization** — See *signed workload authorization*.

**Bootstrap certificate** — The self-signed ECDSA P-256 certificate an unenrolled Office creates locally
(`Subject == Issuer`) so it can make its first enrollment call.

**Broker lease** — The host-supplied bound on a guest session: channel id, protocol version, boot token,
expected guest image digest, expected artifact digest, and expiry. The guest enforces the expiry.

**Builder workload** — Workload kind `0`. Builds a plugin from a repository and uploads an artifact bundle
through the broker. Runs with no artifact media attached.

**Configurator** — `CSweet.Office.Configurator`. A Windows executable that parses the
`csweet-office://enroll/...` handoff URI and can run as the maintenance service.

**Certification** — Evidence that a provider/host/guest-image/broker combination was tested and is currently
valid. Type `IsolationProviderCertification`, with `CertifiedAt`, `ExpiresAt`, `RevokedAt`.

**Certification suite version** — The identifier of the test suite that produced certification evidence
(for example `windows-hyperv-v1`). The guest image registry uses it to detect an out-of-date runtime.

**Drain** — An Office state in which it accepts no new assignments. Recorded as a marker file
`maintenance/drain-state` containing `draining`. Required before an identity-preserving upgrade.

**Dedicated host** — An operator declaration that a machine is used only for Office. Selects the `hardened`
security posture on Linux and macOS installs.

**Development posture** — The `development` security profile. Requires two independent opt-ins: the
installer must allow it and the signed Headquarters request must opt in.

**Enrollment** — The one-time exchange that binds an Office to a C-Sweet installation using a one-use
token. Produces an `OfficeId`, an enrollment receipt, and the pinned assignment signing key.

**Enrollment receipt** — A server-issued value that proves a successful enrollment. Used only for the
bootstrap certificate exchange; it cannot recover identity once spent.

**Fencing epoch** — A monotonically increasing integer carried in a signed assignment. The RuntimeHost
rejects an authorization whose epoch is lower than or equal to the highest epoch already accepted for that
assignment. This is the replay defence.

**Fail closed** — The design rule that an unavailable, uncertified, or unrecognized provider produces an
error rather than a degraded execution path. There is no process or shared-kernel fallback.

**Guest broker** — The in-guest service `CSweet.Office.RuntimeGuest`. It validates boot configuration,
materializes artifacts, performs the authenticated handshake, proxies agent SDK requests, and supervises
the workload process.

**Guest image** — The immutable, signed, certified disk image a provider boots. Exactly one image is
certified per provider configuration.

**Handoff URI** — The `csweet-office://enroll/...` URI the configurator consumes, carrying `handoff`,
`session`, `origin`, and `certificate` parameters.

**Headquarters (HQ)** — The C-Sweet control plane: gateway, scheduling, enrollment approval, certificate
issuance, artifact authorization and storage, guest broker streaming, and fleet UI. Out of repo.

**Helper** — The narrow, digest-pinned platform executable that performs privileged VM lifecycle operations
(`CSweet.Office.Runtime.HyperV.Helper`, `CSweet.Office.Runtime.Firecracker.Helper`, and the Swift
`CSweet.Office.Runtime.AppleVirtualization.Helper`). Speaks typed JSON over stdio, protocol `1.0`, eight
operations.

**Isolation provider** — A platform backend that can run a workload in a certified hardware virtual machine.
Provider ids: `hyperv-gen2`, `firecracker-kvm`, `apple-virtualization`.

**Local broker proxy** — The guest-local HTTP/1.1 endpoint (`GuestLocalBrokerProxy`) on a Unix socket. It is
what the agent SDK talks to. Accepts `POST` to `/mcp`, `/build/fetch`, `/build/artifact`, `/build/progress`.

**Maintenance service** — `CSweet.Office.Maintenance`, running `CSweet.Office.Configurator.exe
--maintenance-service`. Receives authorized repair requests over an outbound connection. No inbound listener.

**Mixed-use host** — A machine that runs Office alongside other workloads. Supported; the host administrator
and host operating system remain trusted. Selects the `baseline` posture.

**Node** — `CSweet.Office.Node`. The unprivileged, outbound-only control client. It cannot manage Hyper-V or
modify RuntimeHost state.

**Office** — The independently installed execution plane: the Node, the RuntimeHost, the maintenance service,
the platform helpers, and the guests. Versioned and released independently of C-Sweet.

**OfficeId** — The `Guid` identifying an enrolled Office. Rides on every control message and every
authorization.

**Payload** — A versioned, manifest-verified directory of Office binaries plus the certified guest image,
helper, certificate, and certification evidence. Installed to `$InstallRoot\<packageVersion>\`.

**Placement** — The decision of which provider and guest image will run a workload. Constrained to certified
hardware virtual machines.

**Posture** — One of `baseline`, `hardened`, or `development`, reported to Headquarters in the heartbeat.
See [40-operations/08-security-postures.md](40-operations/08-security-postures.md).

**Provider inventory** — The per-provider registration the Node reports on the first control message of every
session: provider id, version, broker protocol version, guest image digest, certification suite version,
evidence digest, certification window, and availability.

**Reaper** — `RuntimeHostWorkloadReaper` plus the per-backend `IPlatformWorkloadReaper`. Removes abandoned
workloads on a one-minute timer without depending on the control-plane database.

**Replay ledger** — `accepted-assignments.json` in the RuntimeHost authorization state directory. Maps
assignment id to the highest accepted fencing epoch.

**Runtime workload** — Workload kind `1`. Runs an agent from a materialized artifact with artifact media
attached.

**RuntimeHost** — `CSweet.Office.RuntimeHost`. The privileged virtualization service. Owns the helper
protocol, the authorization gate, and — on Windows — the guest channel. Has no network listener.

**Session epoch** — A per-process monotonic counter (`max(previous + 1, now in milliseconds)`) that every
control message must carry. Fences a control session against stale traffic.

**Signed workload authorization** — The Headquarters-signed structure that binds office, assignment,
workload, provider, specification digest, issue time, expiry, and fencing epoch. Verified twice: once by the
Node and once by the RuntimeHost authorization gate.

**Specification** — The JSON workload description inside an assignment, plus its `sha256:` digest. The digest
is recomputed independently on both sides.

**Toolchain workload** — Workload kind `2`. Runs the certified adapter
`/usr/lib/csweet/toolchain/CSweet.Office.ToolchainGuest` from a materialized package.

**Toolchain adapter** — See *adapter*.

**TransferId** — A `Guid` that lets Headquarters resume an interrupted `DownloadArtifact` stream across the
Office's three download attempts.

**Trust pin** — The `PinnedHeadquartersTrust` record (Office id, assignment signing key id, SPKI public key)
persisted by the RuntimeHost and by the installer. Write-once: a mismatch is fatal and requires re-enrollment.

**vSock** — The guest channel transport. Windows uses a raw `AF_HYPERV` (34) socket to the broker service
GUID; Linux and macOS guests use `AF_VSOCK` (40). Both are implemented by hand because .NET does not map these
address families.

See also: [30-workloads/01-assignment-and-lease-semantics.md](30-workloads/01-assignment-and-lease-semantics.md)
for how assignment, lease, and epoch interact.
