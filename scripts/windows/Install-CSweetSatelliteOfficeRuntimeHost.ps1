[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PayloadRoot,
    [string] $InstallRoot = "$env:ProgramFiles\CSweet\SatelliteOffice",
    [string] $DataRoot = "$env:ProgramData\CSweet\SatelliteOffice",
    [string] $ControlPlaneUserSid,
    [string] $ControlPlaneUrl,
    [string] $ControlPlaneCertificateSha256,
    [string] $EnrollmentTokenInputPath,
    [ValidateSet('baseline', 'hardened', 'development')]
    [string] $SecurityProfile = 'baseline',
    [bool] $MixedUseHost = $true,
    [switch] $AllowDevelopmentAssignments,
    [switch] $NonInteractive,
    [string] $ProgressPath,
    [guid] $ProgressJobId = [guid]::Empty,
    [string] $ProgressWorkflow = 'packaged-installer'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($SecurityProfile -eq 'development' -and -not $AllowDevelopmentAssignments) {
    throw 'The development security profile requires explicit -AllowDevelopmentAssignments consent.'
}
if ($SecurityProfile -eq 'development') {
    Write-Warning 'Development posture is vulnerable by design. Use only disposable test data and credentials; certified VM isolation remains mandatory.'
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This installer must run from the C-Sweet administrator prompt.'
    }
}

function Resolve-SafeChildPath([string] $Root, [string] $RelativePath) {
    if ([String]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "Invalid payload path: $RelativePath"
    }
    $segments = $RelativePath.Replace('/', '\').Split('\')
    if ($segments | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }) {
        throw "Invalid payload path: $RelativePath"
    }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $candidate = [IO.Path]::GetFullPath([IO.Path]::Combine($rootPath, $RelativePath))
    if (-not $candidate.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Payload path escaped its root: $RelativePath"
    }
    return $candidate
}

function Assert-Sha256([string] $Value, [string] $Name) {
    if ($Value -notmatch '^sha256:[0-9a-f]{64}$') {
        throw "$Name must be a lowercase SHA-256 digest."
    }
}

function Invoke-Sc([string[]] $Arguments) {
    $output = @(& "$env:SystemRoot\System32\sc.exe" @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output | Out-Host
    if ($exitCode -ne 0) {
        $details = ($output | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ }) -join ' '
        throw "Windows service configuration failed while running 'sc.exe $($Arguments -join ' ')' with exit code $exitCode. $details"
    }
}

function Get-OptionalObjectProperty([object] $InputObject, [string] $Name) {
    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Resolve-ServiceSid([string] $ServiceName) {
    $output = @(& "$env:SystemRoot\System32\sc.exe" showsid $ServiceName 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "The Windows service SID could not be resolved for $ServiceName." }
    $match = [Regex]::Match(($output -join ' '), 'S-1-5-80-(?:[0-9]+-){4}[0-9]+')
    if (-not $match.Success) { throw "Windows returned an invalid service SID for $ServiceName." }
    return $match.Value
}

function Grant-RuntimeHostHyperVAccess([string] $ServiceName) {
    $hyperVAdministratorsSid = 'S-1-5-32-578'
    $serviceIdentity = "NT SERVICE\$ServiceName"
    $existing = Get-LocalGroupMember -SID $hyperVAdministratorsSid -Member $serviceIdentity -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        Add-LocalGroupMember -SID $hyperVAdministratorsSid -Member $serviceIdentity -ErrorAction Stop
    }
}

function Set-ProtectedPackageAcl([string] $Root, [string] $RuntimeHostSid, [string] $NodeSid) {
    # Give every existing object an effective explicit ACE first. Applying only
    # (OI)(CI) through /T can leave existing files without an effective RX ACE.
    & "$env:SystemRoot\System32\icacls.exe" $Root '/inheritance:r' '/grant:r' `
        "*$RuntimeHostSid`:RX" "*$NodeSid`:RX" `
        '*S-1-5-18:F' '*S-1-5-32-544:F' '/T' '/C' '/Q' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The installed Satellite Office package ACL could not be secured.' }

    $directories = @((Get-Item -LiteralPath $Root -Force)) +
        @(Get-ChildItem -LiteralPath $Root -Directory -Recurse -Force)
    foreach ($directory in $directories) {
        & "$env:SystemRoot\System32\icacls.exe" $directory.FullName '/inheritance:r' '/grant:r' `
            "*$RuntimeHostSid`:(OI)(CI)RX" "*$NodeSid`:(OI)(CI)RX" `
            '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '/Q' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "The installed package directory ACL could not be secured: $($directory.FullName)" }
    }
}

function Grant-HyperVGuestImageReadAccess([string] $GuestImagePath) {
    # Hyper-V opens the full differencing-disk chain as the VM worker identity,
    # not as RuntimeHost. Grant the built-in Virtual Machines group read-only
    # access to the signed, non-secret base image. Never grant write access or
    # inherited access to the rest of the immutable package.
    $virtualMachinesSid = 'S-1-5-83-0'
    $guestImageDirectory = Split-Path -Parent $GuestImagePath
    & "$env:SystemRoot\System32\icacls.exe" $guestImageDirectory '/grant:r' "*$virtualMachinesSid`:RX" '/Q' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The Hyper-V guest-image directory access could not be secured.' }
    & "$env:SystemRoot\System32\icacls.exe" $GuestImagePath '/grant:r' "*$virtualMachinesSid`:R" '/Q' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The Hyper-V guest-image access could not be secured.' }
}

function Assert-FileReadExecuteAce([string] $Path, [string] $Sid) {
    $identity = [Security.Principal.SecurityIdentifier]::new($Sid)
    $rules = (Get-Acl -LiteralPath $Path).GetAccessRules(
        $true, $true, [Security.Principal.SecurityIdentifier])
    $required = [Security.AccessControl.FileSystemRights]::ReadAndExecute
    $allowed = [Security.AccessControl.FileSystemRights]0
    foreach ($rule in $rules) {
        if (-not $rule.IdentityReference.Equals($identity)) { continue }
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Deny -and
            ($rule.FileSystemRights -band $required) -ne 0) {
            throw "The installed executable has a deny ACE for its service identity: $Path"
        }
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            -not $rule.PropagationFlags.HasFlag([Security.AccessControl.PropagationFlags]::InheritOnly)) {
            $allowed = $allowed -bor $rule.FileSystemRights
        }
    }
    if (($allowed -band $required) -ne $required) {
        throw "The installed executable does not grant effective read/execute access to its service identity: $Path"
    }
}

