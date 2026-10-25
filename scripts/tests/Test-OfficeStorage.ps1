[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../windows/CSweet.OfficeStorage.ps1')

# Execute the real resolver against a deterministic disk inventory, without touching services/registry.
$script:installed = $null
function Get-CSweetInstalledOfficeStorage { $script:installed }
function Get-CSweetOfficeVolumes { $script:volumes }
function Assert-Equal($actual, $expected) { if ($actual -ne $expected) { throw "Expected '$expected', got '$actual'." } }
function Assert-Rejected([scriptblock] $Action, [string] $Message) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -notlike "*$Message*") { throw }; return }
    throw "Expected rejection containing '$Message'."
}
$system = [pscustomobject]@{ VolumeId = 'system'; MountPath = "$env:SystemDrive\"; IsSystemVolume = $true; IsEligible = $true; FreeBytes = 200GB; InstallRoot = (Join-Path $env:ProgramFiles 'CSweet\Office'); DataRoot = (Join-Path $env:ProgramData 'CSweet\Office') }
$other = [pscustomobject]@{ VolumeId = 'other'; MountPath = 'Z:\'; IsSystemVolume = $false; IsEligible = $true; FreeBytes = 400GB; InstallRoot = 'Z:\CSweet\Office\program'; DataRoot = 'Z:\CSweet\Office\data' }
$script:volumes = @($system, $other)
Assert-Equal (Resolve-CSweetOfficeStorage).VolumeId 'system'
Assert-Equal (Resolve-CSweetOfficeStorage -VolumeId other).DataRoot 'Z:\CSweet\Office\data'
$system.FreeBytes = 10GB
Assert-Equal (Resolve-CSweetOfficeStorage).VolumeId 'other'
Assert-Rejected { Resolve-CSweetOfficeStorage -VolumeId system } 'free space'
Assert-Rejected { Resolve-CSweetOfficeStorage -VolumeId missing } 'unavailable'
Assert-Rejected { Resolve-CSweetOfficeStorage -VolumeId other -RequiredDiskMb 500000 } 'free space'
Assert-Rejected { Resolve-CSweetOfficeStorage -VolumeId other -DataRoot 'Z:\' } 'directories'
Assert-Rejected { Resolve-CSweetOfficeStorage -VolumeId other -InstallRoot 'Z:\CSweet\Office\..\..\Windows' } 'directories'
$other.IsEligible = $false
Assert-Rejected { Resolve-CSweetOfficeStorage -VolumeId other } 'unsuitable'
$other.IsEligible = $true
$script:installed = [pscustomobject]@{ VolumeId = 'other'; InstallRoot = $other.InstallRoot; DataRoot = $other.DataRoot }
Assert-Rejected { Resolve-CSweetOfficeStorage -VolumeId system } 'Moving'
$other.FreeBytes = 1GB
Assert-Equal (Resolve-CSweetOfficeStorage -SkipSpaceCheck).VolumeId 'other'
$script:volumes = @($system)
Assert-Rejected { Resolve-CSweetOfficeStorage -SkipSpaceCheck } 'unavailable'
Write-Host 'Office storage resolver: 12 behavior checks passed.'
