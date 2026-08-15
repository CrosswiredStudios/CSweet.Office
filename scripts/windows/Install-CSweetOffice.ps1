[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PayloadRoot,
    [Parameter(Mandatory = $true)] [string] $ControlPlaneUrl,
    [string] $ControlPlaneCertificateSha256,
    [ValidateSet('baseline', 'hardened', 'development')]
    [string] $SecurityProfile = 'baseline',
    [bool] $MixedUseHost = $true,
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
    $arguments += @('-SecurityProfile', $SecurityProfile, '-MixedUseHost', $MixedUseHost.ToString())
    if ($AllowDevelopmentAssignments) { $arguments += '-AllowDevelopmentAssignments' }
    $process = Start-Process -FilePath $powershell -Verb RunAs -Wait -PassThru -ArgumentList ($arguments -join ' ')
    if ($process.ExitCode -ne 0) { throw "Execution fleet installation failed with exit code $($process.ExitCode)." }
    return
}

$secureToken = Read-Host 'Paste the one-use office enrollment token' -AsSecureString
$credential = [PSCredential]::new('token', $secureToken)
$token = $credential.GetNetworkCredential().Password
if ($token.Length -lt 32 -or $token.Length -gt 256) { throw 'The enrollment token is invalid.' }
$inputPath = Join-Path $env:TEMP "csweet-enrollment-$([guid]::NewGuid().ToString('N')).secret"
try {
    [IO.File]::WriteAllText($inputPath, $token, [Text.UTF8Encoding]::new($false))
    $token = $null
    & (Join-Path $PSScriptRoot 'Install-CSweetOfficeRuntimeHost.ps1') -PayloadRoot $PayloadRoot `
        -ControlPlaneUrl $ControlPlaneUrl -ControlPlaneCertificateSha256 $ControlPlaneCertificateSha256 `
        -EnrollmentTokenInputPath $inputPath -NonInteractive:$NonInteractive `
        -SecurityProfile $SecurityProfile -MixedUseHost $MixedUseHost `
        -AllowDevelopmentAssignments:$AllowDevelopmentAssignments
    if ($LASTEXITCODE -ne 0) { throw 'The execution fleet installer failed.' }
} finally {
    if (Test-Path -LiteralPath $inputPath) { Remove-Item -LiteralPath $inputPath -Force }
}
