# Installer-owned locations only. Never used as a workload/helper request surface (SEC-INV-20).
Set-StrictMode -Version Latest

function Get-CSweetOfficeVolumes {
    @(Get-Volume -ErrorAction Stop | Where-Object DriveLetter | ForEach-Object {
        $v = $_; $reason = $null
        try { $disks = @($v | Get-Partition | Get-Disk -ErrorAction Stop) } catch { $disks = @() }
        if ($v.DriveType -ne 'Fixed' -or $disks.Count -eq 0 -or @($disks | Where-Object { $_.BusType -in @('USB','SD','MMC','1394','iSCSI') -or $_.IsOffline -or $_.IsReadOnly }).Count -gt 0) {
            $reason = 'Choose a writable fixed local drive.'
        } elseif ($v.FileSystemType -notin @('NTFS','ReFS')) { $reason = 'Office requires NTFS or ReFS on Windows.'
        } elseif ($v.HealthStatus -ne 'Healthy') { $reason = 'This volume is not healthy or is unavailable.' }
        $mount = "$($v.DriveLetter):\"
        $system = "$($v.DriveLetter):" -eq $env:SystemDrive
        [pscustomobject]@{
            VolumeId = [string]$v.UniqueId; MountPath = $mount; Label = [string]$v.FileSystemLabel
            FileSystem = [string]$v.FileSystemType; TotalBytes = [long]$v.Size; FreeBytes = [long]$v.SizeRemaining
            IsSystemVolume = $system; IsEligible = ($null -eq $reason); UnavailableReason = $reason
            InstallRoot = if ($system) { Join-Path $env:ProgramFiles 'CSweet\Office' } else { Join-Path $mount 'CSweet\Office\program' }
            DataRoot = if ($system) { Join-Path $env:ProgramData 'CSweet\Office' } else { Join-Path $mount 'CSweet\Office\data' }
        }
    })
}

