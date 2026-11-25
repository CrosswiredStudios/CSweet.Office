Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$title = 'C-Sweet Office'
$office = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'CSweet\Office'
$build = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'test-build.txt')).Trim()
$receipt = Join-Path $office 'windows-test-build.txt'

function Test-Prepared {
    return ([IO.File]::Exists($receipt) -and [IO.File]::ReadAllText($receipt).Trim() -ceq $build -and
        [IO.File]::Exists((Join-Path $office 'payload\runtime-manifest.json')))
}

try {
    if (Test-Prepared) {
        & (Join-Path $office 'Open-CSweetOffice.ps1')
        return
    }

    $form = [Windows.Forms.Form]::new()
    $form.Text = 'C-Sweet Office Setup'
    $form.Font = [Drawing.Font]::new('Segoe UI', 10)
    $form.AutoScaleMode = 'Font'
    $form.ClientSize = [Drawing.Size]::new(570, 335)
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.StartPosition = 'CenterScreen'
    $form.BackColor = [Drawing.ColorTranslator]::FromHtml('#101824')
    $form.ForeColor = [Drawing.ColorTranslator]::FromHtml('#f3f6fa')
    $accent = [Drawing.ColorTranslator]::FromHtml('#93e2cc')

    $product = [Windows.Forms.Label]::new()
    $product.SetBounds(24, 18, 520, 24)
    $product.Text = 'C-Sweet Office'
    $product.ForeColor = $accent
    $heading = [Windows.Forms.Label]::new()
    $heading.SetBounds(22, 56, 526, 52)
    $heading.Font = [Drawing.Font]::new('Segoe UI', 18, [Drawing.FontStyle]::Bold)
    $heading.Text = 'Set up Office on this PC'
    $details = [Windows.Forms.Label]::new()
    $details.SetBounds(26, 112, 515, 128)
    $details.Text = "Office lets this PC run work from your team's Headquarters. After setup, choose a C-Sweet hosted Headquarters or enter your own address." + [Environment]::NewLine + [Environment]::NewLine +
        'Setup downloads about 7 GB and checks Hyper-V. You need administrator access and at least 64 GiB free on the Windows drive. Windows may need to restart.'
    $bar = [Windows.Forms.ProgressBar]::new()
    $bar.SetBounds(26, 245, 516, 9)
    $bar.Visible = $false
    $secondary = [Windows.Forms.Button]::new()
    $secondary.SetBounds(280, 276, 108, 36)
    $secondary.Text = 'Cancel'
    $secondary.FlatStyle = 'System'
    $primary = [Windows.Forms.Button]::new()
    $primary.SetBounds(398, 276, 144, 36)
    $primary.Text = 'Install'
    $primary.FlatStyle = 'System'
    $form.Controls.AddRange(@($product, $heading, $details, $bar, $primary, $secondary))
    $form.AcceptButton = $primary
    $form.CancelButton = $secondary
    $form.ActiveControl = $primary

    $job = @{ Worker = $null; StatusFile = ''; ProgressFile = ''; OpenOffice = $false }
    $phaseText = @{
        'host-preflight' = 'Checking your Windows settings.'
        'enable-hyperv' = 'Turning on Hyper-V so Office can run work securely.'
        'windows-restart' = 'Windows needs to restart to finish enabling Hyper-V.'
        'wait-existing-build' = 'Waiting for another Office setup operation to finish.'
        'prepare-guest' = 'Preparing the secure environment where Office will run your work.'
        'guest-cache' = 'Checking the Office files already on this PC.'
        'publish-components' = 'Preparing the Office services.'
        'certify-runtime' = 'Checking that this PC can run work securely. This can take several minutes.'
        'sign-guest' = 'Recording the security checks for this PC.'
        'package-runtime' = 'Saving the Office files on this PC.'
        'package-complete' = 'Finishing setup.'
        'setup-complete' = 'Finishing setup.'
    }
    $timer = [Windows.Forms.Timer]::new()
    $timer.Interval = 750
    function Show-Result([string] $TitleText, [string] $Message, [string] $ButtonText) {
        $heading.Text = $TitleText
        $details.Text = $Message
        $primary.Text = $ButtonText
        $primary.Enabled = $true
        $secondary.Text = 'Close'
        $secondary.Visible = $ButtonText -ne 'Close'
        $bar.Visible = $false
        $primary.Focus() | Out-Null
    }

    $primary.add_Click({
        if ($primary.Text -eq 'Open Office') { $job.OpenOffice = $true; $form.Close(); return }
        if ($primary.Text -eq 'Close') { $form.Close(); return }
        $id = [guid]::NewGuid()
        $job.StatusFile = Join-Path $PSScriptRoot ("status-{0}.txt" -f $id.ToString('N'))
        $job.ProgressFile = Join-Path $env:ProgramData ("CSweet\Setup\windows-isolation-{0}.json" -f $id.ToString('N'))
        $shell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
        $arguments = '-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "{0}" -JobId {1}' -f (Join-Path $PSScriptRoot 'Prepare-WindowsTestOffice.ps1'), $id
        try {
            $job.Worker = Start-Process -FilePath $shell -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru -ErrorAction Stop
            $heading.Text = 'Installing Office on this PC'
            $details.Text = 'Checking Windows and Hyper-V. Keep this PC awake while setup runs.'
            $primary.Enabled = $false
            $secondary.Visible = $false
            $bar.Style = 'Marquee'
            $bar.Visible = $true
            $timer.Start()
        } catch {
            Show-Result 'Setup could not start' $_.Exception.Message 'Try again'
        }
    })
    $secondary.add_Click({ $form.Close() })
    $form.add_FormClosing({ param($sender, $eventArgs)
        if ($null -ne $job.Worker -and $eventArgs.CloseReason -eq 'UserClosing') { $eventArgs.Cancel = $true }
    })
    $timer.add_Tick({
        $worker = $job.Worker
        if ($worker.HasExited) {
            $timer.Stop()
            $code = $worker.ExitCode
            $worker.Dispose()
            $job.Worker = $null
            if ($code -eq 3010) {
                Show-Result 'Restart Windows to continue' 'Save your work and restart this PC. Then open C-Sweet Office from Start to continue setup.' 'Close'
            } elseif ($code -eq 0 -and (Test-Prepared)) {
                Show-Result 'Ready to connect to Headquarters' 'Open Office to connect this PC to your hosted or private Headquarters and finish installing the Office services. You can also open Office later from Start.' 'Open Office'
            } else {
                $reason = if ([IO.File]::Exists($job.StatusFile)) { [IO.File]::ReadAllText($job.StatusFile) }
                    else { 'Close any other Office setup window and try again.' }
                Show-Result 'Setup could not finish' $reason 'Try again'
            }
            return
        }
        try {
            if ([IO.File]::Exists($job.ProgressFile)) {
                $progress = [IO.File]::ReadAllText($job.ProgressFile) | ConvertFrom-Json
                $description = $phaseText[[string]$progress.phaseKey]
                $details.Text = if ($description) { $description } else { 'Preparing Office on this PC.' }
                $bar.Style = 'Continuous'
                $bar.Value = [Math]::Max(0, [Math]::Min(100, [int]$progress.percentComplete))
            } elseif ([IO.File]::Exists($job.StatusFile)) {
                $details.Text = [IO.File]::ReadAllText($job.StatusFile)
            }
        } catch [IO.IOException] {} catch [UnauthorizedAccessException] {} catch [ArgumentException] {}
    })
    try { [void]$form.ShowDialog() }
    finally { $timer.Dispose(); $form.Dispose() }
    if ($job.OpenOffice) { & (Join-Path $office 'Open-CSweetOffice.ps1') }
} catch {
    [void][Windows.Forms.MessageBox]::Show($_.Exception.Message, $title, 'OK', 'Error')
}
