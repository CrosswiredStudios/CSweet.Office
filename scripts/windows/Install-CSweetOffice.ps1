[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PayloadRoot,
    [Parameter(Mandatory = $true)] [string] $ControlPlaneUrl,
    [string] $ControlPlaneCertificateSha256,
    [string] $EnrollmentTokenInputPath,
    [guid] $AssistedSetupSessionId = [guid]::Empty,
    [int] $AllocatableCpuCount = 0,
    [int] $AllocatableMemoryMb = 0,
    [int] $AllocatableDiskMb = 0,
    [int] $MaximumConcurrentWorkloads = 0,
    [ValidateSet('none', 'reconnect', 'upgrade')]
    [string] $ExistingInstallationAction = 'none',
    [ValidateSet('baseline', 'hardened', 'development')]
    [string] $SecurityProfile = 'baseline',
    [bool] $MixedUseHost = $true,
    [string] $ProgressPath,
    [guid] $ProgressJobId = [guid]::Empty,
    [string] $ProgressWorkflow = 'packaged-installer',
    [switch] $AllowDevelopmentAssignments,
    [switch] $NonInteractive,
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
        ('"' + $PSCommandPath + '"'), '-PayloadRoot', ('"' + $PayloadRoot + '"'),
        '-ControlPlaneUrl', ('"' + $ControlPlaneUrl + '"'), '-Elevated')
    if (-not [String]::IsNullOrWhiteSpace($ControlPlaneCertificateSha256)) {
        $arguments += @('-ControlPlaneCertificateSha256', ('"' + $ControlPlaneCertificateSha256 + '"'))
    }
    if ($NonInteractive) { $arguments += '-NonInteractive' }
    if (-not [String]::IsNullOrWhiteSpace($EnrollmentTokenInputPath)) {
        $arguments += @('-EnrollmentTokenInputPath', ('"' + $EnrollmentTokenInputPath + '"'))
    }
    if ($AssistedSetupSessionId -ne [guid]::Empty) { $arguments += @('-AssistedSetupSessionId', $AssistedSetupSessionId.ToString('D')) }
    if ($AllocatableCpuCount -gt 0) { $arguments += @('-AllocatableCpuCount', $AllocatableCpuCount) }
    if ($AllocatableMemoryMb -gt 0) { $arguments += @('-AllocatableMemoryMb', $AllocatableMemoryMb) }
    if ($AllocatableDiskMb -gt 0) { $arguments += @('-AllocatableDiskMb', $AllocatableDiskMb) }
    if ($MaximumConcurrentWorkloads -gt 0) { $arguments += @('-MaximumConcurrentWorkloads', $MaximumConcurrentWorkloads) }
    if ($ExistingInstallationAction -ne 'none') { $arguments += @('-ExistingInstallationAction', $ExistingInstallationAction) }
    if (-not [String]::IsNullOrWhiteSpace($ProgressPath)) { $arguments += @('-ProgressPath', ('"' + $ProgressPath + '"')) }
    if ($ProgressJobId -ne [guid]::Empty) { $arguments += @('-ProgressJobId', $ProgressJobId.ToString('D')) }
    if (-not [String]::IsNullOrWhiteSpace($ProgressWorkflow)) { $arguments += @('-ProgressWorkflow', $ProgressWorkflow) }
    $arguments += @('-SecurityProfile', $SecurityProfile, '-MixedUseHost', $MixedUseHost.ToString())
    if ($AllowDevelopmentAssignments) { $arguments += '-AllowDevelopmentAssignments' }
    $process = Start-Process -FilePath $powershell -Verb RunAs -Wait -PassThru -ArgumentList ($arguments -join ' ')
    if ($process.ExitCode -ne 0) { throw "Execution fleet installation failed with exit code $($process.ExitCode)." }
    return
}

$inputPath = $EnrollmentTokenInputPath
$createdInput = $false
if ($ExistingInstallationAction -ne 'upgrade' -and [String]::IsNullOrWhiteSpace($inputPath)) {
    if ($NonInteractive) { throw 'Non-interactive setup requires a protected enrollment input file.' }
    $secureToken = Read-Host 'Paste the one-use office enrollment token' -AsSecureString
    $credential = [PSCredential]::new('token', $secureToken)
    $token = $credential.GetNetworkCredential().Password
    if ($token.Length -lt 32 -or $token.Length -gt 256) { throw 'The enrollment token is invalid.' }
    $inputPath = Join-Path $env:TEMP "csweet-enrollment-$([guid]::NewGuid().ToString('N')).secret"
    [IO.File]::WriteAllText($inputPath, $token, [Text.UTF8Encoding]::new($false))
    $token = $null
    $createdInput = $true
}
try {
    & (Join-Path $PSScriptRoot 'Install-CSweetOfficeRuntimeHost.ps1') -PayloadRoot $PayloadRoot `
        -ControlPlaneUrl $ControlPlaneUrl -ControlPlaneCertificateSha256 $ControlPlaneCertificateSha256 `
        -EnrollmentTokenInputPath $inputPath -NonInteractive:$NonInteractive `
        -SecurityProfile $SecurityProfile -MixedUseHost $MixedUseHost `
        -AllowDevelopmentAssignments:$AllowDevelopmentAssignments `
        -AssistedSetupSessionId $AssistedSetupSessionId -AllocatableCpuCount $AllocatableCpuCount `
        -AllocatableMemoryMb $AllocatableMemoryMb -AllocatableDiskMb $AllocatableDiskMb `
        -MaximumConcurrentWorkloads $MaximumConcurrentWorkloads `
        -ExistingInstallationAction $ExistingInstallationAction `
        -ProgressPath $ProgressPath -ProgressJobId $ProgressJobId -ProgressWorkflow $ProgressWorkflow
    if ($LASTEXITCODE -ne 0) { throw 'The execution fleet installer failed.' }
} finally {
    if ($createdInput -and (Test-Path -LiteralPath $inputPath)) { Remove-Item -LiteralPath $inputPath -Force }
}
