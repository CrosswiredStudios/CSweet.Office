# Code conventions

**Audience:** anyone writing C# in this repository. Everything here is observed in the current code; where
the repository is inconsistent, the inconsistency is named rather than smoothed over.

## Build-level conventions

`Directory.Build.props` applies to every project:

| Setting | Value |
|---|---|
| `TargetFramework` | `net10.0` |
| `ImplicitUsings` | `enable` |
| `Nullable` | `enable` |
| `LangVersion` | `latest` |
| `EnforceCodeStyleInBuild` | `true` |
| `VersionPrefix` | `0.6.0` |

Package versions live only in `Directory.Packages.props` (central package management with transitive
pinning); see [02-build-and-test.md](02-build-and-test.md) for the two-edit workflow.

> **Gap:** `EnforceCodeStyleInBuild` is `true` but there is no `.editorconfig` anywhere in the repository
> and no analyzer package is referenced. Style rules therefore run against compiler defaults, not a
> repository policy, and style drift is not caught by CI. Adding an `.editorconfig` is an open improvement,
> not a violation of anything written down.

## Language and type conventions

| Convention | Example |
|---|---|
| File-scoped namespaces, one per file | `namespace CSweet.Office.Runtime.Abstractions;` in `IsolationPorts.cs`. |
| `sealed record` for every model and value object | `IsolationWorkloadHandle`, `SignedWorkloadAuthorization`, `IsolationProviderCertification`, `ArtifactImportDescriptor`. |
| `record` `with` expressions instead of mutating copies | `request.Requirements with { MinimumAssurance = minimum }`. |
| Enums with explicit numeric values for wire-visible concepts | `IsolationAssurance`, `IsolationWorkloadState`, `IsolationTerminationReason`. |
| Exceptions as plain `sealed class`, not records | `IsolationUnavailableException`, `HelperProtocolException`, `HyperVCommandException`. |
| Primary constructors on services | `FailClosedIsolationProviderSelector(IEnumerable<IAgentIsolationProvider> providers, TimeProvider timeProvider)`, `CertifiedGuestImageRegistry(IAgentIsolationProviderSelector selector)`, `RuntimeHostWorkloadReaper(..., ILogger<RuntimeHostWorkloadReaper> logger)`, `RuntimeHostWorker(RuntimeHostRpcServer server, ILogger<RuntimeHostWorker> logger)`. |
| Collection expressions and target-typed `new` | `[new BackendAdapter(backend)]`, `_ = new MemoryStream()`. |
| Pattern-matching validation | `if (workload is not BuilderWorkloadSpecification and not RuntimeWorkloadSpecification and not ToolchainBuildWorkloadSpecification)`. |
| Guard clauses | `ArgumentNullException.ThrowIfNull(workload); workload.ResourceLimits.Validate();`. |
| Private static validation helpers per class | `ValidateWorkload`, `ValidateHandle`, `IsSha256`, `NormalizeDigest`. |
| `InvalidDataException` for malformed external input | Helper responses, manifests, payload files. |

## Time

Time is a dependency, not an ambient call. `Program.cs` in both services registers
`TimeProvider.System` as a singleton, the provider backends take a `TimeProvider` in the constructor and
call `GetUtcNow()` for lease and certification decisions, and the selector takes one for
`IsActiveAt` checks. `ControlPlaneServerCertificateValidator(OfficeOptions options, TimeProvider timeProvider)`
is the client-side example. Tests substitute a fixed clock (`FixedTimeProvider` in
`IsolationProviderSelectorTests`), which is why certification-window behavior is testable without waiting.

## Visibility and test seams

- Types that exist to wire one executable are `internal` — `HostPlatformProvider` in both `Node` and
  `RuntimeHost`.
- Members that exist for tests or for a sibling type in the same assembly are `internal` —
  `ExternalPlatformStdioGuestChannelConnector.ReadHandshakeAsync`, `ValidateHandle`, `ValidateHandshake`.
