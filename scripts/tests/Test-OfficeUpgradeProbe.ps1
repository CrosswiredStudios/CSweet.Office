$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ("office-probe-test-" + [guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'install'
$data = Join-Path $root 'data'
$node = Join-Path $data 'node'
$auth = Join-Path $data 'authorization'
$maintenance = Join-Path $node 'maintenance'
$active = Join-Path $maintenance 'active-assignments'
$hyperv = Join-Path $data 'hyperv'
$instance = [guid]::NewGuid().ToString('N')
function Get-Service { param($Name, $ErrorAction) [pscustomobject]@{ Name=$Name } }
function Get-CimInstance { param($ClassName, $Filter) [pscustomobject]@{ PathName=('node.exe --contentRoot "' + $install + '"') } }
function Get-Module { param([switch]$ListAvailable, $Name) 'available' }
function Import-Module { param($Name, $ErrorAction) }
function Get-VM { param($ErrorAction) $global:officeProbeTestVms }
function Get-VMHardDiskDrive { param($VM, $ErrorAction) }
$probe = Join-Path $PSScriptRoot '../windows/Get-CSweetOfficeRecoveryState.ps1'
function Assert-State([string]$Expected, [bool]$Upgrade = $true) {
    $actual = & $probe -InstallRoot $install -DataRoot $data -ForUpgrade:$Upgrade
    if ($actual -ne $Expected) { throw "Expected $Expected, got $actual. Last error: $($Error[0])" }
}
try {
    foreach ($path in @($install, $active, $auth, $hyperv)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    @{ CSweet=@{ Office=@{ Node=@{ StateDirectory=$node }; RuntimeHost=@{ Authorization=@{ StateDirectory=$auth } } } } } |
        ConvertTo-Json -Depth 8 | Set-Content (Join-Path $install 'appsettings.json')
    'draining' | Set-Content (Join-Path $maintenance 'drain-state')
    @{ record=@{ ProviderId='hyperv-gen2'; ProviderInstanceId=$instance; ExpiresAt=[DateTimeOffset]::UtcNow.AddDays(1).ToString('O') } } |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $auth 'authorized-workload-handles.json')
    $global:officeProbeTestVms = @([pscustomobject]@{ Name="CSweet-Runtime-$instance"; State='Off'; Path=$hyperv })
    Assert-State 'clean'
    Assert-State 'active' $false
    $global:officeProbeTestVms[0].State = 'Running'
    Assert-State 'active'
    $global:officeProbeTestVms[0].State = 'Saved'
    Assert-State 'active'
    $global:officeProbeTestVms = @()
    Assert-State 'clean'
    'active' | Set-Content (Join-Path $active 'work.active')
    Assert-State 'active'
    Remove-Item -LiteralPath (Join-Path $active 'work.active')
    'ready' | Set-Content (Join-Path $maintenance 'drain-state')
    Assert-State 'active'
    'draining' | Set-Content (Join-Path $maintenance 'drain-state')
    @{ record=@{ ProviderId='unrecognized'; ProviderInstanceId=$instance } } | ConvertTo-Json -Depth 4 |
        Set-Content (Join-Path $auth 'authorized-workload-handles.json')
    Assert-State 'unsafe'
    'Passed 8 upgrade probe scenarios.'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
