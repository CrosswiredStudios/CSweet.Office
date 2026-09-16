# CSweet.Office.BuilderGuest

The in-guest plugin build runner. It runs inside the disposable builder VM, fetches the plugin source through
the authenticated broker, restores NuGet packages through a loopback TLS proxy that it hands the `dotnet` CLI,
publishes the project, packages the publish output as an immutable `.csab` bundle, and streams that bundle
back through the broker for host-side validation. It has no project references, so the guest image never
receives runtime or provider libraries.

## Project facts

| Fact | Value |
|---|---|
| Output kind | console exe (`Microsoft.NET.Sdk`, `<OutputType>Exe</OutputType>`) |
| Target framework | `net10.0` (explicit in the csproj, same value as `Directory.Build.props`) |
| Project references | none |
| Key package references | none of its own |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.BuilderGuest`) |
| `InternalsVisibleTo` | `CSweet.Office.Tests` — declared, but the test project does not reference this project |
| csproj `<Description>` | absent |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `BuilderProgram` | static class (internal) | The whole pipeline: fetch, extract, resolve, restore, publish, bundle, upload, with `isolate`, `restore`, `publish`, and `package` progress reports. |
| `BuilderOptions` | record (internal) | `RepositoryUrl`, `CommitSha`, `ProjectPath`, `MaximumRepositoryBytes`, `MaximumArtifactBytes`, `BrokerSocketPath`, optional `TargetFramework`; `Parse` enforces the argument set and the allowed target frameworks (`net8.0`, `net9.0`, `net10.0`). |
| `BuilderBrokerClient` | class (internal) | HTTP over the guest-local broker socket: `DownloadAsync` (chunked `/build/fetch` with `X-CSweet-Complete` and `X-CSweet-Content-Type`), `UploadArtifactAsync` (768 KiB chunks with `X-CSweet-Sequence`/`X-CSweet-Completed`/`X-CSweet-Digest`), `ReportAsync` (`/build/progress`). |
| `BuilderDownloadException` | exception (internal) | Carries the HTTP status so the NuGet proxy can mirror it to the CLI. |
| `NuGetTrustedRepository` | record (internal) | Downloads NuGet's repository-signatures document through the broker and extracts the SHA-256 fingerprints, requiring `allRepositorySigned: true`. |
| `NuGetLoopbackProxy` | class (internal) | A loopback HTTPS server with a self-signed certificate that serves `/v3/index.json` and rewrites every upstream URL into a local `/upstream/<base64>/…` form, caching each upstream artifact through the broker. |

## Entry points / composition

The executable requires six arguments — `--repository`, `--commit`, `--project`, `--maximum-repository-bytes`,
`--maximum-artifact-bytes`, `--broker-socket` — plus optional `--target-framework`. It then runs, in order:

1. Create the broker client and report `isolate started`.
2. Reset `/run/csweet/workload/build`, download the source archive there, extract it, resolve the approved
   `.csproj` inside it (which must be accompanied by `csweet-plugin.json`), and report `isolate succeeded`.
3. Load the trusted NuGet repository metadata through the broker, start the loopback proxy, and write
   `NuGet.Config` pointing at the proxy with `signatureValidationMode="require"` and the repository signature
   certificate fingerprints.
4. `dotnet restore <project> --nologo --disable-build-servers --configfile <NuGet.Config>` with a fixed
   environment (`DOTNET_CLI_HOME`, `NUGET_PACKAGES`, `SSL_CERT_FILE` pointing at the proxy certificate,
   `NUGET_CERT_REVOCATION_MODE=offline`, and build-server telemetry disabled); report `restore`.
5. `dotnet publish <project> --configuration Release --no-restore --nologo --disable-build-servers --output
   <output>` plus `--framework` when requested, then copy `csweet-plugin.json` beside the output; report
   `publish`.
6. `CreateBundleAsync` writes `agent.csab`, then `UploadArtifactAsync` streams it; report `package succeeded`.

Any failure prints one sanitized line to stderr and returns 1.

## Behaviour worth knowing

- **The source is fetched as an exact archive, never a git clone.** `SourceArchiveUri` accepts only HTTPS
  GitHub URLs with exactly two path segments and rewrites them to
  `https://codeload.github.com/{owner}/{repo}/zip/{commit}`. The commit must be 40 hex characters. The
  `dotnet-publish-v1` profile is named in the error message when the repository shape is wrong.
