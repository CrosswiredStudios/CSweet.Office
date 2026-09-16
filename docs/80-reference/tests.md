# Test reference

**Audience:** contributors checking whether a behaviour is covered before changing it; reviewers using the
suite as the closest thing to a written specification.

All tests live in one flat xunit project, `tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj`, which
references the runtime libraries directly: 25 files, 135 test methods. There is no test project per library
and no fixtures directory; helpers are private nested classes inside each test class.

Two kinds of test share the project, and the distinction matters when you are judging coverage:

| Kind | What it does | How to recognise it |
|---|---|---|
| Behavioural | Constructs the production type and asserts its observable behaviour. | The test instantiates a type from `src/`. |
| Script-text assertion | Reads a script or source file from the repository and asserts a literal string, or an ordering between two literals, is present. | The test calls `File.ReadAllText` on a `scripts/…` or `src/…` path. These pin intent against incidental edits; they do not execute the script. |

The repository root is located by walking up from `AppContext.BaseDirectory` until `CSweet.Office.slnx` is
found, so the script-text tests fail loudly rather than silently passing if the layout changes.

Run the suite with:

```powershell
dotnet test tests\CSweet.Office.Tests\CSweet.Office.Tests.csproj -c Release -p:UseLocalOfficeContracts=false
```

## `AgentArtifactMediaStoreTests` (2 tests)

Behavioural. Temporary root under `%TEMP%\csweet-artifact-media-tests\<guid>`, deleted in `Dispose`.

- `EnsureReadOnlyMediaAsync_CreatesVerifiedContentAddressedIso` — importing a tar bundle and asking for media
  creates `<mediaRoot>\<digest[7..]>.iso` and `SingleFileIso9660.VerifyArtifactDigestAsync` accepts it.
- `VerifyArtifactDigestAsync_RejectsTamperedMedia` — flipping one byte at sector `21 * SingleFileIso9660.SectorSize`
  makes verification return `false`.

## `CertifiedGuestImageRegistryTests` (3 tests)

Behavioural, with a recording selector and a fake provider.

- Resolution without a configured digest or version takes both from the provider's active certification.
- An optional `ExpectedDigest` is passed through to the selector rather than cached.
- A certification whose suite version differs from `RequiredCertificationSuiteVersion` throws
  `IsolationUnavailableException` whose message contains `out of date`, `installed: windows-hyperv-v1`, and
  `required: windows-hyperv-v2`.

## `ControlPlaneServerCertificateValidatorTests` (4 tests)

Behavioural, with a fixed clock at `2026-08-12T20:00:00Z` and self-signed certificates for `localhost`.

- A publicly trusted certificate passes with `SslPolicyErrors.None` even when no pin is configured.
- A matching pin accepts a private chain (`RemoteCertificateChainErrors`).
- A pin does **not** override a hostname mismatch.
- A different or already-expired certificate is rejected, with and without chain errors.

## `ExternalPlatformStdioGuestChannelConnectorTests` (4 tests)

Behavioural.

- `HandshakeReaderStopsAtNewlineWithoutConsumingBrokerBytes` — after reading
  `{"success":true,"guestChannelTransport":"stdio-duplex-v1"}\n`, the next bytes read are exactly `broker-frame`,
  proving the handshake reader does not over-read.
- `HandshakeReaderRejectsOversizedOrAmbiguousFraming` — 4098 bytes without a newline, and a `\r\n`-terminated
  line, both throw `InvalidDataException`.
- `ConnectorRejectsWrongProviderAndControlCharacters` — `ValidateHandle` throws for a handle whose provider id
  is `wrong` and for an instance id containing `\n`.
- `HandshakeMustConfirmCertifiedTransport` — `null`, `""`, and `stdio-duplex-v2` are all rejected by
  `ValidateHandshake` with `IsolationUnavailableException`.

## `FirecrackerHelperSecurityTests` (7 tests)

Behavioural; no external process.

- `ArgumentsAllowOnlyTheFixedTypedProtocolSurface` — `open-guest-channel` parses; `shell` and an extra
  `--command` switch both throw `HelperProtocolException`.
- `JailerArgumentsEnforceNamespacesAndHardResourceLimitsWithoutNetworking` — the jailer argument list contains
  `--new-pid-ns`, `--daemonize`, a `system.slice/csweet-runtime-host.service` cgroup parent,
  `memory.max=671088640`, `pids.max=64`, and `cpu.max=150000 100000`, and contains neither `--netns` nor the
  substring `network`.
