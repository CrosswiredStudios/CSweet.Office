[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows Installer otherwise records only error 1722 for a deferred executable.
# Keep a diagnostic through rollback, without writing arguments, tokens or raw exceptions.
trap {
    $failure = $_
    $location = [IO.Path]::GetFileName([string]$failure.InvocationInfo.ScriptName)
    if ($location -notin @('Initialize-CSweetCombinedOffice.ps1', 'Install-CSweetOffice.ps1',
        'Install-CSweetOfficeRuntimeHost.ps1', 'CSweet.OfficeStorage.ps1', 'CSweet.OfficeImageParts.ps1',
        'CSweet.WindowsSetupProgress.ps1')) { $location = 'Office installer' }
    $reason = 'Check the Office setup progress and gateway availability.'
    if ($failure.Exception.Message -match '^Insufficient free space for Office\. Setup requires (\d+) GiB free') {
        $reason = "Office needs $($Matches[1]) GiB free on one eligible local volume."
    } elseif ($location -eq 'CSweet.OfficeStorage.ps1') {
        $reason = 'Check the selected local volume, its free space and its registered location.'
    } elseif ($location -eq 'Initialize-CSweetCombinedOffice.ps1') {
        $reason = 'Check the protected Office provisioning profile and one-use enrollment file.'
    }
    try {
        [Diagnostics.EventLog]::WriteEntry('CSweet.Office.Setup',
            "Combined Office enrollment failed in $location (line $($failure.InvocationInfo.ScriptLineNumber)). $reason",
            [Diagnostics.EventLogEntryType]::Error, 1603)
    } catch { Write-Warning 'The combined Office setup diagnostic could not be recorded.' }
    throw $failure
}

# The per-machine MSI invokes this installed file once, after InstallFiles. All
# paths are fixed under Program Files. The enrollment secret never enters MSI
# properties, process arguments, output, or the distributed installation media.
$vendor = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'CSweet'
$installed = Join-Path $vendor 'Office'
$provisioning = Join-Path $vendor 'Provisioning'
if ($PSScriptRoot -ine $installed) { throw 'Combined enrollment requires the machine-installed Office package.' }
foreach ($path in @($vendor, $installed, $provisioning)) {
    $entry = Get-Item -LiteralPath $path
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A machine provisioning directory is redirected.' }
}
$settingsPath = Join-Path $provisioning 'office.json'
$tokenPath = Join-Path $provisioning 'office-enrollment.secret'
foreach ($path in @($settingsPath, $tokenPath)) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A provisioning file is redirected.' }
}
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$origin = [uri]$settings.controlPlaneUrl
if (-not $origin.IsAbsoluteUri -or $origin.Scheme -ne 'https' -or $origin.UserInfo -or
    $origin.Query -or $origin.Fragment -or $settings.controlPlaneCertificateSha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw 'Combined enrollment requires the HTTPS gateway and its certificate SHA-256 fingerprint.'
}
if ((Get-Item -LiteralPath $tokenPath).Length -gt 1024) { throw 'Invalid Office enrollment input.' }
$allowedSids = @('S-1-5-18', 'S-1-5-32-544')
foreach ($entry in @((Get-Acl -LiteralPath $tokenPath).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))) {
    if ($entry.AccessControlType -eq 'Allow' -and $entry.IdentityReference.Value -notin $allowedSids) {
        throw 'Restrict the Office enrollment file to SYSTEM and local Administrators.'
    }
}
foreach ($name in @('allocatableCpuCount', 'allocatableMemoryMb', 'allocatableDiskMb', 'maximumConcurrentWorkloads')) {
    $value = $settings.$name
    if (($value -isnot [int] -and $value -isnot [long]) -or $value -le 0 -or $value -gt [int]::MaxValue) {
        throw "An explicit positive Office allocation is required for $name."
    }
}
$storage = @{}
if ($settings.PSObject.Properties['storageVolumeId'] -and $settings.storageVolumeId) {
    $storage.StorageVolumeId = [string]$settings.storageVolumeId
}
$computePolicy = if ($settings.PSObject.Properties['compute']) { $settings.compute }
if ($null -eq $computePolicy) {
    $automaticPolicy = Join-Path $provisioning 'combined-office-compute.json'
    if ([IO.File]::Exists($automaticPolicy)) {
        if ((Get-Item -LiteralPath $automaticPolicy).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected combined compute configuration.' }
        $plan = Get-Content -LiteralPath $automaticPolicy -Raw | ConvertFrom-Json
        if ($plan.PSObject.Properties['reservation']) { $computePolicy = $plan.reservation }
    }
}
& (Join-Path $installed 'Install-CSweetOffice.ps1') @storage -ComputePolicy $computePolicy -NonInteractive -Elevated `
    -PayloadRoot (Join-Path $installed 'payload') -ControlPlaneUrl $origin.AbsoluteUri `
    -ControlPlaneCertificateSha256 $settings.controlPlaneCertificateSha256 -EnrollmentTokenInputPath $tokenPath `
    -AllocatableCpuCount $settings.allocatableCpuCount -AllocatableMemoryMb $settings.allocatableMemoryMb `
    -AllocatableDiskMb $settings.allocatableDiskMb -MaximumConcurrentWorkloads $settings.maximumConcurrentWorkloads
