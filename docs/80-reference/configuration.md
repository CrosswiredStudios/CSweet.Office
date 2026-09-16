# Configuration reference

**Audience:** operators and contributors who need the exact key, default, and validation rule for a setting.

Every .NET service in this repository builds its host with `Host.CreateApplicationBuilder(args)`. Configuration
therefore comes from `appsettings.json` in the content root, environment variables, and command-line
arguments, in the standard provider order. The privileged service is started with an explicit
`--contentRoot` (see [file-layout.md](file-layout.md)), so its `appsettings.json` is always the
installer-written file in the versioned package directory.

Twelve options classes are bound from configuration or constructed directly. One further options record,
`BuilderOptions`, is parsed from guest command-line arguments and is not configuration at all; it is
documented at the end of this page for completeness.

A key with a `SectionName` constant is bound by name. A key without one is bound by a literal string in the
host's `Program.cs`; those are called out in each section below.

> **Out of repo:** this page records what the installers write, not what Headquarters expects. The control
> plane that serves enrollment and certificates lives in C-Sweet.

## Section index

| Section key | Options class | Source file | Bound in |
|---|---|---|---|
| `CSweet:Office:Node` | `OfficeOptions` | `src/CSweet.Office.Node/OfficeOptions.cs` | `src/CSweet.Office.Node/Program.cs` |
| `CSweet:Office:RuntimeHost` | `RuntimeHostEndpointOptions` | `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostEndpointOptions.cs` | Node and RuntimeHost `Program.cs` |
| `CSweet:Office:RuntimeHost:Authentication` | `RuntimeHostAuthenticationOptions` | `src/CSweet.Office.Runtime.Protocol/RuntimeHostAuthentication.cs` | Node and RuntimeHost `Program.cs` |
| `CSweet:Office:RuntimeHost:Authorization` | `RuntimeHostAuthorizationOptions` | `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostAuthorizationGate.cs` | `src/CSweet.Office.RuntimeHost/Program.cs` |
| `CSweet:Office:RuntimeHost:HyperVSocket` | `HyperVSocketTransportOptions` | `src/CSweet.Office.Runtime.HyperV/HyperVSocketTransport.cs` | `src/CSweet.Office.RuntimeHost/Program.cs` (literal key) |
| `CSweet:Office:Providers:HyperV` | `HyperVIsolationBackendOptions` | `src/CSweet.Office.Runtime.HyperV/HyperVIsolationBackend.cs` | `src/CSweet.Office.RuntimeHost/Program.cs` (literal key) |
| `CSweet:Office:Providers:Firecracker` | `FirecrackerIsolationBackendOptions` | `src/CSweet.Office.Runtime.Firecracker/FirecrackerIsolationBackend.cs` | `src/CSweet.Office.RuntimeHost/Program.cs` (literal key) |
| `CSweet:Office:Providers:AppleVirtualization` | `AppleVirtualizationIsolationBackendOptions` | `src/CSweet.Office.Runtime.AppleVirtualization/AppleVirtualizationIsolationBackend.cs` | `src/CSweet.Office.RuntimeHost/Program.cs` (literal key) |
| `CSweet:Office:Node:Artifacts` | `ArtifactStoreOptions` | `src/CSweet.Office.Runtime.Artifacts/ArtifactStoreOptions.cs` | not bound by any host in this repository |
| `CSweet:Office:Node:ArtifactMedia` | `ArtifactMediaOptions` | `src/CSweet.Office.Runtime.Artifacts/ArtifactMediaOptions.cs` | not bound by any host in this repository |
| n/a | `PlatformIsolationBackendOptions` | `src/CSweet.Office.Runtime.Core/ExternalPlatformIsolationBackend.cs` | base class of the three provider options |
| n/a | `GuestServiceOptions` | `src/CSweet.Office.RuntimeGuest/GuestServiceOptions.cs` | guest process only, from environment or boot configuration |

Top-level keys other than `CSweet` come from the installed `appsettings.json`: `Logging:LogLevel:Default`,
`Logging:LogLevel:Microsoft.Hosting.Lifetime`, and `Logging:EventLog:LogLevel:Default` (Windows only).

## `CSweet:Office:Node` — `OfficeOptions`

`public const string SectionName = "CSweet:Office:Node";`

