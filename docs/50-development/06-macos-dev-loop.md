# macOS development loop

**Audience:** a contributor on macOS building the Apple Virtualization backend.

There is no automated isolation test script for macOS in this repository. `scripts/macos` contains
`configure-office.sh`, `install-office.sh`, `new-installer-package.sh`, `new-runtime-payload.sh`,
`uninstall-office.sh`, and two launchd plists — nothing that builds a guest image or certifies one. The
certification smoke runner supports exactly two providers, `hyperv` on Windows and `firecracker` on
Linux, so the Apple provider has no in-repo certification path. The supported way to produce a macOS
payload is `scripts/macos/new-runtime-payload.sh` with inputs produced elsewhere.

## Building the Swift helper

`src/CSweet.Office.Runtime.AppleVirtualization.Helper` is a Swift package, not a .NET project, and it is
in neither solution:

| Fact | Value |
|---|---|
| Tools version | `swift-tools-version: 5.9` |
| Platform floor | `.macOS(.v14)` |
| Product | `CSweet.Office.Runtime.AppleVirtualization.Helper` |
| Target | `CSweetAppleVirtualizationHelper`, path `Sources/CSweetAppleVirtualizationHelper` |
| Sources | `HelperController.swift`, `main.swift`, `Models.swift`, `SecureIO.swift`, `VirtualMachineManager.swift` |
| Entitlement | `CSweet.AppleVirtualization.entitlements` — `com.apple.security.virtualization` = `true` |

```bash
swift build --package-path src/CSweet.Office.Runtime.AppleVirtualization.Helper -c release --arch arm64
swift build --package-path src/CSweet.Office.Runtime.AppleVirtualization.Helper -c release --arch arm64 --show-bin-path
```

The executable is `<bin-path>/CSweet.Office.Runtime.AppleVirtualization.Helper`. Use `--arch x86_64` for
`osx-x64`.

The helper is a stdio tool, so it can be exercised without RuntimeHost. It accepts exactly four
arguments and one JSON request on standard input, and writes one JSON response:

```bash
echo '{}' | ./CSweet.Office.Runtime.AppleVirtualization.Helper --protocol 1.0 --operation probe
```

The operation allow-list is `probe`, `create`, `start`, `inspect`, `stop`, `destroy`, `reap`, `logs`, and
`open-guest-channel`. Anything else is rejected as `invalid-arguments`. The runtime independently enforces
`macOS 14` and reports `unsupported-host` below it. `--workload-host <instance.json>` is the internal
manager mode that `create` spawns; it is not part of the helper protocol surface
(`SEC-INV-20` governs the helper boundary).

Signing: the payload script signs the helper with `--entitlements CSweet.AppleVirtualization.entitlements`,
then fails the build unless `plutil -extract com.apple.security.virtualization raw` returns `true`.
A locally built helper is unsigned, so sign local builds the same way before running anything that needs
the Virtualization framework.

## Running the backend

`RuntimeHost` registers `AppleVirtualizationIsolationBackend` when `OperatingSystem.IsMacOS()`. There is
one provider per host operating system; the descriptor comes from `IsolationProviderCatalog`.

Provider configuration is section `CSweet:Office:Providers:AppleVirtualization`. Installed systems do not
set the individual file paths. The launchd unit `com.csweet.office.runtime.plist` sets environment
variables in double-underscore form:

| Variable | Value |
|---|---|
| `CSweet__Office__Providers__AppleVirtualization__PayloadManifestPath` | `/Library/Application Support/CSweet/Office/runtime-manifest.json` |
| `CSweet__Office__Providers__AppleVirtualization__ArtifactImageRoot` | `/Library/Application Support/CSweet/Office/artifact-media` |
| `CSWEET_APPLE_VIRTUALIZATION_DATA_ROOT` | `/Library/Application Support/CSweet/Office/AppleVirtualization` |

With `PayloadManifestPath` set, `PlatformRuntimePayloadManifest.ApplyIfConfigured` verifies every declared
payload file against its SHA-256 and then populates the helper path, guest image, signature, signing
certificate, evidence, and certification window. A manifest that is not for this exact provider build,
host operating system, host architecture and `providerVersion` is rejected rather than partially applied.

