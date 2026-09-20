param([Parameter(Mandatory)][string] $NodeExecutable)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) "office-prebuilt-test-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $root | Out-Null
try {
    $bundle = Join-Path $root 'bundle'
    foreach ($component in @('runtime', 'helper', 'node', 'configurator')) {
        New-Item -ItemType Directory "$bundle/components/$component" -Force | Out-Null
        [IO.File]::WriteAllText("$bundle/components/$component/fixture.txt", $component)
    }
    Copy-Item -LiteralPath $NodeExecutable "$bundle/components/node/CSweet.Office.Node.exe"
    foreach ($name in @('image.vhdx','image.sig','signer.cer','evidence.json')) {
        [IO.File]::WriteAllText("$root/$name", 'test fixture only')
    }
    # Package construction with prebuilt inputs must never invoke a compiler.
    function dotnet { throw 'A prebuilt payload attempted to run dotnet.' }
    & "$PSScriptRoot/../windows/New-CSweetWindowsRuntimePayload.ps1" -PrebuiltRoot $bundle `
        -GuestImage "$root/image.vhdx" -GuestImageSignature "$root/image.sig" `
        -GuestImageSigningCertificate "$root/signer.cer" -GuestImageSigningCertificateThumbprint ('a' * 40) `
        -CertificationEvidence "$root/evidence.json" -CertificationSuiteVersion 'test-fixture' `
        -CertifiedAt ([DateTimeOffset]::UtcNow) -CertificationExpiresAt ([DateTimeOffset]::UtcNow.AddHours(1).ToString('O')) `
        -PackageVersion 'test-fixture' -OutputRoot "$root/payload"
    $manifest = Get-Content "$root/payload/runtime-manifest.json" -Raw | ConvertFrom-Json
    $expectedVersion = [Version](Get-Item -LiteralPath $NodeExecutable).VersionInfo.FileVersion
    if ($manifest.officeVersion -ne $expectedVersion.ToString(3)) { throw 'Manifest and prebuilt node version disagree.' }
    foreach ($file in $manifest.files) {
        $digest = (Get-FileHash -LiteralPath "$root/payload/$($file.path)" -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($digest -cne $file.sha256) { throw "Incorrect manifest hash: $($file.path)" }
    }
    if ((Get-Content "$root/payload/runtime/fixture.txt") -ne 'runtime') { throw 'Prebuilt runtime was not copied.' }
    Write-Host 'Prebuilt payload copy, version, hashes, and no-compiler checks passed.'
} finally {
    if ([IO.Path]::GetFullPath($root).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