| Property | Type | Default | Notes and validation |
|---|---|---|---|
| `ControlPlaneUrl` | `string` | `https://localhost:7443` | Node `Program.cs` throws `InvalidOperationException` unless it parses as an absolute URI with scheme `https`. |
| `ControlPlaneCertificateSha256` | `string` | `string.Empty` | Optional server pin, 64 hexadecimal characters. Used by `ControlPlaneServerCertificateValidator` and the rotating mTLS handler. |
| `ControlPlaneTrustFilePath` | `string` | `string.Empty` | Path to the trust file written by the installer (`{"schemaVersion":1,"certificateSha256":"…"}`). |
| `EnrollmentToken` | `string` | `string.Empty` | Consumed once; cleared in memory after a successful claim. |
| `EnrollmentTokenFilePath` | `string` | `string.Empty` | Read, then deleted only after the identity state is saved. |
| `AssistedSetupSessionId` | `Guid?` | `null` | Present only for assisted (C-Sweet-driven) enrollment. |
| `StateDirectory` | `string` | `string.Empty` → `%LOCALAPPDATA%\CSweet\Office` | `ResolveStateDirectory()` falls back to `AppContext.BaseDirectory` when the folder is unavailable. |
| `ArtifactCacheDirectory` | `string` | `string.Empty` → `<state>\artifact-cache` | |
| `ArtifactMediaDirectory` | `string` | `string.Empty` → `<state>\artifact-media` | The RuntimeHost holds this path read-only. |
| `OfficeName` | `string` | `Environment.MachineName` | Display name reported at enrollment. |
| `AllocatableCpuCount` | `int` | `Math.Max(1, Environment.ProcessorCount - 1)` | Advertised capacity. |
| `AllocatableMemoryMb` | `int` | `4096` | Advertised capacity. |
| `AllocatableDiskMb` | `int` | `32768` | Advertised capacity. |
| `MaximumConcurrentWorkloads` | `int` | `Math.Max(1, Environment.ProcessorCount / 2)` | Node-side slot semaphore size. |
| `SecurityProfile` | `string` | `baseline` | `baseline`, `hardened`, or `development`. |
| `MixedUseHost` | `bool` | `true` | Reported in the posture; informational. |
| `AllowDevelopmentAssignments` | `bool` | `false` | Required consent for the `development` profile. |
| `EnabledSecurityControls` | `string[]` | `[]` | Normalised before reporting: trimmed, lowercased, `[a-z0-9.-]`, ≤100 characters, distinct, ordinal sort, at most 64 entries. |
| `MissingSecurityControls` | `string[]` | `[]` | Same normalisation; a non-empty array blocks the `hardened` profile. |

Installer overrides (Windows): the installer writes `ControlPlaneUrl`, `ControlPlaneTrustFilePath`,
`StateDirectory`, `ArtifactCacheDirectory`, `ArtifactMediaDirectory`, `EnrollmentTokenFilePath`,
`AssistedSetupSessionId`, all four capacity values, `SecurityProfile`, `MixedUseHost`, and
`AllowDevelopmentAssignments` into `<version>\appsettings.json`; an existing `Node` section is carried over
verbatim on an identity-preserving install. Linux sets the same keys through `/etc/csweet/office.env`, macOS
through the Node plist.

## `CSweet:Office:RuntimeHost` — `RuntimeHostEndpointOptions`

`public const string SectionName = "CSweet:Office:RuntimeHost";`

| Property | Type | Default | Installer value | Validation (`Validate()`) |
|---|---|---|---|---|
| `NamedPipeName` | `string` | `csweet-office-runtime-v1` | `csweet-office-runtime-v1` | Non-empty, ≤100 characters, ASCII alphanumerics, `-`, `_` only. |
| `AllowedClientSid` | `string?` | `null` | interactive control-plane user SID | Used for the interactive client rule on the pipe DACL. |
| `AllowedClientSids` | `string[]` | `[]` | `[ControlPlaneUserSid, nodeServiceSid]` | Each entry gets exactly `ReadWrite \| Synchronize \| CreateNewInstance`. |
| `UnixSocketPath` | `string` | `/run/csweet/csweet-office-runtime-v1.sock` | `/run/csweet/csweet-office-runtime-v1.sock` | Absolute (or fully qualified on Windows), ≤200 characters, no NUL, no `.` or `..` segments. |
| `ConnectTimeoutSeconds` | `int` | `2` | `10` (appsettings.json and Windows installer) | 1–120. |
| `MaximumFrameBytes` | `int` | `1048576` | `1048576` | 4096–16777216 (16 MiB). |

`LoadSharedKeyFileIfNeeded` and `ResolveSharedKeyBase64` accept a fully qualified path; a ignored path leaves
the key unloaded, and the authenticator then throws `InvalidDataException` on first use.

## `CSweet:Office:RuntimeHost:Authentication` — `RuntimeHostAuthenticationOptions`

`public const string SectionName = "CSweet:Office:RuntimeHost:Authentication";`

| Property | Type | Default | Installer value | Notes |
|---|---|---|---|---|
| `KeyId` | `string` | `control-plane` | `office-node` | Must match verbatim on both sides; a mismatch rejects with `unknown-key`. |
| `SharedKeyBase64` | `string` | `string.Empty` | `string.Empty` | Base64 of at least 32 bytes; a shorter key throws `InvalidDataException` when the signature is computed. |
| `SharedKeyFilePath` | `string` | `string.Empty` | `<DataRoot>\runtime-host.key` (Windows), `/var/lib/csweet/office/runtime-host.key` (Linux), `/Library/Application Support/CSweet/Office/runtime-host.key` (macOS) | Fully qualified. The file is re-read on every signature, must be 40–4096 bytes, and must not be a reparse point. |
| `MaximumClockSkewSeconds` | `int` | `60` | not set | Clamped to 1–300 when validating a timestamp; the documented range is `SEC-INV-11`. |
| `ReplayRetentionSeconds` | `int` | `300` | not set | Clamped to 60–3600; nonces are also pruned on each validation. |

