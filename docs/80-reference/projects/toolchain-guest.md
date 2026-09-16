# CSweet.Office.ToolchainGuest

The in-guest toolchain adapter runner. It prepares a source tree (fetched through the broker or copied from a
certified fixture inside the adapter package), launches the certified adapter executable from the materialized
artifact, and — when the adapter signals completion by dropping a marker file — packages the output
deterministically into a `.csab` bundle and streams it back through the broker. Determinism is the point: the
bundle has normalized timestamps, ownership, ordering, and modes so the same inputs produce the same digest.
It has no project references.

## Project facts

| Fact | Value |
|---|---|
| Output kind | console exe (`Microsoft.NET.Sdk`, `<OutputType>Exe</OutputType>`) |
| Target framework | `net10.0` (explicit in the csproj, same value as `Directory.Build.props`) |
| Project references | none |
| Key package references | none of its own |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.ToolchainGuest`) |
| `InternalsVisibleTo` | `CSweet.Office.Tests` |
| csproj `<Description>` | absent |

This project is listed in `CSweet.Office.slnx` but **not** in `CSweet.Office.Independent.slnx`; it is still
built in CI through the test project's unconditional reference. See
[10-system/04-solution-map.md](../../10-system/04-solution-map.md).

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `ToolchainGuestProgram` | static class (internal) | Argument and environment validation, source preparation, adapter launch, ordered upload, and the deterministic bundler. |
| `ToolchainBrokerClient` | class (internal) | The broker HTTP client over the guest-local socket: chunked `DownloadAsync` and sequenced `UploadArtifactAsync` with a 768 KiB chunk size. |

## Entry points / composition

The executable takes exactly one argument, `--adapter-entrypoint <absolute path to the certified adapter>`,
which must already exist in the materialized artifact. Configuration is entirely environmental:

| Variable | Use |
|---|---|
| `CSWEET_BUILD_INPUT_ROOT` | Absolute disposable source root; reset before use. |
| `CSWEET_BUILD_OUTPUT_ROOT` | Absolute disposable output root; reset before use. |
| `CSWEET_BUILD_MAXIMUM_SOURCE_BYTES` | 1 byte … 2 GiB. |
| `CSWEET_BUILD_MAXIMUM_OUTPUT_BYTES` | 1 byte … 10 GiB. |
| `CSWEET_BUILD_SOURCE_URL` | Absolute source repository URI. |
| `CSWEET_BUILD_SOURCE_COMMIT` | Exactly 40 lowercase hexadecimal characters. |
| `CSWEET_BUILD_SOURCE_ARCHIVE_URL` (optional) | Must equal `csweet-source://build/{buildId:N}/{commit}` for the `CSWEET_BUILD_ID` value, otherwise the download is refused. |
| `CSWEET_BUILD_ID` (optional) | The build identity the trusted archive reference must name. |
| `CSWEET_CERTIFICATION_FIXTURE_RESOURCE` (optional) | A relative path inside the adapter package to copy as the source instead of downloading. |
| `CSweet__Agent__McpUnixSocketPath` | The broker socket; defaults to `/run/csweet/broker.sock`. |

Order of operations:

1. Reset both roots, then either copy the certified fixture or download and extract the source archive
   (deleting the archive afterwards).
2. Start the adapter with its own directory as the working directory, copy its stdout and stderr to the
   console, and simultaneously start the upload watcher.
3. Wait for the adapter to exit. A non-zero exit code is returned unchanged and nothing is uploaded.
4. `UploadWhenReadyAsync` polls for `.csweet-adapter-complete` in the output root, refuses to continue if the
   adapter exits first, builds `toolchain-output.csab`, uploads it, writes `.csweet-output-uploaded`, and
   deletes the local bundle.

## Behaviour worth knowing

- **The trusted source archive is bound to the build.** `SourceArchiveUri` only accepts the
  `csweet-source://build/{buildGuid:N}/{commit}` form when it matches the current `CSWEET_BUILD_ID` and commit;
  otherwise it falls back to the exact GitHub archive URL for the pinned commit, or throws if the repository
  is not an HTTPS GitHub URL with two path segments.
- **Extraction supports two layouts.** A provider-rooted archive must contain exactly one root directory; a
  flat layout is accepted when the broker reports `X-CSweet-Archive-Layout: flat`, and repository-root files
  are then preserved. Entry counts are capped at 100 000, expanded bytes at the source limit, and every path
  is normalized and checked for traversal, duplicates, and control characters.
- **The output bundle is deterministic by construction.** Files are filtered (`.csweet-*` marker files are
  excluded), sorted ordinally, capped at 100 000 files and the output byte limit, and written as PAX tar
  entries with `Uid = 0`, `Gid = 0`, empty user and group names, and a fixed modification time of
  `2000-01-01T00:00:00Z`. The bundle always contains a manifest with format version `1.0`, OS `linux`, the
  process architecture, and entrypoint `run-output`, plus a generated `payload/run-output` shell script that
  lists the ingested files, and every original file under `payload/output/`.
- **Symlinks are refused** in both the fixture copy and the output walk, and every copy target is resolved
  beneath its disposable root.
- **The adapter decides when the output is complete.** The `.csweet-adapter-complete` marker is the contract;
  the runner does not guess by watching the filesystem, and it fails closed if the adapter exits before the
  marker appears.
- **Uploads are sequenced and digest-terminated**, exactly like the builder's, so the host can reassemble and
  verify without trusting the guest's arithmetic.
- **The adapter is launched directly, not through a shell.** Arguments come only from the fixed command line;
  the working directory is the adapter's own directory inside the materialized artifact.

> **Out of repo:** the adapter itself, the recipe and target keys, the configuration document, and the
> ingestion and validation of the uploaded output bundle belong to Headquarters and the certified provider;
> this runner only prepares inputs and normalizes outputs.

## Related tests

| Test class | What it pins |
|---|---|
| `ToolchainGuestSecurityTests` | That the offline source path uses only the exact broker build reference, that an ordinary GitHub source stays an exact archive URL, that source extraction rejects traversal, that expansion rejects a byte overflow, and that a trusted flat archive preserves repository-root files. |

The test project references this project directly, so these tests link against `ToolchainGuestProgram` through
`InternalsVisibleTo`.

## Related documentation

- [30-workloads/03-builder-and-toolchain-lifecycles.md](../../30-workloads/03-builder-and-toolchain-lifecycles.md)
  — the toolchain lifecycle this executable implements.
- [30-workloads/06-guest-images.md](../../30-workloads/06-guest-images.md) — the toolchain image and its
  runner.
- [10-system/04-solution-map.md](../../10-system/04-solution-map.md) — the solution asymmetry.
- [runtime-guest.md](runtime-guest.md) — the supervisor that launches this executable and injects the
  `CSWEET_BUILD_*` environments from the signed specification.

## Sources

`src/CSweet.Office.ToolchainGuest/CSweet.Office.ToolchainGuest.csproj`,
`src/CSweet.Office.ToolchainGuest/Program.cs`, `CSweet.Office.slnx`, `CSweet.Office.Independent.slnx`,
`tests/CSweet.Office.Tests/ToolchainGuestSecurityTests.cs`.

Verified: 2026-09-15.
