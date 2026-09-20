[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('win-x64','linux-x64')][string] $RuntimeIdentifier,
    [Parameter(Mandatory)][string] $OutputRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$release = & "$PSScriptRoot/Get-OfficeReleaseMetadata.ps1" -Version ([xml](Get-Content "$root/Directory.Build.props")).Project.PropertyGroup.VersionPrefix
if (Test-Path $OutputRoot) { throw 'Choose a new, empty bundle directory.' }
New-Item -ItemType Directory $OutputRoot | Out-Null
$projects = [ordered]@{
    runtime = 'CSweet.Office.RuntimeHost'; node = 'CSweet.Office.Node'
    smoke = 'CSweet.Office.WindowsSmokeTest'
}
if ($RuntimeIdentifier -eq 'win-x64') {
    $projects.helper = 'CSweet.Office.Runtime.HyperV.Helper'
    $projects.configurator = 'CSweet.Office.Configurator'
} else { $projects.helper = 'CSweet.Office.Runtime.Firecracker.Helper' }
foreach ($entry in $projects.GetEnumerator()) {
    dotnet publish "$root/src/$($entry.Value)/$($entry.Value).csproj" -c Release -r $RuntimeIdentifier --self-contained true `
        -p:UseLocalOfficeContracts=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "$OutputRoot/components/$($entry.Key)"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($entry.Value)" }
}
dotnet publish "$root/src/CSweet.Office.GuestProbe/CSweet.Office.GuestProbe.csproj" -c Release -r linux-x64 --self-contained true `
    -p:UseLocalOfficeContracts=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "$OutputRoot/components/probe"
if ($LASTEXITCODE -ne 0) { throw 'Probe publish failed.' }
Copy-Item "$root/Directory.Build.props" $OutputRoot
New-Item -ItemType Directory "$OutputRoot/scripts" | Out-Null
$platform = if ($RuntimeIdentifier -eq 'win-x64') { 'windows' } else { 'linux' }
Copy-Item "$root/scripts/$platform" "$OutputRoot/scripts/$platform" -Recurse
[ordered]@{ schemaVersion = 1; officeVersion = $release.Version; contractsVersion = $release.ContractsVersion
    runtimeIdentifier = $RuntimeIdentifier; signing = 'development'; requiresHostCertification = $true
} | ConvertTo-Json | Set-Content "$OutputRoot/office-bundle.json" -Encoding utf8
