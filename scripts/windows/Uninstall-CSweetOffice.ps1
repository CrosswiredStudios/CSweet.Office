[CmdletBinding()]
param(
    [switch] $Force,
    [string] $ProgressPath,
    [guid] $ProgressJobId = [guid]::Empty,
    [string] $ProgressWorkflow = 'office-removal',
    [switch] $Elevated
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    if ($Elevated) { throw 'Administrator approval is required.' }
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        ('"' + $PSCommandPath + '"'), '-Elevated')
    if ($Force) { $arguments += '-Force' }
    if (-not [String]::IsNullOrWhiteSpace($ProgressPath)) {
        $arguments += @('-ProgressPath', ('"' + $ProgressPath + '"'))
    }
    if ($ProgressJobId -ne [guid]::Empty) {
        $arguments += @('-ProgressJobId', $ProgressJobId.ToString('D'))
    }
    if (-not [String]::IsNullOrWhiteSpace($ProgressWorkflow)) {
        $arguments += @('-ProgressWorkflow', $ProgressWorkflow)
    }
    $process = Start-Process -FilePath $powershell -Verb RunAs -Wait -PassThru -ArgumentList ($arguments -join ' ')
    if ($process.ExitCode -ne 0) { throw "Execution fleet uninstall failed with exit code $($process.ExitCode)." }
    return
}

$progressEnabled = -not [String]::IsNullOrWhiteSpace($ProgressPath) -and
    $ProgressJobId -ne [guid]::Empty
if ($progressEnabled) {
    . (Join-Path $PSScriptRoot 'CSweet.WindowsSetupProgress.ps1')
}
function Write-OfficeRemovalProgress {
    param(
        [Parameter(Mandatory = $true)][string] $PhaseKey,
        [Parameter(Mandatory = $true)][string] $PhaseDisplayName,
        [Parameter(Mandatory = $true)][string] $Message,
        [Parameter(Mandatory = $true)][ValidateRange(0, 100)][int] $PercentComplete,
        [Nullable[int]] $EstimatedRemainingMinimumSeconds,
        [Nullable[int]] $EstimatedRemainingMaximumSeconds
    )
    if (-not $progressEnabled) { return }
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow $ProgressWorkflow `
        -State running -PhaseKey $PhaseKey -PhaseDisplayName $PhaseDisplayName -Message $Message `
        -PercentComplete $PercentComplete `
        -EstimatedRemainingMinimumSeconds $EstimatedRemainingMinimumSeconds `
        -EstimatedRemainingMaximumSeconds $EstimatedRemainingMaximumSeconds
}

$nodeRoot = Join-Path $env:ProgramData 'CSweet\Office'
$programFilesRoot = Join-Path $env:ProgramFiles 'CSweet\Office'
$runtimeDataRoot = Join-Path $env:ProgramData 'CSweet\Office'
$hyperVDataRoot = Join-Path $runtimeDataRoot 'hyperv'
$legacyNodeRoot = Join-Path $env:ProgramData 'CSweet\SatelliteOffice'
$legacyProgramFilesRoot = Join-Path $env:ProgramFiles 'CSweet\SatelliteOffice'
$legacyHyperVDataRoot = Join-Path $legacyNodeRoot 'hyperv'
$officeServiceNames = @(
    'CSweet.Office.Maintenance',
    'CSweet.Office.Node',
    'CSweet.Office.RuntimeHost',
    'CSweet.SatelliteOffice.Node',
    'CSweet.SatelliteOffice.RuntimeHost'
)
$fleetInstalled = @($officeServiceNames | Where-Object {
    $null -ne (Get-Service -Name $_ -ErrorAction SilentlyContinue)
}).Count -gt 0 -or (Test-Path -LiteralPath $programFilesRoot) -or
    (Test-Path -LiteralPath $nodeRoot) -or (Test-Path -LiteralPath $legacyProgramFilesRoot) -or
    (Test-Path -LiteralPath $legacyNodeRoot)
$protectedNodeRoot = Join-Path $nodeRoot 'node'
$maintenance = if (Test-Path -LiteralPath $protectedNodeRoot -PathType Container) {
    Join-Path $protectedNodeRoot 'maintenance'
} else { Join-Path $nodeRoot 'maintenance' }
$drainPath = Join-Path $maintenance 'drain-state'
$activeRoot = Join-Path $maintenance 'active-assignments'
$drainState = if (Test-Path -LiteralPath $drainPath -PathType Leaf) {
    [IO.File]::ReadAllText($drainPath).Trim()
} else { '' }
$activeCount = if (Test-Path -LiteralPath $activeRoot -PathType Container) {
    @(Get-ChildItem -LiteralPath $activeRoot -File -Filter '*.active').Count
} else { 0 }
if ($fleetInstalled -and -not $Force -and ($drainState -ne 'draining' -or $activeCount -ne 0)) {
    throw 'Drain this node in C-Sweet and wait for active assignments to reach zero before uninstalling. Use -Force only after revocation.'
}