For a development run, publish RuntimeHost for the host architecture and start it with the same variables
pointing at a payload you built:

```bash
dotnet publish src/CSweet.Office.RuntimeHost/CSweet.Office.RuntimeHost.csproj \
    -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o /tmp/csweet-runtime-host
```

## Building a payload

```
usage: new-runtime-payload.sh OUTPUT_ROOT RID SIGNING_IDENTITY VMLINUX GUEST_IMG GUEST_SIG \
           SIGNING_CERT CERT_THUMBPRINT EVIDENCE SUITE_VERSION CERTIFIED_AT [EXPIRES_AT]
```

| Argument | Rule |
|---|---|
| `OUTPUT_ROOT` | Must be empty when it already exists; the script refuses to write into a non-empty directory. |
| `RID` | `osx-arm64` or `osx-x64`; selects `arm64` or `x86_64` for the Swift build. |
| `SIGNING_IDENTITY` | Passed to `codesign --sign`; must be usable with `--timestamp --options runtime`. |
| `GUEST_IMG`, `GUEST_SIG`, `SIGNING_CERT`, `EVIDENCE` | Must exist; they are the certified guest inputs. |
| `CERT_THUMBPRINT` | Hexadecimal only. |
| `CERTIFIED_AT`, `EXPIRES_AT` | `EXPIRES_AT` is optional and becomes `null` in the manifest when omitted. |

The script requires `codesign dotnet jq plutil python3 shasum swift`, publishes RuntimeHost and Node
self-contained, builds the Swift helper in release, installs the payload contents, signs all three
executables, verifies the helper's entitlement, then writes a manifest-backed payload:

| Payload path | Content |
|---|---|
| `CSweet.Office.RuntimeHost`, `CSweet.Office.Node`, `CSweet.Office.Runtime.AppleVirtualization.Helper` | Signed executables. |
| `apple-virtualization/vmlinux` | Pinned kernel. |
| `images/csweet-agent-guest.img` and `.img.sig` | Guest image and detached signature. |
| `certificates/guest-image-signing.cer` | Pinned guest image signing certificate. |
| `certification/macos-apple-virtualization.json` | Certification evidence. |
| `com.csweet.office.runtime.plist`, `com.csweet.office.node.plist` | launchd units. |
| `install-office.sh`, `uninstall-office.sh` | Install and removal entry points. |
| `runtime-manifest.json` | `providerId: "apple-virtualization"`, `providerVersion: "1.0.0"`, `hostOperatingSystem: "macos"`, `brokerProtocolVersion: "1.0"`, image and evidence digests, and a SHA-256 for every other file. |

The signed and notarized installer package is a separate step:
`scripts/macos/new-installer-package.sh PAYLOAD_ROOT OUTPUT_PKG VERSION INSTALLER_SIGNING_IDENTITY NOTARY_KEYCHAIN_PROFILE`
requires `codesign ditto pkgbuild pkgutil plutil productbuild python3 spctl xcrun`, rejects symbolic links
in the payload, re-verifies all three signatures plus the entitlement, stages the payload under
`/Library/Application Support/CSweet/Office/InstallerPayload`, and submits the package to
`notarytool` before stapling.

> **Out of repo:** the kernel, the guest image, its signature, the signing certificate, and the
> certification evidence are inputs to this repository's payload script, not outputs of it. Producing
> and certifying an Apple Virtualization guest image is owned outside this repository; the in-repo
> evidence for that boundary is the argument list of `scripts/macos/new-runtime-payload.sh` and the
> absence of any macOS entry in the smoke runner's provider checks.

## Sources

`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Package.swift`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/CSweet.AppleVirtualization.entitlements`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Sources/CSweetAppleVirtualizationHelper/{main.swift,HelperController.swift}`,
`scripts/macos/new-runtime-payload.sh`, `scripts/macos/new-installer-package.sh`, `scripts/macos/install-office.sh`,
`scripts/macos/com.csweet.office.runtime.plist`, `src/CSweet.Office.RuntimeHost/Program.cs`,
`src/CSweet.Office.Runtime.Core/PlatformRuntimePayloadManifest.cs`, `src/CSweet.Office.WindowsSmokeTest/Program.cs`,
`docs/30-workloads/07-provider-backends.md`.

Verified: 2026-09-15.
