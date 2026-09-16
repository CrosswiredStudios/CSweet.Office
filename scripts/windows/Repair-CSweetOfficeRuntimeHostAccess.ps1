[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ControlPlaneUserSid,
    [string] $InstallRoot = "$env:ProgramFiles\CSweet\Office",
    [string] $DataRoot = "$env:ProgramData\CSweet\Office",
    [string] $ProgressPath,
    [guid] $ProgressJobId = [guid]::Empty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This repair must run from the C-Sweet administrator prompt.'
    }
}

function Resolve-ProtectedInstalledPath([string] $Root, [string] $Candidate) {
    if ([String]::IsNullOrWhiteSpace($Candidate) -or -not [IO.Path]::IsPathRooted($Candidate)) {
        throw 'The installed RuntimeHost path is invalid.'
    }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Candidate)
    if (-not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The installed RuntimeHost path is outside the protected installation directory.'
    }
    return $fullPath
}

function Resolve-ServiceSid([string] $ServiceName) {
    $output = @(& "$env:SystemRoot\System32\sc.exe" showsid $ServiceName 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "The Windows service SID could not be resolved for $ServiceName." }
    $match = [Regex]::Match(($output -join ' '), 'S-1-5-80-(?:[0-9]+-){4}[0-9]+')
    if (-not $match.Success) { throw "Windows returned an invalid service SID for $ServiceName." }
    return $match.Value
}

