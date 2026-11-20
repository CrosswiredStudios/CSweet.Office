[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$CertificateThumbprint)
$ErrorActionPreference = 'Stop'
$name = "csweet-office-$Version-windows-x64.msi"
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase((Get-Item -LiteralPath (Join-Path $Directory $name)).FullName, 0)
$media = $db.OpenView('SELECT `Cabinet` FROM `Media` ORDER BY `DiskId`')
$media.Execute()
$names = @($name)
while ($record = $media.Fetch()) {
    $cab = $record.StringData(1)
    if (-not $cab -or $cab.StartsWith('#')) { continue }
    if ($cab -cne "csweet-office-$Version-$($names.Count).cab") { throw 'Unexpected external cabinet identity.' }
    $names += $cab
}
$media.Close()
$assets = @(foreach ($item in $names) {
    $file = Get-Item -LiteralPath (Join-Path $Directory $item)
    if ((Get-AuthenticodeSignature $file.FullName).SignerCertificate.Thumbprint -ine $CertificateThumbprint) { throw "Unexpected publisher on $item." }
    @{ name=$item; size=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant() }
})
$manifest = Join-Path $Directory 'office-windows-update.json'
[IO.File]::WriteAllText($manifest, (@{ schemaVersion=1; officeVersion=$Version; platform='windows-x64'; assets=$assets } | ConvertTo-Json -Depth 5))
& (Join-Path $PSScriptRoot 'Sign-OfficeBootstrapManifest.ps1') -ManifestPath $manifest -CertificateThumbprint $CertificateThumbprint -OutputName 'office-windows-update.p7m.b64'
