# 50 · Development

Working on Office itself: getting a checkout building, running the suite, and iterating on one platform
at a time. These pages assume [10-system/04-solution-map.md](../10-system/04-solution-map.md), which
defines the two solutions and the contracts switch this section relies on.

| Page | Answers |
|---|---|
| [01-getting-started.md](01-getting-started.md) | Prerequisites, clone layout, the two build commands, the first test run, and an ordered first hour. |
| [02-build-and-test.md](02-build-and-test.md) | Both solutions, the exact `UseLocalOfficeContracts` semantics, central package management, and how to run the suite. |
| [03-contracts-dependency.md](03-contracts-dependency.md) | Why `CSweet.Office.Contracts` exists as a package, where it is pinned, and the cross-repository change workflow. |
| [04-windows-dev-loop.md](04-windows-dev-loop.md) | The one-command Hyper-V loop: the guest image cache, the certification smoke run, payload assembly, install. |
| [05-linux-dev-loop.md](05-linux-dev-loop.md) | The Firecracker loop: root prerequisites, Firecracker acquisition, the development signer, payload creation. |
| [06-macos-dev-loop.md](06-macos-dev-loop.md) | Building and signing the Swift helper and assembling a macOS payload. |
| [07-testing-guide.md](07-testing-guide.md) | What the suite does and does not pin, the script-text assertions, the upgrade-probe harness, and the manual-only scenarios. |
| [08-adding-an-isolation-provider.md](08-adding-an-isolation-provider.md) | The ordered checklist for a new provider, from the catalog entry to certification. |
| [09-guest-image-changes.md](09-guest-image-changes.md) | What invalidates the cached guest image, what a rebuild costs, and what must be re-certified. |
| [10-code-conventions.md](10-code-conventions.md) | The conventions the code actually follows, each with the file that proves it. |

## Prerequisites

| Requirement | Needed for | Detail |
|---|---|---|
| .NET SDK 10 on `PATH` | every build | `Directory.Build.props` sets `TargetFramework` to `net10.0`. There is no `global.json`, so the SDK is whatever `PATH` resolves. CI pins `10.0.x`. |
| Sibling `CSweet.Office.Contracts` checkout | `CSweet.Office.slnx` | Default root `..\CSweet.Office.Contracts` (overridable with `-p:CSweetOfficeContractsRepositoryRoot=<path>`). See [03-contracts-dependency.md](03-contracts-dependency.md). |
| Sibling `CSweet.Isolation` checkout | Windows guest-image builds | `New-CSweetHyperVTestGuest.ps1` imports `tools\LinuxImage\CSweet.LinuxImage.psd1` from it, and its directory is part of the guest build fingerprint. See [09-guest-image-changes.md](09-guest-image-changes.md). |
| Administrator rights, Hyper-V enabled, a usable Hyper-V switch | [04-windows-dev-loop.md](04-windows-dev-loop.md) | `Initialize-CSweetWindowsIsolationTest.ps1` self-elevates and enables the `Microsoft-Hyper-V` feature when it is missing. |
| Root, cgroup v2, read/write `/dev/kvm` | [05-linux-dev-loop.md](05-linux-dev-loop.md) | Checked by `scripts/linux/initialize-firecracker-test.sh` before anything is downloaded. |
| macOS 14 or later, Swift toolchain, `codesign`, `plutil`, `python3` | [06-macos-dev-loop.md](06-macos-dev-loop.md) | `Package.swift` requires `swift-tools-version: 5.9` and `platforms: [.macOS(.v14)]`. |
| WiX Toolset v4 (`wix`) and Windows SDK `signtool` | MSI packaging only | Required by `scripts/windows/New-CSweetOfficeMsi.ps1`. |

Verification rules for this section are the same as everywhere in `docs/`: every claim cites a source
file, and each page ends with the date it was verified.

## Sources

`Directory.Build.props`, `Directory.Packages.props`, `README.md`, `AGENTS.md`, `scripts/windows/New-CSweetHyperVTestGuest.ps1`,
`scripts/windows/New-CSweetOfficeMsi.ps1`, `scripts/linux/initialize-firecracker-test.sh`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Package.swift`.

Verified: 2026-09-15.
