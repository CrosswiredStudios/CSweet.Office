[CmdletBinding()]
param([guid] $JobId = [guid]::NewGuid(), [switch] $Remove)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CSweet.OfficeRelease.ps1')
. (Join-Path $PSScriptRoot 'CSweet.OfficeStorage.ps1')

$programs = [Environment]::GetFolderPath('ProgramFiles')
$office = Join-Path $programs 'CSweet\Office'
$build = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'test-build.txt')).Trim()
$receipt = Join-Path $office 'windows-test-build.txt'
$statusFile = Join-Path $PSScriptRoot ("status-{0}.txt" -f $JobId.ToString('N'))
$work = Join-Path $PSScriptRoot 'preparation'
$lock = $null

function Report([string] $Message) {
    [IO.File]::WriteAllText($statusFile, $Message)
}
function Remove-TestDirectory([string] $Path, [string] $Allowed) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ine $Allowed) { throw 'Unexpected setup cleanup path.' }
    Assert-CSweetOfficePath $full
    if ([IO.Directory]::Exists($full)) { Remove-Item -LiteralPath $full -Recurse -Force }
}

try {
    if ($PSScriptRoot -ine (Join-Path $programs 'CSweet\Office Test Setup')) { throw 'Run the installed copy of Office test setup.' }
    Assert-CSweetOfficePath $PSScriptRoot
    Assert-CSweetOfficePath $office
    $lock = [IO.File]::Open((Join-Path $PSScriptRoot 'preparation.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $owned = [IO.File]::Exists($receipt) -and [IO.File]::ReadAllText($receipt).Trim() -ceq $build
    $services = @(Get-Service -Name 'CSweet.Office.*','CSweet.SatelliteOffice.*' -ErrorAction SilentlyContinue)
    $data = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'CSweet\Office'
    if ($Remove) {
        if ($owned) {
            if ($services.Count -or (Test-Path -LiteralPath $data) -or (Test-Path 'HKLM:\SOFTWARE\CSweet\Office\Storage')) {
                & (Join-Path $office 'Uninstall-CSweetOffice.ps1') -Elevated
            }
            if ([IO.Directory]::Exists($office)) { Remove-TestDirectory $office (Join-Path $programs 'CSweet\Office') }
        }
        Remove-TestDirectory $work (Join-Path $PSScriptRoot 'preparation')
        Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter 'status-*.txt' | Remove-Item -Force
        return
    }
    if ($owned -and [IO.File]::Exists((Join-Path $office 'payload\runtime-manifest.json'))) { return }
    if ((Test-Path -LiteralPath $office) -or $services.Count -or (Test-Path -LiteralPath $data) -or
        (Test-Path 'HKLM:\SOFTWARE\CSweet\Office\Storage') -or (Test-Path (Join-Path $programs 'CSweet\SatelliteOffice'))) {
        throw 'An existing Office or its data is present. Use a fresh Windows test PC.'
    }
    Report 'Checking Windows and Hyper-V...'
    $feature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -ErrorAction Stop
    if ($feature.State -ne 'Enabled') {
        if ($feature.State -ne 'EnablePending') {
            Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All -NoRestart | Out-Null
        }
        Report 'Save your work, restart Windows, then open C-Sweet Office (Windows test) from Start.'
        exit 3010
    }
    $manifest = Read-CSweetSignedReleaseManifest ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'office-bootstrap.p7m.b64'))) (Join-Path $PSScriptRoot 'office-release-signer.cer')
    $version = $manifest.officeVersion
    if ($version -cnotmatch '^\d+\.\d+\.\d+$' -or !$build.StartsWith("windows-test-$version-") -or
        $manifest.signing -cne 'development' -or $manifest.requiresHostCertification -ne $true) { throw 'Incompatible signed development release.' }
    $bundles = @($manifest.assets | Where-Object name -CEQ "csweet-office-$version-windows-x64.zip")
    if ($bundles.Count -ne 1) { throw 'The signed manifest is missing the Windows bundle.' }
    Assert-CSweetOfficeAsset $bundles[0] $version 'windows-x64' $true
    # Always work in a machine-owned directory. The final rename on the same volume makes the payload visible atomically.
    Remove-TestDirectory $work (Join-Path $PSScriptRoot 'preparation')
    [IO.Directory]::CreateDirectory($work) | Out-Null
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($office))
    if ($drive.AvailableFreeSpace -lt 64GB) { throw 'Free at least 64 GiB on the Windows system drive, then reopen Office test setup.' }
    Report 'Downloading and checking the Windows guest bundle. This is a multi-gigabyte download.'
    $bundle = Receive-CSweetOfficeAsset $bundles[0] (Join-Path $work 'bundle')
    $scripts = Join-Path $bundle 'scripts\windows'
    $result = Join-Path $work 'payload-path.txt'
    Report 'Testing Hyper-V isolation and preparing the certified payload...'
    & (Join-Path $scripts 'Initialize-CSweetWindowsIsolationTest.ps1') -PrebuiltRoot $bundle -WorkingRoot $work -SkipInstall -NoElevation -DevelopmentTools -PayloadResultPath $result -ProgressJobId $JobId
    if (!(Test-Path -LiteralPath $result -PathType Leaf)) { throw 'Preparation did not finish. Check Hyper-V, restart Windows if required, and open Office again.' }
    $payload = [IO.Path]::GetFullPath([IO.File]::ReadAllText($result).Trim())
    if (!$payload.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase) -or
        !(Test-Path (Join-Path $payload 'runtime-manifest.json') -PathType Leaf)) { throw 'Missing certified Windows payload.' }
    $ready = [IO.Directory]::CreateDirectory((Join-Path $work 'ready')).FullName
    [IO.Directory]::Move($payload, (Join-Path $ready 'payload'))
    Copy-Item -Path (Join-Path $scripts '*.ps1') -Destination $ready
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'office-release-signer.cer') -Destination $ready
    [IO.File]::WriteAllText((Join-Path $ready 'windows-test-build.txt'), $build)
    [IO.Directory]::Move($ready, $office)
    Report 'Office is ready to connect to Headquarters.'
} catch {
    if ($lock -and !$Remove) { Report $_.Exception.Message }
    Write-Error $_ -ErrorAction Continue
    exit 1
} finally {
    if ($lock) {
        try {
            if (!$Remove) { Remove-TestDirectory $work (Join-Path $PSScriptRoot 'preparation') }
        } finally {
            $lock.Dispose()
            if ($Remove) { Remove-Item -LiteralPath (Join-Path $PSScriptRoot 'preparation.lock') -ErrorAction SilentlyContinue }
        }
    }
}