When `SharedKeyBase64` is empty, both hosts fall back to `<CommonApplicationData>\CSweet\Office\runtime-host.key`
through the `defaultPath` argument, which is why the Linux and macOS units only need
`SharedKeyFilePath` and Windows only needs the file that the installer already placed in the data root.

## `CSweet:Office:RuntimeHost:Authorization` — `RuntimeHostAuthorizationOptions`

`public const string SectionName = "CSweet:Office:RuntimeHost:Authorization";`

| Property | Type | Default | Installer value | Notes |
|---|---|---|---|---|
| `StateDirectory` | `string` | `string.Empty` → `<CommonApplicationData>\CSweet\Office\authorization` | `<DataRoot>\authorization`, `/var/lib/csweet/office/authorization`, `/Library/Application Support/CSweet/Office/authorization` | Holds `headquarters-trust.json`, `accepted-assignments.json`, and `authorized-workload-handles.json`. |
| `MaximumAuthorizationLifetimeSeconds` | `int` | `600` | `600` | The gate constructor throws `ArgumentOutOfRangeException` outside 30–3600. |
| `MaximumClockSkewSeconds` | `int` | `120` | `120` | Constructor throws outside 0–600; also the forward-skew allowance for `issuedAt`. |

See [20-security/04-workload-authorization.md](../20-security/04-workload-authorization.md) for the
validation order that these two numbers feed, and `SEC-INV-08`, `SEC-INV-09`.

## `CSweet:Office:RuntimeHost:HyperVSocket` — `HyperVSocketTransportOptions`

Bound by the literal key `CSweet:Office:RuntimeHost:HyperVSocket`. The class has no `SectionName` constant.

| Property | Type | Default | Validation |
|---|---|---|---|
| `LinuxVsockPort` | `int` | `2761` (`DefaultLinuxVsockPort`) | 1024–65535; `ServiceId` derives the broker GUID from the port. |
| `ConnectTimeoutSeconds` | `int` | `60` | 1–300, together with `RetryDelayMilliseconds`. |
| `RetryDelayMilliseconds` | `int` | `250` | 25–5000, together with `ConnectTimeoutSeconds`. |

## `CSweet:Office:Providers:*` — `PlatformIsolationBackendOptions` and subclasses

`PlatformIsolationBackendOptions` (base) has no `SectionName` constant. It is bound from the literal keys
`CSweet:Office:Providers:HyperV`, `CSweet:Office:Providers:Firecracker`, and
`CSweet:Office:Providers:AppleVirtualization`.

| Property | Type | Default | Notes |
|---|---|---|---|
| `PayloadManifestPath` | `string` | `string.Empty` | Firecracker and Apple only. When set, `PlatformRuntimePayloadManifest.ApplyIfConfigured` replaces every other path from the manifest; a relative path throws `InvalidDataException`. |
| `HelperExecutablePath` | `string` | `string.Empty` | Must resolve to a readable file for the probe to succeed. |
| `HelperExecutableDigest` | `string` | `string.Empty` | `sha256:` with 64 lowercase hex characters. |
| `GuestImagePath` | `string` | `string.Empty` | |
| `GuestImageDigest` | `string` | `string.Empty` | `sha256:` digest; must also match the digest the provider returns. |
| `GuestImageSignaturePath` | `string` | `string.Empty` | Detached signature over the image. |
| `GuestImageSigningCertificatePath` | `string` | `string.Empty` | Pinned signing certificate. |
| `GuestImageSigningCertificateThumbprint` | `string` | `string.Empty` | Compared against the certificate in the package. |
| `ArtifactImageRoot` | `string` | `string.Empty` | Read-only ISO media root shared with the Node. |
| `BrokerProtocolVersion` | `string` | `1.0` | Must equal the broker protocol version in the certification evidence. |
| `CertificationSuiteVersion` | `string` | `string.Empty` | Required for a non-null certification. |
| `CertificationEvidencePath` | `string` | `string.Empty` | |
| `CertificationEvidenceDigest` | `string` | `string.Empty` | `sha256:` digest of the evidence file. |
| `CertifiedAt` | `DateTimeOffset?` | `null` | Required; `null` fails the probe. |
| `CertificationExpiresAt` | `DateTimeOffset?` | `null` | Optional; when set, a passed date fails the probe. |
| `HelperTimeoutSeconds` | `int` | `120` | Clamped to 5–600 at each helper invocation. |
| `GuestChannelConnectTimeoutSeconds` | `int` | `30` | Clamped to 5–120 by the stdio connector. |
| `RequiredGuestChannelTransport` | `string` | `string.Empty` | A helper that does not advertise the required transport fails the probe. |

Subclass construction:

| Subclass | Constructor behaviour |
|---|---|
| `HyperVIsolationBackendOptions` | Declared as `public sealed class HyperVIsolationBackendOptions : PlatformIsolationBackendOptions;` — no overrides, so `RequiredGuestChannelTransport` stays empty and the check is skipped. |
| `FirecrackerIsolationBackendOptions` | Sets `RequiredGuestChannelTransport = ExternalPlatformStdioGuestChannelConnector.TransportName` (`stdio-duplex-v1`). |
| `AppleVirtualizationIsolationBackendOptions` | Same as Firecracker. |