function Assert-CSweetOfficePath([string] $Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [String]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Office storage paths cannot contain reparse points.' }
        }
        $parent = [IO.Path]::GetDirectoryName($current.TrimEnd('\'))
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Get-CSweetInstalledOfficeStorage {
    param([switch] $RegisteredOnly)
    $key = 'HKLM:\SOFTWARE\CSweet\Office\Storage'
    if (-not (Test-Path -LiteralPath $key)) {
        if ($RegisteredOnly) { return $null }
        # Legacy releases only supported the established system locations.
        if ($null -eq (Get-Service -Name 'CSweet.Office.Node' -ErrorAction SilentlyContinue)) { return $null }
        $volume = Get-CSweetOfficeVolumes | Where-Object IsSystemVolume | Select-Object -First 1
        if ($null -eq $volume) { throw 'The existing Office system volume is unavailable.' }
        return [pscustomobject]@{ VolumeId = $volume.VolumeId; InstallRoot = $volume.InstallRoot; DataRoot = $volume.DataRoot }
    }
    $record = Get-ItemProperty -LiteralPath $key -ErrorAction Stop
    if ($record.SchemaVersion -ne 1) { throw 'The Office storage registration is unsupported.' }
    [pscustomobject]@{ VolumeId = [string]$record.VolumeId; InstallRoot = [string]$record.InstallRoot; DataRoot = [string]$record.DataRoot }
}

function Resolve-CSweetOfficeStorage {
    param([string] $VolumeId, [string] $InstallRoot, [string] $DataRoot,
          [int] $RequiredDiskMb = 0, [switch] $Interactive, [switch] $IgnoreExisting, [switch] $SkipSpaceCheck)
    $existing = if (-not $IgnoreExisting) { Get-CSweetInstalledOfficeStorage } else { $null }
    if ($null -ne $existing) {
        if ($VolumeId -and $VolumeId -ine $existing.VolumeId) { throw 'Moving an installed Office is not supported. Keep its existing drive.' }
        $VolumeId = $existing.VolumeId
        if (-not $InstallRoot) { $InstallRoot = $existing.InstallRoot }
        if (-not $DataRoot) { $DataRoot = $existing.DataRoot }
    }
    $volumes = @(Get-CSweetOfficeVolumes)
    $requiredBytes = if ($null -ne $existing) { 20480L * 1MB } else { ([long][Math]::Max(32768, $RequiredDiskMb) + 20480) * 1MB }
    $eligible = @($volumes | Where-Object { $_.IsEligible -and $_.FreeBytes -ge $requiredBytes })
    if (-not $VolumeId) {
        $default = @($eligible | Where-Object IsSystemVolume | Select-Object -First 1)
        if ($Interactive) {
            for ($i = 0; $i -lt $eligible.Count; $i++) {
                $v = $eligible[$i]
                Write-Host ("{0}. {1} {2}: {3:N1} GB free / {4:N1} GB total{5}" -f ($i + 1), $v.MountPath, $v.Label, ($v.FreeBytes / 1GB), ($v.TotalBytes / 1GB), $(if ($v.IsSystemVolume) { ' (System, default)' } else { '' }))
            }
            if ($eligible.Count -gt 1) {
                $answer = Read-Host 'Choose the Office drive number (Enter for the system drive)'
                if ($answer) {
                    $number = 0
                    if (-not [int]::TryParse($answer, [ref]$number) -or $number -lt 1 -or $number -gt $eligible.Count) { throw 'Choose a valid Office drive.' }
                    $VolumeId = $eligible[$number - 1].VolumeId
                }
            }
        }
        if (-not $VolumeId -and $default.Count -gt 0) { $VolumeId = $default[0].VolumeId }
        if (-not $VolumeId -and $eligible.Count -eq 1) { $VolumeId = $eligible[0].VolumeId }
    }
    $selected = $volumes | Where-Object { $_.VolumeId -ieq $VolumeId } | Select-Object -First 1
    if ($null -eq $selected -or -not $selected.IsEligible) { throw 'The selected Office drive is unavailable or unsuitable. Refresh drives and choose again.' }
    if (-not $SkipSpaceCheck -and $selected.FreeBytes -lt $requiredBytes) { throw 'The selected Office drive no longer has enough free space. Refresh drives and review storage allocation.' }
    if (-not $InstallRoot) { $InstallRoot = $selected.InstallRoot }
    if (-not $DataRoot) { $DataRoot = $selected.DataRoot }
    # Restrict destinations to installer-owned layouts. A registry value is not deletion authority.
    if ([IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') -ine $selected.InstallRoot.TrimEnd('\') -or
        [IO.Path]::GetFullPath($DataRoot).TrimEnd('\') -ine $selected.DataRoot.TrimEnd('\')) { throw 'The Office installation directories do not match the selected volume.' }
    Assert-CSweetOfficePath $InstallRoot
    Assert-CSweetOfficePath $DataRoot
    [pscustomobject]@{ VolumeId = $selected.VolumeId; InstallRoot = $selected.InstallRoot; DataRoot = $selected.DataRoot }
}

function Save-CSweetOfficeStorage([object] $Storage) {
    $key = 'HKLM:\SOFTWARE\CSweet\Office\Storage'
    New-Item -Path $key -Force | Out-Null
    $acl = New-Object Security.AccessControl.RegistrySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.RegistryAccessRule([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.RegistryAccessRule([Security.Principal.SecurityIdentifier]::new('S-1-5-11'), 'ReadKey', 'ContainerInherit', 'None', 'Allow')))
    Set-Acl -LiteralPath $key -AclObject $acl
    foreach ($name in @('VolumeId','InstallRoot','DataRoot')) {
        New-ItemProperty -LiteralPath $key -Name $name -Value ([string]$Storage.$name) -PropertyType String -Force | Out-Null
    }
    New-ItemProperty -LiteralPath $key -Name SchemaVersion -Value 1 -PropertyType DWord -Force | Out-Null
}

function Initialize-CSweetOfficeStaging([object] $Storage) {
    $root = Join-Path $Storage.DataRoot 'setup'
    Assert-CSweetOfficePath $root
    # Establish a protected vendor parent on a new destination before staging executable code.
    if ($Storage.InstallRoot -ine (Join-Path $env:ProgramFiles 'CSweet\Office')) {
        $vendorRoot = Join-Path ([IO.Path]::GetPathRoot($Storage.InstallRoot)) 'CSweet'
        foreach ($parent in @($vendorRoot, (Join-Path $vendorRoot 'Office'))) {
            if (-not (Test-Path -LiteralPath $parent)) {
                New-Item -ItemType Directory -Path $parent -Force | Out-Null
                & "$env:SystemRoot\System32\icacls.exe" $parent '/inheritance:r' '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'The Office storage parent could not be protected.' }
            }
        }
    }
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    & "$env:SystemRoot\System32\icacls.exe" $root '/inheritance:r' '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Office setup staging could not be protected.' }
    return $root
}