- `ProtectedPathsRejectTraversal` — `SafeChild(root, "..\outside")` and a newline-bearing path both throw.
- `VsockHandshakeIsBoundedAndPreservesBrokerBytes` — `ReadAsciiLineAsync` returns `OK 1073741824` and leaves
  `broker-frame` in the stream.
- `VsockHandshakeRejectsAmbiguousOrOversizedFraming` — CRLF and 130 bytes with a limit of 128 both throw.
- `ReaperOnlySelectsExpiredRuntimeInstances` — an expired runtime is reaped; a builder, a live lease, and the
  current process id are not.
- `ToolVersionParsingIsStrict` — `Firecracker v1.13.1` and `jailer 1.13.1 (release)` yield `1.13.1`;
  `not-a-version` yields `null`.

## `GuestArtifactMaterializerTests` (7 cases over 3 methods)

Behavioural.

- Device resolution accepts `null` (default `/dev/sr0`), `/dev/sr0`, `/dev/vdc`, and rejects `/dev/vdb`,
  `/tmp/artifact.iso`, and `/dev/vdc\n`.
- Mode sanitization forces directories to `rwxr-x---`, data files to `r--r-----`, and executables to
  `r-xr-x---` from the workload's perspective.

## `GuestLocalBrokerProxyTests` (10 cases)

Behavioural, in-memory streams. Includes the observed 2,578,964-byte request, exact-ceiling fixed-length and chunked bodies, HTTP 413 without forwarding above the ceiling, and a multi-megabyte response.

- Chunked request framing is decoded, `Transfer-Encoding` is removed from the forwarded headers, and the body
  round-trips exactly.
- A request that sets both `Content-Length` and `Transfer-Encoding` throws `InvalidDataException`.
- A buffered response is reframed with an explicit `Content-Length: <n>` and `Connection: close`, and
  `Transfer-Encoding` and `Keep-Alive` never reach the client.

## `HyperVInstanceReapingTests` (6 tests)

Behavioural, fixed clock `2026-08-08T12:00:00Z`.

- A runtime with an active lease and a running VM is retained; an expired lease is reaped.
- A powered-off runtime is reaped before its lease expires, but a newly created runtime that has not started
  yet is not.
- Legacy metadata with no lease is reaped fail-closed.
- A builder is never selected by the runtime sweep.

## `InMemoryIsolationProviderTests` (2 tests)

Behavioural, using the shipped in-memory provider.

- `Create → Started → Stop → Destroy` produces `Created`, `Running`, `Completed` termination, and `null`
  after destroy, in that order.
- Creating the same workload identity twice throws `InvalidOperationException`.

## `IsolationProviderSelectorTests` (6 tests)

Behavioural, fixed clock `2026-08-04T12:00:00Z`.

