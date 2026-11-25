[CmdletBinding()]
param([guid] $JobId = [guid]::NewGuid(), [switch] $Remove, [switch] $CheckOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CSweet.OfficeRelease.ps1')
. (Join-Path $PSScriptRoot 'CSweet.OfficeStorage.ps1')

$programs = [Environment]::GetFolderPath('ProgramFiles')
$office = Join-Path $programs 'CSweet\Office'
$build = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'test-build.txt')).Trim()
$receipt = Join-Path $office 'windows-test-build.txt'
$statusFile = Join-Path $PSScriptRoot ("status-{0}.txt" -f $JobId.ToString('N'))
$diagnosticFile = Join-Path $PSScriptRoot ("certification-{0}.log" -f $JobId.ToString('N'))
$work = Join-Path $PSScriptRoot 'preparation'
$lock = $null

function Report([string] $Message) {
    [IO.File]::WriteAllText($statusFile, $Message)
}
function Test-OfficeDataPresent([string] $Path) {
    try {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    } catch [Management.Automation.ItemNotFoundException] {
        return $false
    }
    Assert-CSweetOfficePath $Path
    if (!$item.PSIsContainer) { return $true }
    return $null -ne (Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop | Select-Object -First 1)
}
function Remove-TestDirectory([string] $Path, [string] $Allowed) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ine $Allowed) { throw 'Unexpected setup cleanup path.' }
    Assert-CSweetOfficePath $full
    if ([IO.Directory]::Exists($full)) { Remove-Item -LiteralPath $full -Recurse -Force }
}
function Save-CertificationLog([string] $WorkRoot, [string] $Destination) {
    Assert-CSweetOfficePath $WorkRoot
    $run = Get-ChildItem -LiteralPath $WorkRoot -Directory -Filter 'certification-*' |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (!$run) { return $false }
    $source = Join-Path $run.FullName 'certification.log'
    Assert-CSweetOfficePath $source
    if (![IO.File]::Exists($source)) { return $false }
    Assert-CSweetOfficePath $Destination
    [IO.File]::Copy($source, $Destination)
    return $true
}

