# Upgrade and drain

**Audience:** operators and administrators upgrading an Office without re-enrolling it.

An Office keeps its identity across a version upgrade only if it is drained first. Drain is a marker file, not a
scheduler state: Office records it and refuses upgrades without it, while Headquarters is expected to stop
dispatching. This page covers the marker, the probe that reads it, and what survives an upgrade.

## What drain is

`Drain` is a Headquarters control message. The Node handles it with `OfficeStateStore.SetDraining`, which writes
`maintenance/drain-state`:

| Content | Meaning |
|---|---|
| `draining` | An upgrade or removal may proceed, once there are no active assignments. |
| `ready` | Not draining. The value is also what a missing file implies. |

Nothing in the Node reacts to the marker operationally. It does not cancel work, does not refuse new
assignments, and does not stop the service. Draining is a **reporting** state whose only consumers are the
installers, the recovery probe, and the uninstallers.

> **Out of repo:** Headquarters owns dispatch. Drain only takes effect because C-Sweet stops sending
> assignments; the Office will happily start new work while draining.

## How to drain

| Route | Action |
|---|---|
| C-Sweet | Drain the node in C-Sweet, then wait for active assignments to reach zero. Nothing local is required beyond keeping the service running so the message is delivered. |
| Locally (Windows) | `.\Enter-CSweetOfficeMaintenance.ps1 -OfficeId <guid>` writes the marker itself. |

`Enter-CSweetOfficeMaintenance.ps1` is the OS-side equivalent and is the only script that sets the marker. It
performs the following, in order, and restores the Node service if anything fails:

1. Verify that `-InstallRoot`, `-DataRoot`, and `<DataRoot>\node` exist and are not reparse points.
2. Verify that `node-state.json` exists, is not a reparse point, and carries the requested `OfficeId`
   (failure: `maintenance_unsafe`).
3. Run `Get-CSweetOfficeRecoveryState.ps1`; anything other than `clean` fails with `maintenance_busy` before
   anything is removed.
4. Stop `CSweet.Office.Node` and wait up to 30 seconds.
5. **Re-run the probe** after dispatch has stopped, to close the race with work that arrived in between.
6. Write `draining` to `maintenance/drain-state`.

The re-probe is the point: draining is only meaningful if work is genuinely finished, and the probe is the
local authority on that.

## Active assignments

`OfficeStateStore.MarkAssignmentActive` writes `maintenance/active-assignments/<assignmentId:N>.active` when an
assignment is admitted to the active set, and `MarkAssignmentInactive` deletes it in the teardown block. The
markers are the *only* local record that a workload was running, and every upgrade gate counts them.

## The recovery probe

`scripts/windows/Get-CSweetOfficeRecoveryState.ps1` prints exactly one of four values on its last output line and
also accepts `-ForUpgrade`. Every consumer treats anything outside the four values as `unsafe`.

| State | Returned when |
|---|---|
| `none` | Neither `CSweet.Office.Node` nor `CSweet.Office.RuntimeHost` is registered **and** `%ProgramData%\CSweet\Office` does not exist. A signed MSI payload that has never enrolled is already present under `%ProgramFiles%\CSweet\Office`, so the test is on the data root, not the install root. |
| `clean` | Both services are registered, both `--contentRoot` values resolve inside the install root, `appsettings.json` exists, the Node state directory and the authorization state directory both resolve inside the data root, there are no `*.active` markers, and nothing VM-shaped is running. |
| `active` | Work is or may be in flight: any `*.active` marker; in upgrade mode, a drain state other than `draining`; in non-upgrade mode, any authorization handle record at all; or an Office-owned Hyper-V VM that is not `Off` — a *Running* or *Saved* VM both count. |
| `unsafe` | The state cannot be trusted: only one of the two services registered; a service command without a `--contentRoot` inside the install root; a missing `appsettings.json`; a state directory outside the data root; a handle record whose provider is not `hyperv-gen2` or whose instance id does not parse as a GUID; VM state that cannot be read because the Hyper-V module is unavailable; or any exception at all, since the whole probe is wrapped in a fail-safe catch. |

`-ForUpgrade` relaxes exactly two things:

- Existing authorization **handle records** are tolerated instead of forcing `active`. Every record must still
  be `hyperv-gen2` with a parseable provider instance id, otherwise the state is `unsafe`.
- Office-owned VMs must exist either by matching a recorded handle or by living under `<DataRoot>\hyperv`, and
  every one of them must be `Off`. A VM that is running or saved keeps the state `active`.

Everything else, including the drain requirement, still applies. That is the behaviour Office 0.5.3 permits an
identity-preserving upgrade with: saved authorization records and powered-off VMs are treated as historical
state, not as live work. Removal and re-enrollment keep the stricter checks.

## Who gates on what