- A shared-kernel provider is rejected for every trust level: *"required isolation capabilities"*.
- An available provider with no certification is rejected: *"no active matching certification"`.
- A certification for a different guest image is rejected.
- The highest assurance certified provider wins (`remote` over `hyperv`).
- With no pinned digest, the certification's digest is discovered and surfaced on the probe.
- A preferred-but-unavailable provider throws and never falls back: the alternative's probe count stays `0`.

## `LinuxInstallationTests` (4 tests)

**Script-text assertions.** These read the scripts and assert literal strings; none of them executes a script.
No OS guard, so they run on every platform.

- `Installer_IsBoundToSupportedUbuntuAndHardwareVirtualization` — `install-office.sh` contains
  `Ubuntu 24.04 LTS`, `/sys/fs/cgroup/cgroup.controllers`, `/dev/kvm`, `group/world-writable`, and
  `Execution packages may not contain symbolic links`.
- `Configurator_ExplainsInformationalSecurityLabelsAndKeepsTokenOffCommandLine` — `configure-office.sh`
  contains `Security label: Baseline`, `This label is informational and does not disable agents`,
  `--dedicated-host`, `--accept-baseline-risk`, and does **not** contain `--enrollment-token`.
- `NativePackage_InstallsTheManualConfigurator` — `new-native-packages.sh` contains
  `/usr/sbin/csweet-configure-office`, `csweet-office_`, `deb_arch=amd64`, `deb_arch=arm64`.
- `ProductionRelease_PublishesCertifiedUbuntuDebOnly` — `Invoke-LinuxRelease.ps1` contains `--format deb` and
  does **not** contain `--format all`; `New-OfficeReleaseManifest.ps1` does **not** contain the
  `Pattern = '*.rpm'` definition.

## `MaintenanceHandoffTests` (7 cases over 2 methods)

Behavioural, over `MaintenanceWorker.ValidHandoff`.

- Only `repair` and `upgrade` are accepted; `remove` and `powershell` are refused even in an otherwise valid
  handoff.
- A handoff for another office id, another origin, or a downgraded (`http%3A`) origin is refused.
- Missing, duplicated, or non-URI-shaped values fail closed.

## `OfficeCertificateRecoveryTests` (3 cases over 2 methods)

Behavioural, with a stub `HttpMessageHandler`. The temporary identity root is deleted in `Dispose`.

- The theory runs both `expired: true` and `expired: false`; both must call `/challenge` then `/recover`,
  never send the spent enrollment receipt, sign the proof over
  `OfficeCertificateRecoveryProof.Payload(officeId, challenge)`, and persist the replacement certificate with
  its private key.
- `RejectedCertificateDoesNotOverwriteDurableIdentity` — a certificate whose private key the server does not
  hold throws `ArgumentException`, a wrong thumbprint throws `CryptographicException`, and the on-disk PFX is
  byte-identical afterwards.

This is the test named by `SEC-INV-02`.

## `OfficeCertificateTlsTests` (1 test)

Behavioural, and the only test that opens a real TLS listener on `127.0.0.1` (it also accepts the OS-specific
CNG key types through a `TestKeys` helper).

- Two successive connections over one `HttpClient` are observed by the server with the original and then the
  rotated client certificate thumbprint, proving the rotating handler selects the current lease on each
  handshake.
- The same validator still rejects the pinned certificate when chain errors are present, so rotation does not
  weaken the pin. `AllowTlsResume = false` is asserted indirectly by the two distinct handshakes.

## `OfficeMaintenanceStateTests` (2 tests)

Behavioural, temporary state root deleted in `Dispose`.

- `SetDraining(true)` writes exactly `draining` to `maintenance\drain-state`; `SetDraining(false)` writes
  exactly `ready`.
- Assignment markers create one `*.active` file per assignment; clearing one leaves one; `InitializeMaintenanceSession()`
  clears the directory.

## `OfficeTests` (10 tests)

Mixed; the source-text ones read `src/CSweet.Office.Node/OfficeWorker.cs`.

| Test | Kind | Pins |
|---|---|---|
| `NodeVersionAdvertisesCanonicalAssignmentDigestSupport` | behavioural | The assembly version is at least `0.1.0`. |
| `DevelopmentPostureRequiresExplicitOfficeConsent` | behavioural | `SecurityProfile = development` throws until `AllowDevelopmentAssignments` is set; the report then carries the profile and both flags. |
| `LocalEndpointUsesBrandedV1Identity` | behavioural | `RuntimeHostEndpointOptions.SectionName` is `CSweet:Office:RuntimeHost`, the pipe is `csweet-office-runtime-v1`, the socket path contains `csweet-office-runtime-v1.sock`. |
| `RuntimeHostAuthenticationAcceptsSignedEnvelopeOnlyOnce` | behavioural | The second validation of the same signed envelope returns `replayed-request`. |
| `WorkloadMapperRoundTripsSharedContract` | behavioural | Every runtime specification field survives `ToProtocol` → `FromProtocol`. |
| `DrainAndActiveAssignmentMarkersAreDurable` | behavioural | Marker file contents `draining` and one `*.active` file. |
| `EnrollmentTokenFileIsDeletedOnlyAfterStateIsSaved` | source text | `File.Delete(enrollmentTokenPath)` appears after `await stateStore.SaveAsync(state, cancellationToken)`, and the old `File.Delete(tokenPath)` form is absent. |
| `EnrollmentNormalizesCertificateExpirationToUtcAndReportsInvalidServerResponses` | source text | `certificate.NotAfter.ToUniversalTime()`, `returned HTTP {(int)response.StatusCode}`, and `details.Length > 512`. |
| `WorkloadTunnelIsOpenedBeforeWaitingForGuestPayload` | source text | Inside `RelayGuestChannelAsync`, the opening frame (`Sequence = 0`, `Content = …ByteString.Empty`, `Completed = false`) is written before `var upload = Task.Run`, and `long sequence = 1` starts the guest frames. |
| `DownloadedArtifactIsClosedBeforeHashVerificationAndCommit` | behavioural | The temporary download file is gone and the destination holds the verified bytes. |

## `OfficeWorkerFailureTests` (3 tests)

Behavioural, over `OfficeWorker.DescribeExecutionFailure`.

- A `FailedPrecondition` gRPC status yields `headquarters-broker-rejected`, keeps `guest broker protocol`
  in the detail, and strips the embedded `\0`.
- An `InvalidOperationException("password=do-not-leak")` yields `office-error`, never contains
  `do-not-leak`, and tells the operator to use the assignment identifier.
- `IsolationUnavailableException("Hyper-V is not enabled.")` yields `isolation-provider-unavailable` with the
  message unchanged.

## `PlatformIsolationBackendTests` (3 tests)

Behavioural, no OS guard: each backend is constructed with empty options and must fail closed.

- `HyperV_FailsClosedWithoutInstalledHelperAndCertification`,
  `Firecracker_FailsClosedOnNonLinuxOrMissingHelper`, and
  `AppleVirtualization_FailsClosedOnNonMacOrMissingHelper` each assert `IsAvailable == false` and
  `Certification == null`.

## `PlatformRuntimePayloadManifestTests` (3 tests)

Behavioural; temporary package root deleted in `Dispose`.

- A valid manifest applies only the five declared settings (`HelperExecutablePath`, `HelperExecutableDigest`,
  `GuestImagePath`, `CertificationSuiteVersion`, `BrokerProtocolVersion`), with the digest recomputed as
  `sha256:`.
- Appending one byte to a declared file makes `ApplyIfConfigured` throw `InvalidDataException`.
- A manifest for a different provider is rejected, and rewriting `helperExecutable` to `../provider-helper`
  is rejected as a traversal.

## `RuntimeHostAuthenticationTests` (5 tests)

Behavioural, fixed clock `2026-08-04T12:00:00Z`.

- A signed envelope validates once, then returns `replayed-request`.
- Changing the body after signing returns `invalid-signature`.
- Signing five minutes earlier returns `expired-request`.
- `LoadSharedKeyFileIfNeeded` reads a bounded, non-reparse key file, stores the absolute path, and records the
  key verbatim.
- A key file created after start-up is picked up on the next signature: the first `Sign` throws
  `InvalidDataException` while the file is missing, and the next one succeeds after the file appears.

## `RuntimeHostProtocolMapperTests` (5 tests)

Behavioural.

- A runtime specification round-trips field by field.
- An artifact digest that disagrees with the broker lease throws `ArgumentException` when mapping to the wire.
- A builder repository URL containing embedded credentials (`https://token@…`) throws `InvalidDataException`
  when mapping from the wire.
