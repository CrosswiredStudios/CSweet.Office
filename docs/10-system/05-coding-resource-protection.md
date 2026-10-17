# Coding resource protection

Verified: October 16, 2026. Source: `GuestWorkloadSupervisor.StartAsync`,
`PrepareWorkloadCgroup`, `WorkloadMemoryBudget`, `GuestBrokerSession.ObserveExitAsync`,
`ProviderCertificationEvidence.RequiredInternetChecks` and
`WindowsSmokeTest.Program.RunInternetSmokeAsync`.

Office 0.10.0 accepts the authenticated coding workload memory/process envelope in the
host's start command. Its fixed launch trampoline moves the artifact into a private
memory/pids cgroup before dropping privileges. Broker supervision stays in another child.
The workload receives at most 75% of authorized/physical memory, reserving at least
256 MiB for guest supervision. It cannot modify cgroup controllers. Whole-group OOM
termination retains a typed exit and kernel peak measurement through the live broker.
Coordination guests without the new start fields keep their previous launch behavior.

The image service delegates memory/pids controllers. These guest source changes require
an image rebuild. Certification suite v16 adds a separate memory-pressure VM, using a
bounded native allocation fixture. It succeeds only when the authenticated broker returns
`workload-memory-limit-exceeded` and a measured peak; an ordinary exit 137 is insufficient.
The new public/development certificate requires that check. Existing v15 evidence cannot
certify the resource-protection guarantee. Source tests do not replace real VM certification.

> **Out of repo:** Headquarters owns the 4 GiB default, 8/16 GiB growth policy, durable
> checkpoints, work identities, grant checks, aggregate placement and replacement sequence.
> Those are enforced in C-Sweet `WorkloadResourcePolicy`, `AgentWorkInbox.Resources` and
> `AgentRuntimeManager`; no Office unsigned authorization or resize bypass is introduced.

## Sources

`src/CSweet.Office.RuntimeGuest/GuestWorkloadSupervisor.cs`,
`src/CSweet.Office.RuntimeGuest/GuestBrokerSession.cs`,
`src/CSweet.Office.Runtime.Core/ExternalPlatformIsolationBackend.cs`,
`src/CSweet.Office.WindowsSmokeTest/{Program.cs,CertificationBrokerHost.cs}`,
`src/CSweet.Office.GuestProbe/Program.cs`,
`build/{windows-hyperv,linux-firecracker}/provision-guest.sh`.

Verified: 2026-10-16. Real v16 VM certification has not been run for this source change.