| Caller | Gate |
|---|---|
| `Install-CSweetOfficeRuntimeHost.ps1` with `-ExistingInstallationAction upgrade` | Drain state `draining`, zero `*.active` markers, and an upgrade-mode probe result of `clean` immediately before the copy. A change in between fails with *"Office work or local state changed before installation."* |
| The same installer with any other action on an existing Office | Drain state `draining` and zero `*.active` markers. |
| `Enter-CSweetOfficeMaintenance.ps1` | Strict probe result `clean`, twice. |
| `Uninstall-CSweetOffice.ps1`, `uninstall-office.sh`, macOS `uninstall-office.sh` | Drain state `draining` and zero `*.active` markers, exit code 3 otherwise. `-Force` / `--force` bypasses this gate and is intended only after revocation. |
| `csweet-office` package removal (deb/rpm `prerm`) | Delegates to the uninstaller, so the same gate applies. |
| Guided Windows repair and upgrade | `CSweet.Office.Configurator` calls the probe before the preflight and normalizes anything it does not recognize to `unsafe`. |

## What an upgrade changes

| Action | Deleted | Preserved |
|---|---|---|
| In-place refresh (`none` on an existing Office) | Nothing. Configuration for the Node is carried over from the installed `appsettings.json`. | Identity, state, artifacts, VMs. |
| `reconnect` | `<DataRoot>\node`, `<DataRoot>\authorization`, `<DataRoot>\artifact-media`, `<DataRoot>\hyperv`, and `<DataRoot>\runtime-host.key`. | Nothing that carries trust. (`SEC-INV-18`.) |
| `upgrade` | The previous package contents only. | The Office identity, the enrollment receipt, the Headquarters trust pin, the authorization ledger, the artifact cache, and the Hyper-V data root. |

An `upgrade` requires an existing identity and configuration and **must not** carry enrollment material. Each
package version installs to `<install root>\<packageVersion>` with its own `appsettings.json`, and the installer
does not remove earlier version roots.

## What remains after an upgrade

Expect all of the following to survive, and do not "clean" them:

- `authorization\accepted-assignments.json` — the fencing-epoch ledger, deliberately never pruned
  (`SEC-INV-09`). See [20-security/12-known-limits-and-tradeoffs.md](../20-security/12-known-limits-and-tradeoffs.md).
- `authorization\headquarters-trust.json` — write-once, with one permitted `Guid.Empty → real OfficeId`
  transition (`SEC-INV-01`).
- `authorization\authorized-workload-handles.json` — records for workloads the RuntimeHost created. After a
  successful upgrade these are accepted as history by the upgrade probe, so a powered-off VM left behind by a
  previous run does not block the next upgrade.
- `<DataRoot>\artifact-media\*.iso` — cached images, keyed by artifact digest.
- `<DataRoot>\hyperv\instances\` and `<DataRoot>\hyperv\vm-config\` — VM state under the RuntimeHost's ACL.

## Why identity preservation needs a real drain

`SEC-INV-18` states the rule; the operational reasons are:

- The RuntimeHost is replaced underneath work it may still be running, including handle records and VMs. The
  probe exists to prove that nothing is live before that happens.
- An upgrade restarts the RuntimeHost, so an in-flight authorization would be abandoned mid-execution with no
  one left to destroy the workload.
- A changed Headquarters assignment signing key cannot be re-pinned in place (`SEC-INV-01`). A different key
  means drain, then `reconnect` or a fresh install, because identity is never migrated.

## Validating the probe

`scripts/tests/Test-OfficeUpgradeProbe.ps1` stubs the services, the configuration, and the Hyper-V cmdlets, then
asserts eight scenarios against the probe, including the drain state, the marker count, a running VM, a saved
VM, and an unrecognized provider record. Run it after touching
`scripts/windows/Get-CSweetOfficeRecoveryState.ps1`.

## Sources

`scripts/windows/{Install-CSweetOfficeRuntimeHost.ps1,Get-CSweetOfficeRecoveryState.ps1,Enter-CSweetOfficeMaintenance.ps1,Uninstall-CSweetOffice.ps1}`,
`scripts/linux/{install-office.sh,uninstall-office.sh}`, `scripts/macos/{install-office.sh,uninstall-office.sh}`,
`scripts/tests/Test-OfficeUpgradeProbe.ps1`, `src/CSweet.Office.Node/{OfficeWorker.cs,OfficeStateStore.cs}`,
`src/CSweet.Office.Configurator/Program.cs`, `releases/0.5.3.md`,
`docs/20-security/11-security-invariants.md`, `docs/30-workloads/01-assignment-and-lease-semantics.md`.

Verified: 2026-09-15.

## Guided local debug builds

The Headquarters Office page exposes Build and update (debug) for the explicitly configured local
development source, including same-version source edits. The existing drain, certification and
identity-preserving upgrade gates still apply.

Source: scripts/windows/CSweet.DevelopmentBuild.ps1 serializes bootstrap processes with a machine-wide
mutex and waits for live developer-bootstrap progress owners, including command-line builds started
before this helper existed. It never terminates existing work. Unreadable progress fails closed;
waiting is bounded at two hours. Older running records without a valid owner process are ignored only
when their update timestamp predates the Windows boot time. Current-boot or unverifiable ownerless
records stop setup with a recovery message, preserving concurrent-build exclusion after database resets.
Completed image markers remain reusable, but a subsequent operation
may certify that image again.

Source: scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1 returns the exact completed payload
through -PayloadResultPath. The Headquarters development launcher consumes that result and does not
select a payload directory by its timestamp. Verify coordination with
scripts/tests/Test-DevelopmentBuildCoordination.ps1.

Verified: 2026-09-16.
