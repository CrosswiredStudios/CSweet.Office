# Security postures

**Audience:** administrators choosing how much assurance an Office claims, and reviewers checking that the claim
is enforced.

An Office reports one of three postures — `baseline`, `hardened`, or `development` — plus whether it is a
mixed-use host, whether development assignments are allowed, and the normalized lists of enabled and missing
security controls. The report is evaluated locally by `OfficeOptions.SecurityPosture()`, sent on enrollment and
on every heartbeat, and it is a **claim about the host**, not a switch that weakens the isolation model.

| Posture | Intended host | What it asserts |
|---|---|---|
| `baseline` | A shared or personal machine that also runs C-Sweet or other work. | Office runs with default protections; the operator has accepted that a host-level compromise of the OS, hypervisor, or provider reaches other data on that machine. |
| `hardened` | A dedicated, patched host used only for Office. | Every control in `EnabledSecurityControls` is present and `MissingSecurityControls` is empty. |
| `development` | A disposable test machine. | The operator has explicitly accepted a vulnerable-by-design configuration, and development placement has been allowed. |

`mixed-use` (`MixedUseHost`) is orthogonal to the profile: `true` declares that the machine is shared, `false`
declares it dedicated. On Linux and macOS the dedicated declaration is what selects the `hardened` profile; on
Windows the two are independent parameters.

## What the posture does not do

- It does not relax certification. `EnforcePlatformMinimum` raises any request below
  `IsolationAssurance.CertifiedHardwareVirtualMachine` and never lowers the floor (`SEC-INV-10`). A certified
  hardware-virtualization provider is required in every posture.
- It does not provide a fallback. An unavailable or uncertified provider fails closed; there is no process or
  shared-kernel-container path, in any posture.
- It does not protect against the host administrator or the host operating system. Both remain trusted
  throughout, and Office does not defend against them.
- It does not make `development` safe. Development placement is meant for disposable test data and credentials
  only.

## `SecurityPosture()` fails closed

`OfficeOptions.SecurityPosture()` throws `InvalidOperationException` rather than reporting a posture that
contradicts the configuration:

| Condition | Message |
|---|---|
| The profile is not `baseline`, `hardened`, or `development` after trimming and lower-casing | *"SecurityProfile must be baseline, hardened, or development."* |
| `development` without `AllowDevelopmentAssignments` | *"The development security profile requires explicit AllowDevelopmentAssignments consent."* |
| `hardened` with a non-empty `MissingSecurityControls` | *"The hardened security profile cannot be reported while required controls are missing."* |

Because the report is produced during the enrollment claim and on every heartbeat, neither failure is cosmetic:
the first prevents enrollment, and the second terminates the control session on the next heartbeat. A state of
"hardened, but actually missing controls" is not representable.

The installers apply the same rule earlier, so the operator sees a usable error instead of a service that will
not enroll:

| Platform | Behaviour |
|---|---|
| Windows | `-SecurityProfile development` without `-AllowDevelopmentAssignments` throws before anything is changed, and a development install prints *"Development posture is vulnerable by design. Use only disposable test data and credentials; certified VM isolation remains mandatory."* |
| Linux, macOS | `--security-profile development` without `--allow-development-assignments` exits 2, and a development install writes the same warning to standard error. |

## The dual opt-in for development placement

Development work is not a fallback that Headquarters can select, and it is not something the installer can
choose alone. **Two independent opt-ins are required**, and neither side can see or set the other's:

| Opt-in | Set by | Where it lands |
|---|---|---|
| Installer opt-in | The administrator installing Office (`-AllowDevelopmentAssignments`, `--allow-development-assignments`). | `AllowDevelopmentAssignments` in the Node configuration, reported as `DevelopmentAssignmentsAllowed`. |
| Request opt-in | The signed workload request from Headquarters. | Inside the signed authorization, so it cannot be added or removed in transit. |

> **Out of repo:** Headquarters is what decides whether a workload may be placed on a development Office. Office
> only reports its own opt-in and enforces what the signed request carries.

## Choosing a posture

| Situation | Windows | Linux | macOS |
|---|---|---|---|
| Shared machine, defaults | `-SecurityProfile baseline` (default) with `-MixedUseHost $true` (default) | `sudo csweet-configure-office https://…` and accept the baseline notice, or pass `--accept-baseline-risk` non-interactively | `sudo csweet-configure-office https://…`, or `install-office.sh --security-profile baseline` directly |
| Dedicated Office host | `-SecurityProfile hardened -MixedUseHost $false` | `sudo csweet-configure-office https://… --dedicated-host` | `install-office.sh --security-profile hardened --dedicated-host` from the payload directory |
| Disposable test host | `-SecurityProfile development -AllowDevelopmentAssignments` | `install-office.sh … --security-profile development --allow-development-assignments` | `install-office.sh … --security-profile development --allow-development-assignments` |

On Linux the configurator prints the posture it selected and what it implies before the enrollment token is
requested. `--accept-baseline-risk` only acknowledges that notice; it does not change the profile. On macOS the
root-only configurator takes no profile flags, so the dedicated and development paths are reached by invoking the
payload's own `install-office.sh`.

## Normalized control lists

`EnabledSecurityControls` and `MissingSecurityControls` are free-form string arrays in configuration that are
normalized before they are reported. The normalization is part of the contract with Headquarters:

| Rule | Effect |
|---|---|
| Trim, then lower-case | Case and surrounding whitespace are not significant. |
| Keep only values of 1–100 characters | An empty value, or one longer than 100 characters, is dropped silently. |
| Keep only ASCII letters, digits, `-`, and `.` | Anything else is dropped silently. |
| Distinct, ordinal-sorted, at most 64 entries | Ordering and duplicates are removed so two Offices with the same controls report identically. |

A dropped control is a *missing* control only if it appears in `MissingSecurityControls`. For a `hardened`
Office, listing any missing control is what makes the report throw; listing an unnormalizable value in
`MissingSecurityControls` makes it disappear, so operators should use plain lowercase identifiers.

## Sources

`src/CSweet.Office.Node/{OfficeOptions.cs,OfficeWorker.cs}`, `scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`,
`scripts/linux/{install-office.sh,configure-office.sh}`, `scripts/macos/{install-office.sh,configure-office.sh}`,
`README.md`, `docs/10-system/02-trust-model.md`, `docs/GLOSSARY.md`,
`docs/20-security/11-security-invariants.md`, `releases/0.4.0.md`.

Verified: 2026-09-15.