function Test-PathWithinRoot([string] $Candidate, [string] $Root) {
    if ([String]::IsNullOrWhiteSpace($Candidate)) { return $false }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $candidatePath = [IO.Path]::GetFullPath($Candidate).TrimEnd('\')
    return $candidatePath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $candidatePath.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Remove-OfficeHyperVResources([string] $DataRoot) {
    if (-not (Test-Path -LiteralPath $DataRoot -PathType Container)) { return }
    if ($null -eq (Get-Module -ListAvailable -Name Hyper-V)) { return }

    Import-Module Hyper-V -ErrorAction Stop
    $ownedVms = @(Get-VM -ErrorAction Stop | Where-Object {
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
        @($paths | Where-Object { Test-PathWithinRoot $_ $DataRoot }).Count -gt 0
    })

    foreach ($vm in $ownedVms) {
        Write-Host "Removing C-Sweet Office Hyper-V VM '$($vm.Name)'..."
        if ($vm.State -ne [Microsoft.HyperV.PowerShell.VMState]::Off) {
            Stop-VM -VM $vm -TurnOff -Force -Confirm:$false -ErrorAction Stop
        }
        Remove-VM -VM $vm -Force -Confirm:$false -ErrorAction Stop
    }

    Get-ChildItem -LiteralPath $DataRoot -Recurse -File -Include '*.vhd','*.vhdx' -ErrorAction SilentlyContinue |
        ForEach-Object {
            $virtualDisk = Get-VHD -Path $_.FullName -ErrorAction SilentlyContinue
            if ($null -ne $virtualDisk -and $virtualDisk.Attached) {
                Dismount-VHD -Path $_.FullName -ErrorAction Stop
            }
        }
}

function Remove-InstalledDirectory([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            break
        }
        catch [IO.IOException] {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Milliseconds 500
        }
        catch [UnauthorizedAccessException] {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
    if (Test-Path -LiteralPath $Path) {
        throw "Uninstall did not completely remove '$Path'."
    }
}

Write-OfficeRemovalProgress -PhaseKey 'stop-office-services' -PhaseDisplayName 'Stopping Office services' `
    -Message 'Windows is stopping the existing Office before removal.' -PercentComplete 10 `
    -EstimatedRemainingMinimumSeconds 5 -EstimatedRemainingMaximumSeconds 60
foreach ($serviceName in $officeServiceNames) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}

Write-OfficeRemovalProgress -PhaseKey 'remove-office-workloads' -PhaseDisplayName 'Removing Office workloads' `
    -Message 'Windows is removing Office-owned virtual machines and mutable runtime disks.' `
    -PercentComplete 35 -EstimatedRemainingMinimumSeconds 5 -EstimatedRemainingMaximumSeconds 90
Remove-OfficeHyperVResources $hyperVDataRoot
Remove-OfficeHyperVResources $legacyHyperVDataRoot

$hyperVAdministratorsSid = 'S-1-5-32-578'
foreach ($runtimeHostServiceName in @('CSweet.Office.RuntimeHost', 'CSweet.SatelliteOffice.RuntimeHost')) {
    $runtimeHostServiceIdentity = "NT SERVICE\$runtimeHostServiceName"
    $runtimeMembership = Get-LocalGroupMember -SID $hyperVAdministratorsSid `
        -Member $runtimeHostServiceIdentity -ErrorAction SilentlyContinue
    if ($null -ne $runtimeMembership) {
        Remove-LocalGroupMember -SID $hyperVAdministratorsSid `
            -Member $runtimeHostServiceIdentity -Confirm:$false -ErrorAction Stop
    }
}

Write-OfficeRemovalProgress -PhaseKey 'remove-office-registration' -PhaseDisplayName 'Removing Office registration' `
    -Message 'Windows is removing Office services, privileges, and protocol registration.' `
    -PercentComplete 60 -EstimatedRemainingMinimumSeconds 3 -EstimatedRemainingMaximumSeconds 45
foreach ($serviceName in $officeServiceNames) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        & "$env:SystemRoot\System32\sc.exe" delete $serviceName | Out-Host
        if ($LASTEXITCODE -notin @(0, 1060)) { throw "The $serviceName service could not be removed." }
    }
}

$serviceId = '00000ac9-facb-11e6-bd58-64006a7986d3'
$serviceRegistryPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\$serviceId"
Remove-Item -LiteralPath $serviceRegistryPath -Recurse -Force -ErrorAction SilentlyContinue
foreach ($name in @('CSWEET_HYPERV_BROKER_SERVICE_ID', 'CSWEET_HYPERV_DATA_ROOT', 'CSWEET_ARTIFACT_MEDIA_ROOT')) {
    [Environment]::SetEnvironmentVariable($name, $null, 'Machine')
}

Remove-Item -LiteralPath 'HKLM:\Software\Classes\csweet-office' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKLM:\Software\CSweet\Office' -Recurse -Force -ErrorAction SilentlyContinue

Write-OfficeRemovalProgress -PhaseKey 'remove-office-data' -PhaseDisplayName 'Removing Office data' `
    -Message 'Windows is deleting Office identities, caches, runtime data, and installed files.' `
    -PercentComplete 80 -EstimatedRemainingMinimumSeconds 2 -EstimatedRemainingMaximumSeconds 60
foreach ($path in @($programFilesRoot, $runtimeDataRoot, $legacyProgramFilesRoot, $legacyNodeRoot) | Select-Object -Unique) {
    Remove-InstalledDirectory $path
}

Write-OfficeRemovalProgress -PhaseKey 'office-removed-locally' -PhaseDisplayName 'Office removal complete' `
    -Message 'Windows removed the existing Office. C-Sweet is confirming completion.' `
    -PercentComplete 95 -EstimatedRemainingMinimumSeconds 0 -EstimatedRemainingMaximumSeconds 15
Write-Host 'C-Sweet Office was fully uninstalled, including installed versions, cached artifacts, guest disks, and owned Hyper-V VMs. Repository build artifacts were not removed. Revoke the host in fleet administration if it was not already revoked.' -ForegroundColor Green