Provider binding is platform-exclusive: `RuntimeHost/Program.cs` registers exactly one backend for the
current operating system and passes only Firecracker and Apple through `ApplyIfConfigured`. On Windows the
Hyper-V paths therefore come from the installer-written `appsettings.json`, not from a manifest.

## `CSweet:Office:Node:Artifacts` — `ArtifactStoreOptions`

`public const string SectionName = "CSweet:Office:Node:Artifacts";`

| Property | Type | Default | Validation (`ValidatedRootPath()` / `ValidatedProvider()`) |
|---|---|---|---|
| `RootPath` | `string` | `string.Empty` | Must be fully qualified, must not be a filesystem root. |
| `Provider` | `string` | `filesystem` | `filesystem` or `s3` after trimming and lowercasing; anything else throws. |
| `MaximumFileCount` | `int` | `10_000` | 1–1,000,000. |
| `MaximumPathLength` | `int` | `512` | 32–4096. |
| `MaximumUncompressedBytes` | `long` | `2L * 1024 * 1024 * 1024` (2 GiB) | 1 byte–100 GiB. |
| `MaximumManifestBytes` | `int` | `1024 * 1024` (1 MiB) | 128 bytes–16 MiB. |

**This section is declared but not bound by any host in this repository.** `FileSystemAgentArtifactStore`
takes an `ArtifactStoreOptions` instance directly; in the shipped Office the Node uses its own
`OfficeArtifactCache`, rooted at `CSweet:Office:Node:ArtifactCacheDirectory`.

## `CSweet:Office:Node:ArtifactMedia` — `ArtifactMediaOptions`

`public const string SectionName = "CSweet:Office:Node:ArtifactMedia";`

| Property | Type | Default | Validation |
|---|---|---|---|
| `RootPath` | `string` | `string.Empty` | Must be fully qualified and must not be a filesystem root. |

Like `ArtifactStoreOptions`, this section is **not bound** by any host. `OfficeArtifactCache` constructs
`FileSystemAgentArtifactMediaStore(new ArtifactMediaOptions { RootPath = options.ResolveArtifactMediaDirectory() }, …)`,
so the effective media root is `CSweet:Office:Node:ArtifactMediaDirectory`.

> **Cross-check:** `CSweet:Office:Node:ArtifactMediaDirectory` (Node) and
> `CSweet:Office:Providers:HyperV:ArtifactImageRoot` (RuntimeHost) must name the same directory. See the
> *"same directory, two settings"* callout in [file-layout.md](file-layout.md).

## Guest configuration — `GuestServiceOptions`

`GuestServiceOptions` is a `record` with positional defaults, not an `IConfiguration` section. It is built
either from environment variables (`FromEnvironment`, used with the `stdio` transport) or from the
Headquarters-authored boot configuration (`FromBootConfiguration`, used with the vSock transports).

| Constructor parameter | Type | Default | Source when the boot configuration is used |
|---|---|---|---|
| `WorkloadId` | `Guid` | — | `GuestBootConfiguration.workload_id` |
| `ChannelId` | `Guid` | — | `channel_id` |
| `ProtocolVersion` | `string` | — | `protocol_version` (must be `1.0`) |
| `GuestImageDigest` | `string` | — | `guest_image_digest` |
| `ArtifactDigest` | `string?` | — | `artifact_digest` (empty becomes `null`) |
| `BootToken` | `string` | — | `boot_token` |
| `LeaseExpiresAt` | `DateTimeOffset` | — | `lease_expires_at_unix_seconds` |
| `ArtifactRoot` | `string` | — | `artifact_root` |
| `WorkloadKind` | `int` | — | `workload_kind` (0, 1, 2) |
| `InstallationId` | `Guid?` | — | `installation_id` |
| `BusinessId` | `string?` | — | `business_id` |
| `TickId` | `Guid?` | — | `tick_id` |
| `LocalBrokerSocketPath` | `string` | `/run/csweet/broker.sock` | `local_broker_socket_path` |
| `WorkloadTokenPath` | `string` | `/run/csweet/workload-token` | `workload_token_path` |
| `MaximumFrameBytes` | `int` | `1_048_576` | `maximum_frame_bytes` |

Environment variables read by `FromEnvironment`:

| Variable | Required | Notes |
|---|---|---|
| `CSWEET_GUEST_WORKLOAD_ID` | yes | Missing values throw `Required guest setting <name> is missing.` |
| `CSWEET_GUEST_CHANNEL_ID` | yes | |
| `CSWEET_GUEST_PROTOCOL_VERSION` | yes | |
| `CSWEET_GUEST_IMAGE_DIGEST` | yes | |
| `CSWEET_GUEST_ARTIFACT_DIGEST` | no | `null` when absent. |
| `CSWEET_GUEST_BOOT_TOKEN` | yes | |
| `CSWEET_GUEST_LEASE_EXPIRES_AT` | yes | Unix seconds, invariant culture. |
| `CSWEET_GUEST_ARTIFACT_ROOT` | no | Default `/opt/csweet/artifact/payload`. |
| `CSWEET_GUEST_WORKLOAD_KIND` | yes | |
| `CSWEET_GUEST_INSTALLATION_ID` | no | Parsed when it is a GUID. |
| `CSWEET_GUEST_BUSINESS_ID` | no | |
| `CSWEET_GUEST_TICK_ID` | no | Parsed when it is a GUID. |
| `CSWEET_GUEST_LOCAL_BROKER_SOCKET` | no | Default `/run/csweet/broker.sock`. |
| `CSWEET_GUEST_WORKLOAD_TOKEN_PATH` | no | Default `/run/csweet/workload-token`. |
| `CSWEET_GUEST_BROKER_TRANSPORT` | no | `stdio` (default), `hyperv-vsock`, `firecracker-vsock`; anything else throws. |
| `CSWEET_GUEST_VSOCK_PORT` | no | Default `5000`; only read for `firecracker-vsock`, valid range 1024–65535. |
| `CSWEET_GUEST_ARTIFACT_DEVICE` | no | Read by the artifact materializer; only `/dev/sr0` and `/dev/vdc` are accepted. |