- A toolchain specification round-trips every bound and identity field.
- A dependency registry host expressed as `https://registry.npmjs.org:443` throws `InvalidDataException`.

## `RuntimeHostRpcIntegrationTests` (9 tests)

Behavioural. Three tests guard Windows-only behaviour with `if (!OperatingSystem.IsWindows()) return;`
(`WindowsPipeSecurity_GrantsExactDuplexClientConnectionRights` additionally carries
`[SupportedOSPlatform("windows")]`). No external process; transports are a real named pipe or Unix socket and
in-memory streams.

- `ProbeFailsClosedWhenProviderHasNoGuestChannelConnector` — the probe is unavailable, the reason contains
  `guest-channel connector`, and `CertificationJson` is empty.
- `WindowsPipeSecurity_GrantsExactDuplexClientConnectionRights` — the pipe DACL contains exactly one allow
  rule for the built-in users SID with `ReadWrite \| Synchronize \| CreateNewInstance`. This is the test named
  by `SEC-INV-12`.
- `ClientAndServer_AuthenticateAndDispatchTypedLifecycle` — pin trust, create, start, inspect (`Running`),
  stop, destroy, inspect (`null`) over a real transport with a shared key of 32 zero bytes.
- `BackendFailureReturnsCorrelatedTypedErrorInsteadOfClosingPipe` — the client sees `provider-create-failed`
  and `Diagnostic request:` rather than a dropped connection.
- `IsolationFailurePreservesSanitizedProviderDiagnostic` — the response carries `provider-unavailable` and the
  exact text `Platform helper rejected the operation (hyperv-command-failed): New-VM permission denied.`
- `DispatcherSurfacesSignedAuthorizationRejectionWithCorrelatedDiagnostic` — appending one space to the
  specification JSON yields `authorization-rejected` with a correlated diagnostic.
- `PrivilegedAuthorizationRejectsReplayAndProviderSubstitution` — the first commit succeeds, the replay
  throws, a provider substitution throws, and a mismatched workload id throws with
  `identifier does not match`.
- `PrivilegedAuthorizationRejectsTamperingExpiryAndTrustReplacement` — a tampered specification, an expired
  window, and a re-pin with a different key all throw.
