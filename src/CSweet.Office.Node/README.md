# CSweet.Office.Node

The unprivileged half of an Office: it enrolls the machine with Headquarters, owns the Office identity and its certificate lifecycle, maintains the control session and heartbeat, validates signed assignments before executing them, downloads and verifies workload artifacts, and relays the authenticated guest-channel byte stream. It never talks to a hypervisor; its only privileged interaction is the local RPC client.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/node.md`](../../docs/80-reference/projects/node.md).

**Output:** worker service exe
**References:** `CSweet.Office.Runtime.Abstractions`, `CSweet.Office.Runtime.Artifacts`, `CSweet.Office.Runtime.LocalRpc`, `CSweet.Office.Runtime.Protocol`

## What it owns

- `OfficeWorker` — the whole control loop: enroll, bump the session epoch, pin RuntimeHost trust, refresh the certificate, run one control session, execute assignments, and relay the guest channel.
- `OfficeOptions` — section `CSweet:Office:Node`, including allocations, security posture, and the state, artifact-cache, and media directory resolvers.
- `OfficeStateStore` — the durable identity (`node-state.json`, `node-identity.pfx`) and the maintenance drain and assignment markers.
- `ControlPlaneServerCertificateValidator` and `ControlPlaneCertificateProbe` — the pinned HTTP handlers and the probe/initialize CLI modes that run before the host is built.
- `OfficeArtifactCache` and `RuntimeHostInventory` — verified artifact download plus media generation, and the provider inventory rows for enrollment and heartbeat.

## Notes for contributors

- `CreateAsync` on the provider client is unreachable by design (SEC-INV-07); assignments go through `CreateAuthorizedAsync`.
- `OfficeWorker.ValidateAssignment` duplicates `RuntimeHostAuthorizationGate.ValidateAndCommit` on purpose; two verifications at two privilege levels is the design, not an oversight — see [docs/20-security/12-known-limits-and-tradeoffs.md](../../docs/20-security/12-known-limits-and-tradeoffs.md).
- The tunnel sends a `Sequence = 0` frame with an empty body before relaying; three parties deadlock without it, and one test asserts on the source order. Do not reorder.
- `OfficeArtifactCache.ImportAsync` throws `NotSupportedException` deliberately.
- The shared key is read from `%PROGRAMDATA%\CSweet\Office\runtime-host.key`; rotation is an installer concern, not a code path here.

## Tests

`OfficeTests` (mixed behavioural and source-text assertions), `OfficeCertificateRecoveryTests`, `OfficeCertificateTlsTests`, `ControlPlaneServerCertificateValidatorTests`, `OfficeMaintenanceStateTests`, and `OfficeWorkerFailureTests`.

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
