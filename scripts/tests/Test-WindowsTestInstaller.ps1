[CmdletBinding()]
param([Parameter(Mandatory)][string] $Output)
$ErrorActionPreference = 'Stop'
$exe = @(Get-ChildItem $Output -Filter '*.exe')
$msi = @(Get-ChildItem $Output -Filter '*.msi')
if ($exe.Count -ne 1 -or $msi.Count -ne 1) { throw 'Missing installer pair.' }
$stream = [Reflection.Assembly]::LoadFile($exe[0].FullName).GetManifestResourceStream('OfficeFiles')
$reader = [IO.StreamReader]::new($stream)
try {
    $build = $reader.ReadLine()
    if ($build -notmatch '^\d+\.\d+\.\d+-\d+$' -or $reader.ReadLine() -cne "$($msi[0].Name)|$($msi[0].Length)|$((Get-FileHash $msi[0].FullName).Hash)" -or !$reader.EndOfStream) { throw 'Setup does not pin this MSI.' }
} finally { $reader.Dispose() }
$programs = [Environment]::GetFolderPath('ProgramFiles')
$setup = Join-Path $programs 'CSweet\Office Test Setup'
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'C-Sweet Office (Windows test).lnk'
$office = Join-Path $programs 'CSweet\Office'
if ((Test-Path $setup) -or (Test-Path $office)) { throw 'Use a clean Windows test runner.' }
function Invoke-Msi([string] $Action, [string] $Ui = '/qn') {
    $log = [IO.Path]::GetFullPath((Join-Path $Output "msi-$Action.log"))
    $proc = Start-Process msiexec.exe -ArgumentList "/$Action `"$($msi[0].FullName)`" $Ui /norestart /l*v `"$log`"" -PassThru
    if (!$proc.WaitForExit(120000)) { throw "Windows Installer did not finish. See $log" }
    if ($proc.ExitCode -ne 0) { throw "Windows Installer returned $($proc.ExitCode). See $log" }
}
Invoke-Msi i '/qf'
try {
    # Exercise the same full-UI entry point as double-clicking the MSI. Cancel before any host changes.
    Add-Type -AssemblyName UIAutomationClient
    $root = [Windows.Automation.AutomationElement]::RootElement
    $windowTitle = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'C-Sweet Office Setup')
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $window = $root.FindFirst([Windows.Automation.TreeScope]::Children, $windowTitle)
        if (!$window) { Start-Sleep -Milliseconds 300 }
    } until ($window -or [DateTime]::UtcNow -ge $deadline)
    if (!$window) { throw 'Double-click installation did not open the Office setup wizard.' }
    foreach ($name in @('Install', 'Cancel')) {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name)
        $button = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if (!$button -or !$button.Current.IsEnabled) { throw "Missing enabled wizard button: $name" }
    }
    $wizard = Get-Process -Id $window.Current.ProcessId
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (!$wizard.WaitForExit(15000)) { throw 'Cancelling the wizard did not close it.' }
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    if (!(Test-Path $shortcut) -or $link.Arguments -notlike "*$setup\Open-WindowsTestOffice.ps1*") { throw 'The Start menu launcher is missing.' }
    . (Join-Path $setup 'CSweet.OfficeRelease.ps1')
    $manifest = Read-CSweetSignedReleaseManifest ([IO.File]::ReadAllText("$setup\office-bootstrap.p7m.b64")) "$setup\office-release-signer.cer"
    $version = $manifest.officeVersion
    if ("windows-test-$build" -cne [IO.File]::ReadAllText("$setup\test-build.txt") -or !$build.StartsWith("$version-")) { throw 'The installed manifest and downloader differ.' }
    $support = @($manifest.assets | Where-Object name -CEQ "csweet-office-$version-windows-support.zip")
    $dest = Join-Path $Output 'support'
    $parts = [Collections.Generic.List[string]]::new()
    $verified = Receive-CSweetOfficeAsset $support[0] $dest -OnPart { param($number, $count) $parts.Add("$number/$count") }
    if (($parts -join ',') -cne '1/1') { throw 'The download did not report its part count.' }
    if (!(Test-Path "$verified\scripts\windows\Initialize-CSweetWindowsIsolationTest.ps1") -or
        (Get-FileHash "$verified\scripts\windows\office-release-signer.cer").Hash -ne (Get-FileHash "$setup\office-release-signer.cer").Hash) {
        throw 'The real release support payload does not match the installed bootstrap.'
    }
    if (Get-Service -Name 'CSweet.Office.*' -ErrorAction SilentlyContinue) { throw 'Services were registered before certification and pairing.' }
    # Preparation must preserve an unregistered installation, including one without services.
    $existing = New-Item -ItemType Directory -Path $office -ErrorAction Stop
    try {
        $job = [guid]::NewGuid()
        $shell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
        $probe = Start-Process $shell -ArgumentList ('-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "{0}" -JobId {1}' -f "$setup\Prepare-WindowsTestOffice.ps1", $job) -WindowStyle Hidden -PassThru -Wait
        $reason = [IO.File]::ReadAllText((Join-Path $setup ("status-{0}.txt" -f $job.ToString('N'))))
        if ($probe.ExitCode -ne 1 -or $reason -notlike 'An existing Office*') { throw 'Existing Office protection did not run before preparation.' }
    } finally { [IO.Directory]::Delete($existing.FullName, $false) }
} finally { Invoke-Msi x }
if ((Test-Path $setup) -or (Test-Path $shortcut)) { throw 'Test setup did not uninstall cleanly.' }
Write-Host 'Verified Setup EXE pin, full-UI MSI launching the wizard, Cancel, Start menu launcher, signed support download, and uninstall.'