- **Extraction is a single-rooted, validated expand.** The archive must have one repository root and 1-20 000
  entries, names are normalized and checked for `..`, absolute paths, control characters, and duplicates, and
  the expanded total is bounded by `maximumRepositoryBytes`.
- **NuGet is forced through the broker.** The loopback proxy terminates TLS with a generated localhost
  certificate, serves the rewritten service index, rewrites every nested JSON URL into a tokenized local path,
  and fetches each upstream document through `/build/fetch`. The CLI is told to trust only that certificate
  and to require repository-signed packages; the proxy's cache key is the SHA-256 of the upstream URL.
- **Bundle layout is fixed and validated by the host.** `artifact.json` carries `formatVersion`, `linux`, the
  guest architecture, and the entrypoint (the project file name), and every published file is written under
  `payload/`, sorted, with UID and GID 0. Symlinks in the publish output are rejected, the file count is
  capped at 10 000, and the total size is capped by `maximumArtifactBytes`. Files ending in `.sh` and the
  entrypoint receive the execute bits.
- **The broker upload is sequenced and digest-ended.** Chunks carry a monotonically increasing sequence
  number; a final empty request carries the completed flag and the SHA-256 of the whole bundle, which the host
  re-computes.
- **The workload runs unprivileged and artifact-scoped.** It is started by `RuntimeGuest`'s supervisor through
  `setpriv` with the working directory fixed to the builder artifact root
  ([SEC-INV-21](../../20-security/11-security-invariants.md)); the builder never sees a host path.
- **Nothing about the source is supplied by the guest image.** The repository URL, commit, project path, and
  both byte limits arrive as arguments derived from the signed assignment.

> **Out of repo:** the `dotnet-publish-v1` build profile, the source manifest contract (`csweet-plugin.json`),
> and the host-side validation of the uploaded bundle are Headquarters behaviour. The only in-repo
> implementation of a host-side broker that serves these endpoints is
> `src/CSweet.Office.WindowsSmokeTest/CertificationBrokerHost.cs`, which is test-only.

## Related tests

**No test class covers this executable.** `tests/CSweet.Office.Tests` declares an `InternalsVisibleTo` for it
but does not reference the project, so `BuilderProgram` runs only under the certification smoke test, which
exercises a fixture repository end to end (`WindowsSmokeTest`'s builder phase asserts
`builder-source-brokered`, `builder-package-trust-metadata-brokered`, `builder-progress-reported`,
`builder-artifact-streamed`, `builder-artifact-digest-verified`, and `builder-artifact-entrypoint-executable`).
The sibling project `ToolchainGuest` does have a security test class, which is why the asymmetry is worth
noting.

## Related documentation

- [30-workloads/03-builder-and-toolchain-lifecycles.md](../../30-workloads/03-builder-and-toolchain-lifecycles.md)
  — the builder lifecycle this executable implements.
- [30-workloads/04-guest-broker-protocol.md](../../30-workloads/04-guest-broker-protocol.md) — the
  `build.fetch`, `build.artifact`, and `build.progress` purposes.
- [30-workloads/06-guest-images.md](../../30-workloads/06-guest-images.md) — why the builder image needs the
  .NET SDK.
- [30-workloads/07-provider-backends.md](../../30-workloads/07-provider-backends.md) — the create and reap
  asymmetry that skips builder instances.
- [runtime-guest.md](runtime-guest.md) — the supervisor that launches this executable.

## Sources

`src/CSweet.Office.BuilderGuest/CSweet.Office.BuilderGuest.csproj`,
`src/CSweet.Office.BuilderGuest/Program.cs`, `src/CSweet.Office.WindowsSmokeTest/Program.cs`,
`tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj`.

Verified: 2026-09-15.
