# Known limits and trade-offs

**Audience:** security reviewers and anyone about to "fix" something that looks wrong.

Each entry below is deliberate. The rationale is the point of this page: a change that removes a limitation
without addressing the reason it exists is a regression, not an improvement.

## Identity and credentials

### The Office private key is a passwordless, exportable PFX

`node-identity.pfx` is written with no password and loaded with `X509KeyStorageFlags.Exportable |
X509KeyStorageFlags.PersistKeySet`. All protection is filesystem ACLs.

**Why:** a service that must be able to use its key unattended has no human to supply a password. DPAPI
machine scope would bind the key to the machine but adds a failure mode on host migration without changing the
threat model, since a host administrator can decrypt it anyway.

**Blast radius:** anyone who can read the Node state directory can exfiltrate the only credential that can
drive the recovery endpoint. Restrict who can read `%ProgramData%\CSweet\Office\node` (or the Linux
equivalent).

### There is no in-place Headquarters key rotation

A changed assignment signing key requires drain plus reconnect/re-enrollment, or a fresh install.

**Why:** allowing a pin to change at runtime would give a compromised Headquarters a way to swap the trust
anchor without administrator involvement — exactly what pinning exists to prevent. `SEC-INV-01` is the
enforcement.

### The control-plane certificate pin is optional, and it replaces PKI rather than supplementing it

Without `ControlPlaneCertificateSha256` (or a trust file), the Node accepts any OS-trusted chain. With a pin,
the leaf fingerprint is compared in fixed time and name and validity are enforced, but chain-building errors
are deliberately ignored.

**Why:** a pin is meant to be the trust anchor for private CAs and self-signed deployments, where chain
building against the public root set is not meaningful.

**Operator consequence:** configure a pin. The Windows installer requires an explicit fingerprint in
non-interactive mode for a private CA.

## Cryptography

### Most of the crypto lives in another repository

`AssignmentEnvelope`, `WorkloadAuthorizationEnvelope`, `OfficeCertificateRecoveryProof`, the guest handshake,
`LengthDelimitedProtobuf`, and the control-plane protos all come from `CSweet.Office.Contracts`.

**Consequence:** changing a signing payload or the handshake is a cross-repository release. Follow
[50-development/03-contracts-dependency.md](../50-development/03-contracts-dependency.md). Note also that
`UseLocalOfficeContracts` silently prefers a sibling checkout when one exists, so local tests can pass against
uncommitted contract code.

### Two signature-format conventions coexist

The assignment and guest-handshake paths use the default .NET ECDSA format (DER). The certificate-recovery
proof pins `IeeeP1363FixedFieldConcatenation`.

**Why:** the recovery proof predates the others and its format is fixed by the server implementation.

**Hazard:** porting a signature between paths without changing the format fails silently at verification time.

### HMAC canonicalization assumes deterministic protobuf serialization

`ComputeSignature` clears only `AuthenticationSignature` and re-serializes with the generated
`ToByteArray()`. Correctness relies on deterministic field ordering.

**Hazard:** adding map fields, or changing proto options, could invalidate the canonical form on one side
without an obvious compile error.

## Replay protection

### The nonce replay cache is process-local

`ConcurrentDictionary` in memory; a restart clears it. The usable replay window is bounded by clock skew plus
retention, not persistence.

**Why:** the transport is a local pipe. Persisting nonces would add disk I/O to every RPC for a threat that is
already blocked by the pipe DACL.

### The assignment replay ledger is unbounded

`accepted-assignments.json` retains one entry per assignment id forever, with no pruning and no documented
retention.

**Why:** the epoch is the fencing contract. Pruning reopens the replay window for the pruned assignments.

**Operational note:** the file grows slowly. Do not treat it as a cache, and never delete it to unstick a
workload.

### Fencing is enforced by cancellation, not by the gate

