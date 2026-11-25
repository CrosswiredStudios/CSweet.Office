Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$title = 'C-Sweet Office (Windows test)'
$office = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'CSweet\Office'
$build = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'test-build.txt')).Trim()
$receipt = Join-Path $office 'windows-test-build.txt'

function Test-Prepared {
    return ([IO.File]::Exists($receipt) -and [IO.File]::ReadAllText($receipt).Trim() -ceq $build -and
        [IO.File]::Exists((Join-Path $office 'payload\runtime-manifest.json')))
}

try {
    if (!(Test-Prepared)) {
        $intro = "Prepare this PC for Office testing? Setup downloads the Windows guest bundle (about 7 GB), then tests Hyper-V on this computer. You will need administrator approval. Keep the PC awake until the Headquarters sign-in page opens."
        if ([Windows.Forms.MessageBox]::Show($intro, $title, 'OKCancel', 'Information') -ne 'OK') { return }
        $id = [guid]::NewGuid()
        $statusFile = Join-Path $PSScriptRoot ("status-{0}.txt" -f $id.ToString('N'))
        $progressFile = Join-Path $env:ProgramData ("CSweet\Setup\windows-isolation-{0}.json" -f $id.ToString('N'))
        $shell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
        $arguments = '-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "{0}" -JobId {1}' -f (Join-Path $PSScriptRoot 'Prepare-WindowsTestOffice.ps1'), $id
        $worker = Start-Process -FilePath $shell -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
        $form = [Windows.Forms.Form]::new()
        $form.Text = $title
        $form.StartPosition = 'CenterScreen'
        $form.ClientSize = [Drawing.Size]::new(520, 150)
        $form.Padding = [Windows.Forms.Padding]::new(20)
        $form.ControlBox = $false
        $label = [Windows.Forms.Label]::new()
        $label.Dock = 'Fill'
        $label.Font = [Drawing.SystemFonts]::MessageBoxFont
        $label.Text = 'Preparing this computer...'
        $bar = [Windows.Forms.ProgressBar]::new()
        $bar.Dock = 'Bottom'
        $bar.Style = 'Marquee'
        $form.Controls.Add($label)
        $form.Controls.Add($bar)
        $timer = [Windows.Forms.Timer]::new()
        $timer.Interval = 750
        $timer.add_Tick({
            if ($worker.HasExited) { $form.Close(); return }
            try {
                if ([IO.File]::Exists($progressFile)) {
                    $progress = [IO.File]::ReadAllText($progressFile) | ConvertFrom-Json
                    $label.Text = $progress.phaseDisplayName + [Environment]::NewLine + $progress.message
                } elseif ([IO.File]::Exists($statusFile)) {
                    $label.Text = [IO.File]::ReadAllText($statusFile)
                }
            } catch [IO.IOException] { } catch [ArgumentException] { }
        })
        try { $timer.Start(); [void]$form.ShowDialog() }
        finally { $timer.Dispose(); $form.Dispose() }
        $code = $worker.ExitCode
        $worker.Dispose()
        if ($code -ne 0 -or !(Test-Prepared)) {
            $reason = if ([IO.File]::Exists($statusFile)) { [IO.File]::ReadAllText($statusFile) }
                else { 'Close any other running setup, then reopen Office (Windows test) from Start.' }
            [void][Windows.Forms.MessageBox]::Show($reason, $title)
            return
        }
    }
    & (Join-Path $office 'Open-CSweetOffice.ps1')
} catch {
    [void][Windows.Forms.MessageBox]::Show($_.Exception.Message, $title, 'OK', 'Error')
}