`Validate(TimeProvider)` rejects: an empty `WorkloadId` or `ChannelId`; a `ProtocolVersion` other than `1.0`;
image or artifact references that are not `sha256:` digests; a boot token shorter than 16 characters; an
already-expired lease; a relative `ArtifactRoot`; a runtime artifact root other than the fixed
`/run/csweet/artifact/payload` for kinds 1 and 2; a kind outside 0/1/2; incomplete runtime identity for kinds
1 and 2; broker or token paths outside `/run/csweet`; and a frame limit outside 4096–16 MiB.

## Builder command line — `BuilderOptions`

`internal sealed record BuilderOptions` in `src/CSweet.Office.BuilderGuest/Program.cs` is parsed from
`--key value` pairs, not from configuration. Arguments are validated as they are parsed.

| Argument | Required | Validation |
|---|---|---|
| `--repository` | yes | Must parse as an absolute URI. |
| `--commit` | yes | Exactly 40 hexadecimal characters; stored lowercase. |
| `--project` | yes | |
| `--maximum-repository-bytes` | yes | 1 byte–2 GiB. |
| `--maximum-artifact-bytes` | yes | 1 byte–10 GiB. |
| `--broker-socket` | yes | |
| `--target-framework` | no | `net8.0`, `net9.0`, or `net10.0`. |

## Configuration provider mechanics

### Double-underscore environment form

Environment variables cannot contain `:`. The configuration providers built by
`Host.CreateApplicationBuilder` map a double underscore (`__`) to the section separator, so
`CSweet__Office__RuntimeHost__Authentication__SharedKeyFilePath` addresses the key
`CSweet:Office:RuntimeHost:Authentication:SharedKeyFilePath`. Collections use the `__0`, `__1` suffix form
(for example `CSweet__Office__RuntimeHost__AllowedClientSids__0`). Environment variables are applied after
`appsettings.json`, so an environment variable wins over the same key in the file.

Worked example, using the real Linux value from `csweet-office-runtime.service`:

```ini
[Service]
Environment=CSweet__Office__RuntimeHost__Authentication__SharedKeyFilePath=/var/lib/csweet/office/runtime-host.key
```

| Equivalent `appsettings.json` | Effect |
|---|---|
| `CSweet:Office:RuntimeHost:Authentication:SharedKeyFilePath` = `/var/lib/csweet/office/runtime-host.key` | Both the Node and RuntimeHost read the shared key from that absolute path on every signature instead of using `SharedKeyBase64`. |

The same form appears in the macOS runtime plist
(`CSweet__Office__Providers__AppleVirtualization__PayloadManifestPath`), in `/etc/csweet/office.env`, and in
the Windows installer's service `Environment` value — but only for the three machine-scope `CSWEET_*`
variables listed below. Windows supplies all `CSweet__Office__*` values through `appsettings.json`.

### `appsettings.json` written by the Windows installer

The installer writes `ConvertTo-Json -Depth 8` of the following structure to `<InstallRoot>\<version>\appsettings.json`
(UTF-8 without a BOM). Values shown are the literal values the script writes; `$…` denotes a computed value.

