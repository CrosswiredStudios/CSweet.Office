# Reviewed runtime public internet

**Audience:** runtime, networking and image contributors; Office operators.

The signed `RuntimeWorkloadSpecification.NetworkPolicy` authorizes public TCP/UDP for one runtime.
`OfficeInternetTunnel` terminates guest egress locally after Headquarters accepts the authenticated
guest lease. Other broker messages retain their Headquarters relay. `RuntimeInternetEgress` owns bounded
connected sockets and closes them on assignment, tunnel or control-session termination. Per-direction
two-frame credit windows preserve sustained transfers while keeping the broker reader responsive.

`PublicInternetPolicy` validates actual IP destinations, administrative CIDRs and host interface addresses.
No host listener, private-network route or hypervisor NIC is added. `GuestInternetProxy` creates a guest-local
TUN and SOCKS5 bridge only for the approved boot policy. End-to-end TLS remains in the workload application.

`Prepare-CSweetDevelopmentPayload.ps1` and `provision-development.sh` verify the immutable artifacts in
`development-tools.lock.json`. Node/npm, .NET, Python and distro tools are measured as part of the guest image.
Tools/home/cache directories belong to the unprivileged workload. Native system changes require rebuilding
the image; the internet permission does not give administrator access. SEC-INV-07, SEC-INV-08, SEC-INV-10,
SEC-INV-20 and SEC-INV-23 continue to apply.

## Certification and rollout

The smoke runner's `--public-internet true` mode retains restricted runtime/builder probes and adds a separate
public-egress VM. It checks public TCP/UDP/IPv6, private/host denial, Node/npm/npx, a generated Phaser lockfile,
`npm ci`, test/build, portable npm tooling and relay cleanup. `ProviderCertificationEvidence.SupportsPublicInternet`
requires protocol 1.1, suite v15 and every named check. The explicitly limited `v15-ipv4` suite requires all
checks except public IPv6 reachability and retains that check's actual result. It advertises IPv4 only;
full signed policies remain unavailable on limited evidence. A missing required check keeps support false.
The same Linux guest path applies to Hyper-V, Firecracker and Apple Virtualization.

Windows preparation: run `Initialize-CSweetWindowsIsolationTest.ps1 -DevelopmentTools -SkipInstall` from
an elevated prompt; the user-approved Windows IPv4 rollout adds `-PublicInternetIpv4Only`. The `SkipInstall`
gate preserves the installed Office while measuring the new payload.
Linux rootfs preparation accepts `CSWEET_DEVELOPMENT_PAYLOAD`; Apple uses that same matching-architecture
Linux guest image with its native helper. Each destination host runs its own real smoke test before installation.
Runtime manifests derive the guest protocol from the measured evidence. Upgrade an existing Office only
after drain and confirmed zero active assignments (SEC-INV-18).

> **Out of repo:** Headquarters owns manifest/grant review, signed runtime-policy derivation, fleet placement,
> current-grant reads, grant-change fencing and the live delivery retry. Its implementation map is
> `../csweet/docs/implementation/features/runtime-public-internet.md`.

Source/unit checks do not certify a VM or establish live ticket recovery. Real-provider verification remains
complete for the user-approved Hyper-V IPv4 scope. Evidence from `certification-20261013-141714` verifies
restricted isolation/builder behavior, public IPv4 TLS, UDP, private/host denial, relay cleanup and the
development package/lockfile/clean-restore/test/build/portable-tool fixture. The guest digest is
`sha256:17bec17271622ba012887fa4c0db84774bf03cc1f094ec06259881b0b2fbcf10`. The host has no public IPv6
route; the evidence retains that failed check and the limited suite advertises IPv4 only. Live agent
validation and ticket recovery remain pending;
Linux/macOS destination hosts are not available here and retain unavailable internet capability inventory.

## Sources

`src/CSweet.Office.Node/{OfficeInternetTunnel.cs,RuntimeInternetEgress.cs,PublicInternetPolicy.cs,OfficeWorker.cs,ProviderInventory.cs}`;
`src/CSweet.Office.RuntimeGuest/{GuestInternetProxy.cs,GuestBrokerSession.cs,GuestServiceOptions.cs,GuestWorkloadSupervisor.cs}`;
`src/CSweet.Office.Runtime.Core/ExternalPlatformIsolationBackend.cs`;
`src/CSweet.Office.WindowsSmokeTest/{Program.cs,CertificationBrokerHost.cs}`;
`src/CSweet.Office.GuestProbe/Program.cs`;
`build/{development-tools.lock.json,provision-development.sh}`;
`scripts/{Prepare-CSweetDevelopmentPayload.ps1,windows/Initialize-CSweetWindowsIsolationTest.ps1}`.

Verified: 2026-10-13.