function Invoke-Sc([string[]] $Arguments) {
    $output = @(& "$env:SystemRoot\System32\sc.exe" @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output | Out-Host
    if ($exitCode -ne 0) {
        throw "Windows service configuration failed while running sc.exe $($Arguments -join ' ')."
    }
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
    & "$env:SystemRoot\System32\icacls.exe" $Root '/inheritance:r' '/grant:r' `
        "*$RuntimeHostSid`:RX" "*$NodeSid`:RX" `
        '*S-1-5-18:F' '*S-1-5-32-544:F' '/T' '/C' '/Q' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The installed Office package permissions could not be repaired.' }

    $directories = @((Get-Item -LiteralPath $Root -Force)) +
        @(Get-ChildItem -LiteralPath $Root -Directory -Recurse -Force)
    foreach ($directory in $directories) {
        & "$env:SystemRoot\System32\icacls.exe" $directory.FullName '/inheritance:r' '/grant:r' `
            "*$RuntimeHostSid`:(OI)(CI)RX" "*$NodeSid`:(OI)(CI)RX" `
            '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '/Q' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "The installed package directory permissions could not be repaired: $($directory.FullName)" }
    }
}

function Grant-HyperVGuestImageReadAccess([string] $GuestImagePath) {
    $virtualMachinesSid = 'S-1-5-83-0'
    $guestImageDirectory = Split-Path -Parent $GuestImagePath
    & "$env:SystemRoot\System32\icacls.exe" $guestImageDirectory '/grant:r' "*$virtualMachinesSid`:RX" '/Q' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The Hyper-V guest-image directory access could not be repaired.' }
    & "$env:SystemRoot\System32\icacls.exe" $GuestImagePath '/grant:r' "*$virtualMachinesSid`:R" '/Q' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The Hyper-V guest-image access could not be repaired.' }
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

function Set-ServiceVirtualAccount([string] $ServiceName) {
    $serviceConfiguration = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
    if ($null -eq $serviceConfiguration) { throw "The Windows service configuration could not be loaded for $ServiceName." }
    $changeResult = Invoke-CimMethod -InputObject $serviceConfiguration -MethodName Change -Arguments @{
        StartName = "NT SERVICE\$ServiceName"
        StartPassword = $null
    }
    if ($null -eq $changeResult -or [int]$changeResult.ReturnValue -ne 0) {
        $returnValue = if ($null -eq $changeResult) { 'no result' } else { [string]$changeResult.ReturnValue }
        throw "The virtual service account could not be configured for $ServiceName. Win32_Service.Change returned $returnValue."
    }
}

function Stop-OrphanedOfficeProcesses([string] $ProtectedInstallRoot) {
    $rootPath = [IO.Path]::GetFullPath($ProtectedInstallRoot).TrimEnd('\') + '\'
    $serviceProcessIds = @(Get-CimInstance -ClassName Win32_Service |
        Where-Object { $_.Name -in @('CSweet.Office.RuntimeHost', 'CSweet.Office.Node') -and [int]$_.ProcessId -gt 0 } |
        ForEach-Object { [int]$_.ProcessId })
    $processNames = @('CSweet.Office.RuntimeHost.exe', 'CSweet.Office.Node.exe')
    foreach ($process in @(Get-CimInstance -ClassName Win32_Process | Where-Object { $_.Name -in $processNames })) {
        if ([int]$process.ProcessId -in $serviceProcessIds -or [String]::IsNullOrWhiteSpace([string]$process.ExecutablePath)) {
            continue
        }
        $executablePath = [IO.Path]::GetFullPath([string]$process.ExecutablePath)
        if (-not $executablePath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction Stop
    }
}

Assert-Administrator
try {
    $controlPlaneIdentity = [Security.Principal.SecurityIdentifier]::new($ControlPlaneUserSid)
    $null = $controlPlaneIdentity.Translate([Security.Principal.NTAccount])
} catch {
    throw 'The C-Sweet control-plane Windows user identity is invalid.'
}
if ($null -eq $ProgressJobId -or $ProgressJobId -eq [guid]::Empty) {
    $ProgressJobId = [guid]::NewGuid()
}
if ([String]::IsNullOrWhiteSpace($ProgressPath)) {
    $ProgressPath = Join-Path $env:ProgramData "CSweet\Setup\windows-isolation-$($ProgressJobId.ToString('N')).json"
}
. (Join-Path $PSScriptRoot 'CSweet.WindowsSetupProgress.ps1')
$ProgressPath = Initialize-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId `
    -ControlPlaneUserSid $ControlPlaneUserSid
$restartNodeService = $true

try {
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow 'access-repair' `
        -State running -PhaseKey repair-runtime-access -PhaseDisplayName 'Refreshing secure runtime access' `
        -Message 'C-Sweet is updating protected RuntimeHost access for this Windows account.' -PercentComplete 98 `
        -EstimatedRemainingMinimumSeconds 5 -EstimatedRemainingMaximumSeconds 60

    $serviceName = 'CSweet.Office.RuntimeHost'
    $runtimeHostServiceSid = Resolve-ServiceSid $serviceName
    $nodeServiceName = 'CSweet.Office.Node'
    $nodeServiceSid = Resolve-ServiceSid $nodeServiceName
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) { throw 'The RuntimeHost Windows service is not installed.' }
    $nodeService = Get-Service -Name $nodeServiceName -ErrorAction SilentlyContinue
    if ($null -eq $nodeService) { throw 'The Office Node Windows service is not installed.' }

    $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $imagePath = [string](Get-ItemProperty -LiteralPath $serviceRegistryPath -Name ImagePath).ImagePath
    $match = [regex]::Match($imagePath, '^"(?<exe>[^"]+)"\s+--contentRoot\s+"(?<root>[^"]+)"$')
    if (-not $match.Success) { throw 'The installed RuntimeHost service command is invalid.' }

    $InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
    $contentRoot = Resolve-ProtectedInstalledPath $InstallRoot $match.Groups['root'].Value
    $runtimeHostExe = Resolve-ProtectedInstalledPath $InstallRoot $match.Groups['exe'].Value
    $expectedExecutable = Join-Path $contentRoot 'runtime\CSweet.Office.RuntimeHost.exe'
    if (-not $runtimeHostExe.Equals([IO.Path]::GetFullPath($expectedExecutable), [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $runtimeHostExe -PathType Leaf)) {
        throw 'The installed RuntimeHost executable could not be verified.'
    }
    Stop-OrphanedOfficeProcesses -ProtectedInstallRoot $InstallRoot

    # Repair the validated installation root before reading configuration. A
    # previous interrupted repair can leave appsettings.json inaccessible even
    # though the service registration and executable are still trustworthy.
    Invoke-Sc @('sidtype', $serviceName, 'unrestricted')
    Invoke-Sc @('sidtype', $nodeServiceName, 'unrestricted')
    Grant-RuntimeHostHyperVAccess -ServiceName $serviceName
    Set-ProtectedPackageAcl -Root $contentRoot -RuntimeHostSid $runtimeHostServiceSid -NodeSid $nodeServiceSid
    Assert-FileReadExecuteAce -Path $runtimeHostExe -Sid $runtimeHostServiceSid

    $configurationPath = Join-Path $contentRoot 'appsettings.json'
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        throw 'The installed RuntimeHost configuration is missing.'
    }
    # Remove any stale explicit ACE left by an interrupted earlier repair, then
    # convert the hardened parent's four inherited ACEs back to explicit ACEs.
    & "$env:SystemRoot\System32\icacls.exe" $configurationPath '/reset' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The installed RuntimeHost configuration permissions could not be reset.' }
    & "$env:SystemRoot\System32\icacls.exe" $configurationPath '/inheritance:r' '/grant:r' `
        "*$runtimeHostServiceSid`:R" "*$nodeServiceSid`:R" `
        '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The installed RuntimeHost configuration permissions could not be secured.' }
    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    if ($null -eq $configuration.CSweet -or $null -eq $configuration.CSweet.Office -or
        $null -eq $configuration.CSweet.Office.RuntimeHost) {
        throw 'The installed RuntimeHost configuration is invalid.'
    }
    $hyperVConfiguration = $configuration.CSweet.Office.Providers.HyperV
    if ($null -eq $hyperVConfiguration -or [String]::IsNullOrWhiteSpace([string]$hyperVConfiguration.GuestImagePath)) {
        throw 'The installed Hyper-V guest-image configuration is invalid.'
    }
    $guestImagePath = Resolve-ProtectedInstalledPath -Root $contentRoot -Candidate ([string]$hyperVConfiguration.GuestImagePath)
    if (-not (Test-Path -LiteralPath $guestImagePath -PathType Leaf)) {
        throw 'The installed Hyper-V guest image is missing.'
    }
    Grant-HyperVGuestImageReadAccess -GuestImagePath $guestImagePath
    $runtimeHostConfiguration = $configuration.CSweet.Office.RuntimeHost
    $allowedClientSidProperty = $runtimeHostConfiguration.PSObject.Properties['AllowedClientSid']
    $allowedClientSidsProperty = $runtimeHostConfiguration.PSObject.Properties['AllowedClientSids']
    $currentAllowedClientSid = if ($null -eq $allowedClientSidProperty) { '' } else { [string]$allowedClientSidProperty.Value }
    $currentAllowedClientSids = if ($null -eq $allowedClientSidsProperty) { @() } else { @($allowedClientSidsProperty.Value) }
    $requiredAllowedClientSids = @($ControlPlaneUserSid, $nodeServiceSid)
    $configurationNeedsUpdate = -not $currentAllowedClientSid.Equals($ControlPlaneUserSid, [StringComparison]::OrdinalIgnoreCase) -or
        $currentAllowedClientSids.Count -ne $requiredAllowedClientSids.Count -or
        @($requiredAllowedClientSids | Where-Object { $_ -notin $currentAllowedClientSids }).Count -ne 0
    if ($configurationNeedsUpdate) {
        $runtimeHostConfiguration.AllowedClientSid = $ControlPlaneUserSid
        if ($null -eq $allowedClientSidsProperty) {
            $runtimeHostConfiguration | Add-Member -NotePropertyName AllowedClientSids -NotePropertyValue $requiredAllowedClientSids
        } else {
            $runtimeHostConfiguration.AllowedClientSids = $requiredAllowedClientSids
        }
    }

    if ($nodeService.Status -ne 'Stopped') {
        Stop-Service -Name $nodeServiceName -Force
        $nodeService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Set-ServiceVirtualAccount -ServiceName $serviceName
    Set-ServiceVirtualAccount -ServiceName $nodeServiceName
    # Rewrite the existing file so its protected ACL remains attached to the
    # same filesystem entry. Replacing it requires delete access on the parent
    # directory and fails on hardened installations even for an elevated repair.
    if ($configurationNeedsUpdate) {
        [IO.File]::WriteAllText($configurationPath,
            ($configuration | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    }

    $DataRoot = [IO.Path]::GetFullPath($DataRoot)
    $keyPath = Join-Path $DataRoot 'runtime-host.key'
    if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
        throw 'The RuntimeHost authentication key is missing.'
    }
    & "$env:SystemRoot\System32\icacls.exe" $keyPath '/inheritance:r' '/grant:r' "*$ControlPlaneUserSid`:R" "*$nodeServiceSid`:R" "*$runtimeHostServiceSid`:R" '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The RuntimeHost key permissions could not be repaired.' }
    foreach ($artifactRoot in @((Join-Path $DataRoot 'artifacts'), (Join-Path $DataRoot 'artifact-media'))) {
        if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) { continue }
        & "$env:SystemRoot\System32\icacls.exe" $artifactRoot '/inheritance:r' '/grant:r' "*$nodeServiceSid`:(OI)(CI)M" "*$runtimeHostServiceSid`:(OI)(CI)R" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "The RuntimeHost artifact permissions could not be repaired: $artifactRoot" }
    }
    $hyperVDataRoot = Join-Path $DataRoot 'hyperv'
    if (Test-Path -LiteralPath $hyperVDataRoot -PathType Container) {
        & "$env:SystemRoot\System32\icacls.exe" $hyperVDataRoot '/inheritance:r' '/grant:r' "*$runtimeHostServiceSid`:(OI)(CI)M" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'The RuntimeHost Hyper-V state permissions could not be repaired.' }
    }
    $nodeDataRoot = Join-Path $DataRoot 'node'
    if (Test-Path -LiteralPath $nodeDataRoot -PathType Container) {
        & "$env:SystemRoot\System32\icacls.exe" $nodeDataRoot '/inheritance:r' '/grant:r' "*$nodeServiceSid`:(OI)(CI)M" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'The Office Node state permissions could not be repaired.' }
    }

    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    if ($restartNodeService) {
        Start-Service -Name $nodeServiceName
        (Get-Service -Name $nodeServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    }
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow 'access-repair' `
        -State completed -PhaseKey repair-complete -PhaseDisplayName 'Secure runtime access refreshed' `
        -Message 'RuntimeHost access is ready for automatic validation.' -PercentComplete 100
    Write-Host 'C-Sweet RuntimeHost access was repaired successfully.' -ForegroundColor Green
} catch {
    try {
        $stoppedService = Get-Service -Name 'CSweet.Office.RuntimeHost' -ErrorAction SilentlyContinue
        if ($null -ne $stoppedService -and $stoppedService.Status -ne 'Running') {
            Start-Service -Name 'CSweet.Office.RuntimeHost'
        }
    } catch { }
    try {
        $stoppedNodeService = Get-Service -Name 'CSweet.Office.Node' -ErrorAction SilentlyContinue
        if ($restartNodeService -and $null -ne $stoppedNodeService -and $stoppedNodeService.Status -ne 'Running') {
            Start-Service -Name 'CSweet.Office.Node'
        }
    } catch { }
    try {
        Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow 'access-repair' `
            -State failed -PhaseKey repair-failed -PhaseDisplayName 'Secure runtime repair needs setup' `
            -Message 'C-Sweet could not refresh the existing RuntimeHost installation.' -PercentComplete 100 `
            -ErrorCode 'runtime-access-repair-failed' -ErrorMessage $_.Exception.Message
    } catch { }
    Write-Error $_.Exception.Message
    exit 1
}
