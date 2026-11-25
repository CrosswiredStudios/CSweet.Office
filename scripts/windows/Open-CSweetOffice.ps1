Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$node = Join-Path $PSScriptRoot 'payload\node\CSweet.Office.Node.exe'
function Open-OfficePage { Start-Process -FilePath $node -ArgumentList '--open-office' -WindowStyle Hidden }
Open-OfficePage
$mutex = [Threading.Mutex]::new($false, 'Local\CSweet.Office.Tray')
if (-not $mutex.WaitOne(0)) { $mutex.Dispose(); return }
$tray = [Windows.Forms.NotifyIcon]::new()
try {
    $tray.Icon = [Drawing.SystemIcons]::Application
    $tray.Text = 'C-Sweet Office'
    $menu = [Windows.Forms.ContextMenuStrip]::new()
    $status = $menu.Items.Add('Checking Office services')
    $status.Enabled = $false
    $menu.add_Opening({
        $nodeService = Get-Service -Name 'CSweet.Office.Node' -ErrorAction SilentlyContinue
        $status.Text = if ($nodeService) { "Office Node: $($nodeService.Status)" } else { 'Office is awaiting setup' }
    })
    [void]$menu.Items.Add('Open C-Sweet Office', $null, { Open-OfficePage })
    [void]$menu.Items.Add('Quit tray icon', $null, { [Windows.Forms.Application]::ExitThread() })
    $tray.ContextMenuStrip = $menu
    $tray.add_DoubleClick({ Open-OfficePage })
    $tray.Visible = $true
    [Windows.Forms.Application]::Run()
} finally { $tray.Dispose(); $mutex.ReleaseMutex(); $mutex.Dispose() }
