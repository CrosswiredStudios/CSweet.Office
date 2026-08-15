[CmdletBinding()]
param(
    [switch] $Force,
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
    $process = Start-Process -FilePath $powershell -Verb RunAs -Wait -PassThru -ArgumentList ($arguments -join ' ')
    if ($process.ExitCode -ne 0) { throw "Execution fleet uninstall failed with exit code $($process.ExitCode)." }
    return
}

$nodeRoot = Join-Path $env:ProgramData 'CSweet\Office'
$programFilesRoot = Join-Path $env:ProgramFiles 'CSweet\Office'
$runtimeDataRoot = Join-Path $env:ProgramData 'CSweet\Office'
$hyperVDataRoot = Join-Path $runtimeDataRoot 'hyperv'
$nodeService = Get-Service -Name 'CSweet.Office.Node' -ErrorAction SilentlyContinue
$runtimeService = Get-Service -Name 'CSweet.Office.RuntimeHost' -ErrorAction SilentlyContinue
$fleetInstalled = $null -ne $nodeService -or $null -ne $runtimeService -or
    (Test-Path -LiteralPath $programFilesRoot) -or (Test-Path -LiteralPath $nodeRoot)
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

foreach ($serviceName in @('CSweet.Office.Node', 'CSweet.Office.RuntimeHost')) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}

Remove-OfficeHyperVResources $hyperVDataRoot

$runtimeHostServiceName = 'CSweet.Office.RuntimeHost'
$runtimeHostServiceIdentity = "NT SERVICE\$runtimeHostServiceName"
$hyperVAdministratorsSid = 'S-1-5-32-578'
$runtimeMembership = Get-LocalGroupMember -SID $hyperVAdministratorsSid `
    -Member $runtimeHostServiceIdentity -ErrorAction SilentlyContinue
if ($null -ne $runtimeMembership) {
    Remove-LocalGroupMember -SID $hyperVAdministratorsSid `
        -Member $runtimeHostServiceIdentity -Confirm:$false -ErrorAction Stop
}

foreach ($serviceName in @('CSweet.Office.Node', 'CSweet.Office.RuntimeHost')) {
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

foreach ($path in @($programFilesRoot, $runtimeDataRoot) | Select-Object -Unique) {
    Remove-InstalledDirectory $path
}

Write-Host 'C-Sweet Office was fully uninstalled, including installed versions, cached artifacts, guest disks, and owned Hyper-V VMs. Repository build artifacts were not removed. Revoke the host in fleet administration if it was not already revoked.' -ForegroundColor Green