function Assert-NotDomainController {
    $computer = Get-CimInstance -ClassName Win32_ComputerSystem
    if ($null -ne $computer -and [int]$computer.DomainRole -in @(4, 5)) {
        throw 'C-Sweet Satellite Office cannot be installed on a domain controller.'
    }
}

function Initialize-WindowsEventLogSource([string] $SourceName) {
    if (-not [Diagnostics.EventLog]::SourceExists($SourceName, '.')) {
        New-EventLog -LogName Application -Source $SourceName
    }

    $registeredLog = [Diagnostics.EventLog]::LogNameFromSourceName($SourceName, '.')
    if (-not $registeredLog.Equals('Application', [StringComparison]::OrdinalIgnoreCase)) {
        throw "The Windows Event Log source '$SourceName' is already registered to '$registeredLog'."
    }

    # Prime the source before the service starts. If the host creates the source during
    # startup, its first concurrent background-service log can race Event Log source
    # registration and terminate the host when logging throws access denied.
    Write-EventLog -LogName Application -Source $SourceName -EntryType Information -EventId 0 `
        -Message 'C-Sweet Satellite Office service logging initialized.'
}

function Normalize-CertificateSha256([string] $Value) {
    if ([String]::IsNullOrWhiteSpace($Value)) { return '' }
    $normalized = $Value.Trim().Replace(':', '').Replace('-', '')
    if ($normalized.StartsWith('sha256', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(6).TrimStart(':')
    }
    if ($normalized -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'ControlPlaneCertificateSha256 must contain exactly 64 hexadecimal characters.'
    }
    return $normalized.ToLowerInvariant()
}

function Resolve-ControlPlaneCertificateSha256(
    [string] $NodeExecutable,
    [string] $Url,
    [string] $ExpectedSha256,
    [bool] $IsNonInteractive) {
    $uri = [Uri]$Url
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https') {
        throw 'ControlPlaneUrl must be an absolute HTTPS URL.'
    }
    Write-Host "Verifying the control-plane TLS certificate at $($uri.AbsoluteUri)..."
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $NodeExecutable
    $startInfo.Arguments = '--probe-control-plane-certificate "' + $uri.AbsoluteUri.Replace('"', '\"') + '"'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $probe = [Diagnostics.Process]::new()
    $probe.StartInfo = $startInfo
    if (-not $probe.Start()) { throw 'The control-plane certificate probe could not be started.' }
    $standardOutput = $probe.StandardOutput.ReadToEndAsync()
    $standardError = $probe.StandardError.ReadToEndAsync()
    if (-not $probe.WaitForExit(20000)) {
        try { $probe.Kill() } catch { }
        $probe.Dispose()
        throw 'The control-plane certificate probe timed out. Rebuild the Satellite Office payload with the current Node executable.'
    }
    $probeOutput = $standardOutput.GetAwaiter().GetResult()
    $probeError = $standardError.GetAwaiter().GetResult()
    $probeExitCode = $probe.ExitCode
    $probe.Dispose()
    if ($probeExitCode -ne 0) {
        $detail = $probeError.Trim()
        throw "The control-plane TLS certificate could not be inspected. $detail"
    }
    try { $certificate = $probeOutput | ConvertFrom-Json }
    catch { throw 'The control-plane certificate probe returned an invalid response.' }
    $actual = Normalize-CertificateSha256 ([string]$certificate.certificateSha256)
    $expected = Normalize-CertificateSha256 $ExpectedSha256
    if (-not [String]::IsNullOrWhiteSpace($expected) -and $expected -cne $actual) {
        throw "The control-plane certificate fingerprint did not match. Expected $expected but received $actual."
    }

    $policyErrors = [int]$certificate.policyErrors
    if (($policyErrors -band 2) -ne 0) {
        throw "The control-plane certificate does not match host '$($uri.DnsSafeHost)'."
    }
    $notBefore = [DateTime]$certificate.notBefore
    $notAfter = [DateTime]$certificate.notAfter
    $now = (Get-Date).ToUniversalTime()
    if ($notBefore.ToUniversalTime() -gt $now -or $notAfter.ToUniversalTime() -lt $now) {
        throw "The control-plane certificate is not currently valid ($($notBefore.ToUniversalTime().ToString('u')) through $($notAfter.ToUniversalTime().ToString('u')))."
    }
    if ($policyErrors -eq 0) { return $expected }
    if (($policyErrors -band (-bnot 4)) -ne 0) {
        throw "The control-plane TLS certificate could not be validated ($($certificate.policyErrorNames))."
    }
    if (-not [String]::IsNullOrWhiteSpace($expected)) { return $expected }
    if ($IsNonInteractive) {
        throw "The control plane uses a private certificate. Rerun with -ControlPlaneCertificateSha256 '$actual' after verifying the fingerprint."
    }

    Write-Host ''
    Write-Warning 'The control plane uses a certificate that is not trusted by Windows.'
    Write-Host "URL:         $($uri.AbsoluteUri)"
    Write-Host "Subject:     $($certificate.subject)"
    Write-Host "Issuer:      $($certificate.issuer)"
    Write-Host "Valid from:  $($notBefore.ToUniversalTime().ToString('u'))"
    Write-Host "Valid until: $($notAfter.ToUniversalTime().ToString('u'))"
    Write-Host "SHA-256:     $actual" -ForegroundColor Cyan
    Write-Host ''
    $answer = Read-Host 'Trust this certificate only for C-Sweet Satellite Office? Type TRUST to continue'
    if (-not $answer.Equals('TRUST', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The control-plane certificate was not trusted.'
    }
    return $actual
}

function Get-HeadquartersAssignmentTrust(
    [string] $NodeExecutable,
    [string] $Url,
    [string] $CertificateSha256) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $NodeExecutable
    $startInfo.Arguments = '--probe-headquarters-assignment-trust "' + $Url.Replace('"', '\"') + '" "' + $CertificateSha256 + '"'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $probe = [Diagnostics.Process]::new()
    $probe.StartInfo = $startInfo
    if (-not $probe.Start()) { throw 'The Headquarters assignment trust probe could not be started.' }
    $outputTask = $probe.StandardOutput.ReadToEndAsync()
    $errorTask = $probe.StandardError.ReadToEndAsync()
    if (-not $probe.WaitForExit(20000)) {
        try { $probe.Kill() } catch { }
        $probe.Dispose()
        throw 'The Headquarters assignment trust probe timed out.'
    }
    $output = $outputTask.GetAwaiter().GetResult()
    $error = $errorTask.GetAwaiter().GetResult()
    $exitCode = $probe.ExitCode
    $probe.Dispose()
    if ($exitCode -ne 0) { throw "The Headquarters assignment trust could not be verified. $($error.Trim())" }
    try { $trust = $output | ConvertFrom-Json }
    catch { throw 'The Headquarters assignment trust probe returned invalid JSON.' }
    if ([String]::IsNullOrWhiteSpace([string]$trust.assignmentSigningKeyId)) {
        throw 'The Headquarters assignment signing key ID is missing.'
    }
    try { $key = [Convert]::FromBase64String([string]$trust.assignmentVerificationPublicKeyBase64) }
    catch { throw 'The Headquarters assignment verification key is invalid.' }
    if ($key.Length -lt 64 -or $key.Length -gt 1024) { throw 'The Headquarters assignment verification key size is invalid.' }
    return $trust
}

function Test-PathWithinRoot([string] $Candidate, [string] $Root) {
    if ([String]::IsNullOrWhiteSpace($Candidate)) { return $false }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $candidatePath = [IO.Path]::GetFullPath($Candidate).TrimEnd('\')
    return $candidatePath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $candidatePath.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Remove-LegacyHyperVResources([string] $LegacyRoot) {
    if (-not (Test-Path -LiteralPath $LegacyRoot -PathType Container)) { return }
    Import-Module Hyper-V -ErrorAction Stop
    $legacyVms = @(Get-VM -ErrorAction Stop | Where-Object {
        $vm = $_
        $paths = [Collections.Generic.List[string]]::new()
        foreach ($propertyName in @('Path', 'ConfigurationLocation', 'SnapshotFileLocation', 'SmartPagingFilePath')) {
            $property = $vm.PSObject.Properties[$propertyName]
            if ($null -ne $property -and -not [String]::IsNullOrWhiteSpace([string]$property.Value)) {
                $paths.Add([string]$property.Value)
            }
        }
        Get-VMHardDiskDrive -VM $vm -ErrorAction SilentlyContinue | ForEach-Object {
            if (-not [String]::IsNullOrWhiteSpace($_.Path)) { $paths.Add([string]$_.Path) }
        }
        @($paths | Where-Object { Test-PathWithinRoot $_ $LegacyRoot }).Count -gt 0
    })
    foreach ($vm in $legacyVms) {
        Write-Host "Removing legacy C-Sweet Hyper-V VM '$($vm.Name)'..."
        if ($vm.State -ne [Microsoft.HyperV.PowerShell.VMState]::Off) {
            Stop-VM -VM $vm -TurnOff -Force -ErrorAction Stop
        }
        Remove-VM -VM $vm -Force -ErrorAction Stop
    }

    Get-ChildItem -LiteralPath $LegacyRoot -Recurse -File -Include '*.vhd','*.vhdx' -ErrorAction SilentlyContinue |
        ForEach-Object {
            $virtualDisk = Get-VHD -Path $_.FullName -ErrorAction SilentlyContinue
            if ($null -ne $virtualDisk -and $virtualDisk.Attached) {
                Dismount-VHD -Path $_.FullName -ErrorAction Stop
            }
        }
}

function Remove-LegacyDirectory([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch [IO.IOException] {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

Assert-Administrator
Assert-NotDomainController
$runtimeHostServiceName = 'CSweet.SatelliteOffice.RuntimeHost'
$nodeServiceName = 'CSweet.SatelliteOffice.Node'
$runtimeHostServiceSid = Resolve-ServiceSid $runtimeHostServiceName
$nodeServiceSid = Resolve-ServiceSid $nodeServiceName
if ([String]::IsNullOrWhiteSpace($ControlPlaneUserSid)) {
    $ControlPlaneUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
}
if ($null -eq $ProgressJobId -or $ProgressJobId -eq [guid]::Empty) {
    $ProgressJobId = [guid]::NewGuid()
}
if ([String]::IsNullOrWhiteSpace($ProgressPath)) {
    $ProgressPath = Join-Path $env:ProgramData "CSweet\Setup\windows-isolation-$($ProgressJobId.ToString('N')).json"
}
try {
    $controlPlaneIdentity = [Security.Principal.SecurityIdentifier]::new($ControlPlaneUserSid)
    $null = $controlPlaneIdentity.Translate([Security.Principal.NTAccount])
} catch {
    throw 'The C-Sweet control-plane Windows user identity is invalid.'
}
. (Join-Path $PSScriptRoot 'CSweet.WindowsSetupProgress.ps1')
$ProgressPath = Initialize-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId `
    -ControlPlaneUserSid $ControlPlaneUserSid

try {
Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
    -State running -PhaseKey verify-package -PhaseDisplayName 'Verifying the secure runtime package' `
    -Message 'Every packaged file is being checked against the signed release manifest.' -PercentComplete 92 `
    -EstimatedRemainingMinimumSeconds 30 -EstimatedRemainingMaximumSeconds 180
$PayloadRoot = [IO.Path]::GetFullPath($PayloadRoot)
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
if (-not (Test-Path -LiteralPath $PayloadRoot -PathType Container)) {
    throw 'The signed RuntimeHost payload directory is missing.'
}
$manifestPath = Join-Path $PayloadRoot 'runtime-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'The RuntimeHost payload manifest is missing.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.packageVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') {
    throw 'The RuntimeHost payload manifest schema or package version is invalid.'
}
Assert-Sha256 $manifest.guestImageDigest 'guestImageDigest'
Assert-Sha256 $manifest.certificationEvidenceDigest 'certificationEvidenceDigest'
if ($null -eq $manifest.files -or @($manifest.files).Count -lt 1 -or @($manifest.files).Count -gt 1000) {
    throw 'The RuntimeHost payload file manifest is invalid.'
}
$payloadSatelliteOfficeExe = Resolve-SafeChildPath $PayloadRoot ([string]$manifest.satelliteOfficeExecutable)
if (-not (Test-Path -LiteralPath $payloadSatelliteOfficeExe -PathType Leaf)) {
    throw 'The Satellite Office executable declared by the payload is missing.'
}
try {
    $payloadSatelliteOfficeVersion = [Version](Get-Item -LiteralPath $payloadSatelliteOfficeExe).VersionInfo.FileVersion
} catch {
    throw 'The Satellite Office executable version is invalid.'
}
$minimumSatelliteOfficeVersion = [Version]'1.0.2.0'
if ($payloadSatelliteOfficeVersion -lt $minimumSatelliteOfficeVersion) {
    throw "This payload contains C-Sweet Satellite Office $payloadSatelliteOfficeVersion, which predates privileged signed-assignment enforcement. Rebuild the Windows certification payload with Satellite Office 1.0.2 or later before installing."
}

$existingNodeService = Get-Service -Name $nodeServiceName -ErrorAction SilentlyContinue
$existingNodeConfiguration = $null
if ($null -ne $existingNodeService) {
    $existingNodeServiceConfiguration = Get-CimInstance -ClassName Win32_Service -Filter "Name='$nodeServiceName'"
    $contentRootMatch = [Regex]::Match(
        [string]$existingNodeServiceConfiguration.PathName,
        '--contentRoot\s+"([^"]+)"',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $contentRootMatch.Success) {
        throw 'The existing Satellite Office service does not declare a protected content root.'
    }
    $existingContentRoot = [IO.Path]::GetFullPath($contentRootMatch.Groups[1].Value)
    $protectedInstallRoot = $InstallRoot.TrimEnd('\') + '\'
    if (-not ($existingContentRoot.TrimEnd('\') + '\').StartsWith(
            $protectedInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The existing Satellite Office content root is outside the protected install directory.'
    }
    $existingConfigurationPath = Join-Path $existingContentRoot 'appsettings.json'
    if (-not (Test-Path -LiteralPath $existingConfigurationPath -PathType Leaf)) {
        throw 'The existing Satellite Office configuration is missing.'
    }
    $existingConfiguration = Get-Content -LiteralPath $existingConfigurationPath -Raw | ConvertFrom-Json
    $existingCSweetConfiguration = Get-OptionalObjectProperty $existingConfiguration 'CSweet'
    $existingSatelliteOfficeConfiguration = Get-OptionalObjectProperty $existingCSweetConfiguration 'SatelliteOffice'
    $existingRuntimeHostConfiguration = Get-OptionalObjectProperty $existingSatelliteOfficeConfiguration 'RuntimeHost'
    if ($null -eq $existingRuntimeHostConfiguration) {
        throw 'The existing Satellite Office RuntimeHost configuration is incomplete.'
    }
    $existingNodeConfiguration = Get-OptionalObjectProperty $existingSatelliteOfficeConfiguration 'Node'
    if ($null -ne $existingNodeConfiguration) {
        $existingControlPlaneUrl = Get-OptionalObjectProperty $existingNodeConfiguration 'ControlPlaneUrl'
        $existingStateDirectory = Get-OptionalObjectProperty $existingNodeConfiguration 'StateDirectory'
        if ([String]::IsNullOrWhiteSpace([string]$existingControlPlaneUrl) -or
            [String]::IsNullOrWhiteSpace([string]$existingStateDirectory)) {
            throw 'The existing Satellite Office identity configuration is incomplete.'
        }
    }
}
$legacyNodeStatePath = Join-Path $DataRoot 'node-state.json'
$protectedNodeStatePath = Join-Path (Join-Path $DataRoot 'node') 'node-state.json'
$existingNodeStatePath = if (Test-Path -LiteralPath $protectedNodeStatePath -PathType Leaf) {
    $protectedNodeStatePath
} else { $legacyNodeStatePath }
if ($null -ne $existingNodeService -and $null -eq $existingNodeConfiguration -and
    (Test-Path -LiteralPath $existingNodeStatePath -PathType Leaf)) {
    throw 'The existing Satellite Office identity state has no matching Node configuration. Uninstall and enroll this office again.'
}
if ($null -ne $existingNodeService -and
    (Test-Path -LiteralPath $legacyNodeStatePath -PathType Leaf) -and
    -not (Test-Path -LiteralPath $protectedNodeStatePath -PathType Leaf)) {
    throw 'This Satellite Office uses the legacy shared Windows state layout. Drain and reinstall it to apply the isolated service security model.'
}
if ($null -ne $existingNodeService -and
    (Test-Path -LiteralPath $existingNodeStatePath -PathType Leaf)) {
    $maintenance = Join-Path ([IO.Path]::GetDirectoryName($existingNodeStatePath)) 'maintenance'
    $drainPath = Join-Path $maintenance 'drain-state'
    $activeRoot = Join-Path $maintenance 'active-assignments'
    $drainState = if (Test-Path -LiteralPath $drainPath -PathType Leaf) {
        [IO.File]::ReadAllText($drainPath).Trim()
    } else { '' }
    $activeCount = if (Test-Path -LiteralPath $activeRoot -PathType Container) {
        @(Get-ChildItem -LiteralPath $activeRoot -File -Filter '*.active').Count
    } else { 0 }
    if ($drainState -ne 'draining' -or $activeCount -ne 0) {
        throw 'Drain this node in C-Sweet and wait for active assignments to reach zero before upgrading RuntimeHost.'
    }
}
elseif ($null -eq $existingNodeService) {
    # Clean v1 cutover: legacy identities and certificates are deliberately not migrated.
    foreach ($legacyServiceName in @('CSweet.ExecutionNode', 'CSweet.RuntimeHost')) {
        $legacyService = Get-Service -Name $legacyServiceName -ErrorAction SilentlyContinue
        if ($null -ne $legacyService) {
            if ($legacyService.Status -ne 'Stopped') {
                Stop-Service -Name $legacyServiceName -Force -ErrorAction SilentlyContinue
                $legacyService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
            }
            & "$env:SystemRoot\System32\sc.exe" delete $legacyServiceName | Out-Host
        }
    }
    $legacyAgentRuntimeRoot = "$env:ProgramData\CSweet\AgentRuntime"
    Remove-LegacyHyperVResources $legacyAgentRuntimeRoot
    foreach ($legacyRoot in @(
        "$env:ProgramFiles\CSweet\ExecutionNode",
        "$env:ProgramFiles\CSweet\RuntimeHost",
        "$env:ProgramData\CSweet\ExecutionNode",
        $legacyAgentRuntimeRoot)) {
        Remove-LegacyDirectory $legacyRoot
    }
}

$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifest.files) {
    if (-not $seen.Add([string]$file.path)) { throw "Duplicate payload path: $($file.path)" }
    $source = Resolve-SafeChildPath $PayloadRoot ([string]$file.path)
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Payload file is missing: $($file.path)" }
    if ([string]$file.sha256 -notmatch '^[0-9a-f]{64}$') { throw "Invalid payload digest: $($file.path)" }
    $actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne [string]$file.sha256) { throw "Payload integrity check failed: $($file.path)" }
}

$versionRoot = Join-Path $InstallRoot ([string]$manifest.packageVersion)
Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
    -State running -PhaseKey install-runtime -PhaseDisplayName 'Installing RuntimeHost' `
    -Message 'The verified runtime, helper, signed guest, and certification evidence are being installed.' `
    -PercentComplete 94 -EstimatedRemainingMinimumSeconds 20 -EstimatedRemainingMaximumSeconds 120
New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null
foreach ($file in $manifest.files) {
    $source = Resolve-SafeChildPath $PayloadRoot ([string]$file.path)
    $destination = Resolve-SafeChildPath $versionRoot ([string]$file.path)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $installedHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($installedHash -ceq [string]$file.sha256) {
            continue
        }
    }
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

$runtimeHostExe = Resolve-SafeChildPath $versionRoot ([string]$manifest.runtimeHostExecutable)
$helperExe = Resolve-SafeChildPath $versionRoot ([string]$manifest.helperExecutable)
$helperManifestPath = ([string]$manifest.helperExecutable).Replace('\\', '/')
$helperManifestEntries = @($manifest.files | Where-Object { ([string]$_.path).Replace('\\', '/') -ceq $helperManifestPath })
if ($helperManifestEntries.Count -ne 1) { throw 'The RuntimeHost helper must have exactly one payload digest entry.' }
$helperExecutableDigest = "sha256:$([string]$helperManifestEntries[0].sha256)"
Assert-Sha256 $helperExecutableDigest 'helperExecutableDigest'
$satelliteOfficeExe = Resolve-SafeChildPath $versionRoot ([string]$manifest.satelliteOfficeExecutable)
$guestImage = Resolve-SafeChildPath $versionRoot ([string]$manifest.guestImage)
$guestSignature = Resolve-SafeChildPath $versionRoot ([string]$manifest.guestImageSignature)
$signingCertificate = Resolve-SafeChildPath $versionRoot ([string]$manifest.guestImageSigningCertificate)
$certificationEvidence = Resolve-SafeChildPath $versionRoot ([string]$manifest.certificationEvidence)
foreach ($required in @($runtimeHostExe, $helperExe, $satelliteOfficeExe, $guestImage, $guestSignature, $signingCertificate, $certificationEvidence)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required installed file is missing: $required" }
}

# Register both service identities before using their service SIDs in ACLs.
# Neither service is started until its configuration, integrity checks, and
# protected state have been completed.
$runtimeHostBinaryPath = '"' + $runtimeHostExe + '" --contentRoot "' + $versionRoot + '"'
$runtimeHostService = Get-Service -Name $runtimeHostServiceName -ErrorAction SilentlyContinue
if ($null -ne $runtimeHostService -and $runtimeHostService.Status -ne 'Stopped') {
    Stop-Service -Name $runtimeHostServiceName -Force
    $runtimeHostService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
if ($null -eq $runtimeHostService) {
    $runtimeHostService = New-Service -Name $runtimeHostServiceName -BinaryPathName $runtimeHostBinaryPath `
        -DisplayName 'C-Sweet RuntimeHost' `
        -Description 'Privileged C-Sweet virtual-machine lifecycle service. No network listener.' `
        -StartupType Automatic
} else {
    Set-Service -Name $runtimeHostServiceName -DisplayName 'C-Sweet RuntimeHost' `
        -Description 'Privileged C-Sweet virtual-machine lifecycle service. No network listener.' `
        -StartupType Automatic
}
$runtimeHostServiceConfiguration = Get-CimInstance -ClassName Win32_Service -Filter "Name='$runtimeHostServiceName'"
if ($null -eq $runtimeHostServiceConfiguration) { throw 'The RuntimeHost service configuration could not be loaded.' }
$runtimeHostChangeResult = Invoke-CimMethod -InputObject $runtimeHostServiceConfiguration -MethodName Change -Arguments @{
    PathName = $runtimeHostBinaryPath
    StartName = "NT SERVICE\$runtimeHostServiceName"
    StartPassword = $null
    StartMode = 'Automatic'
}
if ($null -eq $runtimeHostChangeResult -or [int]$runtimeHostChangeResult.ReturnValue -ne 0) {
    $returnValue = if ($null -eq $runtimeHostChangeResult) { 'no result' } else { [string]$runtimeHostChangeResult.ReturnValue }
    throw "The RuntimeHost virtual service account could not be configured. Win32_Service.Change returned $returnValue."
}
Invoke-Sc @('sidtype', $runtimeHostServiceName, 'unrestricted')

$nodeBinaryPath = '"' + $satelliteOfficeExe + '" --contentRoot "' + $versionRoot + '" --environment Production'
$nodeService = Get-Service -Name $nodeServiceName -ErrorAction SilentlyContinue
if ($null -ne $nodeService -and $nodeService.Status -ne 'Stopped') {
    Stop-Service -Name $nodeServiceName -Force
    $nodeService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
if ($null -eq $nodeService) {
    $nodeService = New-Service -Name $nodeServiceName -BinaryPathName $nodeBinaryPath `
        -DisplayName 'C-Sweet SatelliteOffice' `
        -Description 'Unprivileged outbound C-Sweet satellite office.' -StartupType Manual
}
$nodeServiceConfiguration = Get-CimInstance -ClassName Win32_Service -Filter "Name='$nodeServiceName'"
if ($null -eq $nodeServiceConfiguration) { throw 'The SatelliteOffice service configuration could not be loaded.' }
$nodeChangeResult = Invoke-CimMethod -InputObject $nodeServiceConfiguration -MethodName Change -Arguments @{
    PathName = $nodeBinaryPath
    StartName = "NT SERVICE\$nodeServiceName"
    StartPassword = $null
    StartMode = 'Manual'
}
if ($null -eq $nodeChangeResult -or [int]$nodeChangeResult.ReturnValue -ne 0) {
    $returnValue = if ($null -eq $nodeChangeResult) { 'no result' } else { [string]$nodeChangeResult.ReturnValue }
    throw "The SatelliteOffice service identity could not be registered. Win32_Service.Change returned $returnValue."
}
Invoke-Sc @('sidtype', $nodeServiceName, 'unrestricted')

# Installed payloads are immutable to both virtual service accounts. Only
# SYSTEM and administrators can replace package files.
Set-ProtectedPackageAcl -Root $versionRoot -RuntimeHostSid $runtimeHostServiceSid -NodeSid $nodeServiceSid
Grant-HyperVGuestImageReadAccess -GuestImagePath $guestImage
Assert-FileReadExecuteAce -Path $runtimeHostExe -Sid $runtimeHostServiceSid
Assert-FileReadExecuteAce -Path $satelliteOfficeExe -Sid $nodeServiceSid

$artifactStoreRoot = Join-Path $DataRoot 'artifacts'
$artifactMediaRoot = Join-Path $DataRoot 'artifact-media'
$hyperVDataRoot = Join-Path $DataRoot 'hyperv'
$nodeDataRoot = Join-Path $DataRoot 'node'
$authorizationDataRoot = Join-Path $DataRoot 'authorization'
New-Item -ItemType Directory -Path $artifactStoreRoot, $artifactMediaRoot, $hyperVDataRoot, $nodeDataRoot, $authorizationDataRoot -Force | Out-Null

$keyPath = Join-Path $DataRoot 'runtime-host.key'
Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
    -State running -PhaseKey secure-local-state -PhaseDisplayName 'Securing local runtime state' `
    -Message 'C-Sweet is creating the authentication key and applying protected Windows permissions.' `
    -PercentComplete 96 -EstimatedRemainingMinimumSeconds 10 -EstimatedRemainingMaximumSeconds 90
if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
    $keyBytes = [byte[]]::new(32)
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($keyBytes) } finally { $random.Dispose() }
    [IO.File]::WriteAllText($keyPath, [Convert]::ToBase64String($keyBytes), [Text.UTF8Encoding]::new($false))
}
& "$env:SystemRoot\System32\icacls.exe" $keyPath '/inheritance:r' "/grant:r" "*$ControlPlaneUserSid`:R" "*$nodeServiceSid`:R" "*$runtimeHostServiceSid`:R" '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The RuntimeHost key ACL could not be secured.' }
& "$env:SystemRoot\System32\icacls.exe" $artifactStoreRoot '/inheritance:r' "/grant:r" "*$nodeServiceSid`:(OI)(CI)M" "*$runtimeHostServiceSid`:(OI)(CI)R" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The artifact cache ACL could not be secured.' }
& "$env:SystemRoot\System32\icacls.exe" $artifactMediaRoot '/inheritance:r' "/grant:r" "*$nodeServiceSid`:(OI)(CI)M" "*$runtimeHostServiceSid`:(OI)(CI)R" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The artifact media ACL could not be secured.' }
& "$env:SystemRoot\System32\icacls.exe" $hyperVDataRoot '/inheritance:r' "/grant:r" "*$runtimeHostServiceSid`:(OI)(CI)M" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The Hyper-V runtime state ACL could not be secured.' }
& "$env:SystemRoot\System32\icacls.exe" $authorizationDataRoot '/inheritance:r' "/grant:r" "*$runtimeHostServiceSid`:(OI)(CI)M" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The privileged authorization state ACL could not be secured.' }
& "$env:SystemRoot\System32\icacls.exe" $nodeDataRoot '/inheritance:r' "/grant:r" "*$nodeServiceSid`:(OI)(CI)M" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The Satellite Office Node state ACL could not be secured.' }

$config = @{
    Logging = @{
        LogLevel = @{ Default = 'Information'; 'Microsoft.Hosting.Lifetime' = 'Information' }
        EventLog = @{ LogLevel = @{ Default = 'Information' } }
    }
    CSweet = @{
        SatelliteOffice = @{
            RuntimeHost = @{
                NamedPipeName = 'csweet-satellite-office-runtime-v1'
                AllowedClientSid = $ControlPlaneUserSid
                AllowedClientSids = @($ControlPlaneUserSid, $nodeServiceSid)
                UnixSocketPath = '/run/csweet/csweet-satellite-office-runtime-v1.sock'
                ConnectTimeoutSeconds = 10
                MaximumFrameBytes = 1048576
                Authentication = @{ KeyId = 'satellite-office-node'; SharedKeyBase64 = ''; SharedKeyFilePath = $keyPath }
                Authorization = @{
                    StateDirectory = $authorizationDataRoot
                    MaximumAuthorizationLifetimeSeconds = 600
                    MaximumClockSkewSeconds = 120
                }
            }
            Providers = @{
                HyperV = @{
                    HelperExecutablePath = $helperExe
                    HelperExecutableDigest = $helperExecutableDigest
                    GuestImagePath = $guestImage
                    GuestImageDigest = [string]$manifest.guestImageDigest
                    GuestImageSignaturePath = $guestSignature
                    GuestImageSigningCertificatePath = $signingCertificate
                    GuestImageSigningCertificateThumbprint = [string]$manifest.guestImageSigningCertificateThumbprint
                    ArtifactImageRoot = $artifactMediaRoot
                    BrokerProtocolVersion = '1.0'
                    CertificationSuiteVersion = [string]$manifest.certificationSuiteVersion
                    CertificationEvidencePath = $certificationEvidence
                    CertificationEvidenceDigest = [string]$manifest.certificationEvidenceDigest
                    CertifiedAt = [string]$manifest.certifiedAt
                    CertificationExpiresAt = $manifest.certificationExpiresAt
                }
                Firecracker = @{}
                AppleVirtualization = @{}
            }
        }
    }
}
if ($null -ne $existingNodeConfiguration) {
    $config.CSweet.SatelliteOffice.Node = $existingNodeConfiguration
}
[IO.File]::WriteAllText((Join-Path $versionRoot 'appsettings.json'),
    ($config | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

$serviceId = '00000ac9-facb-11e6-bd58-64006a7986d3'
$legacyServiceRegistryPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\{$serviceId}"
$serviceRegistryPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\$serviceId"
Remove-Item -LiteralPath $legacyServiceRegistryPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -Path $serviceRegistryPath -Force | Out-Null
New-ItemProperty -Path $serviceRegistryPath -Name 'ElementName' -PropertyType String -Value 'C-Sweet authenticated agent broker' -Force | Out-Null
[Environment]::SetEnvironmentVariable('CSWEET_HYPERV_BROKER_SERVICE_ID', $serviceId, 'Machine')
[Environment]::SetEnvironmentVariable('CSWEET_HYPERV_DATA_ROOT', $hyperVDataRoot, 'Machine')
[Environment]::SetEnvironmentVariable('CSWEET_ARTIFACT_MEDIA_ROOT', $artifactMediaRoot, 'Machine')

$serviceName = $runtimeHostServiceName
Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
    -State running -PhaseKey start-service -PhaseDisplayName 'Starting the RuntimeHost service' `
    -Message 'The privileged VM lifecycle service is being registered and started.' -PercentComplete 98 `
    -EstimatedRemainingMinimumSeconds 5 -EstimatedRemainingMaximumSeconds 60
$serviceEnvironment = @(
    "CSWEET_HYPERV_BROKER_SERVICE_ID=$serviceId",
    "CSWEET_HYPERV_DATA_ROOT=$hyperVDataRoot",
    "CSWEET_ARTIFACT_MEDIA_ROOT=$artifactMediaRoot"
)
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name 'Environment' -PropertyType MultiString -Value $serviceEnvironment -Force | Out-Null
Invoke-Sc @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/none/0')
Grant-RuntimeHostHyperVAccess -ServiceName $serviceName
Initialize-WindowsEventLogSource -SourceName $serviceName
try {
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
} catch {
    $runtimeHostFailure = Get-WinEvent -FilterHashtable @{
        LogName = 'Application'
        ProviderName = $serviceName
        StartTime = (Get-Date).AddMinutes(-5)
    } -ErrorAction SilentlyContinue | Where-Object {
        $_.LevelDisplayName -eq 'Error'
    } | Select-Object -First 1
    $diagnostic = if ($null -ne $runtimeHostFailure) {
        (($runtimeHostFailure.Message -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 3) -join ' '
    } else {
        $_.Exception.Message
    }
    throw "The C-Sweet RuntimeHost service failed to start. Windows reported: $diagnostic"
}

if (-not [String]::IsNullOrWhiteSpace($ControlPlaneUrl) -and
    -not [String]::IsNullOrWhiteSpace($EnrollmentTokenInputPath)) {
    $gatewayUri = [Uri]$ControlPlaneUrl
    if (-not $gatewayUri.IsAbsoluteUri -or $gatewayUri.Scheme -ne 'https') { throw 'ControlPlaneUrl must be an absolute HTTPS URL.' }
    $resolvedControlPlaneCertificateSha256 = Resolve-ControlPlaneCertificateSha256 `
        -NodeExecutable $satelliteOfficeExe -Url $gatewayUri.AbsoluteUri `
        -ExpectedSha256 $ControlPlaneCertificateSha256 -IsNonInteractive ([bool]$NonInteractive)
    $headquartersTrust = Get-HeadquartersAssignmentTrust `
        -NodeExecutable $satelliteOfficeExe -Url $gatewayUri.AbsoluteUri `
        -CertificateSha256 $resolvedControlPlaneCertificateSha256
    $prePinnedTrust = [ordered]@{
        SatelliteOfficeId = [Guid]::Empty
        AssignmentSigningKeyId = [string]$headquartersTrust.assignmentSigningKeyId
        AssignmentVerificationPublicKey = [string]$headquartersTrust.assignmentVerificationPublicKeyBase64
    }
    [IO.File]::WriteAllText((Join-Path $authorizationDataRoot 'headquarters-trust.json'),
        ($prePinnedTrust | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))
    & "$env:SystemRoot\System32\icacls.exe" $authorizationDataRoot '/inheritance:r' '/grant:r' "*$runtimeHostServiceSid`:(OI)(CI)M" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The pre-pinned Headquarters trust ACL could not be secured.' }
    $inputPath = [IO.Path]::GetFullPath($EnrollmentTokenInputPath)
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw 'The protected enrollment input is missing.' }
    $token = [IO.File]::ReadAllText($inputPath).Trim()
    Remove-Item -LiteralPath $inputPath -Force
    if ($token.Length -lt 32 -or $token.Length -gt 256) { throw 'The protected enrollment token is invalid.' }
    New-Item -ItemType Directory -Path $nodeDataRoot -Force | Out-Null
    $nodeTokenPath = Join-Path $nodeDataRoot 'enrollment.secret'
    [IO.File]::WriteAllText($nodeTokenPath, $token, [Text.UTF8Encoding]::new($false))
    $token = $null
    & "$env:SystemRoot\System32\icacls.exe" $nodeDataRoot '/inheritance:r' '/grant:r' "*$nodeServiceSid`:(OI)(CI)M" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The SatelliteOffice state ACL could not be secured.' }
    $controlPlaneTrustPath = ''
    if (-not [String]::IsNullOrWhiteSpace($resolvedControlPlaneCertificateSha256)) {
        $normalizedCertificateSha256 = $resolvedControlPlaneCertificateSha256.Trim().Replace(':', '').Replace('-', '').ToLowerInvariant()
        if ($normalizedCertificateSha256 -notmatch '^[0-9a-f]{64}$') {
            throw 'The control-plane certificate SHA-256 fingerprint is invalid.'
        }
        $controlPlaneTrustPath = Join-Path $nodeDataRoot 'control-plane-trust.json'
        $trust = [ordered]@{ schemaVersion = 1; certificateSha256 = $normalizedCertificateSha256 }
        [IO.File]::WriteAllText($controlPlaneTrustPath,
            ($trust | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        & "$env:SystemRoot\System32\icacls.exe" $controlPlaneTrustPath '/inheritance:r' '/grant:r' "*$nodeServiceSid`:R" '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'The control-plane trust file ACL could not be secured.' }
    }
    $config.CSweet.SatelliteOffice.Node = @{
        ControlPlaneUrl = $gatewayUri.AbsoluteUri
        ControlPlaneTrustFilePath = $controlPlaneTrustPath
        StateDirectory = $nodeDataRoot
        ArtifactCacheDirectory = (Join-Path $nodeDataRoot 'artifact-cache')
        ArtifactMediaDirectory = $artifactMediaRoot
        EnrollmentTokenFilePath = $nodeTokenPath
        SecurityProfile = $SecurityProfile
        MixedUseHost = $MixedUseHost
        AllowDevelopmentAssignments = [bool]$AllowDevelopmentAssignments
    }
    [IO.File]::WriteAllText((Join-Path $versionRoot 'appsettings.json'),
        ($config | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $nodeService = Get-Service -Name $nodeServiceName -ErrorAction SilentlyContinue
    if ($null -eq $nodeService) { throw 'The SatelliteOffice service identity is not registered.' }
    if ($nodeService.Status -ne 'Stopped') {
        Stop-Service -Name $nodeServiceName -Force
        $nodeService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Set-Service -Name $nodeServiceName -DisplayName 'C-Sweet SatelliteOffice' `
        -Description 'Unprivileged outbound C-Sweet satellite office.' -StartupType Automatic
    Invoke-Sc @('failure', $nodeServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/none/0')
    Initialize-WindowsEventLogSource -SourceName $nodeServiceName
    $nodeEnrollmentStartedAt = Get-Date
    Start-Service -Name $nodeServiceName
    (Get-Service -Name $nodeServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))

    $nodeStatePath = Join-Path $nodeDataRoot 'node-state.json'
    $enrollmentDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    while (-not (Test-Path -LiteralPath $nodeStatePath -PathType Leaf) -and
           [DateTimeOffset]::UtcNow -lt $enrollmentDeadline) {
        $nodeService = Get-Service -Name $nodeServiceName
        if ($nodeService.Status -ne 'Running') {
            throw 'The Satellite Office Node service stopped before enrollment completed.'
        }
        $enrollmentFailure = Get-WinEvent -FilterHashtable @{
            LogName = 'Application'
            ProviderName = $nodeServiceName
            Level = 2
            StartTime = $nodeEnrollmentStartedAt
        } -ErrorAction SilentlyContinue | Where-Object {
            $_.Message -match 'Satellite Office enrollment failed \(([^)]+)\):\s*([^\r\n]+)'
        } | Select-Object -First 1
        if ($null -ne $enrollmentFailure -and
            $enrollmentFailure.Message -match 'Satellite Office enrollment failed \(([^)]+)\):\s*([^\r\n]+)') {
            $errorCode = $Matches[1]
            $errorMessage = $Matches[2].Trim()
            if ($errorCode -eq 'invalid_enrollment') {
                throw "$errorMessage Generate a new connection code in C-Sweet, then run this installer again."
            }
            throw "Satellite Office enrollment failed ($errorCode): $errorMessage"
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not (Test-Path -LiteralPath $nodeStatePath -PathType Leaf)) {
        throw "The Satellite Office Node service started but did not enroll within 60 seconds. Check the '$nodeServiceName' Windows Application log for the control-plane connection error."
    }
}
elseif ($null -ne $existingNodeConfiguration) {
    Set-Service -Name $nodeServiceName -DisplayName 'C-Sweet SatelliteOffice' `
        -Description 'Unprivileged outbound C-Sweet satellite office.' -StartupType Automatic
    Initialize-WindowsEventLogSource -SourceName $nodeServiceName
    Start-Service -Name $nodeServiceName
    (Get-Service -Name $nodeServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
}
if ($ProgressWorkflow -eq 'packaged-installer') {
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
        -State completed -PhaseKey setup-complete -PhaseDisplayName 'Secure agent runtime ready' `
        -Message 'RuntimeHost is installed and ready for final application validation.' -PercentComplete 100
} else {
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
        -State running -PhaseKey service-ready -PhaseDisplayName 'RuntimeHost service installed' `
        -Message 'RuntimeHost is running. C-Sweet is completing final readiness validation.' -PercentComplete 99 `
        -EstimatedRemainingMinimumSeconds 1 -EstimatedRemainingMaximumSeconds 30
}
Write-Host 'C-Sweet RuntimeHost was installed and started successfully.' -ForegroundColor Green
} catch {
    try {
        Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
            -State failed -PhaseKey install-failed -PhaseDisplayName 'Secure runtime installation failed' `
            -Message 'C-Sweet could not install the secure runtime.' -PercentComplete 100 `
            -ErrorCode 'runtime-install-failed' -ErrorMessage $_.Exception.Message
    } catch { }
    throw
}
