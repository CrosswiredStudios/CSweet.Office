# 40 · Operations

Installing, upgrading, draining, repairing, diagnosing, and removing an Office. These pages address the
administrator performing the work; the guarantees the work rests on are in
[20-security](../20-security/README.md) and [30-workloads](../30-workloads/README.md).

| Page | Covers |
|---|---|
| [01-installation-windows.md](01-installation-windows.md) | Prerequisites, the developer and packaged install paths, installer parameters, and what an install creates. |
| [02-installation-linux.md](02-installation-linux.md) | Ubuntu 24.04 prerequisites, the deb package, `csweet-configure-office`, and the first-install cutover. |
| [03-installation-macos.md](03-installation-macos.md) | launchd labels, the pkg, the root-only configurator, plist configuration, and the helper entitlement. |
| [04-upgrade-and-drain.md](04-upgrade-and-drain.md) | The drain state, the four recovery-probe results, and what an identity-preserving upgrade keeps. |
| [05-repair-and-recovery.md](05-repair-and-recovery.md) | Guided Windows recovery, the maintenance service, and why it works when the Node does not. |
| [06-uninstall-and-removal.md](06-uninstall-and-removal.md) | Removal on all three platforms, what is deleted, and what is deliberately left behind. |
| [07-diagnostics-and-troubleshooting.md](07-diagnostics-and-troubleshooting.md) | Symptom table, failure codes, log locations, and the live smoke test. |
| [08-security-postures.md](08-security-postures.md) | `baseline`, `hardened`, and `development`, the dual opt-in, and mixed-use hosts. |

## Hardware virtualization is not optional

An Office can run work only on a certified hardware-virtualization provider. Installation and enrollment are
expected to succeed on a host without one, but placement cannot: every install path requires an available
certified provider, and there is no process, container, or shared-kernel fallback.

| Platform | Provider id | Prerequisite the install path checks |
|---|---|---|
| Windows | `hyperv-gen2` | A supported Windows edition with the `Microsoft-Hyper-V` feature enabled |
| Linux | `firecracker-kvm` | cgroup v2 and read/write access to `/dev/kvm` |
| macOS | `apple-virtualization` | A signed helper carrying the `com.apple.security.virtualization` entitlement |

The selector is fail-closed: a provider must return a live probe **and** a non-null, active, identity-matching
certification, or it is skipped, and zero survivors raise `IsolationUnavailableException` rather than degrading
(`SEC-INV-10`). The certification carries its own `CertifiedAt` and `ExpiresAt`, sourced from the signed payload
manifest (`certificationExpiresAt`), so a payload stops being placeable when its certification expires even if
every binary is intact. See
[20-security/08-provider-certification.md](../20-security/08-provider-certification.md).

## Rules that apply to every page here

- **Drain before upgrade or removal.** The installer, the recovery probe, and all three uninstallers refuse to
  proceed unless `maintenance/drain-state` contains `draining` and there are zero
  `maintenance/active-assignments/*.active` markers. See [04-upgrade-and-drain.md](04-upgrade-and-drain.md).
- **Identity is never migrated.** A first install deletes legacy services and enrolls fresh; `reconnect` wipes
  the mutable trust. `SEC-INV-18`.
- **Office never self-updates.** An upgrade is an administrator action with a signed package.
- **The host administrator and the host operating system are trusted.** Office does not defend against them.
  A dedicated, patched host is the recommended posture for higher assurance.

> **Out of repo:** the C-Sweet application is what displays the enrollment code, decides drain, dispatches the
> guided setup and recovery flows, and keeps the fleet record. Office performs only the local work described on
> these pages.

## Sources

`README.md`, `AGENTS.md`, `releases/0.4.0.md`, `releases/0.5.0.md`, `releases/0.5.1.md`, `releases/0.5.3.md`,
`docs/20-security/08-provider-certification.md`, `docs/30-workloads/01-assignment-and-lease-semantics.md`,
`scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`, `scripts/linux/install-office.sh`,
`scripts/macos/install-office.sh`, `src/CSweet.Office.Runtime.Core/FailClosedIsolationProviderSelector.cs`.

Verified: 2026-09-15.