| Key | Value |
|---|---|
| `Logging:LogLevel:Default` | `Information` |
| `Logging:LogLevel:Microsoft.Hosting.Lifetime` | `Information` |
| `Logging:EventLog:LogLevel:Default` | `Information` |
| `CSweet:Office:RuntimeHost:NamedPipeName` | `csweet-office-runtime-v1` |
| `CSweet:Office:RuntimeHost:AllowedClientSid` | `$ControlPlaneUserSid` |
| `CSweet:Office:RuntimeHost:AllowedClientSids` | `@($ControlPlaneUserSid, $nodeServiceSid)` |
| `CSweet:Office:RuntimeHost:UnixSocketPath` | `/run/csweet/csweet-office-runtime-v1.sock` |
| `CSweet:Office:RuntimeHost:ConnectTimeoutSeconds` | `10` |
| `CSweet:Office:RuntimeHost:MaximumFrameBytes` | `1048576` |
| `CSweet:Office:RuntimeHost:Authentication:KeyId` | `office-node` |
| `CSweet:Office:RuntimeHost:Authentication:SharedKeyBase64` | `''` |
| `CSweet:Office:RuntimeHost:Authentication:SharedKeyFilePath` | `$DataRoot\runtime-host.key` |
| `CSweet:Office:RuntimeHost:Authorization:StateDirectory` | `$DataRoot\authorization` |
| `CSweet:Office:RuntimeHost:Authorization:MaximumAuthorizationLifetimeSeconds` | `600` |
| `CSweet:Office:RuntimeHost:Authorization:MaximumClockSkewSeconds` | `120` |
| `CSweet:Office:Providers:HyperV:HelperExecutablePath` | `$versionRoot\helper\CSweet.Office.Runtime.HyperV.Helper.exe` |
| `CSweet:Office:Providers:HyperV:HelperExecutableDigest` | `sha256:<digest from runtime-manifest.json>` |
| `CSweet:Office:Providers:HyperV:GuestImagePath` | `$versionRoot\images\csweet-agent-guest.vhdx` |
| `CSweet:Office:Providers:HyperV:GuestImageDigest` | `$manifest.guestImageDigest` |
| `CSweet:Office:Providers:HyperV:GuestImageSignaturePath` | `$versionRoot\images\csweet-agent-guest.vhdx.sig` |
| `CSweet:Office:Providers:HyperV:GuestImageSigningCertificatePath` | `$versionRoot\certificates\guest-image-signing.cer` |
| `CSweet:Office:Providers:HyperV:GuestImageSigningCertificateThumbprint` | `$manifest.guestImageSigningCertificateThumbprint` |
| `CSweet:Office:Providers:HyperV:ArtifactImageRoot` | `$DataRoot\artifact-media` |
| `CSweet:Office:Providers:HyperV:BrokerProtocolVersion` | `1.0` |
| `CSweet:Office:Providers:HyperV:CertificationSuiteVersion` | `$manifest.certificationSuiteVersion` |
| `CSweet:Office:Providers:HyperV:CertificationEvidencePath` | `$versionRoot\certification\windows-hyperv.json` |
| `CSweet:Office:Providers:HyperV:CertificationEvidenceDigest` | `$manifest.certificationEvidenceDigest` |
| `CSweet:Office:Providers:HyperV:CertifiedAt` | `$manifest.certifiedAt` |
| `CSweet:Office:Providers:HyperV:CertificationExpiresAt` | `$manifest.certificationExpiresAt` |
| `CSweet:Office:Providers:Firecracker` | empty object `{}` |
| `CSweet:Office:Providers:AppleVirtualization` | empty object `{}` |
| `CSweet:Office:Node` | copied verbatim from the previous install, or written fresh during enrollment |

The repository's own `src/CSweet.Office.RuntimeHost/appsettings.json` is the development default and is
shipped inside the package; the installer overwrites it at the version root. Its Firecracker and Apple
sections carry `PayloadManifestPath`, `GuestChannelConnectTimeoutSeconds: 30`, and
`RequiredGuestChannelTransport: "stdio-duplex-v1"`, and its Hyper-V section lists every
`PlatformIsolationBackendOptions` path as an empty string.

### systemd `Environment=` overrides (Linux)

`/etc/csweet/office.env` (mode `0600`), written by `scripts/linux/install-office.sh`:

| Key | Value |
|---|---|
| `CSweet__Office__Node__ControlPlaneUrl` | installer argument 2 |
| `CSweet__Office__Node__StateDirectory` | `/var/lib/csweet/office/node` |
| `CSweet__Office__Node__ArtifactCacheDirectory` | `/var/lib/csweet/office/node/artifact-cache` |
| `CSweet__Office__Node__ArtifactMediaDirectory` | `/var/lib/csweet/artifact-media` |
| `CSweet__Office__Node__EnrollmentTokenFilePath` | `/var/lib/csweet/office/node/enrollment.secret` |
| `CSweet__Office__Node__SecurityProfile` | `baseline` \| `hardened` \| `development` |
| `CSweet__Office__Node__MixedUseHost` | `true` \| `false` (from `--dedicated-host`) |
| `CSweet__Office__Node__AllowDevelopmentAssignments` | `true` \| `false` |
| `CSweet__Office__RuntimeHost__UnixSocketPath` | `/run/csweet/csweet-office-runtime-v1.sock` |
| `CSweet__Office__RuntimeHost__Authentication__SharedKeyFilePath` | `/var/lib/csweet/office/runtime-host.key` |
| `CSweet__Office__RuntimeHost__Authorization__StateDirectory` | `/var/lib/csweet/office/authorization` |

`/etc/csweet/runtime-host.env` (mode `0600`): `CSWEET_FIRECRACKER_DATA_ROOT`,
`CSWEET_FIRECRACKER_PACKAGE_ROOT`, `CSWEET_FIRECRACKER_WORKLOAD_UID`, `CSWEET_FIRECRACKER_WORKLOAD_GID`,
`CSWEET_FIRECRACKER_GUEST_VSOCK_PORT` (`5000`), and `CSWEET_ARTIFACT_MEDIA_ROOT`.

`scripts/linux/csweet-office-runtime.service` additionally sets, inline:

