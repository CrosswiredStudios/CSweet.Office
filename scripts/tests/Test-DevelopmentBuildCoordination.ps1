$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../windows/CSweet.DevelopmentBuild.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('office-build-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$name = 'Local\CSweet.Office.Test.' + [guid]::NewGuid().ToString('N')
$job = $null
try {
    # A separate process owns the mutex: the second invocation must not enter.
    $ready = Join-Path $root 'ready'
    $release = Join-Path $root 'release'
    $job = Start-Job -ArgumentList $name,$ready,$release -ScriptBlock {
        param($Name,$Ready,$Release)
        $lock = [Threading.Mutex]::new($false,$Name)
        $null = $lock.WaitOne()
        try {
            [IO.File]::WriteAllText($Ready,'ready')
            while (-not (Test-Path $Release)) { Start-Sleep -Milliseconds 100 }
        } finally { $lock.ReleaseMutex(); $lock.Dispose() }
    }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while (-not (Test-Path $ready)) {
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Test worker did not start.' }
        Start-Sleep -Milliseconds 100
    }
    $prebuiltWaits = 0
    $prebuiltLock = Enter-CSweetOfficePreparation -PrebuiltRoot (Join-Path $root 'prebuilt') `
        -MutexName $name -ProgressRoot $root -TimeoutSeconds 0 -OnWaiting { $script:prebuiltWaits++ }
    if ($null -ne $prebuiltLock -or $prebuiltWaits -ne 0) {
        throw 'Prebuilt preparation waited on the source-build coordinator.'
    }
    $blocked = $false
    try { $unexpected = Enter-CSweetDevelopmentBuild -MutexName $name -ProgressRoot $root -TimeoutSeconds 0 -OnWaiting {} }
    catch { $blocked = $_.Exception.Message -like '*No competing build*' }
    if (-not $blocked) { throw 'A concurrent build was allowed.' }
    [IO.File]::WriteAllText($release,'release')
    $job | Wait-Job -Timeout 10 | Receive-Job | Out-Null

    # An already-running legacy build does not own the new mutex.
    function Get-Process { param($Id,$ErrorAction) if ($Id -eq 123456) { [pscustomobject]@{ Id=$Id } } }
    $record = Join-Path $root 'windows-isolation-test.json'
    @{ state='running'; workflow='developer-bootstrap'; ownerProcessId=123456; phaseKey='prepare-guest' } |
        ConvertTo-Json | Set-Content $record
    $blocked = $false
    try { $unexpected = Enter-CSweetDevelopmentBuild -MutexName $name -ProgressRoot $root -TimeoutSeconds 0 -OnWaiting {} }
    catch { $blocked = $_.Exception.Message -like '*No competing build*' }
    if (-not $blocked) { throw 'A legacy build was ignored.' }

    # Completed records and waiting peers must not deadlock a subsequent owner.
    foreach ($phase in @('completed','wait-existing-build')) {
        @{ state=$(if($phase -eq 'completed'){'completed'}else{'running'}); workflow='developer-bootstrap'; ownerProcessId=123456; phaseKey=$phase } |
            ConvertTo-Json | Set-Content $record
        $lock = Enter-CSweetDevelopmentBuild -MutexName $name -ProgressRoot $root -TimeoutSeconds 0 -OnWaiting {}
        $lock.ReleaseMutex(); $lock.Dispose()
    }
    # Pre-owner schemas survive database resets. Only a reboot proves they stopped.
    function Get-CimInstance { param($ClassName,$ErrorAction) [pscustomobject]@{ LastBootUpTime=[datetime]'2026-09-01T00:00:00Z' } }
    foreach ($ownerValue in @('missing', $null, 0, 'invalid')) {
        $legacy = @{ state='running'; workflow='developer-bootstrap'; phaseKey='build-guest'; updatedAt='2026-08-05T21:42:28Z' }
        if ($ownerValue -ne 'missing') { $legacy.ownerProcessId = $ownerValue }
        $legacy | ConvertTo-Json | Set-Content $record
        $lock = Enter-CSweetDevelopmentBuild -MutexName $name -ProgressRoot $root -TimeoutSeconds 0 -OnWaiting {}
        $lock.ReleaseMutex(); $lock.Dispose()

        $legacy.updatedAt = '2026-09-15T00:00:00Z'
        $legacy | ConvertTo-Json | Set-Content $record
        $blocked = $false
        try { $unexpected = Enter-CSweetDevelopmentBuild -MutexName $name -ProgressRoot $root -TimeoutSeconds 0 -OnWaiting {} }
        catch { $blocked = $_.Exception.Message -like '*No competing build*' }
        if (-not $blocked) { throw 'An ownerless build from the current boot was ignored.' }
    }
    $legacy.updatedAt = 'invalid'
    $legacy | ConvertTo-Json | Set-Content $record
    $blocked = $false
    try { $unexpected = Enter-CSweetDevelopmentBuild -MutexName $name -ProgressRoot $root -TimeoutSeconds 0 -OnWaiting {} }
    catch { $blocked = $_.Exception.Message -like '*completion cannot be verified*' }
    if (-not $blocked) { throw 'An unverifiable ownerless build was ignored.' }
    Write-Host 'PASS: prebuilt bypass, source-build exclusion, legacy detection, waiting-peer recovery, and ownerless compatibility.'
}
finally {
    if ($job) { Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue }
    # This uniquely-created test directory is the sole cleanup target.
    if ([IO.Path]::GetFullPath($root).StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