The gate only *rejects* stale epochs. A `Fence` control message cancels the assignment's cancellation token in
the Node; the gate does not stop a running workload.

**Consequence:** if the Node is unable to deliver the cancellation, a fenced workload continues until its
broker lease expires or its provider is stopped.

## Privilege and blast radius

### The local RPC shared key is readable by the interactive control-plane user

`runtime-host.key` grants `R` to `ControlPlaneUserSid`, the Node SID, and the RuntimeHost SID, plus full control
to `SYSTEM` and `Administrators`.

**Why:** the interactive installer and the guided-recovery flows need to authenticate local RPC calls.
Defence in depth holds because workload creation still requires a Headquarters signature, so the interactive
user gains no ability to run work.

**If you change this:** verify the installer, the repair path, and the maintenance service still function
before narrowing the ACL.

### Windows grants Hyper-V administrators control over every VM on the host

The RuntimeHost SID is in `Hyper-V Administrators`, which is host-wide, not scoped to Office VMs.

**Why:** there is no narrower mechanism for a service to manage VMs it created.

**Mitigation:** use a dedicated Office machine when unrelated or higher-trust Hyper-V workloads are present.
Installation is refused on domain controllers (`SEC-INV-17`).

## Platform behaviour

### The Windows installer skips files whose digest already matches

`Install-CSweetOfficeRuntimeHost.ps1` continues past an installed file whose SHA-256 matches the manifest.
Re-running the installer against a **stale payload** is therefore a no-op rather than a refresh.

*Pinned by:* `WindowsHyperVOnboardingTests.RuntimeHostInstaller_SkipsAlreadyInstalledContentByDigest`.

**Consequence:** after host-code changes you must build a new payload first. The root README's development
section describes the payload-selection loop.

### The Hyper-V helper cannot serve logs

`Logs()` returns an empty array. The interface exists; the Windows implementation is a stub.

**Consequence:** the Office's `LogExcerpt` is usually empty on Windows, and workload stdout is not visible
through the provider. Use guest `runtime.logs` (Firecracker and builder runs) and the Event Log.

### Reaping is asymmetric, and builder VMs are never reaped on Windows or Linux

| Backend | Reaps |
|---|---|
| Hyper-V | Runtime and ToolchainBuild instances only. Builder VMs are explicitly skipped. |
| Firecracker | Runtime and ToolchainBuild only. |
| Apple Virtualization | **Runtime only** (`kind == 1`) — no toolchain or builder VMs. |

**Why:** builder instances are long-running and have no reliable lease to key on.

**Consequence:** an Office that crashes during a builder run can leave an orphaned VM. Clean up manually.

### `ResourceLimits.MaximumDurationSeconds` is not enforced

Nothing in Office or the helpers enforces it. Long-running work is bounded by the broker lease expiry that
Headquarters puts in `BrokerLease.ExpiresAt` — which the guest enforces with `CancelAfter` — and by the
assignment's signed authorization window of at most 10 minutes for the *authorization*, not the runtime.

**If you are relying on this field for enforcement:** it is not doing what you think.

### The artifact ISO digest covers the payload extent, not the whole file

`SingleFileIso9660.VerifyArtifactDigestAsync` re-parses the primary volume descriptor and the root directory
record and hashes only the file extent, so ISO padding and metadata are not covered.

**Why:** the payload bytes are the security-relevant content, and the payload digest is independently verified
against the assignment.

### Firecracker and Apple runtimes ignore `MaximumDurationSeconds` but honour cgroup/rlimit values

Memory, CPU, and process limits are applied through the jailer's cgroup arguments on Linux; Hyper-V declares
`SupportsProcessLimits: false`.

## Protocol details that surprise people