- `PrivilegedLifecycleRejectsForgedAndExpiredProviderHandles` — forged and expired handles are refused while
  termination stays permitted.

## `ToolchainGuestSecurityTests` (5 tests)

Behavioural; creates and deletes temporary directories.

- `OfflineSourceUsesOnlyExactBrokerBuildReference` — the broker scheme resolves only for the exact build id
  and commit; a query string, a fragment, a different commit, a different build id, or an `https` URL throw
  `InvalidDataException`.
- `ExistingGitHubSourceRemainsAnExactArchive` — a GitHub URL resolves to
  `https://codeload.github.com/owner/repo/zip/<commit>`.
- `SourceExtractionRejectsTraversal` — an archive entry `repo/../escape.txt` throws and nothing is written
  outside the destination.
- `SourceExtractionRejectsExpandedByteOverflow` — 2048 expanded bytes with a 1024-byte limit throws.
- `TrustedFlatSourceArchivePreservesRepositoryRootFiles` — with `flatLayout: true`, `package.json` and
  `src/game.ts` land at the destination root.

## `WindowsHyperVOnboardingTests` (39 tests)

The largest class. Three tests call external processes or the Windows platform; the rest are behavioural over
pure helpers or **script-text assertions** against the Windows scripts and helper sources.

| Test | Kind | Pins |
|---|---|---|
| `HyperVPowerShellDiagnostic_DecodesCliXmlErrorText` | behavioural | `PowerShellHyperV.Sanitize` strips `#< CLIXML` framing and returns plain text. |
| `HyperVPowerShellDiagnostic_PreservesBoundedUnderlyingDeviceReason` | behavioural | The underlying device reason survives sanitization. |
| `HyperVPowerShellDiagnostic_UsesPlainTextForRedirectedErrors` | external process, Windows-only early return | `PowerShellHyperV.RunAsync("throw 'csweet-hyperv-diagnostic-test'")` surfaces the message from a real `powershell.exe`. |
| `SupportedEdition_IsExplicit` (7 cases) | behavioural | Professional, ProfessionalWorkstation, Enterprise, Education, ServerStandard are supported; Core and Home are not. |
| `FeatureStateParser_IsBoundedToKnownStates` (5 cases) | behavioural | `State : Enabled/Disabled/Enable Pending/Disable Pending` map to the matching enum; anything else is `Unknown`. |
| `RestartPending_BlocksOnlyHyperVRelevantRestart` (5 cases) | behavioural | Only a pending-enable Hyper-V feature blocks on restart. |
| `HelperArguments_RejectUnknownOperations` / `_AcceptOnlyTypedLifecycleOperation` / `_AcceptBoundedWorkloadReapingOperation` | behavioural | Hyper-V helper argument parsing accepts the eight operations and rejects unknown ones. |
| `LinuxVsockServiceId_UsesMicrosoftHyperVGuidTemplate` | behavioural | Port 2761 maps to `00000ac9-facb-11e6-bd58-64006a7986d3`. |
| `HyperVSocketServiceRegistration_UsesUnbracedRegistryKeyName` | behavioural | The service key path is unbraced; the legacy path is braced. |
| `LinuxVsockNativeAddress_MatchesSockAddrVmLayout` | behavioural | `Marshal.SizeOf<LinuxSockAddrVm>() == 16`. |
| `LinuxVsockAcceptedHandle_UsesIndependentSynchronousStreams` | behavioural | The accepted connection exposes two distinct, synchronous streams. |
| `GuestService_KeepsScratchMountInBrokerProcess` | script text | `build/windows-hyperv/provision-guest.sh` contains `exec /usr/lib/csweet/guest/CSweet.Office.RuntimeGuest` and `ExecStart=/usr/lib/csweet/prepare-runtime.sh`, and contains no `ExecStartPre=`. |
| `RuntimeHostInstaller_DoesNotUseScForQuotedServiceExecutablePath` | script text | The installer creates services with `New-Service` and reconfigures them with `Invoke-CimMethod … Change`, uses `NT SERVICE\…` accounts, applies the key and node ACLs **after** registering the services, configures 86400-second failure resets with `restart/5000/restart/15000/none/0`, pre-creates the event-log source, enforces the `0.1.0.0` minimum Office version, writes `control-plane-trust.json`, grants the virtual machines SID `R` only, and never uses `Invoke-Sc @('create'` or `@('config'`. |
| `PayloadGeneratorRejectsPublishedNodeBeforeSignedAssignmentFix` | script text | `New-CSweetWindowsRuntimePayload.ps1` contains `$publishedNodeVersion -lt [Version]'0.1.0.0'` and `officeVersion = $publishedNodeVersion.ToString(3)`. |
| `OfficeInstallerPromptsForApplicationScopedPrivateCertificateTrust` | script text | The installer probes the certificate with `--probe-control-plane-certificate`, waits `WaitForExit(20000)`, explains `Rebuild the Office payload`, prompts `Trust this certificate only for C-Sweet Office?`, forwards `-ControlPlaneCertificateSha256`, and never writes to `Cert:\LocalMachine\Root`. |
| `AssistedConfigurator_IsRegisteredAndRunsHiddenWithoutTypedTrustOrTokens` | script text | The MSI registers `csweet-office` protocol handling, the configurator runs hidden, and it deletes the transient token file (`File.Delete(tokenPath)`). |
| `AssistedInstaller_MapsAllocationAndDeletesTransientEnrollmentMaterial` | script text | The wrapper and the runtime installer both carry `EnrollmentTokenInputPath`, `AssistedSetupSessionId`, and the four allocation settings. |
| `UpgradeProbeDistinguishesPreservedStateFromExecutingWork` | external process, Windows-only early return | Runs `scripts/tests/Test-OfficeUpgradeProbe.ps1` and requires exit code `0` within 30 seconds. |
| `RecoveryProbe_RejectsActiveWorkAndUnprotectedInstallations` | script text | `Get-CSweetOfficeRecoveryState.ps1` consults `active-assignments`, `authorized-workload-handles.json`, and `Get-VM`, validates the content root with `Test-WithinRoot`, and can return `unsafe`. |
| `Reconnect_ClearsMutableTrustButPreservesVersionedRuntimeContent` | script text | Reconnect deletes `node`, `authorization`, `artifact-media`, `hyperv`, and `runtime-host.key`, throws `[existing_office_active]` and `[reconnect_unsafe]`, never removes the install root, and enforces the drain gate (`SEC-INV-18`). |
| `RecoveryRemoval_IsStagedAndSupportsRegisteredMsiAndDevelopmentInstalls` | script text | `CSWEET_FORCE_REMOVE`, `Value="[ProductCode]"`, `Wait-Process -Id $ParentProcessId`, `msiexec.exe`, the `-Force -Elevated` call, `local-sessions/removal-complete`, the pinned-certificate helper, restoration of `ServerCertificateValidationCallback` (and never assigning `$null` to it), the `Software\Classes\csweet-office` key, progress parameters, and the legacy `CSweet.SatelliteOffice.*` cleanup. |
| `RuntimeHostInstaller_SkipsAlreadyInstalledContentByDigest` / `_UsesUnbracedHyperVSocketRegistration` / `_UnregistersOnlyLegacyRootHyperVVmsBeforeDeletingTheirFiles` | script text | Digest-matched files are skipped; the vSock key is unbraced; legacy VMs are stopped and removed only after their files are known to be inside `%ProgramData%\CSweet\AgentRuntime`. |
| `DeveloperBootstrap_ResolvesOnlyConfiguredGuidedSetupScript` | behavioural | The bootstrap resolves only the configured script next to itself and refuses other locations. |
| `DeveloperBootstrap_PreservesTheFailingPhaseAndIdentifiesDependencyDownloads` | script text | The failing phase name is preserved and dependency downloads are named in the failure. |
| `AccessRepair_ResolvesOnlyBundledSiblingScript` / `_ValidatesInstalledServicePathAndUpdatesOnlyAccessConfiguration` | script text | Repair resolves only the bundled sibling script, validates the installed path, re-applies the guest-image grant with `S-1-5-83-0`, and never removes VMs. |
| `HyperVHelper_CopiesPrivateArtifactMediaIntoThePerVmDirectoryAndReverifiesIt` | source text | `HyperVHelperController.cs` copies to `artifact.iso` with `File.Copy(…, overwrite: false)` and re-verifies the digest of the copy before attaching. |
| `RuntimeHostStartDiagnostic_RequiresElevationAndValidatesMicrosoftProcessMonitor` | script text | Elevation, the Process Monitor URL, `Get-AuthenticodeSignature`, `O=Microsoft Corporation`, the `sc.exe start` probe, `'ACCESS DENIED'`, `/Terminate`, and `Wait-ForUnlockedFile`. |
| `Uninstaller_RemovesRuntimeHostHyperVPrivilegeBeforeDeletingService` | script text | `Remove-LocalGroupMember` precedes `sc.exe delete`, `S-1-5-32-578` and `NT SERVICE\$runtimeHostServiceName` are present, and VM/disk cleanup uses `Get-VMHardDiskDrive`, `Remove-VM -Force`, and `Dismount-VHD`. |
| `ProgressStore_ReadsLatestValidatedProvisioningProgress` | behavioural | A valid progress JSON is read back with job id, `Running`, percentage, and both estimate bounds. |
| `ProgressStore_OnlyResumesRunningWorkAcrossApplicationRestart` (4 cases) | behavioural | Only `running` resumes; `restart-required`, `completed`, and `failed` do not. |
| `ProgressStore_DoesNotReplayTerminalHistoryInNewApplicationProcess` | behavioural | Terminal history is not replayed for a new process id. |
| `Provisioner_StaleLegacyProgressBecomesRetryable` / `_RunningOwnerProcessKeepsProgressActive` / `_ExpectedPhaseWindowExpires` | behavioural | Owner-process and phase-window rules for the provisioning state machine. |

