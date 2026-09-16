---
description: "Rules for installers, uninstallers, recovery scripts, and developer tooling under scripts/. Covers admin/root requirements, the domain-controller refusal, drain gates, atomic trust writes, and why several tests assert on script text."
applyTo: "scripts/**"
---

# Installers, maintenance, and developer tooling

Read [`docs/20-security/06-host-privilege-model.md`](../../docs/20-security/06-host-privilege-model.md) and
[`docs/40-operations/`](../../docs/40-operations/README.md) before editing anything that installs, repairs,
upgrades, or removes an Office.

## Never change these

| Rule | Invariant |
|---|---|
| Keep `Assert-NotDomainController`. Office does not install on a domain controller. | `SEC-INV-17` |
| Keep explicit, non-inherited `RX` ACEs on the immutable package, and keep `Assert-FileReadExecuteAce` as an install gate. | `SEC-INV-16` |
| Never grant write, or broadened inherited access, to `S-1-5-83-0` (Virtual Machines) on the guest image. | `SEC-INV-16` |
| Never migrate a legacy identity. `reconnect` wipes `node`, `authorization`, `artifact-media`, `hyperv`, and `runtime-host.key`, and refuses while the recovery state is `active`. | `SEC-INV-18` |
| Keep the maintenance settings file inside the install root with `SYSTEM`/`Administrators`-only access. | `SEC-INV-19` |
| Enrollment tokens are never passed as command-line arguments. | — |

## Conventions in this folder

- PowerShell scripts are PowerShell 5.1 compatible, run under `powershell.exe`, use
  `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'`, and accept `-NoElevation` when they
  self-elevate.
- Shell scripts assume Ubuntu 24.04, must run as root, and validate their inputs before touching anything.
- Progress reporting goes through `CSweet.WindowsSetupProgress.ps1`. Its path must be exactly
  `%ProgramData%\CSweet\Setup\windows-isolation-<jobId:N>.json`; the helper enforces it.
- Writes are atomic: temp file plus `Move-Item -Force` (PowerShell) or a temp file plus `mv` (shell).
- Secrets are read from a protected file or stdin, then deleted.

## The guest build fingerprint

`Initialize-CSweetWindowsIsolationTest.ps1` computes `Get-GuestBuildFingerprint` over every non-`bin`/`obj`
file under the five in-repo roots listed in
[`docs/50-development/09-guest-image-changes.md`](../../docs/50-development/09-guest-image-changes.md), plus the
sibling `CSweet.Isolation/tools/LinuxImage`, plus `New-CSweetHyperVTestGuest.ps1` and the root props files.
Adding any file under those paths forces a guest rebuild. If you change the fingerprint inputs, say so loudly
in the pull request — it invalidates every developer's cache.

## Why several tests will not protect you

`LinuxInstallationTests.cs` and large parts of `WindowsHyperVOnboardingTests.cs` assert on the **text** of these
scripts, not their behaviour. They pass against a script that is present but broken. A green suite is not
evidence that an installer works; you must run it.

The one behavioural harness is `scripts/tests/Test-OfficeUpgradeProbe.ps1`, which stubs the cmdlets the recovery
probe uses and exercises eight scenarios. Run it after touching `Get-CSweetOfficeRecoveryState.ps1`.

## Before you change an installer

Answer all of these in the pull request:

1. Which installation actions are affected — first install, `reconnect`, or `upgrade`?
2. Does the change still refuse when the Office is not drained or has active assignments?
3. Does it preserve the property that a failed run leaves the previous install usable?
4. Do the ACLs still start from `/inheritance:r`?
5. Which docs pages need updating, and is the `Verified:` date refreshed?
