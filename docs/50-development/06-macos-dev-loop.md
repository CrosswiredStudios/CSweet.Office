# macOS development loop

**Audience:** a contributor on macOS building the Apple Virtualization backend.

A fresh clone can build the native Swift helper and run the independent .NET tests on a Mac with the .NET 10 SDK and Xcode command-line tools. Run the following from the repository root.

```sh
dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release -p:UseLocalOfficeContracts=false
swift build --package-path src/CSweet.Office.Runtime.AppleVirtualization.Helper -c release
```

CI runs both checks on macOS. Live builder and runtime acceptance additionally requires an architecture-matched Linux kernel, Office guest image, Linux guest probe, and an entitled helper on a virtualization-capable Mac.

The Apple helper runs builder, runtime, toolchain-build and generic Compute workloads. The generic Compute partition offers an approved arm64 Linux template from an enrolled Mac Office. See [Mac Compute configuration and hardware verification](../40-operations/03-installation-macos.md#generic-compute-on-apple-silicon). Verified: 2026-11-23. [RuntimeHost registration](../../src/CSweet.Office.RuntimeHost/Program.cs) · [Apple helper](../../src/CSweet.Office.Runtime.AppleVirtualization.Helper/Sources/CSweetAppleVirtualizationHelper/HelperController.swift)

## Building the Swift helper

`src/CSweet.Office.Runtime.AppleVirtualization.Helper` is a Swift package, not a .NET project, and it is
in neither solution:

| Fact | Value |
|---|---|
| Tools version | `swift-tools-version: 5.9` |
| Platform floor | `.macOS(.v14)` |
| Product | `CSweet.Office.Runtime.AppleVirtualization.Helper` |
| Target | `CSweetAppleVirtualizationHelper`, path `Sources/CSweetAppleVirtualizationHelper` |
| Sources | `HelperController.swift`, `main.swift`, `Models.swift`, `SecureIO.swift`, `LinuxGuestConfiguration.swift`, `VirtualMachineManager.swift` |
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

The operation allow-list is `probe`, `create`, `start`, `inspect`, `stop`, `destroy`, `inspect-destruction`, `reap`, `logs`, and
`open-guest-channel`. Anything else is rejected as `invalid-arguments`. The runtime independently enforces
`macOS 14` and reports `unsupported-host` below it. `--workload-host <instance.json>` is the internal
manager mode that `create` spawns; it is not part of the helper protocol surface
(`SEC-INV-20` governs the helper boundary).

Signing: the payload script signs the helper with `--entitlements CSweet.AppleVirtualization.entitlements`,
then fails the build unless the literal entitlement key is Boolean true.
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

## Build and boot a development guest on the Mac

Start Docker Desktop and run these commands from the Office checkout on an Apple silicon Mac. Use a new output directory. On Intel, select `linux/amd64`.

```sh
docker buildx build --platform linux/arm64 -f build/apple-virtualization/Dockerfile \
  --output type=local,dest=artifacts/apple-guest .
./scripts/macos/test-guest-boot.sh artifacts/apple-guest
```

The Docker build exports an Ubuntu kernel (`vmlinux`), its matching `initrd.img`, a read-only ext4 Office guest and the Linux certification probe. It reuses the Office provisioning script for virtio block devices and the guest broker. The boot check signs a temporary test executable locally, starts the shared Apple VM configuration with two vCPUs, 1 GiB RAM, a fresh scratch disk and zero network devices, connects to the Office broker on virtio socket port 5000, and stops the VM. It runs as the logged-in user. [Guest build](../../build/apple-virtualization/Dockerfile) · [Boot check](../../scripts/macos/test-guest-boot.sh)

For a packaged guest that needs an initial RAM disk, set `CSWEET_GUEST_INITRD` to the matching file when building the runtime payload. It is included in the manifest digest checks alongside the kernel. Existing kernel-only payloads can boot without it.

## Run the hardware acceptance test

The shared certification runner boots two disposable VMs. The runtime probe checks the workload identity, network isolation, mounts, and broker. The builder fetches a fixture through the broker, compiles it with the guest SDK, and returns an executable artifact for digest verification. On November 23, 2026, all 18 checks in suite `csweet-hardware-vm-smoke-v14` passed on an Apple silicon Mac Studio with macOS 26.5.

For a local developer run, build the guest above and run the following from your trusted Office checkout. The test uses a locally signed helper and a private directory, with no launchd install.

```sh
helper_project=src/CSweet.Office.Runtime.AppleVirtualization.Helper
swift build --package-path "$helper_project" -c release
helper_dir=$(swift build --package-path "$helper_project" -c release --show-bin-path)
dotnet build src/CSweet.Office.WindowsSmokeTest -c Release

output=$(sudo mktemp -d /private/tmp/csweet-office.XXXXXX)
sudo install -m 0755 "$helper_dir/CSweet.Office.Runtime.AppleVirtualization.Helper" "$output/helper"
sudo codesign --force --sign - --entitlements "$helper_project/CSweet.AppleVirtualization.entitlements" "$output/helper"
sudo install -m 0444 artifacts/apple-guest/{vmlinux,initrd.img,csweet-agent-guest.img,CSweet.Office.GuestProbe} "$output/"
sudo ditto src/CSweet.Office.WindowsSmokeTest/bin/Release/net10.0 "$output/runner"
sudo env CSWEET_APPLE_VIRTUALIZATION_PACKAGE_ROOT="$output" CSWEET_APPLE_VIRTUALIZATION_SOCKET_ROOT="$output/sockets" \
  "$(command -v dotnet)" "$output/runner/CSweet.Office.WindowsSmokeTest.dll" \
  --provider apple --helper "$output/helper" --guest-image "$output/csweet-agent-guest.img" \
  --probe "$output/CSweet.Office.GuestProbe" --output-root "$output/run" --evidence "$output/evidence.json"
```

After a successful run, save the report with `sudo cat "$output/evidence.json" > artifacts/macos-acceptance.json`. The disposable files are under the path printed by `echo "$output"`.

For release evidence, pass `--certification-expires-at "$CSWEET_CERTIFICATION_VALID_UNTIL"` with the protected release expiry in ISO 8601 format. Developer runs can omit this option. The release workflow consumes the guest image, signature, certificate and acceptance evidence as protected inputs. See `scripts/release/Invoke-MacRelease.ps1` and `src/CSweet.Office.WindowsSmokeTest/Program.cs`.

## Sources

`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Package.swift`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/CSweet.AppleVirtualization.entitlements`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Sources/CSweetAppleVirtualizationHelper/{main.swift,HelperController.swift}`,
`scripts/macos/new-runtime-payload.sh`, `scripts/macos/new-installer-package.sh`, `scripts/macos/install-office.sh`,
`scripts/macos/com.csweet.office.runtime.plist`, `src/CSweet.Office.RuntimeHost/Program.cs`,
`src/CSweet.Office.Runtime.Core/PlatformRuntimePayloadManifest.cs`, `src/CSweet.Office.WindowsSmokeTest/Program.cs`,
`docs/30-workloads/07-provider-backends.md`.

Verified: 2026-11-23.
