[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Version,
      [Parameter(Mandatory)][string]$CertificateThumbprint)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
$update = Get-Content -LiteralPath (Join-Path $Directory 'office-windows-update.json') -Raw | ConvertFrom-Json
if ($update.officeVersion -cne $Version -or $update.assets.Count -lt 1) { throw 'Missing verified Windows installer assets.' }
$manifest = New-TemporaryFile
try {
    $lines = @($Version) + @($update.assets | ForEach-Object { "$($_.name)|$($_.size)|$($_.sha256)" })
    [IO.File]::WriteAllLines($manifest.FullName, [string[]]$lines)
    $output = Join-Path $Directory "csweet-office-$Version-windows-x64-setup.exe"
    $compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /target:winexe /optimize+ /reference:System.Net.Http.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/resource:$($manifest.FullName),OfficeFiles" "/out:$output" (Join-Path $PSScriptRoot '..\windows\OfficeSetup.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Office Setup compilation failed.' }
    & signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $output
    if ($LASTEXITCODE -ne 0) { throw 'Office Setup signing failed.' }
    & signtool verify /pa $output
    if ($LASTEXITCODE -ne 0) { throw 'Office Setup signature verification failed.' }
} finally { Remove-Item -LiteralPath $manifest.FullName }