## `WorkspaceEnvironmentTests` (6 cases over 2 methods)

Behavioural, over `GuestWorkloadSupervisor.IsAllowedEnvironmentKey`.

- The three workspace limits `CSWEET_WORKSPACE_MAXIMUM_ARCHIVE_BYTES`,
  `CSWEET_WORKSPACE_MAXIMUM_EXPANDED_BYTES`, and `CSWEET_WORKSPACE_MAXIMUM_FILE_COUNT` are accepted.
- `PATH`, `LD_PRELOAD`, and an arbitrary `CSWEET_WORKSPACE_*` key are rejected.

This is the test named by `SEC-INV-21`.

## Upgrade probe harness

`scripts/tests/Test-OfficeUpgradeProbe.ps1` is the PowerShell harness behind
`WindowsHyperVOnboardingTests.UpgradeProbeDistinguishesPreservedStateFromExecutingWork`, which runs it as a
child process and requires exit code `0`. The script can also be run by hand.

**What it stubs.** Before invoking the probe it defines PowerShell functions that shadow the cmdlets the probe
uses: `Get-Service` (always returns a service object), `Get-CimInstance` (returns a service `PathName` whose
`--contentRoot` points at the fake install root), `Get-Module` (reports the Hyper-V module as available),
`Import-Module` (no-op), `Get-VM` (returns `$global:officeProbeTestVms`), and `Get-VMHardDiskDrive` (returns
nothing). Everything else is real.

