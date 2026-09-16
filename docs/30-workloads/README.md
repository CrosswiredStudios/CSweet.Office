# 30 · Workloads

How work arrives, runs, and finishes. These pages describe the mechanics; the security guarantees behind them
are in [20-security](../20-security/README.md).

| Page | Covers |
|---|---|
| [01-assignment-and-lease-semantics.md](01-assignment-and-lease-semantics.md) | The three clocks: the signed authorization window, the assignment lease renewal, and the broker lease. |
| [02-runtime-workload-lifecycle.md](02-runtime-workload-lifecycle.md) | Workload kind 1, end to end. |
| [03-builder-and-toolchain-lifecycles.md](03-builder-and-toolchain-lifecycles.md) | Workload kinds 0 and 2, including the brokered NuGet path. |
| [04-guest-broker-protocol.md](04-guest-broker-protocol.md) | The guest envelope, the command set, and the proxy purposes. |
| [05-artifacts-and-media.md](05-artifacts-and-media.md) | Download binding, caching, ISO construction, and media hand-off. |
| [06-guest-images.md](06-guest-images.md) | The three in-image runners, device layout, and the certification relationship. |
| [07-provider-backends.md](07-provider-backends.md) | What each backend actually does to create, start, log, and reap a workload. |

## The single most important thing on this page

> **Office is a relay.** It does not construct `GuestBootConfiguration` or `StartCommand`, does not answer the
> guest handshake, and never sets `result_artifact_*` on a status update. Headquarters does all of that over the
> relayed byte stream. The only in-repo implementation of the host side is the test-only certification harness
> `src/CSweet.Office.WindowsSmokeTest/CertificationBrokerHost.cs`, which is useful as documentation of the
> protocol and dangerous as a mental model of production behavior.

If a page here says "Headquarters does X", it means the behavior is real, observable from this repository's
code, and implemented in another repository.
