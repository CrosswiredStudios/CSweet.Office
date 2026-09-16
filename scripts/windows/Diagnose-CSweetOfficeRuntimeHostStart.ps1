[CmdletBinding()]
param(
    [string] $OutputRoot = (Join-Path $env:ProgramData 'CSweet\Diagnostics\RuntimeHostStart'),
    [string] $ProcessMonitorDownloadUrl = 'https://download.sysinternals.com/files/ProcessMonitor.zip'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This diagnostic must run from an Administrator PowerShell.'
    }
}

function Wait-ForPath([string] $Path, [TimeSpan] $Timeout) {
    $deadline = [DateTimeOffset]::UtcNow.Add($Timeout)
    while (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Timed out waiting for Process Monitor output: $Path"
        }
        Start-Sleep -Milliseconds 250
    }
}

function Wait-ForUnlockedFile([string] $Path, [TimeSpan] $Timeout) {
    $deadline = [DateTimeOffset]::UtcNow.Add($Timeout)
    while ($true) {
        try {
            $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
            try { return } finally { $stream.Dispose() }
        } catch [IO.IOException] {
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                throw "Timed out waiting for Process Monitor to finish writing: $Path"
            }
            Start-Sleep -Milliseconds 250
        }
    }
}

Assert-Administrator
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$runRoot = Join-Path $OutputRoot $runId
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$archivePath = Join-Path $runRoot 'ProcessMonitor.zip'
$toolRoot = Join-Path $runRoot 'ProcessMonitor'
$capturePath = Join-Path $runRoot 'runtimehost-start.pml'
$csvPath = Join-Path $runRoot 'runtimehost-start.csv'
$summaryPath = Join-Path $runRoot 'access-denied.txt'

Invoke-WebRequest -Uri $ProcessMonitorDownloadUrl -OutFile $archivePath -UseBasicParsing
Expand-Archive -LiteralPath $archivePath -DestinationPath $toolRoot
$processMonitor = Join-Path $toolRoot 'Procmon64.exe'
$signature = Get-AuthenticodeSignature -LiteralPath $processMonitor
if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
    $null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Subject.IndexOf('O=Microsoft Corporation', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw 'The downloaded Process Monitor executable does not have a valid Microsoft signature.'
}

try {
    & $processMonitor /AcceptEula /Quiet /Minimized /BackingFile $capturePath
    Start-Sleep -Seconds 3
    & "$env:SystemRoot\System32\sc.exe" start 'CSweet.Office.RuntimeHost' 2>&1 | Out-Host
    Start-Sleep -Seconds 3
} finally {
    & $processMonitor /Terminate 2>$null | Out-Null
}

Wait-ForPath -Path $capturePath -Timeout ([TimeSpan]::FromSeconds(15))
& $processMonitor /AcceptEula /Quiet /OpenLog $capturePath /SaveAs $csvPath
Wait-ForPath -Path $csvPath -Timeout ([TimeSpan]::FromSeconds(30))
Wait-ForUnlockedFile -Path $csvPath -Timeout ([TimeSpan]::FromSeconds(30))

$denied = @(Import-Csv -LiteralPath $csvPath | Where-Object {
    $_.Result -in @('ACCESS DENIED', 'PRIVILEGE NOT HELD', 'BAD IMPERSONATION LEVEL') -and
    ($_.ProcessName -in @('services.exe', 'CSweet.Office.RuntimeHost.exe') -or
     $_.Path -match 'CSweet|Office|RuntimeHost')
} | Select-Object 'Time of Day', ProcessName, PID, Operation, Path, Result, Detail)

if ($denied.Count -eq 0) {
    'No matching access-denied rows were found. Inspect the full CSV capture.' | Set-Content -LiteralPath $summaryPath
} else {
    $denied | Format-List | Out-String -Width 4096 | Set-Content -LiteralPath $summaryPath
}

Write-Host "Diagnostic capture: $csvPath" -ForegroundColor Green
Write-Host "Access-denied summary: $summaryPath" -ForegroundColor Green
Get-Content -LiteralPath $summaryPath