- `InternalsVisibleTo("CSweet.Office.Tests")` is declared in the `.csproj` of the projects the suite
  asserts on: `Runtime.Core`, `Runtime.HyperV`, both C# helpers, all guest executables, `Node`, and
  `Configurator`. The suite therefore never forces a type or member to become public for testing.

## Tests

- Naming is `Thing_Behaviour` (`SelectAsync_RejectsAvailableProviderWithoutCertification`) or
  `Thing_Condition` for theories.
- Fakes are small hand-rolled nested classes: `FakeProvider`, `FixedTimeProvider`, `TestGuestConnector`,
  `FailingBackend`, `BackendAdapter`. No mocking library is referenced anywhere in
  `Directory.Packages.props`.
- What each class pins, and which assertions are only script text, is in
  [07-testing-guide.md](07-testing-guide.md).

## Documentation of security-critical code

XML documentation is rare and deliberate. A `/// <summary>` appears on the types where a *why* is load
bearing, and nowhere else. Measured: 14 `/// <summary>` blocks in 11 source files, concentrated in
`Runtime.Abstractions` (`IsolationPorts.cs`), `Runtime.Core` (`SingleFileIso9660.cs`,
`ExternalPlatformIsolationBackend.cs`, `ExternalPlatformStdioGuestChannelConnector.cs`,
`PlatformRuntimePayloadManifest.cs`, `InMemoryAgentIsolationProvider.cs`), and one each in
`RuntimeHostAuthorizationGate.cs`, `GuestArtifactMaterializer.cs`, and `OfficeCertificateLease.cs`.

The register is the reason for the rule, not a description of the member:

```csharp
/// <summary>Provider-owned fail-safe cleanup. This deliberately does not depend on the
/// control-plane database, which may be unavailable or have been recreated.</summary>
public interface IPlatformWorkloadReaper
```

```csharp
/// <summary>Keeps retired credentials alive past the handler's bounded handshake timeout.</summary>
```

If you change behavior that one of those comments explains, update the comment in the same commit; the
comment is often the only in-code record of the invariant (`SEC-INV-05` is that second example).

## Documentation is part of the change

- Every behavioral claim in `docs/` cites a source file, and every page carries `## Sources` and
  `Verified:`. Fix the page and the date in the same commit that changes the behavior.
- Style rules for the docs themselves are normative in
  [70-contributing/02-documentation-style.md](../70-contributing/02-documentation-style.md): declarative
  voice, tables for parameters and comparisons, `> **Out of repo:**` callouts for behavior owned by
  C-Sweet or the Contracts package, and security invariants cited by identifier (`SEC-INV-nn`) rather
  than restated.
- When you change a script, a provider, or the guest image, the change-impact matrix in
  [70-contributing/03-change-impact-matrix.md](../70-contributing/03-change-impact-matrix.md) lists the
  pages that must move with it.

## Sources

`Directory.Build.props`, `Directory.Packages.props`, `src/**/*.csproj`,
`src/CSweet.Office.Runtime.Abstractions/{IsolationPorts.cs,IsolationModels.cs,IsolationProviderCatalog.cs}`,
`src/CSweet.Office.Runtime.Core/{FailClosedIsolationProviderSelector.cs,ExternalPlatformIsolationBackend.cs,ExternalPlatformStdioGuestChannelConnector.cs,CertifiedGuestImageRegistry.cs,PlatformRuntimePayloadManifest.cs}`,
`src/CSweet.Office.RuntimeHost/{Program.cs,HostPlatformProvider.cs,RuntimeHostWorkloadReaper.cs,RuntimeHostWorker.cs}`,
`src/CSweet.Office.Node/{Program.cs,HostPlatformProvider.cs}`, `src/CSweet.Office.Node/ControlPlaneServerCertificateValidator.cs`,
`src/CSweet.Office.Node/OfficeCertificateLease.cs`, `src/CSweet.Office.RuntimeGuest/GuestArtifactMaterializer.cs`,
`tests/CSweet.Office.Tests/IsolationProviderSelectorTests.cs`, `docs/70-contributing/02-documentation-style.md`.

Verified: 2026-09-16.
