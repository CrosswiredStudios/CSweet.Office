[CmdletBinding()]
param(
    [switch] $ForUpgrade,
    [string] $InstallRoot = "$env:ProgramFiles\CSweet\Office",
    [string] $DataRoot = "$env:ProgramData\CSweet\Office"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-WithinRoot([string] $Candidate, [string] $Root) {
    if ([String]::IsNullOrWhiteSpace($Candidate)) { return $false }
    $candidatePath = [IO.Path]::GetFullPath($Candidate).TrimEnd('\')
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    return $candidatePath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $candidatePath.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Get-Property([object] $InputObject, [string] $Name) {
    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

try {
    $nodeService = Get-Service -Name 'CSweet.Office.Node' -ErrorAction SilentlyContinue
    $runtimeService = Get-Service -Name 'CSweet.Office.RuntimeHost' -ErrorAction SilentlyContinue
    if ($null -eq $nodeService -and $null -eq $runtimeService) {
        # The signed MSI payload is already present at InstallRoot before first enrollment.
        # Mutable data without either registered service is not a recognized fresh install.
        if (Test-Path -LiteralPath $DataRoot) { 'unsafe' }
        else { 'none' }
        return
    }
    if ($null -eq $nodeService -or $null -eq $runtimeService) { 'unsafe'; return }

    $contentRoot = $null
    foreach ($serviceName in @('CSweet.Office.Node', 'CSweet.Office.RuntimeHost')) {
        $serviceConfiguration = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'"
        $contentRootMatch = [Regex]::Match([string]$serviceConfiguration.PathName,
            '--contentRoot\s+"([^"]+)"', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $contentRootMatch.Success) { 'unsafe'; return }
        $serviceContentRoot = [IO.Path]::GetFullPath($contentRootMatch.Groups[1].Value)
        if (-not (Test-WithinRoot $serviceContentRoot $InstallRoot)) { 'unsafe'; return }
        if ($serviceName -eq 'CSweet.Office.Node') { $contentRoot = $serviceContentRoot }
    }
    if ([String]::IsNullOrWhiteSpace($contentRoot)) { 'unsafe'; return }

    $configurationPath = Join-Path $contentRoot 'appsettings.json'
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) { 'unsafe'; return }
    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    $office = Get-Property (Get-Property $configuration 'CSweet') 'Office'
    $node = Get-Property $office 'Node'
    $runtime = Get-Property $office 'RuntimeHost'
    $nodeStateRoot = [string](Get-Property $node 'StateDirectory')
    $authorizationRoot = [string](Get-Property (Get-Property $runtime 'Authorization') 'StateDirectory')
    if (-not (Test-WithinRoot $nodeStateRoot $DataRoot) -or
        -not (Test-WithinRoot $authorizationRoot $DataRoot)) { 'unsafe'; return }

    $activeRoot = Join-Path (Join-Path $nodeStateRoot 'maintenance') 'active-assignments'
    if ((Test-Path -LiteralPath $activeRoot -PathType Container) -and
        @(Get-ChildItem -LiteralPath $activeRoot -File -Filter '*.active').Count -gt 0) { 'active'; return }

    # In-place updates preserve authorization records and stopped VMs. These are not
    # proof of executing work; require an explicit drain and check actual VM state.
    if ($ForUpgrade) {
        $drainPath = Join-Path (Join-Path $nodeStateRoot 'maintenance') 'drain-state'
        if (-not (Test-Path -LiteralPath $drainPath -PathType Leaf) -or
            [IO.File]::ReadAllText($drainPath).Trim() -ne 'draining') { 'active'; return }
    }
    $knownInstances = @()
    $handlesPath = Join-Path $authorizationRoot 'authorized-workload-handles.json'
    if (Test-Path -LiteralPath $handlesPath -PathType Leaf) {
        $handles = Get-Content -LiteralPath $handlesPath -Raw | ConvertFrom-Json
        if ($null -ne $handles -and @($handles.PSObject.Properties).Count -gt 0) {
            if (-not $ForUpgrade) { 'active'; return }
            foreach ($entry in $handles.PSObject.Properties) {
                $handle = $entry.Value
                $instance = [guid]::Empty
                if ((Get-Property $handle 'ProviderId') -ne 'hyperv-gen2' -or
                    -not [guid]::TryParseExact([string](Get-Property $handle 'ProviderInstanceId'), 'N', [ref]$instance)) {
                    'unsafe'; return
                }
                $knownInstances += $instance.ToString('N')
            }
        }
    }

    $hyperVRoot = Join-Path $DataRoot 'hyperv'
    if ((Test-Path -LiteralPath $hyperVRoot -PathType Container) -or $knownInstances.Count -gt 0) {
        if ($null -eq (Get-Module -ListAvailable -Name Hyper-V)) { 'unsafe'; return }
        Import-Module Hyper-V -ErrorAction Stop
        foreach ($vm in @(Get-VM -ErrorAction Stop)) {
            $paths = [Collections.Generic.List[string]]::new()
            foreach ($name in @('Path', 'ConfigurationLocation', 'SnapshotFileLocation', 'SmartPagingFilePath')) {
                $property = $vm.PSObject.Properties[$name]
                if ($null -ne $property -and -not [String]::IsNullOrWhiteSpace([string]$property.Value)) {
                    $paths.Add([string]$property.Value)
                }
            }
            Get-VMHardDiskDrive -VM $vm -ErrorAction Stop | ForEach-Object {
                if (-not [String]::IsNullOrWhiteSpace($_.Path)) { $paths.Add([string]$_.Path) }
            }
            $knownVm = @($knownInstances | Where-Object { $vm.Name.EndsWith("-$_", [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
            if ($knownVm -or @($paths | Where-Object { Test-WithinRoot $_ $hyperVRoot }).Count -gt 0) {
                if (-not $ForUpgrade -or [string]$vm.State -ne 'Off') { 'active'; return }
            }
        }
    }
    'clean'
}
catch {
    'unsafe'
}