| `Environment=` | Value |
|---|---|
| `CSweet__Office__Providers__Firecracker__ArtifactImageRoot` | `/var/lib/csweet/artifact-media` |
| `CSweet__Office__Providers__Firecracker__PayloadManifestPath` | `/opt/csweet/office/runtime-manifest.json` |
| `CSweet__Office__RuntimeHost__Authentication__SharedKeyFilePath` | `/var/lib/csweet/office/runtime-host.key` |
| `CSweet__Office__RuntimeHost__Authorization__StateDirectory` | `/var/lib/csweet/office/authorization` |

### launchd keys (macOS)

`scripts/macos/com.csweet.office.node.plist` (`com.csweet.office`) carries the `EnvironmentVariables`
dictionary below; `__CONTROL_PLANE_URL__`, `__SECURITY_PROFILE__`, `__MIXED_USE_HOST__`, and
`__ALLOW_DEVELOPMENT_ASSIGNMENTS__` are substituted by `install-office.sh` before the plist is copied to
`/Library/LaunchDaemons`. The job runs as `_csweetnode:_csweet` with `Umask` 27.

| Key | Value |
|---|---|
| `CSweet__Office__Node__ControlPlaneUrl` | `__CONTROL_PLANE_URL__` |
| `CSweet__Office__Node__StateDirectory` | `/Library/Application Support/CSweet/Office/node` |
| `CSweet__Office__Node__ArtifactCacheDirectory` | `/Library/Application Support/CSweet/Office/node/artifact-cache` |
| `CSweet__Office__Node__ArtifactMediaDirectory` | `/Library/Application Support/CSweet/Office/artifact-media` |
| `CSweet__Office__Node__EnrollmentTokenFilePath` | `/Library/Application Support/CSweet/Office/node/enrollment.secret` |
| `CSweet__Office__Node__SecurityProfile` | `__SECURITY_PROFILE__` |
| `CSweet__Office__Node__MixedUseHost` | `__MIXED_USE_HOST__` |
| `CSweet__Office__Node__AllowDevelopmentAssignments` | `__ALLOW_DEVELOPMENT_ASSIGNMENTS__` |
| `CSweet__Office__RuntimeHost__Authentication__SharedKeyFilePath` | `/Library/Application Support/CSweet/Office/runtime-host.key` |

`scripts/macos/com.csweet.office.runtime.plist` (`com.csweet.office.runtime`) runs as root with `Umask` 27:

| Key | Value |
|---|---|
| `CSweet__Office__Providers__AppleVirtualization__ArtifactImageRoot` | `/Library/Application Support/CSweet/Office/artifact-media` |
| `CSweet__Office__Providers__AppleVirtualization__PayloadManifestPath` | `/Library/Application Support/CSweet/Office/runtime-manifest.json` |
| `CSweet__Office__RuntimeHost__Authentication__SharedKeyFilePath` | `/Library/Application Support/CSweet/Office/runtime-host.key` |
| `CSweet__Office__RuntimeHost__Authorization__StateDirectory` | `/Library/Application Support/CSweet/Office/authorization` |
| `CSWEET_APPLE_VIRTUALIZATION_DATA_ROOT` | `/Library/Application Support/CSweet/Office/AppleVirtualization` |
| `CSWEET_APPLE_VIRTUALIZATION_PACKAGE_ROOT` | `/Library/Application Support/CSweet/Office/apple-virtualization` |
| `CSWEET_APPLE_VIRTUALIZATION_GUEST_PORT` | `5000` |
| `CSWEET_APPLE_VIRTUALIZATION_SOCKET_ROOT` | `/var/run/csweet-av` |
| `CSWEET_ARTIFACT_MEDIA_ROOT` | `/Library/Application Support/CSweet/Office/artifact-media` |

### Machine-scope `CSWEET_*` variables

These are the variables the platform code reads directly with `Environment.GetEnvironmentVariable`; they are
not configuration keys and cannot be expressed in `appsettings.json`.

| Variable | Platform | Set by | Consumed by |
|---|---|---|---|
| `CSWEET_HYPERV_BROKER_SERVICE_ID` | Windows | installer (`Machine` scope and the service `Environment` value) | Hyper-V broker registration and the helper. |
| `CSWEET_HYPERV_DATA_ROOT` | Windows | installer (both places) | `HyperVHelperPaths` — default `%ProgramData%\CSweet\RuntimeHost\HyperV`. |
| `CSWEET_ARTIFACT_MEDIA_ROOT` | Windows, Linux, macOS | installer, env file, plist | Artifact media root; the Hyper-V helper falls back to `%ProgramData%\CSweet\Office\artifact-media`. |
| `CSWEET_FIRECRACKER_DATA_ROOT`, `CSWEET_FIRECRACKER_PACKAGE_ROOT`, `CSWEET_FIRECRACKER_WORKLOAD_UID`, `CSWEET_FIRECRACKER_WORKLOAD_GID`, `CSWEET_FIRECRACKER_GUEST_VSOCK_PORT` | Linux | `/etc/csweet/runtime-host.env` | `FirecrackerHelperPaths` / the Firecracker helper. |
| `CSWEET_APPLE_VIRTUALIZATION_DATA_ROOT`, `CSWEET_APPLE_VIRTUALIZATION_PACKAGE_ROOT`, `CSWEET_APPLE_VIRTUALIZATION_GUEST_PORT`, `CSWEET_APPLE_VIRTUALIZATION_SOCKET_ROOT` | macOS | runtime plist | Apple Virtualization helper and backend. |
| `CSWEET_GUEST_BROKER_TRANSPORT`, `CSWEET_GUEST_VSOCK_PORT`, `CSWEET_GUEST_ARTIFACT_DEVICE` | In-guest | `build/**/provision-guest.sh` | `CSweet.Office.RuntimeGuest` (`CSWEET_GUEST_ARTIFACT_DEVICE=firecracker` images use `/dev/vdc`). |
| `CSWEET_GUEST_*` boot values | In-guest | Headquarters boot configuration on the vSock transports | `GuestServiceOptions`. |

