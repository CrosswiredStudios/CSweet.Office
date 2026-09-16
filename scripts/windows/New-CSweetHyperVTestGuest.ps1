[CmdletBinding()]
param(
    [string] $SwitchName = 'Default Switch',
    [string] $UbuntuVersion = '24.04.4',
    [string] $PackerVersion = '1.15.4',
    [string] $OutputPath = "$PSScriptRoot\..\..\artifacts\windows-runtime\source\csweet-agent-guest.vhdx",
    [string] $IsolationRoot = "$PSScriptRoot\..\..\..\CSweet.Isolation",
    [string] $ProgressPath,
    [guid] $ProgressJobId = [guid]::Empty
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $IsolationRoot 'tools\LinuxImage\CSweet.LinuxImage.psd1') -Force
$progressHelper = Join-Path $PSScriptRoot 'CSweet.WindowsSetupProgress.ps1'
$progressState = @{ Heartbeat = $null }
$ownerProcessId = $PID
$report = {
    param($phase, $message)
    Write-Host $message
    if (-not $ProgressPath -or $ProgressJobId -eq [guid]::Empty) { return }
    . $progressHelper
    $percent = switch ($phase) {
        'publish-guest' { 16 }; 'resolve-ubuntu' { 19 }; 'prepare-packer' { 21 }
        'build-guest' { 24 }; 'guest-complete' { 55 }; default { 12 }
    }
    Write-CSweetSetupProgress -Path $ProgressPath -JobId $ProgressJobId -Workflow 'developer-bootstrap' `
        -State running -PhaseKey $phase -PhaseDisplayName 'Preparing the Linux guest image' `
        -Message $message -PercentComplete $percent -EstimatedRemainingMinimumSeconds 300 -EstimatedRemainingMaximumSeconds 2400
    if ($phase -eq 'build-guest') {
        $progressState.Heartbeat = Start-Job -ScriptBlock {
            param($helper, $path, $jobId, $owner)
            . $helper
            $started = [DateTimeOffset]::UtcNow
            while ($true) {
                Start-Sleep -Seconds 30
                $elapsed = [int]([DateTimeOffset]::UtcNow - $started).TotalSeconds
                Write-CSweetSetupProgress -Path $path -JobId $jobId -Workflow 'developer-bootstrap' `
                    -State running -PhaseKey 'build-guest' -PhaseDisplayName 'Building the Linux guest image' `
                    -Message 'Ubuntu is still installing in the secure VM. No action is needed.' `
                    -PercentComplete ([Math]::Min(49, 24 + [Math]::Floor($elapsed / 90))) `
                    -EstimatedRemainingMinimumSeconds ([Math]::Max(0, 600 - $elapsed)) `
                    -EstimatedRemainingMaximumSeconds ([Math]::Max(60, 2100 - $elapsed)) -OwnerProcessId $owner
            }
        } -ArgumentList $progressHelper, $ProgressPath, $ProgressJobId, $ownerProcessId
    }
    if ($phase -eq 'guest-complete' -and $null -ne $progressState.Heartbeat) {
        Stop-Job $progressState.Heartbeat
        Remove-Job $progressState.Heartbeat -Force
        $progressState.Heartbeat = $null
    }
}.GetNewClosure()
$prepare = {
    param($payload)
    foreach ($project in @('CSweet.Office.RuntimeGuest', 'CSweet.Office.BuilderGuest', 'CSweet.Office.ToolchainGuest')) {
        $published = Join-Path (Split-Path -Parent $payload) $project
        dotnet publish (Join-Path $repositoryRoot "src\$project\$project.csproj") `
            -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true -o $published
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $published $project))) {
            throw "The Linux $project publish failed."
        }
        # Only the single-file executable belongs in the image staging root.
        Copy-Item -LiteralPath (Join-Path $published $project) -Destination (Join-Path $payload $project'.bin')
    }
}.GetNewClosure()
try {
    $result = New-CSweetLinuxHyperVImage -ProfileDirectory (Join-Path $repositoryRoot 'build\windows-hyperv') `
        -PreparePayload $prepare -GuestServiceName 'csweet-agent-guest.service' `
        -ArtifactDirectory (Join-Path $IsolationRoot 'artifacts\linux-images') -OutputPath $OutputPath `
        -SwitchName $SwitchName -UbuntuVersion $UbuntuVersion -PackerVersion $PackerVersion -ReportProgress $report
    Write-Output $result.ImagePath
} finally {
    if ($null -ne $progressState.Heartbeat) {
        Stop-Job $progressState.Heartbeat -ErrorAction SilentlyContinue
        Remove-Job $progressState.Heartbeat -Force -ErrorAction SilentlyContinue
    }
}