# CSweet.Office.Configurator

The Windows enrollment handoff and the maintenance service host. It parses the browser-issued `csweet-office://enroll/v1` URI, re-launches itself elevated, talks to the control plane's assisted-setup endpoints, and runs the shipped installer with the values Headquarters returned. With `--maintenance-service` the same executable becomes `CSweet.Office.Maintenance`, the third Windows service, which polls for authorized repair and upgrade requests. It has no project references and shares nothing with the runtime libraries.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/configurator.md`](../../docs/80-reference/projects/configurator.md).

**Output:** console exe
**References:** none

## What it owns

- `Program.cs` — the handoff flow: fixed URI parsing, the elevation retry, preflight and redeem, recovery-state classification, and the installer invocation with the token as a locked-down file.
- `Arguments` — the only accepted URI shape (`csweet-office` scheme, `enroll` host, `/v1` path) plus query parsing.
- `OfficeMaintenanceService` — the `--maintenance-service` host and its `MaintenanceSettings` record.
- `MaintenanceWorker` — the 30-second poll that recovers the identity, claims a handoff, validates it, and launches the Configurator with a fixed `--uri` argument.

## Notes for contributors

- Headquarters cannot pass a command line: the worker launches only the installer-written `ConfiguratorPath` with a fixed argument (SEC-INV-19).
- Enrollment tokens are files with write-through and explicit ACLs, passed as paths and deleted in a `finally`; never move them onto a command line.
- `MaintenanceWorker.ValidHandoff` accepts only `repair` and `upgrade`; do not widen the operation set.
- Exit codes are meaningful: 2 usage or platform, 3 failed elevation, 4 rejected preflight or redeem, 5 failed install, 6 failed removal.
- On a non-Windows host the process exits `2` immediately.

## Tests

`MaintenanceHandoffTests`, `OfficeMaintenanceStateTests`, and several script-text assertions in `WindowsHyperVOnboardingTests` covering the assisted installer path this executable invokes.

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
