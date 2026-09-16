---
description: "Add a new isolation provider to C-Sweet Office: catalog entry, capabilities, backend, helper, guest-channel connector, RuntimeHost registration, configuration, build scripts, and certification."
mode: agent
---

# Add an isolation provider

This is a large, cross-cutting change. Read
[`docs/50-development/08-adding-an-isolation-provider.md`](../../docs/50-development/08-adding-an-isolation-provider.md)
first, then work through the checklist below in order.

## Before writing code — decide these

1. **Provider id.** Stable, lowercase, ordinal-compared everywhere. Existing ids are `hyperv-gen2`,
   `firecracker-kvm`, `apple-virtualization`.
2. **Host OS and architecture.** Selection filters on `HostOperatingSystem` matching the current platform.
3. **Priority.** Higher wins among equal assurance. Existing values are 300, 200, 100.
4. **Capabilities.** Every flag you claim must be true of the implementation. Claiming a capability you do not
   enforce is a security defect.
5. **Guest-channel mechanism.** The transport string must match what `ProbeAsync` returns and what
   `RequiredGuestChannelTransport` expects.

## Checklist

| # | Change | File or project |
|---|---|---|
| 1 | Add the catalog entry with **named** capability arguments | `src/CSweet.Office.Runtime.Abstractions/IsolationProviderCatalog.cs` |
| 2 | Add the backend, deriving from `ExternalPlatformIsolationBackend` | `src/CSweet.Office.Runtime.<Platform>/` |
| 3 | Add the options subclass | alongside the backend |
| 4 | Add the helper project with the eight-operation allow-list | `src/CSweet.Office.Runtime.<Platform>.Helper/` |
| 5 | Add the guest-channel connector | provider project or the helper, depending on the platform |
| 6 | Register the backend in the OS branch of `Program.cs` | `src/CSweet.Office.RuntimeHost/Program.cs` |
| 7 | Add the configuration section | `CSweet:Office:Providers:<Name>` |
| 8 | Extend the payload generator and manifest schema | `scripts/<platform>/new-runtime-payload.sh` or the PowerShell equivalent |
| 9 | Extend the certification harness | `src/CSweet.Office.WindowsSmokeTest/` |
| 10 | Add tests | `tests/CSweet.Office.Tests/` |
| 11 | Rebuild the guest image if the platform needs one | see the guest image page |
| 12 | Re-certify | evidence must cover the new provider id, version, OS, arch, image digest, and broker protocol |

## Constraints you must not break

- The assurance floor is `CertifiedHardwareVirtualMachine` and `EnforcePlatformMinimum` may only raise it
  (`SEC-INV-10`).
- The helper surface is eight typed operations, protocol `1.0`, typed JSON on stdio, digest verified per
  invocation, no shell, no request-supplied paths (`SEC-INV-20`).
- Helpers exit `0` on typed failures.
- A new provider must not become a fallback for an unavailable one. Selection returns one provider or throws.

> **Out of repo:** a new provider id must also be understood by Headquarters placement, and adding a `Value`
> to a shared enum in `CSweet.Office.Contracts` is a cross-repository release. Follow
> [`docs/50-development/03-contracts-dependency.md`](../../docs/50-development/03-contracts-dependency.md).

## Report back

State which capability flags you set and the evidence for each, which invariants the change touches, and which
steps remain before the provider can be certified.