**What it builds.** A temporary tree `%TEMP%\office-probe-test-<guid>` containing `install\appsettings.json`
(with `Node:StateDirectory` and `RuntimeHost:Authorization:StateDirectory` pointing into the fake data root),
`data\node\maintenance\drain-state`, `data\authorization\authorized-workload-handles.json`, and
`data\hyperv\`. The probe is then called through a local `Assert-State` helper that passes `-ForUpgrade:$true`
unless a scenario asks otherwise.

**The eight scenarios**, in order:

| # | Setup | Expected |
|---|---|---|
| 1 | Drained, a saved (`Off` → `Saved`) `CSweet-Runtime-<instance>` VM, and a `hyperv-gen2` handle record | `clean` with `-ForUpgrade` |
| 2 | The same tree, evaluated without `-ForUpgrade` | `active` |
| 3 | The VM state changed to `Running` | `active` |
| 4 | The VM state changed to `Saved` | `active` |
| 5 | VM list emptied | `clean` |
| 6 | An `*.active` marker added | `active` |
| 7 | Drain state set to `ready` | `active` |
| 8 | The handle record replaced with an unrecognized provider id | `unsafe` |

**How to run it** (from the repository root):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\tests\Test-OfficeUpgradeProbe.ps1
```

**Expected success line:** `Passed 8 upgrade probe scenarios.`, exit code `0`. Any other state throws with
`Expected <expected>, got <actual>. Last error: <error>` and a non-zero exit code. The `finally` block deletes
the temporary tree after verifying that it is inside the temp directory.

## Invariant coverage

The `Pinned by` references from [20-security/11-security-invariants.md](../20-security/11-security-invariants.md)
are reproduced below. "Exercised" lists tests that touch the invariant's code path without the invariant page
naming them; it is descriptive, not a declaration.

