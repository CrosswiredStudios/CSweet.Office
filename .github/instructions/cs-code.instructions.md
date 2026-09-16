---
description: "C# conventions for all C-Sweet Office source projects: target framework, nullable, central package management, TimeProvider, sealed records, internals visibility, and the rule that privileged boundaries are never widened."
applyTo: "src/**/*.cs"
---

# C# conventions in this repository

## Non-negotiables before you change code

- The security invariants are normative. Read
  [`docs/20-security/11-security-invariants.md`](../../docs/20-security/11-security-invariants.md) before
  touching anything privileged, then cite identifiers from it rather than restating rules in comments.
- The deliberate oddities are deliberate. Read
  [`docs/20-security/12-known-limits-and-tradeoffs.md`](../../docs/20-security/12-known-limits-and-tradeoffs.md)
  before "cleaning up" something that looks wrong.

## Language and project settings

- `TargetFramework` is `net10.0`, `ImplicitUsings` and `Nullable` are `enable`, `LangVersion` is `latest`,
  `EnforceCodeStyleInBuild` is `true`. All of it comes from `Directory.Build.props` — do not override it
  per project without a reason you can state in the pull request.
- There is **no `.editorconfig`**. Do not assume a house style is machine-enforced; match the surrounding file.
- Central package management is on. Add a `PackageReference` **without** a version and add the
  `PackageVersion` to `Directory.Packages.props`. Never pin a version inline.
- `Office.GlobalUsings.cs` is compiled into every project and supplies `CSweet.Office.Contracts.{ControlPlane,
  Guest, Security, Workloads}`. Do not add a `using` for those namespaces.
- File-scoped namespaces. `sealed` records for data, primary constructors for services.

## Patterns to follow

| Need | Do this |
|---|---|
| Current time | Inject `TimeProvider` and call `GetUtcNow()`. Never `DateTimeOffset.UtcNow` inside logic that is tested. |
| Cancellation | Accept `CancellationToken`, pass it down, and respect it in loops. |
| Atomic file writes | Temp file with `FileMode.CreateNew` + `FileOptions.WriteThrough`, then `File.Move(source, destination, overwrite: true)`. |
| Digest comparison | `CryptographicOperations.FixedTimeEquals`. Never `SequenceEqual`, never `==` on hex strings. |
| Digest formatting | Canonical lowercase `sha256:<64 hex>`, validated by the helpers in the relevant options type. |
| Test visibility | `internal` types plus `InternalsVisibleTo` for `CSweet.Office.Tests` (declared in the csproj, not assembly attributes). |
| Test doubles | Hand-written fakes. The test project deliberately has no mocking library. |
| Test names | `Thing_Behaviour`, for example `Authenticator_LoadsKeyCreatedAfterControlPlaneStartup`. |

## Things that will get a change rejected

- Adding a code path that creates work without a signed Headquarters authorization.
- Widening what a helper accepts, or making it exit non-zero on a typed failure.
- Accepting a request-supplied path, device, mount option, or command string.
- Catching an exception and continuing when the failure was a security decision.
- Adding an XML `///` comment only on the happy path of a security-critical type — the existing comments are
  concentrated exactly where the reasoning is non-obvious, and that is the convention.
- Adding a file under `src/CSweet.Office.RuntimeGuest`, `src/CSweet.Office.BuilderGuest`,
  `src/CSweet.Office.ToolchainGuest`, or `src/CSweet.Office.Runtime.Protocol`. Those directories are part of
  the guest-image fingerprint; any new file forces a full guest rebuild. See
  [`docs/30-workloads/06-guest-images.md`](../../docs/30-workloads/06-guest-images.md).

## Where behaviour is documented

| Touching | Read first |
|---|---|
| Node control loop | [`docs/30-workloads/02-runtime-workload-lifecycle.md`](../../docs/30-workloads/02-runtime-workload-lifecycle.md) |
| Authorization, handles, ledgers | [`docs/20-security/04-workload-authorization.md`](../../docs/20-security/04-workload-authorization.md) |
| Local RPC, authentication | [`docs/20-security/05-local-rpc-boundary.md`](../../docs/20-security/05-local-rpc-boundary.md) |
| Providers, helpers | [`docs/30-workloads/07-provider-backends.md`](../../docs/30-workloads/07-provider-backends.md) |
| Guests, environment, mounts | [`docs/20-security/07-guest-isolation.md`](../../docs/20-security/07-guest-isolation.md) |
| Any project's key types | [`docs/80-reference/projects/`](../../docs/80-reference/projects/README.md) |