| Behaviour | Reason |
|---|---|
| Helpers exit `0` on typed failures | A non-zero exit would make RuntimeHost discard the typed error and raise a generic `IOException`. |
| The Node sends an empty tunnel frame with `Sequence = 0` before relaying | Headquarters cannot start its broker session until the first bound frame arrives, and the Linux guest waits for boot configuration. Waiting for guest bytes would three-way deadlock. |
| Tunnel download asserts strict sequence contiguity from `0` and a matching fencing epoch | Detects gaps and cross-assignment mix-ups. |
| `Stop` and `Destroy` are allowed after lease expiry, but `start`, `inspect`, and `logs` are not | A failed or expired workload must always be tearable-down; it must not be re-inspectable. |
| The guest powers its own VM off when the broker session ends | The lease and reaper are a backstop, not the primary lifecycle. |
| `ReadLogsAsync` in the Node caps at 64 KiB | Bounds the status update size. |
| Reconnection is a flat 5-second retry with no backoff and no jitter | Deliberate simplicity; Headquarters is the only peer, and a thundering herd of one Office is not a concern. |
| `BusinessId` is typed as a string but must parse as a GUID | Historical; the validation is the contract. |
| The artifact materializer's destination root is the **parent** of `ArtifactRoot` | `ArtifactRoot` must be exactly `/run/csweet/artifact/payload`, so extraction targets `/run/csweet`. |
| `BuilderGuest` and `ToolchainGuest` bundle whatever is in the publish output directory | Source markdown is not copied to output, so documentation does not enter an artifact bundle — but adding `CopyToOutputDirectory` to a non-code file would. |

## Process caveats

### Adding files to four `src` folders or `build/windows-hyperv` invalidates the guest image cache

`Get-GuestBuildFingerprint` in `scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1` SHA-256 hashes
**every** non-`bin`/`obj` file under:

- `src/CSweet.Office.RuntimeGuest`
- `src/CSweet.Office.BuilderGuest`
- `src/CSweet.Office.ToolchainGuest`
- `src/CSweet.Office.Runtime.Protocol`
- `build/windows-hyperv`
- `../CSweet.Isolation/tools/LinuxImage` (a sibling repository)
- `scripts/windows/New-CSweetHyperVTestGuest.ps1` (a single file)

plus the root `Directory.Build.props`, `Directory.Packages.props`, and `global.json` when present.

Adding any file there — including documentation — forces a full Packer guest rebuild on the next Windows
isolation test run. This is a property of the build tooling, not a bug. See
[50-development/09-guest-image-changes.md](../50-development/09-guest-image-changes.md).

### The test suite is the de-facto specification

There is no separate spec document. Several test classes assert on the **text** of scripts rather than their
behavior, so they pass against a script that is present but broken. See
[80-reference/tests.md](../80-reference/tests.md) for which classes pin which invariant.

## Sources

`src/CSweet.Office.Node/{OfficeStateStore.cs,OfficeWorker.cs,OfficeCertificateLease.cs,ControlPlaneServerCertificateValidator.cs,OfficeArtifactCache.cs}`,
`src/CSweet.Office.Runtime.LocalRpc/{RuntimeHostAuthorizationGate.cs,RuntimeHostRequestDispatcher.cs,RuntimeHostProviderClient.cs}`,
`src/CSweet.Office.Runtime.Protocol/RuntimeHostAuthentication.cs`,
`src/CSweet.Office.Runtime.Core/{SingleFileIso9660.cs,PlatformRuntimePayloadManifest.cs}`,
`src/CSweet.Office.RuntimeGuest/{GuestServiceOptions.cs,GuestArtifactMaterializer.cs,GuestBrokerSession.cs,GuestSystemPower.cs}`,
`src/CSweet.Office.Runtime.HyperV/HyperVSocketTransport.cs`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Sources/CSweetAppleVirtualizationHelper/HelperController.swift`,
`scripts/windows/{Initialize-CSweetWindowsIsolationTest.ps1,Install-CSweetOfficeRuntimeHost.ps1}`,
`tests/CSweet.Office.Tests/*`.

Verified: 2026-09-15.