| Invariant | Pinned by (declared) | Also exercised |
|---|---|---|
| `SEC-INV-01` | none declared | `RuntimeHostRpcIntegrationTests.PrivilegedAuthorizationRejectsTamperingExpiryAndTrustReplacement` (re-pin with a different key throws) |
| `SEC-INV-02` | `OfficeCertificateRecoveryTests.RejectedCertificateDoesNotOverwriteDurableIdentity` | `OfficeCertificateRecoveryTests.ExpiredOrSupersededCertificateRecoversWithKeyProofAndPersistsReplacement` |
| `SEC-INV-03` | none declared | `OfficeCertificateRecoveryTests` (both methods) assert the challenge-then-recover shape and that no receipt is sent |
| `SEC-INV-04` | none declared | `OfficeCertificateTlsTests` (two distinct handshakes over a rotating handler) |
| `SEC-INV-05` | none declared | none |
| `SEC-INV-06` | none declared | none |
| `SEC-INV-07` | none declared | `RuntimeHostRpcIntegrationTests.ClientAndServer_AuthenticateAndDispatchTypedLifecycle` (only `CreateAuthorizedAsync` is used) |
| `SEC-INV-08` | none declared | `RuntimeHostRpcIntegrationTests.{PrivilegedAuthorizationRejectsReplayAndProviderSubstitution,PrivilegedAuthorizationRejectsTamperingExpiryAndTrustReplacement,DispatcherSurfacesSignedAuthorizationRejectionWithCorrelatedDiagnostic}` |
| `SEC-INV-09` | none declared | `RuntimeHostRpcIntegrationTests.PrivilegedAuthorizationRejectsReplayAndProviderSubstitution` (replay rejected) |
| `SEC-INV-10` | none declared | `IsolationProviderSelectorTests.SelectAsync_RejectsAvailableProviderWithoutCertification`, `CertifiedGuestImageRegistryTests.ResolveAsync_RejectsCertifiedImageFromAStaleGuestContract` |
| `SEC-INV-11` | none declared | `RuntimeHostAuthenticationTests`, `HyperVInstanceReapingTests`, `FirecrackerHelperSecurityTests` cover parts of the range table |
| `SEC-INV-12` | `WindowsPipeSecurity_GrantsExactDuplexClientConnectionRights` | none |
| `SEC-INV-13` | none declared | `RuntimeHostAuthenticationTests.Validate_RejectsChangedBody`, `OfficeTests.RuntimeHostAuthenticationAcceptsSignedEnvelopeOnlyOnce` |
| `SEC-INV-14` | none declared | none |
| `SEC-INV-15` | none declared | `RuntimeHostRpcIntegrationTests.PrivilegedLifecycleRejectsForgedAndExpiredProviderHandles` |
| `SEC-INV-16` | none declared | `WindowsHyperVOnboardingTests.OfficeInstallerPromptsForApplicationScopedPrivateCertificateTrust` (script text; no root store write, virtual machines SID gets `R` only) |
| `SEC-INV-17` | none declared | `WindowsHyperVOnboardingTests.RuntimeHostInstaller_DoesNotUseScForQuotedServiceExecutablePath` (asserts `Assert-NotDomainController` is present) |
| `SEC-INV-18` | `WindowsHyperVOnboardingTests` upgrade-probe scenarios, `scripts/tests/Test-OfficeUpgradeProbe.ps1` | `WindowsHyperVOnboardingTests.Reconnect_ClearsMutableTrustButPreservesVersionedRuntimeContent`, `RecoveryProbe_RejectsActiveWorkAndUnprotectedInstallations` |
| `SEC-INV-19` | none declared | none |
| `SEC-INV-20` | none declared | `WindowsHyperVOnboardingTests.HelperArguments_*`, `HyperVHelper_CopiesPrivateArtifactMediaIntoThePerVmDirectoryAndReverifiesIt` |
| `SEC-INV-21` | `WorkspaceEnvironmentTests` | none |
| `SEC-INV-22` | none declared | `GuestArtifactMaterializerTests`, `AgentArtifactMediaStoreTests`, `HyperVInstanceReapingTests` (adjacent paths only) |
| `SEC-INV-23` | none declared | none |

**Invariants with no test at all** — neither declared nor exercised by any test in this repository:
`SEC-INV-05`, `SEC-INV-06`, `SEC-INV-14`, `SEC-INV-19`, and `SEC-INV-23`. Every one of them is verified by
reading the code, and three of them (`SEC-INV-06`, `SEC-INV-19`, `SEC-INV-23`) have no test named on the
invariants page either. Changing any of the five is a change to an untested guarantee.

## Sources

`tests/CSweet.Office.Tests/*.cs` (all 25 files), `tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj`,
`scripts/tests/Test-OfficeUpgradeProbe.ps1`, `scripts/windows/Get-CSweetOfficeRecoveryState.ps1`,
`docs/20-security/11-security-invariants.md`.

Verified: 2026-09-16.
