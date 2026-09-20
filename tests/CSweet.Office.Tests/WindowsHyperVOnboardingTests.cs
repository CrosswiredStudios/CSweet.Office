using CSweet.Office.Runtime.HyperV;
using CSweet.Office.Runtime.HyperV.Helper;
using CSweet.Office.RuntimeGuest;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CSweet.Office.Tests;

public sealed class WindowsHyperVOnboardingTests
{
    [Fact]
    public void HyperVPowerShellDiagnostic_DecodesCliXmlErrorText()
    {
        const string cliXml = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
            "<Obj S=\"progress\"><MS><PR N=\"Record\"><AV>Preparing modules for first use.</AV></PR></MS></Obj>" +
            "<S S=\"Error\">Get-VMHost : You do not have the required permission._x000D__x000A_</S>" +
            "<S S=\"Error\">At line:1 char:1_x000D__x000A_</S></Objs>";

        var result = PowerShellHyperV.Sanitize(cliXml);

        Assert.Equal("Get-VMHost : You do not have the required permission.", result);
        Assert.DoesNotContain("CLIXML", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Preparing modules", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HyperVPowerShellDiagnostic_PreservesBoundedUnderlyingDeviceReason()
    {
        const string error = "Add-VMHardDiskDrive : Failed to add device 'Virtual Hard Disk'.\r\n" +
            "The virtual machine account does not have read permission to the parent disk.\r\n" +
            "At line:12 char:3\r\n+ Add-VMHardDiskDrive";

        var result = PowerShellHyperV.Sanitize(error);

        Assert.Contains("Failed to add device", result, StringComparison.Ordinal);
        Assert.Contains("does not have read permission", result, StringComparison.Ordinal);
        Assert.DoesNotContain("At line", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HyperVPowerShellDiagnostic_UsesPlainTextForRedirectedErrors()
    {
        if (!OperatingSystem.IsWindows()) return;

        var exception = await Assert.ThrowsAsync<HyperVCommandException>(() =>
            PowerShellHyperV.RunAsync("throw 'csweet-hyperv-diagnostic-test'"));

        Assert.Equal("hyperv-command-failed", exception.ErrorCode);
        Assert.Contains("csweet-hyperv-diagnostic-test", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CLIXML", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Professional", true)]
    [InlineData("ProfessionalWorkstation", true)]
    [InlineData("Enterprise", true)]
    [InlineData("Education", true)]
    [InlineData("ServerStandard", true)]
    [InlineData("Core", false)]
    [InlineData("Home", false)]
    public void SupportedEdition_IsExplicit(string edition, bool expected)
    {
        Assert.Equal(expected, WindowsHyperVHostProbe.IsSupportedEdition(edition));
    }

    [Theory]
    [InlineData("State : Enabled", WindowsOptionalFeatureState.Enabled)]
    [InlineData("State : Disabled", WindowsOptionalFeatureState.Disabled)]
    [InlineData("State : Enable Pending", WindowsOptionalFeatureState.EnablePending)]
    [InlineData("State : Disable Pending", WindowsOptionalFeatureState.DisablePending)]
    [InlineData("unexpected", WindowsOptionalFeatureState.Unknown)]
    public void FeatureStateParser_IsBoundedToKnownStates(
        string output,
        WindowsOptionalFeatureState expected)
    {
        Assert.Equal(expected, WindowsHyperVHostProbe.ParseFeatureState(output));
    }

    [Theory]
    [InlineData(WindowsOptionalFeatureState.EnablePending, false, false, true)]
    [InlineData(WindowsOptionalFeatureState.Enabled, false, true, true)]
    [InlineData(WindowsOptionalFeatureState.Enabled, true, true, false)]
    [InlineData(WindowsOptionalFeatureState.Unknown, false, true, false)]
    [InlineData(WindowsOptionalFeatureState.Disabled, false, true, false)]
    public void RestartPending_BlocksOnlyHyperVRelevantRestart(
        WindowsOptionalFeatureState featureState,
        bool hypervisorPresent,
        bool windowsRestartPending,
        bool expected)
    {
        Assert.Equal(expected, WindowsHyperVHostProbe.IsHyperVRestartPending(
            featureState,
            hypervisorPresent,
            windowsRestartPending));
    }

    [Fact]
    public void HelperArguments_RejectUnknownOperations()
    {
        var exception = Assert.Throws<HelperProtocolException>(() =>
            HelperArguments.Parse(["--protocol", "1.0", "--operation", "execute-command"]));

        Assert.Equal("invalid-arguments", exception.ErrorCode);
    }

    [Fact]
    public void HelperArguments_AcceptOnlyTypedLifecycleOperation()
    {
        var result = HelperArguments.Parse(["--protocol", "1.0", "--operation", "create"]);

        Assert.Equal("1.0", result.ProtocolVersion);
        Assert.Equal("create", result.Operation);
    }

    [Fact]
    public void HelperArguments_AcceptBoundedWorkloadReapingOperation()
    {
        var result = HelperArguments.Parse(["--protocol", "1.0", "--operation", "reap"]);

        Assert.Equal("reap", result.Operation);
    }

    [Fact]
    public void LinuxVsockServiceId_UsesMicrosoftHyperVGuidTemplate()
    {
        Assert.Equal(
            Guid.Parse("00000ac9-facb-11e6-bd58-64006a7986d3"),
            HyperVSocketTransportOptions.LinuxVsockServiceId(2761));
    }

    [Fact]
    public void HyperVSocketServiceRegistration_UsesUnbracedRegistryKeyName()
    {
        var serviceId = HyperVSocketTransportOptions.LinuxVsockServiceId(2761);

        Assert.Equal(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\00000ac9-facb-11e6-bd58-64006a7986d3",
            WindowsHyperVSocketServiceRegistration.ServiceKeyPath(serviceId));
        Assert.EndsWith(
            @"\{00000ac9-facb-11e6-bd58-64006a7986d3}",
            WindowsHyperVSocketServiceRegistration.LegacyBracedServiceKeyPath(serviceId),
            StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxVsockNativeAddress_MatchesSockAddrVmLayout()
    {
        Assert.Equal(16, Marshal.SizeOf<LinuxHyperVSocketGuestTransport.LinuxSockAddrVm>());
    }

    [Fact]
    public async Task LinuxVsockAcceptedHandle_UsesIndependentSynchronousStreams()
    {
        var path = Path.Combine(Path.GetTempPath(), $"csweet-vsock-handle-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(path, []);
            var inputHandle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                FileOptions.None);
            var outputHandle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite,
                FileOptions.None);
            await using var connection = LinuxHyperVSocketGuestTransport.OpenAcceptedConnection(
                inputHandle,
                outputHandle);

            Assert.NotSame(connection.Input, connection.Output);
            Assert.False(((FileStream)connection.Input).IsAsync);
            Assert.False(((FileStream)connection.Output).IsAsync);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GuestService_KeepsScratchMountInBrokerProcess()
    {
        var provisioningScript = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "build", "windows-hyperv", "provision-guest.sh"));

        Assert.Contains(
            "exec /usr/lib/csweet/guest/CSweet.Office.RuntimeGuest",
            provisioningScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExecStart=/usr/lib/csweet/prepare-runtime.sh",
            provisioningScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ExecStartPre=", provisioningScript, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeHostInstaller_DoesNotUseScForQuotedServiceExecutablePath()
    {
        var installer = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Install-CSweetOfficeRuntimeHost.ps1"));

        Assert.Contains("New-Service -Name $runtimeHostServiceName -BinaryPathName $runtimeHostBinaryPath", installer, StringComparison.Ordinal);
        Assert.Contains("Invoke-CimMethod -InputObject $runtimeHostServiceConfiguration -MethodName Change", installer, StringComparison.Ordinal);
        Assert.Contains("PathName = $runtimeHostBinaryPath", installer, StringComparison.Ordinal);
        Assert.Contains("StartName = \"NT SERVICE\\$runtimeHostServiceName\"", installer, StringComparison.Ordinal);
        Assert.Contains("StartName = \"NT SERVICE\\$nodeServiceName\"", installer, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(installer, "StartPassword = \\$null", RegexOptions.CultureInvariant).Count);
        Assert.Contains("Invoke-Sc @('sidtype', $runtimeHostServiceName, 'unrestricted')", installer, StringComparison.Ordinal);
        Assert.Contains("Invoke-Sc @('sidtype', $nodeServiceName, 'unrestricted')", installer, StringComparison.Ordinal);
        Assert.Contains("Grant-RuntimeHostHyperVAccess -ServiceName $serviceName", installer, StringComparison.Ordinal);
        Assert.Contains("S-1-5-32-578", installer, StringComparison.Ordinal);
        Assert.Contains("Assert-NotDomainController", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("StartName = 'LocalSystem'", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("NT AUTHORITY\\LocalService", installer, StringComparison.Ordinal);
        Assert.True(
            installer.IndexOf("New-Service -Name $runtimeHostServiceName", StringComparison.Ordinal) <
            installer.IndexOf("icacls.exe\" $keyPath", StringComparison.Ordinal));
        Assert.True(
            installer.IndexOf("New-Service -Name $nodeServiceName", StringComparison.Ordinal) <
            installer.IndexOf("icacls.exe\" $nodeDataRoot", StringComparison.Ordinal));
        Assert.Contains("'reset=', '86400'", installer, StringComparison.Ordinal);
        Assert.Contains("'actions=', 'restart/5000/restart/15000/none/0'", installer, StringComparison.Ordinal);
        Assert.Contains("function Initialize-WindowsEventLogSource", installer, StringComparison.Ordinal);
        Assert.Contains("Initialize-WindowsEventLogSource -SourceName $serviceName", installer, StringComparison.Ordinal);
        Assert.Contains("Initialize-WindowsEventLogSource -SourceName $nodeServiceName", installer, StringComparison.Ordinal);
        Assert.Contains("$nodeStatePath = Join-Path $nodeDataRoot 'node-state.json'", installer, StringComparison.Ordinal);
        Assert.Contains("$legacyNodeStatePath = Join-Path $DataRoot 'node-state.json'", installer, StringComparison.Ordinal);
        Assert.Contains("$protectedNodeStatePath = Join-Path (Join-Path $DataRoot 'node') 'node-state.json'", installer, StringComparison.Ordinal);
        Assert.Contains("Test-Path -LiteralPath $existingNodeStatePath -PathType Leaf", installer, StringComparison.Ordinal);
        Assert.Contains("elseif ($null -eq $existingNodeService)", installer, StringComparison.Ordinal);
        Assert.Contains("$existingNodeConfiguration = Get-OptionalObjectProperty $existingOfficeConfiguration 'Node'", installer, StringComparison.Ordinal);
        Assert.Contains("$null -eq $existingNodeConfiguration", installer, StringComparison.Ordinal);
        Assert.Contains("$config.CSweet.Office.Node = $existingNodeConfiguration", installer, StringComparison.Ordinal);
        Assert.Contains("elseif ($null -ne $existingNodeConfiguration)", installer, StringComparison.Ordinal);
        Assert.Contains("The existing Office content root is outside the protected install directory.", installer, StringComparison.Ordinal);
        Assert.Contains("did not enroll within 60 seconds", installer, StringComparison.Ordinal);
        Assert.Contains("Generate a new connection code in C-Sweet", installer, StringComparison.Ordinal);
        Assert.Contains("Office enrollment failed", installer, StringComparison.Ordinal);
        Assert.Contains("$minimumOfficeVersion = [Version]'0.1.0.0'", installer, StringComparison.Ordinal);
        Assert.Contains("predates privileged signed-assignment enforcement", installer, StringComparison.Ordinal);
        Assert.Contains("control-plane-trust.json", installer, StringComparison.Ordinal);
        Assert.Contains("ControlPlaneTrustFilePath = $controlPlaneTrustPath", installer, StringComparison.Ordinal);
        Assert.Contains("*$nodeServiceSid`:R", installer, StringComparison.Ordinal);
        Assert.Contains("*$runtimeHostServiceSid`:R", installer, StringComparison.Ordinal);
        Assert.Contains("$hyperVDataRoot '/inheritance:r'", installer, StringComparison.Ordinal);
        Assert.Contains("$nodeDataRoot '/inheritance:r'", installer, StringComparison.Ordinal);
        Assert.Contains("*$RuntimeHostSid`:(OI)(CI)RX", installer, StringComparison.Ordinal);
        Assert.Contains("*$NodeSid`:(OI)(CI)RX", installer, StringComparison.Ordinal);
        Assert.Contains("Set-ProtectedPackageAcl -Root $versionRoot", installer, StringComparison.Ordinal);
        Assert.Contains("Grant-HyperVGuestImageReadAccess -GuestImagePath $guestImage", installer, StringComparison.Ordinal);
        Assert.Contains("$virtualMachinesSid = 'S-1-5-83-0'", installer, StringComparison.Ordinal);
        Assert.Contains("\"*$virtualMachinesSid`:R\"", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("\"*$virtualMachinesSid`:M\"", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("\"*$virtualMachinesSid`:F\"", installer, StringComparison.Ordinal);
        Assert.Contains("\"*$RuntimeHostSid`:RX\" \"*$NodeSid`:RX\"", installer, StringComparison.Ordinal);
        Assert.Contains("Assert-FileReadExecuteAce -Path $runtimeHostExe", installer, StringComparison.Ordinal);
        Assert.Contains("Assert-FileReadExecuteAce -Path $officeExe", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Sc @('create'", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Sc @('config'", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadGeneratorRejectsPublishedNodeBeforeSignedAssignmentFix()
    {
        var generator = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "New-CSweetWindowsRuntimePayload.ps1"));

        Assert.Contains("$publishedNodeVersion -lt [Version]'0.1.0.0'", generator, StringComparison.Ordinal);
        Assert.Contains("officeVersion = $publishedNodeVersion.ToString(3)", generator, StringComparison.Ordinal);
    }

    [Fact]
    public void OfficeInstallerPromptsForApplicationScopedPrivateCertificateTrust()
    {
        var installer = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Install-CSweetOffice.ps1"));
        var runtimeInstaller = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Install-CSweetOfficeRuntimeHost.ps1"));

        Assert.Contains("Resolve-ControlPlaneCertificateSha256", runtimeInstaller, StringComparison.Ordinal);
        Assert.Contains("--probe-control-plane-certificate", runtimeInstaller, StringComparison.Ordinal);
        Assert.Contains("WaitForExit(20000)", runtimeInstaller, StringComparison.Ordinal);
        Assert.Contains("Rebuild the Office payload", runtimeInstaller, StringComparison.Ordinal);
        Assert.Contains("The control-plane certificate does not match host", runtimeInstaller, StringComparison.Ordinal);
        Assert.Contains("Trust this certificate only for C-Sweet Office?", runtimeInstaller, StringComparison.Ordinal);
        Assert.Contains("-ControlPlaneCertificateSha256", installer, StringComparison.Ordinal);
        Assert.Contains("$NonInteractive", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Cert:\\LocalMachine\\Root", installer + runtimeInstaller, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssistedConfigurator_IsRegisteredAndRunsHiddenWithoutTypedTrustOrTokens()
    {
        var root = RepositoryRoot();
        var msi = File.ReadAllText(Path.Combine(root, "scripts", "windows", "New-CSweetOfficeMsi.ps1"));
        var configurator = File.ReadAllText(Path.Combine(
            root, "src", "CSweet.Office.Configurator", "Program.cs"));

        Assert.Contains("Software\\Classes\\csweet-office", msi, StringComparison.Ordinal);
        Assert.Contains("URL Protocol", msi, StringComparison.Ordinal);
        Assert.Contains("CSweet.Office.Configurator.exe", msi, StringComparison.Ordinal);
        Assert.Contains("signtool failed to sign the MSI", msi, StringComparison.Ordinal);
        Assert.Contains("signature could not be verified", msi, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Verb = \"runas\"", configurator, StringComparison.Ordinal);
        Assert.Contains("CreateNoWindow = true", configurator, StringComparison.Ordinal);
        Assert.Contains("local-sessions/redeem", configurator, StringComparison.Ordinal);
        Assert.Contains("local-sessions/preflight", configurator, StringComparison.Ordinal);
        Assert.True(configurator.IndexOf("local-sessions/preflight", StringComparison.Ordinal) <
            configurator.IndexOf("local-sessions/redeem", StringComparison.Ordinal));
        Assert.Contains("redemption.ExistingInstallationAction", configurator, StringComparison.Ordinal);
        Assert.Contains("redemption.SetupReceipt", configurator, StringComparison.Ordinal);
        Assert.Contains("icacls.exe", configurator, StringComparison.Ordinal);
        Assert.DoesNotContain("Read-Host", configurator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUST", configurator, StringComparison.Ordinal);
    }

    [Fact]
    public void AssistedInstaller_MapsAllocationAndDeletesTransientEnrollmentMaterial()
    {
        var root = RepositoryRoot();
        var wrapper = File.ReadAllText(Path.Combine(root, "scripts", "windows", "Install-CSweetOffice.ps1"));
        var runtime = File.ReadAllText(Path.Combine(root, "scripts", "windows", "Install-CSweetOfficeRuntimeHost.ps1"));
        var configurator = File.ReadAllText(Path.Combine(
            root, "src", "CSweet.Office.Configurator", "Program.cs"));

        Assert.Contains("EnrollmentTokenInputPath", wrapper, StringComparison.Ordinal);
        Assert.Contains("AssistedSetupSessionId", wrapper, StringComparison.Ordinal);
        Assert.Contains("AllocatableCpuCount", wrapper + runtime, StringComparison.Ordinal);
        Assert.Contains("AllocatableMemoryMb", wrapper + runtime, StringComparison.Ordinal);
        Assert.Contains("AllocatableDiskMb", wrapper + runtime, StringComparison.Ordinal);
        Assert.Contains("MaximumConcurrentWorkloads", wrapper + runtime, StringComparison.Ordinal);
        Assert.Contains("File.Delete(tokenPath)", configurator, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpgradeProbeDistinguishesPreservedStateFromExecutingWork()
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(RepositoryRoot(), "scripts", "tests", "Test-OfficeUpgradeProbe.ps1") })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await errors);
    }
    [Fact]
    public void RecoveryProbe_RejectsActiveWorkAndUnprotectedInstallations()
    {
        var probe = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Get-CSweetOfficeRecoveryState.ps1"));

        Assert.Contains("active-assignments", probe, StringComparison.Ordinal);
        Assert.Contains("authorized-workload-handles.json", probe, StringComparison.Ordinal);
        Assert.Contains("Get-VM", probe, StringComparison.Ordinal);
        Assert.Contains("Test-WithinRoot $serviceContentRoot $InstallRoot", probe, StringComparison.Ordinal);
        Assert.Contains("'unsafe'", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconnect_ClearsMutableTrustButPreservesVersionedRuntimeContent()
    {
        var installer = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Install-CSweetOfficeRuntimeHost.ps1"));

        Assert.Contains("ExistingInstallationAction -eq 'reconnect'", installer, StringComparison.Ordinal);
        Assert.Contains("'[existing_office_active]", installer, StringComparison.Ordinal);
        Assert.Contains("'[reconnect_unsafe]", installer, StringComparison.Ordinal);
        Assert.Contains("(Join-Path $DataRoot 'authorization')", installer, StringComparison.Ordinal);
        Assert.Contains("(Join-Path $DataRoot 'artifact-media')", installer, StringComparison.Ordinal);
        Assert.Contains("(Join-Path $DataRoot 'hyperv')", installer, StringComparison.Ordinal);
        Assert.Contains("$runtimeKey = Join-Path $DataRoot 'runtime-host.key'", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-LegacyDirectory $InstallRoot", installer, StringComparison.Ordinal);
        Assert.Contains("$ExistingInstallationAction -ne 'reconnect'", installer, StringComparison.Ordinal);
        Assert.Contains("Drain this node in C-Sweet", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryRemoval_IsStagedAndSupportsRegisteredMsiAndDevelopmentInstalls()
    {
        var root = RepositoryRoot();
        var msi = File.ReadAllText(Path.Combine(root, "scripts", "windows", "New-CSweetOfficeMsi.ps1"));
        var helper = File.ReadAllText(Path.Combine(root, "scripts", "windows", "Remove-CSweetOfficeForRecovery.ps1"));
        var uninstaller = File.ReadAllText(Path.Combine(root, "scripts", "windows", "Uninstall-CSweetOffice.ps1"));

        Assert.Contains("CSWEET_FORCE_REMOVE", msi, StringComparison.Ordinal);
        Assert.Contains("Value=\"[ProductCode]\"", msi, StringComparison.Ordinal);
        Assert.Contains("Wait-Process -Id $ParentProcessId", helper, StringComparison.Ordinal);
        Assert.Contains("msiexec.exe", helper, StringComparison.Ordinal);
        Assert.Contains("& $UninstallScript -Force -Elevated", helper, StringComparison.Ordinal);
        Assert.Contains("local-sessions/removal-complete", helper, StringComparison.Ordinal);
        Assert.Contains("function Invoke-CSweetPinnedRemovalRequest", helper, StringComparison.Ordinal);
        Assert.Contains("ServerCertificateValidationCallback = $previousCallback", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerCertificateValidationCallback = $null", helper, StringComparison.Ordinal);
        Assert.Contains("Software\\Classes\\csweet-office", uninstaller, StringComparison.Ordinal);
        Assert.Contains("[string] $ProgressPath", uninstaller, StringComparison.Ordinal);
        Assert.Contains("[guid] $ProgressJobId", uninstaller, StringComparison.Ordinal);
        Assert.Contains("[string] $ProgressWorkflow", uninstaller, StringComparison.Ordinal);
        Assert.Contains("Write-OfficeRemovalProgress", uninstaller, StringComparison.Ordinal);
        Assert.Contains("CSweet.SatelliteOffice.Node", uninstaller, StringComparison.Ordinal);
        Assert.Contains("CSweet.SatelliteOffice.RuntimeHost", uninstaller, StringComparison.Ordinal);
        Assert.Contains("CSweet\\SatelliteOffice", uninstaller, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeHostInstaller_SkipsAlreadyInstalledContentByDigest()
    {
        var installer = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Install-CSweetOfficeRuntimeHost.ps1"));

        Assert.Contains("Get-FileHash -LiteralPath $destination -Algorithm SHA256", installer, StringComparison.Ordinal);
        Assert.Contains("if ($installedHash -ceq [string]$file.sha256)", installer, StringComparison.Ordinal);
        Assert.Contains("continue", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeHostInstaller_UsesUnbracedHyperVSocketRegistration()
    {
        var installer = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Install-CSweetOfficeRuntimeHost.ps1"));

        Assert.Contains(
            "$serviceId = '00000ac9-facb-11e6-bd58-64006a7986d3'",
            installer,
            StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $legacyServiceRegistryPath", installer, StringComparison.Ordinal);
        Assert.Contains("AllowedClientSid = $ControlPlaneUserSid", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeHostInstaller_UnregistersOnlyLegacyRootHyperVVmsBeforeDeletingTheirFiles()
    {
        var installer = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Install-CSweetOfficeRuntimeHost.ps1"));

        Assert.Contains("function Test-PathWithinRoot", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("$legacyAgentRuntimeRoot))", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-LegacyDirectory $legacyAgentRuntimeRoot", installer, StringComparison.Ordinal);
        Assert.Contains("function Remove-LegacyHyperVResources", installer, StringComparison.Ordinal);
        Assert.Contains("Get-VMHardDiskDrive -VM $vm", installer, StringComparison.Ordinal);
        Assert.Contains("Stop-VM -VM $vm -TurnOff -Force", installer, StringComparison.Ordinal);
        Assert.Contains("Remove-VM -VM $vm -Force", installer, StringComparison.Ordinal);
        Assert.Contains("$legacyAgentRuntimeRoot = \"$env:ProgramData\\CSweet\\AgentRuntime\"", installer, StringComparison.Ordinal);
        Assert.True(
            installer.IndexOf("Remove-LegacyHyperVResources $legacyAgentRuntimeRoot", StringComparison.Ordinal) <
            installer.IndexOf("Remove-LegacyDirectory $legacyRoot", StringComparison.Ordinal));
    }

    [Fact]
    public void DeveloperBootstrap_ResolvesOnlyConfiguredGuidedSetupScript()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"csweet-windows-bootstrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Initialize-CSweetWindowsIsolationTest.ps1");
        File.WriteAllText(path, "# test bootstrap");
        File.WriteAllText(Path.Combine(directory, "CSweet.WindowsSetupProgress.ps1"), "# test progress helper");
        var original = Environment.GetEnvironmentVariable(
            WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable,
                path);

            Assert.True(WindowsRuntimeHostProvisioner.TryResolveDeveloperBootstrap(out var resolved));
            Assert.Equal(Path.GetFullPath(path), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable,
                original);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DeveloperBootstrap_PreservesTheFailingPhaseAndIdentifiesDependencyDownloads()
    {
        var bootstrap = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Initialize-CSweetWindowsIsolationTest.ps1"));

        Assert.Contains("$failurePhaseKey = [string]$previousProgress.phaseKey", bootstrap, StringComparison.Ordinal);
        Assert.Contains("$failurePhaseDisplayName = [string]$previousProgress.phaseDisplayName", bootstrap, StringComparison.Ordinal);
        Assert.Contains("-State failed -PhaseKey $failurePhaseKey", bootstrap, StringComparison.Ordinal);

        // The guest-builder module lives in the sibling CSweet.Isolation checkout, which is a
        // local-dev-only companion: GitHub Actions must never depend on sibling checkouts.
        // Skip these assertions when the sibling is absent; local devs with the sibling cloned
        // still get drift detection.
        var guestBuilderPath = Path.Combine(
            RepositoryRoot(), "..", "CSweet.Isolation", "tools", "LinuxImage", "CSweet.LinuxImage.psm1");
        if (!File.Exists(guestBuilderPath)) return;
        var guestBuilder = File.ReadAllText(guestBuilderPath);
        Assert.Contains("function Invoke-CSweetDownload", guestBuilder, StringComparison.Ordinal);
        Assert.Contains("The published HashiCorp Packer checksums", guestBuilder, StringComparison.Ordinal);
        Assert.Contains("The pinned HashiCorp Packer archive", guestBuilder, StringComparison.Ordinal);
        Assert.Contains("The published Ubuntu image checksums", guestBuilder, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessRepair_ResolvesOnlyBundledSiblingScript()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"csweet-windows-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var bootstrap = Path.Combine(directory, "Initialize-CSweetWindowsIsolationTest.ps1");
        var repair = Path.Combine(directory, "Repair-CSweetOfficeRuntimeHostAccess.ps1");
        File.WriteAllText(bootstrap, "# test bootstrap");
        File.WriteAllText(repair, "# test repair");
        File.WriteAllText(Path.Combine(directory, "CSweet.WindowsSetupProgress.ps1"), "# test progress helper");
        var original = Environment.GetEnvironmentVariable(
            WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable,
                bootstrap);

            Assert.True(WindowsRuntimeHostProvisioner.TryResolveAccessRepair(out var resolved));
            Assert.Equal(Path.GetFullPath(repair), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable,
                original);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AccessRepair_ValidatesInstalledServicePathAndUpdatesOnlyAccessConfiguration()
    {
        var repair = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Repair-CSweetOfficeRuntimeHostAccess.ps1"));

        Assert.Contains("Resolve-ProtectedInstalledPath", repair, StringComparison.Ordinal);
        Assert.Contains("$runtimeHostConfiguration.AllowedClientSid = $ControlPlaneUserSid", repair, StringComparison.Ordinal);
        Assert.Contains("AllowedClientSids -NotePropertyValue $requiredAllowedClientSids", repair, StringComparison.Ordinal);
        Assert.Contains("runtime-host.key", repair, StringComparison.Ordinal);
        Assert.Contains("Stop-Service -Name $serviceName", repair, StringComparison.Ordinal);
        Assert.Contains("Start-Service -Name $serviceName", repair, StringComparison.Ordinal);
        Assert.Contains("Resolve-ServiceSid", repair, StringComparison.Ordinal);
        Assert.Contains("*$runtimeHostServiceSid`:(OI)(CI)M", repair, StringComparison.Ordinal);
        Assert.Contains("Set-ProtectedPackageAcl -Root $contentRoot", repair, StringComparison.Ordinal);
        Assert.Contains("\"*$RuntimeHostSid`:RX\" \"*$NodeSid`:RX\"", repair, StringComparison.Ordinal);
        Assert.Contains("Assert-FileReadExecuteAce -Path $runtimeHostExe", repair, StringComparison.Ordinal);
        Assert.Contains("Set-ServiceVirtualAccount -ServiceName $serviceName", repair, StringComparison.Ordinal);
        Assert.Contains("Set-ServiceVirtualAccount -ServiceName $nodeServiceName", repair, StringComparison.Ordinal);
        Assert.Contains("StartName = \"NT SERVICE\\$ServiceName\"", repair, StringComparison.Ordinal);
        Assert.Contains("Stop-OrphanedOfficeProcesses -ProtectedInstallRoot $InstallRoot", repair, StringComparison.Ordinal);
        Assert.Contains("$executablePath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)", repair, StringComparison.Ordinal);
        Assert.Contains("-in $serviceProcessIds", repair, StringComparison.Ordinal);
        Assert.True(
            repair.IndexOf("Stop-Service -Name $nodeServiceName", StringComparison.Ordinal) <
            repair.IndexOf("Set-ServiceVirtualAccount -ServiceName $nodeServiceName", StringComparison.Ordinal));
        Assert.True(
            repair.IndexOf("Set-ProtectedPackageAcl -Root $contentRoot", StringComparison.Ordinal) <
            repair.IndexOf("Get-Content -LiteralPath $configurationPath", StringComparison.Ordinal));
        Assert.Contains("$configurationPath '/reset'", repair, StringComparison.Ordinal);
        Assert.Contains("$configurationPath '/inheritance:r' '/grant:r'", repair, StringComparison.Ordinal);
        Assert.True(
            repair.IndexOf("$configurationPath '/reset'", StringComparison.Ordinal) <
            repair.IndexOf("Get-Content -LiteralPath $configurationPath", StringComparison.Ordinal));
        Assert.Contains("[IO.File]::WriteAllText($configurationPath", repair, StringComparison.Ordinal);
        Assert.Contains("if ($configurationNeedsUpdate)", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("[IO.File]::Replace", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("$temporaryConfiguration", repair, StringComparison.Ordinal);
        Assert.Contains("Stop-Service -Name $nodeServiceName", repair, StringComparison.Ordinal);
        Assert.Contains("Start-Service -Name $nodeServiceName", repair, StringComparison.Ordinal);
        Assert.Contains("$restartNodeService = $true", repair, StringComparison.Ordinal);
        Assert.Contains("} catch { }\n    try {\n        $stoppedNodeService", repair.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("Grant-RuntimeHostHyperVAccess -ServiceName $serviceName", repair, StringComparison.Ordinal);
        Assert.Contains("Grant-HyperVGuestImageReadAccess -GuestImagePath $guestImagePath", repair, StringComparison.Ordinal);
        Assert.Contains("Resolve-ProtectedInstalledPath -Root $contentRoot", repair, StringComparison.Ordinal);
        Assert.Contains("$virtualMachinesSid = 'S-1-5-83-0'", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-VM", repair, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HyperVHelper_CopiesPrivateArtifactMediaIntoThePerVmDirectoryAndReverifiesIt()
    {
        var helper = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "CSweet.Office.Runtime.HyperV.Helper", "HyperVHelperController.cs"));

        Assert.Contains("Path.Combine(instanceDirectory, \"artifact.iso\")", helper, StringComparison.Ordinal);
        Assert.Contains("File.Copy(artifactImage, attachedArtifactImage, overwrite: false)", helper, StringComparison.Ordinal);
        Assert.Contains("VerifyArtifactDigestAsync(\n                        attachedArtifactImage", helper.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("attachedArtifactImage);", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeHostStartDiagnostic_RequiresElevationAndValidatesMicrosoftProcessMonitor()
    {
        var diagnostic = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Diagnose-CSweetOfficeRuntimeHostStart.ps1"));

        Assert.Contains("Assert-Administrator", diagnostic, StringComparison.Ordinal);
        Assert.Contains("https://download.sysinternals.com/files/ProcessMonitor.zip", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", diagnostic, StringComparison.Ordinal);
        Assert.Contains("O=Microsoft Corporation", diagnostic, StringComparison.Ordinal);
        Assert.Contains("sc.exe\" start 'CSweet.Office.RuntimeHost'", diagnostic, StringComparison.Ordinal);
        Assert.Contains("'ACCESS DENIED'", diagnostic, StringComparison.Ordinal);
        Assert.Contains("/Terminate", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Wait-ForUnlockedFile", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstaller_RemovesRuntimeHostHyperVPrivilegeBeforeDeletingService()
    {
        var uninstall = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "windows", "Uninstall-CSweetOffice.ps1"));

        var removeMembership = uninstall.IndexOf("Remove-LocalGroupMember", StringComparison.Ordinal);
        var deleteService = uninstall.IndexOf("sc.exe\" delete $serviceName", StringComparison.Ordinal);
        Assert.True(removeMembership >= 0);
        Assert.True(deleteService > removeMembership);
        Assert.Contains("S-1-5-32-578", uninstall, StringComparison.Ordinal);
        Assert.Contains("NT SERVICE\\$runtimeHostServiceName", uninstall, StringComparison.Ordinal);
        Assert.Contains("$protectedNodeRoot", uninstall, StringComparison.Ordinal);
        Assert.Contains("Remove-OfficeHyperVResources $hyperVDataRoot", uninstall, StringComparison.Ordinal);
        Assert.Contains("Get-VMHardDiskDrive -VM $vm", uninstall, StringComparison.Ordinal);
        Assert.Contains("Remove-VM -VM $vm -Force", uninstall, StringComparison.Ordinal);
        Assert.Contains("Dismount-VHD", uninstall, StringComparison.Ordinal);
        Assert.Contains("Remove-InstalledDirectory $path", uninstall, StringComparison.Ordinal);
        Assert.Contains("Uninstall did not completely remove", uninstall, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressStore_ReadsLatestValidatedProvisioningProgress()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"csweet-windows-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var original = Environment.GetEnvironmentVariable(
            WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                directory);
            var jobId = Guid.NewGuid();
            var startedAt = DateTimeOffset.UtcNow.AddMinutes(-4);
            var path = WindowsRuntimeHostProgressStore.CreatePath(jobId);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                jobId,
                workflow = "developer-bootstrap",
                state = "running",
                phaseKey = "build-guest",
                phaseDisplayName = "Building the hardened guest image",
                message = "Ubuntu is installing into the isolated VM.",
                percentComplete = 24,
                startedAt,
                updatedAt = DateTimeOffset.UtcNow,
                estimatedRemainingMinimumSeconds = 900,
                estimatedRemainingMaximumSeconds = 2100,
                requiresRestart = false,
                errorCode = (string?)null,
                errorMessage = (string?)null,
                ownerProcessId = Environment.ProcessId
            }));

            var progress = WindowsRuntimeHostProgressStore.ReadLatest(null);

            Assert.NotNull(progress);
            Assert.Equal(jobId, progress.JobId);
            Assert.Equal(WindowsRuntimeHostProvisioningState.Running, progress.State);
            Assert.Equal(24, progress.PercentComplete);
            Assert.Equal(900, progress.EstimatedRemainingMinimumSeconds);
            Assert.Equal(2100, progress.EstimatedRemainingMaximumSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                original);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(WindowsRuntimeHostProvisioningState.Running, true)]
    [InlineData(WindowsRuntimeHostProvisioningState.RestartRequired, false)]
    [InlineData(WindowsRuntimeHostProvisioningState.Completed, false)]
    [InlineData(WindowsRuntimeHostProvisioningState.Failed, false)]
    public void ProgressStore_OnlyResumesRunningWorkAcrossApplicationRestart(
        WindowsRuntimeHostProvisioningState state,
        bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var progress = new WindowsRuntimeHostProvisioningProgress(
            Guid.NewGuid(),
            "developer-bootstrap",
            state,
            "certify-runtime",
            "Certifying hardware isolation",
            "A disposable VM is running the certification probe.",
            70,
            now.AddMinutes(-5),
            now,
            120,
            480,
            false,
            state == WindowsRuntimeHostProvisioningState.Failed ? "certification-failed" : null,
            state == WindowsRuntimeHostProvisioningState.Failed ? "An earlier probe failed." : null,
            Environment.ProcessId);

        Assert.Equal(expected, WindowsRuntimeHostProgressStore.CanResumeAcrossApplicationRestart(progress));
    }

    [Fact]
    public void ProgressStore_DoesNotReplayTerminalHistoryInNewApplicationProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"csweet-windows-terminal-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var original = Environment.GetEnvironmentVariable(
            WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                directory);
            var jobId = Guid.NewGuid();
            var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            var path = WindowsRuntimeHostProgressStore.CreatePath(jobId);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                jobId,
                workflow = "developer-bootstrap",
                state = "failed",
                phaseKey = "certify-runtime",
                phaseDisplayName = "Certifying hardware isolation",
                message = "An earlier certification probe failed.",
                percentComplete = 71,
                startedAt,
                updatedAt = DateTimeOffset.UtcNow,
                estimatedRemainingMinimumSeconds = (int?)null,
                estimatedRemainingMaximumSeconds = (int?)null,
                requiresRestart = false,
                errorCode = "certification-failed",
                errorMessage = "An earlier probe failed.",
                ownerProcessId = Environment.ProcessId
            }));

            Assert.Null(WindowsRuntimeHostProgressStore.ReadLatest(null));
            Assert.Equal(
                WindowsRuntimeHostProvisioningState.Failed,
                WindowsRuntimeHostProgressStore.ReadLatest(path)?.State);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                original);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Provisioner_StaleLegacyProgressBecomesRetryable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"csweet-windows-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var bootstrap = Path.Combine(directory, "Initialize-CSweetWindowsIsolationTest.ps1");
        File.WriteAllText(bootstrap, "# test bootstrap");
        File.WriteAllText(Path.Combine(directory, "CSweet.WindowsSetupProgress.ps1"), "# test progress helper");
        var originalRoot = Environment.GetEnvironmentVariable(
            WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable);
        var originalBootstrap = Environment.GetEnvironmentVariable(
            WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                directory);
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable,
                bootstrap);
            var jobId = Guid.NewGuid();
            // Anchor the fixture to the actual host boot time (same clock as GetProgress):
            // startedAt must postdate boot by >1min to skip the boot-recency branch, while
            // updatedAt stays stale on the heartbeat check but fresh inside the expected
            // phase window (maxSeconds + 2min grace), so the dead-owner branch
            // ("preparation-stopped") is reached deterministically on any host, including
            // a freshly-booted CI VM.
            var now = DateTimeOffset.UtcNow;
            var bootedAt = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
            var startedAt = bootedAt.AddMinutes(2);
            if (startedAt > now.AddMinutes(-1)) startedAt = now.AddMinutes(-1);
            var updatedAt = now.Subtract(
                WindowsRuntimeHostProvisioner.LegacyProgressHeartbeatTimeout).AddSeconds(-5);
            if (updatedAt < startedAt) updatedAt = startedAt;
            File.WriteAllText(
                WindowsRuntimeHostProgressStore.CreatePath(jobId),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    jobId,
                    workflow = "developer-bootstrap",
                    state = "running",
                    phaseKey = "build-guest",
                    phaseDisplayName = "Building the hardened guest image",
                    message = "Ubuntu is installing.",
                    percentComplete = 24,
                    startedAt,
                    updatedAt,
                    estimatedRemainingMinimumSeconds = 0,
                    estimatedRemainingMaximumSeconds = 86_400,
                    requiresRestart = false,
                    errorCode = (string?)null,
                    errorMessage = (string?)null
                }));

            var provisioner = new WindowsRuntimeHostProvisioner();
            var progress = provisioner.GetProgress();
            var info = provisioner.GetProvisioningInfo();

            Assert.NotNull(progress);
            Assert.Equal(WindowsRuntimeHostProvisioningState.Failed, progress.State);
            Assert.Equal("preparation-stopped", progress.ErrorCode);
            Assert.True(info.CanLaunch);
            Assert.Equal(WindowsRuntimeHostProvisioningMode.DeveloperBootstrap, info.Mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                originalRoot);
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProvisioner.DeveloperBootstrapEnvironmentVariable,
                originalBootstrap);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Provisioner_RunningOwnerProcessKeepsProgressActive()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"csweet-windows-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var original = Environment.GetEnvironmentVariable(
            WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                directory);
            using var owner = System.Diagnostics.Process.GetCurrentProcess();
            var jobId = Guid.NewGuid();
            var ownerStartedAt = new DateTimeOffset(owner.StartTime.ToUniversalTime(), TimeSpan.Zero);
            File.WriteAllText(
                WindowsRuntimeHostProgressStore.CreatePath(jobId),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    jobId,
                    workflow = "developer-bootstrap",
                    state = "running",
                    phaseKey = "build-guest",
                    phaseDisplayName = "Building the hardened guest image",
                    message = "Ubuntu is installing.",
                    percentComplete = 30,
                    startedAt = ownerStartedAt,
                    updatedAt = DateTimeOffset.UtcNow,
                    ownerProcessId = owner.Id,
                    estimatedRemainingMinimumSeconds = 300,
                    estimatedRemainingMaximumSeconds = 1200,
                    requiresRestart = false,
                    errorCode = (string?)null,
                    errorMessage = (string?)null
                }));

            var progress = new WindowsRuntimeHostProvisioner().GetProgress();

            Assert.NotNull(progress);
            Assert.Equal(WindowsRuntimeHostProvisioningState.Running, progress.State);
            Assert.Equal(owner.Id, progress.OwnerProcessId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                WindowsRuntimeHostProgressStore.ProgressRootEnvironmentVariable,
                original);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Provisioner_ExpectedPhaseWindowExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var progress = new WindowsRuntimeHostProvisioningProgress(
            Guid.NewGuid(),
            "developer-bootstrap",
            WindowsRuntimeHostProvisioningState.Running,
            "certify-runtime",
            "Certifying hardware isolation",
            "A disposable VM is running the certification probe.",
            70,
            now.AddMinutes(-20),
            now.AddMinutes(-11),
            120,
            480,
            false,
            null,
            null,
            Environment.ProcessId);

        Assert.True(WindowsRuntimeHostProvisioner.HasExceededExpectedPhaseWindow(progress));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.Office.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The C-Sweet Office repository root was not found.");
    }
}
