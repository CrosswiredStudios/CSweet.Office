---
description: "Rules for isolation providers and platform helpers: Runtime.Core, the Hyper-V, Firecracker, and Apple Virtualization backends, and the helper executables. Covers the fail-closed selector, certification, the helper operation allow-list, and digest pinning."
applyTo: "src/CSweet.Office.Runtime.Core/**,src/CSweet.Office.Runtime.HyperV*/**,src/CSweet.Office.Runtime.Firecracker*/**,src/CSweet.Office.Runtime.AppleVirtualization*/**,src/CSweet.Office.Runtime.Abstractions/**"
---

# Isolation providers and platform helpers

Read [`docs/20-security/08-provider-certification.md`](../../docs/20-security/08-provider-certification.md),
[`docs/20-security/09-helper-protocol.md`](../../docs/20-security/09-helper-protocol.md), and
[`docs/30-workloads/07-provider-backends.md`](../../docs/30-workloads/07-provider-backends.md) before editing.

## Never change these

| Rule | Invariant |
|---|---|
| `EnforcePlatformMinimum` raises the assurance floor; it never lowers it. No provider is returned without a live probe and an active, identity-matching certification. | `SEC-INV-10` |
| The helper surface is fixed: protocol `1.0`, eight typed operations over stdio, digest re-verified on **every** invocation. | `SEC-INV-20` |
| Helpers exit `0` on typed failures. A non-zero exit discards the typed error. | `SEC-INV-20` |
| No shell, no request-supplied path, device, mount option, or command string. | `SEC-INV-20` |
| Request data reaches PowerShell only through environment variables, never by interpolating into script text. | `SEC-INV-20` |
| Keep the numeric clamps. | `SEC-INV-11` |

## Helper edits

The eight operations are `probe`, `create`, `start`, `inspect`, `stop`, `destroy`, `reap`, and `logs`. If your
change needs a ninth, that is a protocol change: it touches the shared contracts, the certification evidence,
and every platform backend. Discuss it before writing code.

When you add an operation or change a message shape:

1. Update `PlatformHelperContracts.cs`.
2. Update the C# helper's `HelperArguments` allow-list and its `HelperProtocolException` codes.
3. Update the Swift helper if the contract is shared (`runtime-apple-virtualization-helper`).
4. Rebuild the helper and update `HelperExecutableDigest` in the payload manifest. The digest is computed, never
   trusted from the manifest.
5. Re-certify, because the helper digest is part of the certified configuration.

## Hand-rolled interop — be careful

Several places implement wire formats or address families by hand because the managed APIs do not support them.
They are correct today and easy to break:

| Location | What it does |
|---|---|
| `HyperVSocketTransport.cs` | 36-byte `SOCKADDR_HV` with GUIDs at byte offsets 4 and 20, address family 34, `ProtocolType.Raw`, non-blocking `Connect` plus `Socket.Poll` because `ConnectEx` returns `WSAEINVAL`. |
| `LinuxHyperVSocketGuestTransport` (in `RuntimeGuest`) | `socket`/`bind`/`listen`/`accept4` for `AF_VSOCK` 40 with a hand-built `sockaddr_vm`. |
| `FirecrackerApiClient.cs` | Raw `AF_UNIX` socket through `SocketsHttpHandler.ConnectCallback`. |
| `GuestArtifactMaterializer.cs` | `libc` `mount`/`umount2` P/Invoke. |
| `SingleFileIso9660.cs` | A minimal ISO9660 writer and reader. |

If you touch one of these, state in the pull request exactly which bytes or flags changed and why.

## Provider capability descriptors

`IsolationProviderCatalog` uses named arguments for Hyper-V and **positional** arguments for Firecracker and
Apple. Editing the positional entries means counting twelve booleans. Convert them to named arguments if you
touch them.

Adding a new provider is a multi-repository change. Follow
[`docs/50-development/08-adding-an-isolation-provider.md`](../../docs/50-development/08-adding-an-isolation-provider.md).

## Tests that will catch you

`IsolationProviderSelectorTests.cs`, `PlatformIsolationBackendTests.cs`, `PlatformRuntimePayloadManifestTests.cs`,
`CertifiedGuestImageRegistryTests.cs`, `FirecrackerHelperSecurityTests.cs`, `HyperVInstanceReapingTests.cs`,
`WindowsHyperVOnboardingTests.cs`.