try {
    if ($PSScriptRoot -ine (Join-Path $programs 'CSweet\Office Setup')) { throw 'Open C-Sweet Office from Start to run setup.' }
    Assert-CSweetOfficePath $PSScriptRoot
    Assert-CSweetOfficePath $office
    $lock = [IO.File]::Open((Join-Path $PSScriptRoot 'preparation.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $owned = [IO.File]::Exists($receipt) -and [IO.File]::ReadAllText($receipt).Trim() -ceq $build
    $services = @(Get-Service -Name 'CSweet.Office.*','CSweet.SatelliteOffice.*' -ErrorAction SilentlyContinue)
    $data = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'CSweet\Office'
    if ($Remove) {
        if ($owned) {
            # The user-session page can outlive its browser and keep the prepared Node EXE open.
            $uiNode = Join-Path $office 'payload\node\CSweet.Office.Node.exe'
            $trayScript = [regex]::Escape((Join-Path $PSScriptRoot 'Open-WindowsTestOffice.ps1'))
            foreach ($process in Get-CimInstance Win32_Process -Filter "Name='CSweet.Office.Node.exe' OR Name='powershell.exe'") {
                if (($process.ExecutablePath -ieq $uiNode -and $process.CommandLine -match '\s--open-office\s*$') -or
                    ($process.Name -ieq 'powershell.exe' -and $process.CommandLine -match ('\s-File\s+"' + $trayScript + '"\s*$'))) {
                    Stop-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
                }
            }
            if ($services.Count -or (Test-Path 'HKLM:\SOFTWARE\CSweet\Office\Storage') -or (Test-OfficeDataPresent $data)) {
                & (Join-Path $office 'Uninstall-CSweetOffice.ps1') -Elevated
            }
            if ([IO.Directory]::Exists($office)) { Remove-TestDirectory $office (Join-Path $programs 'CSweet\Office') }
        }
        Remove-TestDirectory $work (Join-Path $PSScriptRoot 'preparation')
        Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter 'status-*.txt' | Remove-Item -Force
        Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter 'certification-*.log' | Remove-Item -Force
        return
    }
    if ($owned -and [IO.File]::Exists((Join-Path $office 'payload\runtime-manifest.json'))) { return }
    $legacyOffice = Join-Path $programs 'CSweet\SatelliteOffice'
    if ($services.Count) {
        throw "An Office service is still installed: $($services[0].Name). Uninstall the earlier Office in Windows Settings, then try again."
    }
    if (Test-Path 'HKLM:\SOFTWARE\CSweet\Office\Storage') {
        throw 'An earlier Office still has registered storage. Uninstall that Office in Windows Settings, then try again.'
    }
    foreach ($installPath in @($office, $legacyOffice)) {
        if (Test-Path -LiteralPath $installPath) {
            throw "Earlier Office installation files remain at $installPath. Finish uninstalling the earlier Office, then try again."
        }
    }
    if (Test-OfficeDataPresent $data) {
        throw "Earlier Office data remains at $data. If the previous Office was uninstalled, back up its data before moving it and trying again."
    }
    if ($CheckOnly) { return }
    Report 'Checking Windows and Hyper-V...'
    $feature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -ErrorAction Stop
    if ($feature.State -ne 'Enabled') {
        if ($feature.State -ne 'EnablePending') {
            Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All -NoRestart | Out-Null
        }
        Report 'Save your work, restart Windows, then open C-Sweet Office from Start.'
        exit 3010
    }
    $manifest = Read-CSweetSignedReleaseManifest ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'office-bootstrap.p7m.b64'))) (Join-Path $PSScriptRoot 'office-release-signer.cer')
    $version = $manifest.officeVersion
    if ($version -cnotmatch '^\d+\.\d+\.\d+$' -or !$build.StartsWith("windows-test-$version-") -or
        $manifest.signing -cne 'development' -or $manifest.requiresHostCertification -ne $true) { throw 'These Office files do not match this installer. Download the matching installer from the Office release.' }
    $bundles = @($manifest.assets | Where-Object name -CEQ "csweet-office-$version-windows-x64.zip")
    if ($bundles.Count -ne 1) { throw 'The signed manifest is missing the Windows bundle.' }
    Assert-CSweetOfficeAsset $bundles[0] $version 'windows-x64' $true
    # Always work in a machine-owned directory. The final rename on the same volume makes the payload visible atomically.
    Remove-TestDirectory $work (Join-Path $PSScriptRoot 'preparation')
    [IO.Directory]::CreateDirectory($work) | Out-Null
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($office))
    if ($drive.AvailableFreeSpace -lt 64GB) { throw 'Free at least 64 GiB on the Windows system drive, then open C-Sweet Office from Start.' }
    Report 'Downloading and verifying the Office files. Keep this PC connected to the internet.'
    $bundle = Receive-CSweetOfficeAsset $bundles[0] (Join-Path $work 'bundle') -TimeoutSeconds 14400 -OnPart {
        param($Part, $Count)
        Report "Downloading and verifying Office files (part $Part of $Count)..."
    }
    $scripts = Join-Path $bundle 'scripts\windows'
    $result = Join-Path $work 'payload-path.txt'
    Report 'Checking that this PC can run Office workloads securely...'
    try {
        & (Join-Path $scripts 'Initialize-CSweetWindowsIsolationTest.ps1') -PrebuiltRoot $bundle -WorkingRoot $work -SkipInstall -NoElevation -DevelopmentTools -PayloadResultPath $result -ProgressJobId $JobId
    } catch {
        try { [void](Save-CertificationLog $work $diagnosticFile) }
        catch { Write-Warning "Office could not save the certification log. $($_.Exception.Message)" }
        throw
    }
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
    if ($lock -and !$Remove) {
        $message = if ([IO.File]::Exists($diagnosticFile)) {
            'Office could not complete its checks on this PC. Open the setup log for details, then try again after resolving the issue.'
        } else { $_.Exception.Message }
        Report $message
    }
    Write-Error $_ -ErrorAction Continue
    exit 1
} finally {
    if ($lock) {
        try {
            if (!$Remove -and !$CheckOnly) { Remove-TestDirectory $work (Join-Path $PSScriptRoot 'preparation') }
        } finally {
            $lock.Dispose()
            if ($Remove) { Remove-Item -LiteralPath (Join-Path $PSScriptRoot 'preparation.lock') -ErrorAction SilentlyContinue }
        }
    }
}
