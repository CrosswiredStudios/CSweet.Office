[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][guid] $OfficeId,
    [string] $InstallRoot = "$env:ProgramFiles\CSweet\Office",
    [string] $DataRoot = "$env:ProgramData\CSweet\Office"
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The helper runs with explicit administrator approval. Never use the server's last
# reported work count as permission to stop local work or discard identity files.
$stateRoot = Join-Path $DataRoot 'node'
foreach ($root in @($InstallRoot, $DataRoot, $stateRoot)) {
    if (-not (Test-Path -LiteralPath $root -PathType Container) -or
        ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '[maintenance_unsafe] Office files could not be verified.'
    }
}
$statePath = Join-Path $stateRoot 'node-state.json'
if (-not (Test-Path -LiteralPath $statePath -PathType Leaf) -or
    ((Get-Item -LiteralPath $statePath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw '[maintenance_unsafe] The Office identity is unavailable.'
}
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
if ([guid]$state.OfficeId -ne $OfficeId) { throw '[maintenance_unsafe] This is a different Office.' }
$probe = Join-Path $PSScriptRoot 'Get-CSweetOfficeRecoveryState.ps1'
if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw '[maintenance_unsafe] The Office safety check is unavailable.' }
if ((& $probe -InstallRoot $InstallRoot -DataRoot $DataRoot | Select-Object -Last 1) -ne 'clean') {
    throw '[maintenance_busy] Office work or local state needs attention. Nothing has been removed.'
}
$service = Get-Service -Name 'CSweet.Office.Node' -ErrorAction Stop
$wasRunning = $service.Status -ne 'Stopped'
try {
    if ($wasRunning) {
        Stop-Service -Name 'CSweet.Office.Node' -ErrorAction Stop
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    # Recheck after the dispatcher is stopped to close the race with incoming work.
    if ((& $probe -InstallRoot $InstallRoot -DataRoot $DataRoot | Select-Object -Last 1) -ne 'clean') {
        throw '[maintenance_busy] Office work appeared while preparing maintenance.'
    }
    $maintenance = Join-Path $stateRoot 'maintenance'
    if ((Test-Path -LiteralPath $maintenance) -and
        ((Get-Item -LiteralPath $maintenance -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '[maintenance_unsafe] The maintenance folder could not be verified.'
    }
    New-Item -ItemType Directory -Path $maintenance -Force | Out-Null
    $drain = Join-Path $maintenance 'drain-state'
    if ((Test-Path -LiteralPath $drain) -and
        ((Get-Item -LiteralPath $drain -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '[maintenance_unsafe] The maintenance state could not be verified.'
    }
    [IO.File]::WriteAllText($drain, 'draining')
    'clean'
}
catch {
    if ($wasRunning) { Start-Service -Name 'CSweet.Office.Node' -ErrorAction SilentlyContinue }
    throw
}
