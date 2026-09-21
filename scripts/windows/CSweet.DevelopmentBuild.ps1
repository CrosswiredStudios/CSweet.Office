Set-StrictMode -Version Latest

# Shared by command-line and guided development setup. Never terminate another build.
function Enter-CSweetDevelopmentBuild {
    param([Parameter(Mandatory = $true)][scriptblock] $OnWaiting,
          [int] $TimeoutSeconds = 7200,
          [string] $MutexName = 'Global\CSweet.Office.DevelopmentBuild',
          [string] $ProgressRoot = (Join-Path $env:ProgramData 'CSweet\Setup'))
    $mutex = [Threading.Mutex]::new($false, $MutexName)
    $held = $false
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    try {
        do {
            try { $held = $mutex.WaitOne(1000) }
            catch [Threading.AbandonedMutexException] { $held = $true }
            if (-not $held) {
                & $OnWaiting
                if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Another Office build is still running. No competing build was started.' }
            }
        } while (-not $held)

        # Builds started before this coordination code was installed do not hold the mutex.
        # Their protected progress records still identify the live owner, including interactive PowerShell.
        do {
            $busy = $false

            if (Test-Path -LiteralPath $progressRoot) {
                foreach ($file in Get-ChildItem -LiteralPath $progressRoot -Filter 'windows-isolation-*.json' -File -ErrorAction Stop) {
                    try { $state = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop | ConvertFrom-Json }
                    catch { throw 'An Office progress record is being updated or cannot be read. Retry after the existing build finishes.' }
                    if ($state.state -ne 'running' -or $state.workflow -ne 'developer-bootstrap') { continue }
                    # Older progress schemas have no process owner. A reboot proves that those
                    # builds have stopped; age alone does not prove an unattended build is idle.
                    $ownerProperty = $state.PSObject.Properties['ownerProcessId']
                    $recordOwnerId = 0
                    if ($null -eq $ownerProperty -or
                        -not [int]::TryParse([string]$ownerProperty.Value, [ref]$recordOwnerId) -or
                        $recordOwnerId -le 0) {
                        try {
                            $updatedAt = [DateTimeOffset]$state.updatedAt
                            $bootTime = [DateTimeOffset](Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime
                        } catch {
                            throw 'An older Office build record has no valid process owner and its completion cannot be verified. Restart Windows before retrying setup.'
                        }
                        if ($updatedAt -lt $bootTime) { continue }
                        throw 'An older Office build record has no valid process owner and may still be running. Wait for it to finish, or restart Windows before retrying setup. No competing build was started.'
                    }
                    if ($recordOwnerId -eq $PID -or $state.phaseKey -eq 'wait-existing-build') { continue }
                    $owner = Get-Process -Id $state.ownerProcessId -ErrorAction SilentlyContinue
                    if ($null -ne $owner) { $busy = $true; break }
                }
            }
            if ($busy) {
                & $OnWaiting
                if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'The existing Office build has not finished. No competing build was started.' }
                Start-Sleep -Seconds 2
            }
        } while ($busy)
        return $mutex
    }
    catch {
        if ($held) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
        throw
    }
}

function Enter-CSweetOfficePreparation {
    param([string] $PrebuiltRoot,
          [Parameter(Mandatory = $true)][scriptblock] $OnWaiting,
          [int] $TimeoutSeconds = 7200,
          [string] $MutexName = 'Global\CSweet.Office.DevelopmentBuild',
          [string] $ProgressRoot = (Join-Path $env:ProgramData 'CSweet\Setup'))

    # A hosted bundle already contains the binaries and guest image. Its unique extraction and
    # certification directories do not touch the source-build cache, so a source build must not
    # delay or mislabel prebuilt preparation.
    if (-not [String]::IsNullOrWhiteSpace($PrebuiltRoot)) { return $null }
    return Enter-CSweetDevelopmentBuild -OnWaiting $OnWaiting -TimeoutSeconds $TimeoutSeconds `
        -MutexName $MutexName -ProgressRoot $ProgressRoot
}
