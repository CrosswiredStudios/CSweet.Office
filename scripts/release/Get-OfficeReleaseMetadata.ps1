[CmdletBinding()]
param([Parameter(Mandatory)][string] $Tag)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($Tag -notmatch '^v(?<version>[0-9]+[.][0-9]+[.][0-9]+)$') { throw 'Release tag must be vMAJOR.MINOR.PATCH.' }
$version = $Matches.version
[xml]$build = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$runtimeVersion = [string]$build.Project.PropertyGroup.VersionPrefix
if ($runtimeVersion -cne $version) {
    throw "Release tag $Tag does not match the runtime version $runtimeVersion in Directory.Build.props."
}
[xml]$packages = Get-Content (Join-Path $root 'Directory.Packages.props') -Raw
$contract = @($packages.SelectNodes('//PackageVersion[@Include="CSweet.Office.Contracts"]'))
if ($contract.Count -ne 1 -or $contract[0].Version -notmatch '^[0-9]+[.][0-9]+[.][0-9]+$') {
    throw 'A single Office contracts package version is required.'
}
[pscustomobject]@{ Version = $version; ContractsVersion = [string]$contract[0].Version }