## Validation enforced at startup

| Options class | Check | Failure |
|---|---|---|
| `OfficeOptions` | `SecurityProfile` is `baseline`, `hardened`, or `development` | `InvalidOperationException` from `SecurityPosture()`. |
| `OfficeOptions` | `development` profile without `AllowDevelopmentAssignments` | `InvalidOperationException`. |
| `OfficeOptions` | `hardened` profile with a non-empty `MissingSecurityControls` | `InvalidOperationException`. |
| `OfficeOptions` | `ControlPlaneUrl` is an absolute HTTPS URL | `InvalidOperationException` in `Program.cs`. |
| `RuntimeHostEndpointOptions` | Pipe name, socket path, connect timeout, frame size | `InvalidOperationException` from `Validate()`, called by both hosts before the host is built. |
| `RuntimeHostAuthorizationOptions` | Lifetime 30–3600 s, clock skew 0–600 s | `ArgumentOutOfRangeException` from the `RuntimeHostAuthorizationGate` constructor. |
| `RuntimeHostAuthenticationOptions` | Shared key is Base64 and at least 32 bytes | `InvalidDataException` on first signature computation or validation. |
| `RuntimeHostAuthenticationOptions` | Key file exists, 40–4096 bytes, not a reparse point | The file is treated as absent and the key stays unloaded. |
| `HyperVSocketTransportOptions` | Port 1024–65535, timeout 1–300 s, retry 25–5000 ms | `InvalidOperationException` from `Validate()`. |
| `PlatformIsolationBackendOptions` | Manifest path absolute; every declared file digest matches; provider identity matches; required metadata present | `InvalidDataException` from `PlatformRuntimePayloadManifest.ApplyIfConfigured`. |
| `PlatformIsolationBackendOptions` | Helper digest, guest image digest, evidence digest, signature, and live helper probe | The probe returns unavailable with a reason; no certification is produced. |
| `ArtifactStoreOptions` | Root absolute and not a filesystem root; provider `filesystem`/`s3`; numeric limits | `InvalidOperationException`. |
| `ArtifactMediaOptions` | Root absolute and not a filesystem root | `InvalidOperationException`. |
| `GuestServiceOptions` | Identity, protocol version, digests, boot token, lease, artifact root, kind, `/run/csweet` confinement, frame limit | `InvalidOperationException` from `Validate`. |
| `GuestServiceOptions.FromEnvironment` | All required variables present | `InvalidOperationException("Required guest setting <name> is missing.")`. |

`SEC-INV-11` fixes the permitted ranges for the security-relevant numeric values above. Widening one of them
is a security change, not a tuning change.

## Sources

`src/CSweet.Office.Node/{OfficeOptions.cs,Program.cs,OfficeArtifactCache.cs,ControlPlaneCertificateProbe.cs}`,
`src/CSweet.Office.RuntimeHost/{Program.cs,appsettings.json}`,
`src/CSweet.Office.Runtime.LocalRpc/{RuntimeHostEndpointOptions.cs,RuntimeHostAuthorizationGate.cs}`,
`src/CSweet.Office.Runtime.Protocol/RuntimeHostAuthentication.cs`,
`src/CSweet.Office.Runtime.Core/{ExternalPlatformIsolationBackend.cs,PlatformRuntimePayloadManifest.cs,ExternalPlatformStdioGuestChannelConnector.cs}`,
`src/CSweet.Office.Runtime.HyperV/{HyperVSocketTransport.cs,HyperVIsolationBackend.cs}`,
`src/CSweet.Office.Runtime.Firecracker/FirecrackerIsolationBackend.cs`,
`src/CSweet.Office.Runtime.AppleVirtualization/AppleVirtualizationIsolationBackend.cs`,
`src/CSweet.Office.Runtime.Artifacts/{ArtifactStoreOptions.cs,ArtifactMediaOptions.cs}`,
`src/CSweet.Office.RuntimeGuest/{GuestServiceOptions.cs,Program.cs}`,
`src/CSweet.Office.BuilderGuest/Program.cs`,
`src/CSweet.Office.Runtime.HyperV.Helper/HyperVHelperPaths.cs`,
`scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`, `scripts/linux/install-office.sh`,
`scripts/linux/csweet-office-runtime.service`, `scripts/macos/com.csweet.office.node.plist`,
`scripts/macos/com.csweet.office.runtime.plist`, `docs/20-security/11-security-invariants.md`.

Verified: 2026-09-15.
