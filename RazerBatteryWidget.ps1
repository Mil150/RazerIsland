#Requires -Version 5.1
param([switch]$OpenSettings)
# Razer Island (раніше Razer Battery Widget) — показує заряд бездротових пристроїв Razer (дані з логів Razer Synapse 4)
# Плаваючий віджет на робочому столі + іконки з відсотком у треї.

$ErrorActionPreference = 'Stop'
$script:LogPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'error.log'
function Write-Log([string]$msg) {
    try { Add-Content -Path $script:LogPath -Value ("[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $msg) -Encoding UTF8 } catch {}
}
trap {
    Write-Log ("ПОМИЛКА ЗАПУСКУ: $_`r`n" + $_.InvocationInfo.PositionMessage + "`r`n" + $_.ScriptStackTrace)
    break
}
Write-Log ('Старт. PowerShell ' + $PSVersionTable.PSVersion + ', ' + [IntPtr]::Size * 8 + '-bit')
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing

# --- один екземпляр ---
$createdNew = $false
$script:Mutex = New-Object System.Threading.Mutex($true, 'Local\RazerBatteryWidget_mil', [ref]$createdNew)
if (-not $createdNew) {
    # повторний запуск = перезапуск: закриваємо старий екземпляр і займаємо його місце
    Write-Log 'Знайдено запущений екземпляр — перезапускаю.'
    try {
        Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction Stop |
            Where-Object { $_.ProcessId -ne $PID -and "$($_.CommandLine)" -like '*RazerBatteryWidget.ps1*' } |
            ForEach-Object { Write-Log "Закриваю старий процес $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    } catch { Write-Log "Не вдалося знайти старий процес: $_" }
    $got = $false
    try { $got = $script:Mutex.WaitOne(5000) } catch [System.Threading.AbandonedMutexException] { $got = $true }
    if (-not $got) { Write-Log 'Старий екземпляр не закрився — вихід.'; exit }
}

Add-Type -Namespace RBW -Name Native -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool DestroyIcon(System.IntPtr h);'
Add-Type -Namespace RBW -Name Win -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern System.IntPtr FindWindow(string cls, string name);
[System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
public static extern System.IntPtr GetWindowLongPtr64(System.IntPtr h, int i);
[System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
public static extern System.IntPtr SetWindowLongPtr64(System.IntPtr h, int i, System.IntPtr v);
[System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
public static extern int GetWindowLong32(System.IntPtr h, int i);
[System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
public static extern int SetWindowLong32(System.IntPtr h, int i, int v);
public static long GetLong(System.IntPtr h, int i) { return System.IntPtr.Size == 8 ? GetWindowLongPtr64(h, i).ToInt64() : GetWindowLong32(h, i); }
public static void SetLong(System.IntPtr h, int i, long v) { if (System.IntPtr.Size == 8) SetWindowLongPtr64(h, i, new System.IntPtr(v)); else SetWindowLong32(h, i, (int)v); }
'@

$Root         = Split-Path -Parent $MyInvocation.MyCommand.Path
$SettingsPath = Join-Path $Root 'settings.json'
$LogDir       = Join-Path $env:LOCALAPPDATA 'Razer\RazerAppEngine\User Data\Logs'
$RefreshSec   = 3

# --- налаштування ---
$script:Settings = @{ Left = $null; Top = $null; Topmost = $false; Visible = $true; AutostartSet = $false; Pinned = $false; Mode = 'strip'; TrayStyle = 'icon'
                     WeatherLat = 60.7377; WeatherLon = 24.7774; WeatherCity = 'Riihimäki'
                     DiscordClientId = ''; DiscordClientSecret = ''; DiscordToken = ''; DiscordRefresh = ''
                     BgOpacity = 0.8; FirstRunDone = $false; DesktopShortcut = $false }
if (Test-Path $SettingsPath) {
    try {
        $s = Get-Content $SettingsPath -Raw | ConvertFrom-Json
        foreach ($p in $s.PSObject.Properties) { $script:Settings[$p.Name] = $p.Value }
    } catch {}
}
# v2: за замовчуванням не поверх вікон
if (-not $script:Settings.ContainsKey('V2')) { $script:Settings.Topmost = $false; $script:Settings.V2 = $true }
function Save-Settings {
    try { $script:Settings | ConvertTo-Json | Set-Content -Path $SettingsPath -Encoding UTF8 } catch {}
}

# --- кольори ---
$BrushConv = New-Object System.Windows.Media.BrushConverter
function Brush([string]$hex) { $BrushConv.ConvertFromString($hex) }
$C_Green  = '#44D62C'
$C_Yellow = '#FFD23F'
$C_Red    = '#FF3B3B'
$C_Gray   = '#6B7078'
function Get-LevelColor([int]$level, [string]$state) {
    if ($state -eq 'off') { return $C_Gray }
    if ($level -le 20) { return $C_Red }
    if ($level -le 50) { return $C_Yellow }
    return $C_Green
}

# ================== ЧИТАННЯ ДАНИХ SYNAPSE ==================
function Read-Tail([string]$path, [long]$maxBytes) {
    $share = [System.IO.FileShare]([int][System.IO.FileShare]::ReadWrite -bor [int][System.IO.FileShare]::Delete)
    $fs = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
    try {
        $len   = $fs.Length
        $start = [Math]::Max([long]0, $len - $maxBytes)
        [void]$fs.Seek($start, [System.IO.SeekOrigin]::Begin)
        $count = [int]($len - $start)
        $buf   = New-Object byte[] $count
        $read  = 0
        while ($read -lt $count) {
            $n = $fs.Read($buf, $read, $count - $read)
            if ($n -le 0) { break }
            $read += $n
        }
        return [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
    } finally { $fs.Dispose() }
}

function Get-State([string]$cs) {
    $s = "$cs".ToLowerInvariant()
    if ($s -eq 'off' -or $s -match 'disconnect|offline|sleep|unknown') { return 'off' }
    if ($s -match 'nocharge|discharg|notcharg') { return 'normal' }
    if ($s -match 'charg') { return 'charging' }
    return 'normal'
}

function Parse-Line([string]$line) {
    $devices = New-Object System.Collections.ArrayList
    $segments = $line.Split([string[]]@('{"serialNumber":"'), [System.StringSplitOptions]::None)
    for ($k = 1; $k -lt $segments.Count; $k++) {
        $seg = $segments[$k]
        $ps = [regex]::Match($seg, '"powerStatus":\{([^}]*)\}')
        if (-not $ps.Success) { continue }
        $lv = [regex]::Match($ps.Groups[1].Value, '"level":(-?\d+)')
        if (-not $lv.Success) { continue }
        $cs = [regex]::Match($ps.Groups[1].Value, '"chargingStatus":"([^"]*)"')
        $nm = [regex]::Match($seg, '"name":\{"en":"([^"]+)"')
        $ct = [regex]::Match($seg, '"category":"([A-Za-z_]+)"')
        $pr = [regex]::Match($seg, '"productId":(\d+)')
        $serial = $seg.Substring(0, [Math]::Max(0, $seg.IndexOf('"')))
        $name = if ($nm.Success) { $nm.Groups[1].Value -replace '^Razer\s+', '' } else { 'Razer' }
        [void]$devices.Add([pscustomobject]@{
            Serial   = $serial
            Name     = $name
            Category = if ($ct.Success) { $ct.Groups[1].Value.ToUpperInvariant() } else { '' }
            Level    = [Math]::Max(0, [Math]::Min(100, [int]$lv.Groups[1].Value))
            State    = Get-State ($(if ($cs.Success) { $cs.Groups[1].Value } else { '' }))
            ProductId = if ($pr.Success) { $pr.Groups[1].Value } else { '' }
            Time     = [datetime]::MinValue
        })
    }
    return ,$devices
}

function Get-LineTime([string]$line) {
    if ($line -match '^\[(\d{4}/\d\d/\d\d \d\d:\d\d:\d\d)') {
        try { return [datetime]::ParseExact($Matches[1], 'yyyy/MM/dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture) } catch {}
    }
    return [datetime]::MinValue
}

# кеш розбору: файл перечитується лише коли змінився його розмір/час
$script:FileCache = @{}
function Get-Cached($file, [string]$parser) {
    $k = "$($file.Length):$($file.LastWriteTime.Ticks)"
    $c = $script:FileCache[$file.FullName]
    if ($c -and $c.Key -eq $k) { return $c.Value }
    $v = & $parser $file
    $script:FileCache[$file.FullName] = @{ Key = $k; Value = $v }
    return $v
}

# лог трею Synapse: повний список пристроїв із зарядом
function Parse-SystrayFile($f) {
    try { $text = Read-Tail $f.FullName 4MB } catch { return $null }
    $lines = $text.Split("`n")
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        $line = $lines[$i]
        if ($line.IndexOf('"powerStatus"') -lt 0) { continue }
        $devs = Parse-Line $line
        if ($devs.Count -eq 0) { continue }
        return [pscustomobject]@{ Time = (Get-LineTime $line); Devices = $devs }
    }
    return $null
}

# лог конкретного пристрою (products_<id>_mw): тут заряд оновлюється найчастіше
function Parse-ProductFile($f) {
    try { $text = Read-Tail $f.FullName 1MB } catch { return $null }
    $lines = $text.Split("`n")
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        $line = $lines[$i]
        if ($line.IndexOf('"chargingStatus"') -lt 0 -or $line.IndexOf('"powerStatus"') -ge 0) { continue }
        $m = [regex]::Match($line, '"chargingStatus":"([^"]*)","level":(-?\d+)')
        if (-not $m.Success) { continue }
        return [pscustomobject]@{
            Time  = (Get-LineTime $line)
            Level = [Math]::Max(0, [Math]::Min(100, [int]$m.Groups[2].Value))
            State = Get-State $m.Groups[1].Value
        }
    }
    return $null
}

function Get-RazerDevices {
    if (-not (Test-Path $LogDir)) { return $null }
    $all = @(Get-ChildItem -Path $LogDir -Filter '*.log' -File -ErrorAction SilentlyContinue)
    $sys = @($all | Where-Object { $_.Name -like 'systray_systrayv*' } | Sort-Object LastWriteTime -Descending | Select-Object -First 4)
    $best = $null
    foreach ($f in $sys) {
        $r = Get-Cached $f 'Parse-SystrayFile'
        if ($r -and (-not $best -or $r.Time -gt $best.Time)) { $best = $r }
    }
    if (-not $best) { return $null }

    $latest = $best.Time
    $out = New-Object System.Collections.ArrayList
    foreach ($d in $best.Devices) {
        $dev = $d.PSObject.Copy()
        $dev.Time = $best.Time
        if ($dev.ProductId) {
            $pf = @($all | Where-Object { $_.Name -like "products_$($dev.ProductId)_mw*" } | Sort-Object LastWriteTime -Descending | Select-Object -First 2)
            foreach ($f in $pf) {
                $p = Get-Cached $f 'Parse-ProductFile'
                if ($p -and $p.Time -gt $dev.Time) { $dev.Level = $p.Level; $dev.State = $p.State; $dev.Time = $p.Time }
            }
        }
        if ($dev.Time -gt $latest) { $latest = $dev.Time }
        [void]$out.Add($dev)
    }
    return [pscustomobject]@{ Time = $latest; Devices = $out }
}

# ================== ВІКНО ВІДЖЕТА ==================
[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Razer Battery" WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ResizeMode="NoResize" ShowInTaskbar="False" Topmost="False" Width="300" Height="200"
        UseLayoutRounding="True" FontFamily="Segoe UI" WindowStartupLocation="Manual">
  <Border x:Name="Card" Margin="14" CornerRadius="15" Padding="16,12,16,12" Width="272"
          HorizontalAlignment="Center" VerticalAlignment="Top"
          Background="#CC141619" BorderBrush="#26FFFFFF" BorderThickness="1">
    <Border.Effect>
      <DropShadowEffect BlurRadius="16" ShadowDepth="0" Opacity="0.3" Color="Black"/>
    </Border.Effect>
    <StackPanel>
      <Border x:Name="Strip" ClipToBounds="True" Height="0" Opacity="0">
        <Grid VerticalAlignment="Center">
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/>
          </Grid.ColumnDefinitions>
          <StackPanel x:Name="LeftItems" Grid.Column="0" Orientation="Horizontal" HorizontalAlignment="Left" VerticalAlignment="Center"/>
          <StackPanel x:Name="StripCenter" Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center" Margin="14,0,14,0">
            <Border x:Name="MicBadgeHost" VerticalAlignment="Center" Margin="0,0,6,0" Visibility="Collapsed"/>
            <TextBlock x:Name="StripClock" Foreground="#F2F3F5" FontSize="13" FontWeight="SemiBold" VerticalAlignment="Center"/>
          </StackPanel>
          <StackPanel x:Name="RightItems" Grid.Column="2" Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center"/>
        </Grid>
      </Border>
      <Border x:Name="Notice" ClipToBounds="True" Height="0" Opacity="0">
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" VerticalAlignment="Center">
          <Border x:Name="NoticeIconHost" VerticalAlignment="Center"/>
          <TextBlock x:Name="NoticeText" FontSize="14" FontWeight="SemiBold" VerticalAlignment="Center" Margin="10,0,0,0"/>
        </StackPanel>
      </Border>
      <Border x:Name="Details" ClipToBounds="True">
      <StackPanel x:Name="DetailsContent">
      <Grid Margin="0,0,0,6">
        <StackPanel Orientation="Horizontal" VerticalAlignment="Center" HorizontalAlignment="Left">
          <Ellipse Width="8" Height="8" Fill="#44D62C" Margin="0,0,8,0" VerticalAlignment="Center"/>
          <TextBlock Text="RAZER" Foreground="#44D62C" FontWeight="Bold" FontSize="12"/>
        </StackPanel>
        <TextBlock x:Name="PinBtn" HorizontalAlignment="Right" VerticalAlignment="Center" FontSize="13" Cursor="Hand"
                   FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" Background="Transparent"/>
      </Grid>
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="230"/>
          <ColumnDefinition Width="Auto"/>
          <ColumnDefinition Width="*"/>
          <ColumnDefinition Width="Auto"/>
          <ColumnDefinition Width="260"/>
        </Grid.ColumnDefinitions>

        <!-- ЗЛІВА: батарея -->
        <StackPanel x:Name="BatteryCol" Grid.Column="0" VerticalAlignment="Top">
          <TextBlock Text="БАТАРЕЯ" Foreground="#6B7078" FontSize="10" FontWeight="SemiBold"/>
          <StackPanel x:Name="Rows"/>
          <TextBlock x:Name="Hint" Foreground="#8A8F98" FontSize="11" TextWrapping="Wrap" Margin="0,6,0,2" Visibility="Collapsed"/>
          <TextBlock x:Name="Updated" Foreground="#6B7078" FontSize="10" Margin="0,2,0,0"/>
        </StackPanel>

        <Border Grid.Column="1" Width="1" Background="#1FFFFFFF" Margin="16,2,16,2"/>

        <!-- ПОСЕРЕДИНІ: годинник, дата, погода -->
        <Border x:Name="CenterCol" Grid.Column="2" ClipToBounds="True" VerticalAlignment="Top">
        <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center">
          <TextBlock x:Name="HeaderClock" HorizontalAlignment="Center" Foreground="#F2F3F5" FontSize="40" FontWeight="SemiBold"/>
          <TextBlock x:Name="DateText" HorizontalAlignment="Center" Foreground="#A9ADB5" FontSize="12" Margin="0,-4,0,0"/>
          <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,8,0,0">
            <TextBlock x:Name="WeatherIcon" FontFamily="Segoe UI Symbol" FontSize="22" Foreground="#F2F3F5" VerticalAlignment="Center" Margin="0,0,8,0"/>
            <TextBlock x:Name="WeatherTemp" FontSize="22" FontWeight="SemiBold" Foreground="#F2F3F5" VerticalAlignment="Center" Margin="0,0,10,0"/>
            <StackPanel VerticalAlignment="Center">
              <TextBlock x:Name="WeatherText" Foreground="#A9ADB5" FontSize="11"/>
              <TextBlock x:Name="WeatherCity" Foreground="#6B7078" FontSize="10"/>
            </StackPanel>
          </StackPanel>
        </StackPanel>
        </Border>

        <Border Grid.Column="3" Width="1" Background="#1FFFFFFF" Margin="16,2,16,2"/>

        <!-- СПРАВА: розклад з Google Календаря -->
        <StackPanel x:Name="CalCol" Grid.Column="4" ClipToBounds="True" VerticalAlignment="Top">
          <TextBlock Text="РОЗКЛАД" Foreground="#6B7078" FontSize="10" FontWeight="SemiBold"/>
          <StackPanel x:Name="CalRows" Margin="0,4,0,0"/>
          <TextBlock x:Name="CalHint" Foreground="#8A8F98" FontSize="11" TextWrapping="Wrap" Margin="0,6,0,0" Visibility="Collapsed"/>
        </StackPanel>
      </Grid>
      </StackPanel>
      </Border>
    </StackPanel>
  </Border>
</Window>
'@
$Window  = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$RowsPanel = $Window.FindName('Rows')
$Updated   = $Window.FindName('Updated')
$Hint      = $Window.FindName('Hint')
$Card      = $Window.FindName('Card')
$Strip     = $Window.FindName('Strip')
$LeftItems  = $Window.FindName('LeftItems')
$RightItems = $Window.FindName('RightItems')
$StripClock = $Window.FindName('StripClock')
$StripCenter = $Window.FindName('StripCenter')
$MicBadgeHost = $Window.FindName('MicBadgeHost')
$Notice      = $Window.FindName('Notice')
$NoticeIconHost = $Window.FindName('NoticeIconHost')
$NoticeText  = $Window.FindName('NoticeText')
$HeaderClock = $Window.FindName('HeaderClock')
$Details   = $Window.FindName('Details')
$DetailsContent = $Window.FindName('DetailsContent')
$DateText    = $Window.FindName('DateText')
$WeatherIcon = $Window.FindName('WeatherIcon')
$WeatherTemp = $Window.FindName('WeatherTemp')
$WeatherText = $Window.FindName('WeatherText')
$WeatherCity = $Window.FindName('WeatherCity')
$CalRows     = $Window.FindName('CalRows')
$CalHint     = $Window.FindName('CalHint')
$BatteryCol  = $Window.FindName('BatteryCol')
$CenterCol   = $Window.FindName('CenterCol')
$CalCol      = $Window.FindName('CalCol')
$Window.Topmost = [bool]$script:Settings.Topmost

# не показувати в Alt+Tab і не ховатися при «Згорнути все» (Win+D)
$script:Exiting = $false
$Window.Add_SourceInitialized({
    try {
        $h = (New-Object System.Windows.Interop.WindowInteropHelper($Window)).Handle
        $GWL_EXSTYLE = -20; $GWL_HWNDPARENT = -8
        $ex = [RBW.Win]::GetLong($h, $GWL_EXSTYLE)
        $ex = ($ex -bor 0x80) -band (-bnot 0x40000)   # TOOLWINDOW, без APPWINDOW
        [RBW.Win]::SetLong($h, $GWL_EXSTYLE, $ex)
        $pm = [RBW.Win]::FindWindow('Progman', $null)
        if ($pm -ne [IntPtr]::Zero) { [RBW.Win]::SetLong($h, $GWL_HWNDPARENT, $pm.ToInt64()) }
    } catch {}
})
# якщо Провідник перезапуститься, вікно зникне разом з ним — тоді віджет перезапуститься сам
$Window.Add_Closed({
    if ($script:Exiting) { return }
    try { Exit-App -Restart } catch {}
})

$IconFont = New-Object System.Windows.Media.FontFamily('Segoe Fluent Icons, Segoe MDL2 Assets')
function Get-Glyph([string]$category) {
    switch -Regex ($category) {
        'MOUSE'          { return [string][char]0xE962 }
        'AUDIO|HEADSET'  { return [string][char]0xE7F6 }
        'KEYBOARD'       { return [string][char]0xE765 }
        'CONTROLLER|GAMEPAD' { return [string][char]0xE7FC }
        default          { return [string][char]0xE83F }
    }
}

$script:Rows = @{}
function New-Row($dev) {
    $grid = New-Object System.Windows.Controls.Grid
    $grid.Margin = New-Object System.Windows.Thickness(0, 8, 0, 6)
    foreach ($w in @('Auto', '*', 'Auto')) {
        $cd = New-Object System.Windows.Controls.ColumnDefinition
        $cd.Width = (New-Object System.Windows.GridLengthConverter).ConvertFromString($w)
        [void]$grid.ColumnDefinitions.Add($cd)
    }
    foreach ($h in 1..2) { [void]$grid.RowDefinitions.Add((New-Object System.Windows.Controls.RowDefinition)) }

    $icon = New-Object System.Windows.Controls.TextBlock
    $icon.FontFamily = $IconFont; $icon.FontSize = 22; $icon.Foreground = Brush '#D6D9DE'
    $icon.Text = Get-Glyph $dev.Category
    $icon.VerticalAlignment = 'Center'; $icon.Margin = New-Object System.Windows.Thickness(0, 0, 12, 0)
    [System.Windows.Controls.Grid]::SetColumn($icon, 0)

    $texts = New-Object System.Windows.Controls.StackPanel
    $texts.VerticalAlignment = 'Center'
    $name = New-Object System.Windows.Controls.TextBlock
    $name.Text = $dev.Name; $name.Foreground = Brush '#F2F3F5'; $name.FontSize = 13; $name.FontWeight = 'SemiBold'
    $name.TextTrimming = 'CharacterEllipsis'
    $status = New-Object System.Windows.Controls.TextBlock
    $status.Foreground = Brush '#8A8F98'; $status.FontSize = 11
    [void]$texts.Children.Add($name); [void]$texts.Children.Add($status)
    [System.Windows.Controls.Grid]::SetColumn($texts, 1)

    $pct = New-Object System.Windows.Controls.TextBlock
    $pct.FontSize = 24; $pct.FontWeight = 'Bold'; $pct.VerticalAlignment = 'Center'
    [System.Windows.Controls.Grid]::SetColumn($pct, 2)

    # смужка заряду
    $track = New-Object System.Windows.Controls.Border
    $track.Height = 6; $track.CornerRadius = New-Object System.Windows.CornerRadius(3)
    $track.Background = Brush '#24FFFFFF'; $track.Margin = New-Object System.Windows.Thickness(0, 8, 0, 0)
    $barGrid = New-Object System.Windows.Controls.Grid
    $c1 = New-Object System.Windows.Controls.ColumnDefinition
    $c2 = New-Object System.Windows.Controls.ColumnDefinition
    [void]$barGrid.ColumnDefinitions.Add($c1); [void]$barGrid.ColumnDefinitions.Add($c2)
    $fill = New-Object System.Windows.Controls.Border
    $fill.CornerRadius = New-Object System.Windows.CornerRadius(3)
    [System.Windows.Controls.Grid]::SetColumn($fill, 0)
    [void]$barGrid.Children.Add($fill)
    $track.Child = $barGrid
    [System.Windows.Controls.Grid]::SetRow($track, 1)
    [System.Windows.Controls.Grid]::SetColumnSpan($track, 3)

    foreach ($el in @($icon, $texts, $pct, $track)) { [void]$grid.Children.Add($el) }
    [void]$RowsPanel.Children.Add($grid)

    # міні-елемент для смужки
    $mini = New-Object System.Windows.Controls.StackPanel
    $mini.Orientation = 'Horizontal'; $mini.Margin = New-Object System.Windows.Thickness(8, 0, 8, 0)
    $mIcon = New-Object System.Windows.Controls.TextBlock
    $mIcon.FontFamily = $IconFont; $mIcon.FontSize = 13; $mIcon.Foreground = Brush '#D6D9DE'
    $mIcon.Text = Get-Glyph $dev.Category; $mIcon.VerticalAlignment = 'Center'
    $mIcon.Margin = New-Object System.Windows.Thickness(0, 0, 5, 0)
    $mPct = New-Object System.Windows.Controls.TextBlock
    $mPct.FontSize = 12; $mPct.FontWeight = 'Bold'; $mPct.VerticalAlignment = 'Center'
    $mTrack = New-Object System.Windows.Controls.Border
    $mTrack.Width = 26; $mTrack.Height = 4; $mTrack.CornerRadius = New-Object System.Windows.CornerRadius(2)
    $mTrack.Background = Brush '#24FFFFFF'; $mTrack.VerticalAlignment = 'Center'
    $mTrack.Margin = New-Object System.Windows.Thickness(6, 1, 0, 0)
    $mGrid = New-Object System.Windows.Controls.Grid
    $m1 = New-Object System.Windows.Controls.ColumnDefinition
    $m2 = New-Object System.Windows.Controls.ColumnDefinition
    [void]$mGrid.ColumnDefinitions.Add($m1); [void]$mGrid.ColumnDefinitions.Add($m2)
    $mFill = New-Object System.Windows.Controls.Border
    $mFill.CornerRadius = New-Object System.Windows.CornerRadius(2)
    [void]$mGrid.Children.Add($mFill)
    $mTrack.Child = $mGrid
    foreach ($el in @($mIcon, $mPct, $mTrack)) { [void]$mini.Children.Add($el) }

    return @{ Grid = $grid; Icon = $icon; Name = $name; Status = $status; Pct = $pct; Fill = $fill; C1 = $c1; C2 = $c2
              Mini = $mini; MIcon = $mIcon; MPct = $mPct; MFill = $mFill; M1 = $m1; M2 = $m2 }
}

function Update-Row($row, $dev, [bool]$present) {
    $state = if ($present) { $dev.State } else { 'off' }
    $color = Get-LevelColor $dev.Level $state
    $row.Name.Text = $dev.Name
    $row.Pct.Text = "$($dev.Level)%"
    $row.Pct.Foreground = Brush $color
    $row.Fill.Background = Brush $color
    $row.C1.Width = New-Object System.Windows.GridLength([double]$dev.Level, [System.Windows.GridUnitType]::Star)
    $row.C2.Width = New-Object System.Windows.GridLength([double](100 - $dev.Level), [System.Windows.GridUnitType]::Star)
    $row.Icon.Opacity = if ($state -eq 'off') { 0.45 } else { 1.0 }
    $row.MPct.Text = "$($dev.Level)%"
    $row.MPct.Foreground = Brush $color
    $row.MFill.Background = Brush $color
    $row.M1.Width = New-Object System.Windows.GridLength([double]$dev.Level, [System.Windows.GridUnitType]::Star)
    $row.M2.Width = New-Object System.Windows.GridLength([double](100 - $dev.Level), [System.Windows.GridUnitType]::Star)
    $row.MIcon.Opacity = $row.Icon.Opacity
    $row.Status.Text = switch ($state) {
        'charging' { "$([char]0x26A1) Заряджається" }
        'off'      { 'Вимкнено / сон · останнє значення' }
        default    { if ($dev.Level -le 20) { 'Низький заряд — підзаряди' } else { 'Працює від батареї' } }
    }
}

# ================== ТРЕЙ ==================
$script:Tray = @{}
$TrayMenu = New-Object System.Windows.Forms.ContextMenuStrip
$miShow = $TrayMenu.Items.Add('Показати / сховати віджет')
$miTop  = New-Object System.Windows.Forms.ToolStripMenuItem('Поверх усіх вікон')
$miTop.CheckOnClick = $true; $miTop.Checked = [bool]$script:Settings.Topmost
[void]$TrayMenu.Items.Add($miTop)
$miAuto = New-Object System.Windows.Forms.ToolStripMenuItem('Запускати разом з Windows')
$miAuto.CheckOnClick = $true
$miPin = New-Object System.Windows.Forms.ToolStripMenuItem('Закріпити на місці')
$miPin.CheckOnClick = $true
[void]$TrayMenu.Items.Add($miPin)
$miPin.Add_Click({ Set-Pinned $miPin.Checked })
[void]$TrayMenu.Items.Add($miAuto)
[void]$TrayMenu.Items.Add('-')
$miExit = $TrayMenu.Items.Add('Вийти')

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# --- мінімалістичні монохромні іконки трею (білі на темній темі, чорні на світлій) ---
function Get-SystemLightTheme {
    try { return ((Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name SystemUsesLightTheme -ErrorAction Stop).SystemUsesLightTheme -eq 1) }
    catch { return $false }
}
$script:GlyphFontName = 'Segoe MDL2 Assets'
try {
    $fams = (New-Object System.Drawing.Text.InstalledFontCollection).Families | ForEach-Object { $_.Name }
    if ($fams -contains 'Segoe Fluent Icons') { $script:GlyphFontName = 'Segoe Fluent Icons' }
} catch {}

function Set-TrayIcon($ni, [int]$level, [string]$category, [string]$state) {
    $light = Get-SystemLightTheme
    $style = "$($script:Settings.TrayStyle)"
    $key = "$level|$state|$light|$style|$category"
    if ($script:IconKeys[$ni.GetHashCode()] -eq $key) { return }
    $script:IconKeys[$ni.GetHashCode()] = $key

    $size = 32
    $alpha = if ($state -eq 'off') { 120 } else { 255 }
    $col = if ($light) { [System.Drawing.Color]::FromArgb($alpha, 0, 0, 0) } else { [System.Drawing.Color]::FromArgb($alpha, 255, 255, 255) }
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)
        $brush = New-Object System.Drawing.SolidBrush($col)
        $pen = New-Object System.Drawing.Pen($col, 2)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'

        # верхня частина: значок пристрою або цифри
        if ($style -eq 'number') {
            $text = "$level"
            $fs = if ($text.Length -ge 3) { 15 } else { 20 }
            $font = New-Object System.Drawing.Font('Segoe UI Semibold', $fs, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
            $g.DrawString($text, $font, $brush, (New-Object System.Drawing.RectangleF(0, -1, $size, 23)), $sf)
        } else {
            $glyph = Get-Glyph $category
            $font = New-Object System.Drawing.Font($script:GlyphFontName, 19, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
            $g.DrawString($glyph, $font, $brush, (New-Object System.Drawing.RectangleF(0, 0, $size, 22)), $sf)
        }
        $font.Dispose()

        # нижня частина: батарейка з рівнем заряду
        $bx = 3; $by = 22; $bw = 23; $bh = 8
        $g.DrawRectangle($pen, $bx, $by, $bw, $bh)
        $g.FillRectangle($brush, ($bx + $bw + 1), ($by + 2), 3, ($bh - 4))
        $inner = [Math]::Round(($bw - 4) * [Math]::Max(0, [Math]::Min(100, $level)) / 100.0)
        if ($level -gt 0 -and $inner -lt 1) { $inner = 1 }
        if ($inner -gt 0) { $g.FillRectangle($brush, ($bx + 2), ($by + 2), $inner, ($bh - 4) + 1) }

        # заряджання — маленька блискавка у правому верхньому куті
        if ($state -eq 'charging') {
            $pts = [System.Drawing.PointF[]]@(
                (New-Object System.Drawing.PointF(27, 0)), (New-Object System.Drawing.PointF(22, 9)),
                (New-Object System.Drawing.PointF(26, 9)), (New-Object System.Drawing.PointF(24, 16)),
                (New-Object System.Drawing.PointF(31, 6)), (New-Object System.Drawing.PointF(27, 6)),
                (New-Object System.Drawing.PointF(30, 0)))
            $g.FillPolygon($brush, $pts)
        }
        $brush.Dispose(); $pen.Dispose(); $sf.Dispose()
    } finally { $g.Dispose() }

    $h = $bmp.GetHicon()
    $bmp.Dispose()
    $ni.Icon = [System.Drawing.Icon]::FromHandle($h)
    $old = $ni.Tag
    $ni.Tag = $h
    if ($old -is [System.IntPtr] -and $old -ne [System.IntPtr]::Zero) { [void][RBW.Native]::DestroyIcon($old) }
}
$script:IconKeys = @{}

function Toggle-Widget {
    if ($Window.IsVisible) { $Window.Hide(); $script:Settings.Visible = $false }
    else { $Window.Show(); $Window.Activate() | Out-Null; $script:Settings.Visible = $true }
    Save-Settings
    try { Update-Topmost; Update-MicWindow } catch {}
}

function Get-TrayItem($dev) {
    if ($script:Tray.ContainsKey($dev.Serial)) { return $script:Tray[$dev.Serial] }
    $ni = New-Object System.Windows.Forms.NotifyIcon
    $ni.ContextMenuStrip = $TrayMenu
    $ni.Add_MouseClick({ param($s, $e) if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) { Toggle-Widget } })
    $ni.Visible = $true
    $script:Tray[$dev.Serial] = $ni
    return $ni
}

# ================== ОНОВЛЕННЯ ==================
$script:Known = @{}
function Refresh-All {
    $data = $null
    try { $data = Get-RazerDevices } catch {}
    $present = @{}
    if ($data -and $data.Devices) {
        foreach ($d in $data.Devices) { $script:Known[$d.Serial] = $d; $present[$d.Serial] = $true }
        $Updated.Text = if ($data.Time -and $data.Time -gt [datetime]::MinValue) { 'дані Synapse о ' + $data.Time.ToString('HH:mm') } else { '' }
        $Hint.Visibility = 'Collapsed'
    } else {
        $Hint.Text = 'Немає даних від Razer Synapse. Переконайся, що Synapse запущений і пристрої увімкнені.'
        $Hint.Visibility = 'Visible'
        $Updated.Text = ''
    }

    foreach ($serial in @($script:Known.Keys)) {
        $d = $script:Known[$serial]
        $isOn = $present.ContainsKey($serial)
        if (-not $script:Rows.ContainsKey($serial)) { $script:Rows[$serial] = New-Row $d }
        Update-Row $script:Rows[$serial] $d $isOn

        $state = if ($isOn) { $d.State } else { 'off' }
        $color = Get-LevelColor $d.Level $state
        $ni = Get-TrayItem $d
        Set-TrayIcon $ni $d.Level $d.Category $state
        $suffix = switch ($state) { 'charging' { ' (заряджається)' } 'off' { ' (вимкнено)' } default { '' } }
        $tip = "$($d.Name): $($d.Level)%$suffix"
        if ($tip.Length -gt 63) { $tip = $tip.Substring(0, 63) }
        $ni.Text = $tip
    }

    # розкласти міні-елементи смужки: мишка зліва від годинника, навушники справа
    $ordered = @($script:Known.Values | Sort-Object @{ Expression = { if ($_.Category -eq 'MOUSE') { 0 } elseif ($_.Category -match 'AUDIO|HEADSET') { 2 } else { 1 } } }, Name)
    $LeftItems.Children.Clear(); $RightItems.Children.Clear()
    $half = [Math]::Ceiling($ordered.Count / 2)
    for ($i = 0; $i -lt $ordered.Count; $i++) {
        $mini = $script:Rows[$ordered[$i].Serial].Mini
        if ($i -lt $half) { [void]$LeftItems.Children.Add($mini) } else { [void]$RightItems.Children.Add($mini) }
    }
    Sync-Layout
}

# ================== ПОДІЇ ==================
$PinBtn = $Window.FindName('PinBtn')
function Set-Pinned([bool]$on) {
    $script:Settings.Pinned = $on
    $PinBtn.Text = [string][char]0xE718
    $PinBtn.Foreground = Brush $(if ($on) { $C_Green } else { '#6B7078' })
    $PinBtn.Opacity = if ($on) { 1.0 } else { 0.7 }
    $PinBtn.ToolTip = if ($on) { 'Закріплено — натисни, щоб відкріпити' } else { 'Закріпити на місці' }
    try { $wPin.IsChecked = $on; $miPin.Checked = $on } catch {}
    Save-Settings
}
$PinBtn.Add_MouseLeftButtonDown({ param($s, $e) $e.Handled = $true; Set-Pinned (-not [bool]$script:Settings.Pinned) })

$Window.Add_MouseLeftButtonDown({
    if ([bool]$script:Settings.Pinned -or $script:Settings.Mode -eq 'strip') { return }
    try { $Window.DragMove() } catch {}
    $script:Settings.Left = [int]$Window.Left
    $script:Settings.Top  = [int]$Window.Top
    Save-Settings
})

$ctx = New-Object System.Windows.Controls.ContextMenu
$wTop = New-Object System.Windows.Controls.MenuItem
$wTop.Header = 'Поверх усіх вікон'; $wTop.IsCheckable = $true; $wTop.IsChecked = [bool]$script:Settings.Topmost
$wHide = New-Object System.Windows.Controls.MenuItem; $wHide.Header = 'Сховати (іконка в треї лишиться)'
$wExit = New-Object System.Windows.Controls.MenuItem; $wExit.Header = 'Вийти'
$wPin = New-Object System.Windows.Controls.MenuItem
$wPin.Header = 'Закріпити на місці'; $wPin.IsCheckable = $true
[void]$ctx.Items.Add($wPin); [void]$ctx.Items.Add($wTop); [void]$ctx.Items.Add($wHide)
$wPin.Add_Click({ Set-Pinned $wPin.IsChecked })
Set-Pinned ([bool]$script:Settings.Pinned)
[void]$ctx.Items.Add((New-Object System.Windows.Controls.Separator)); [void]$ctx.Items.Add($wExit)
$Window.ContextMenu = $ctx

function Set-Topmost([bool]$on) {
    $script:Settings.Topmost = $on; $wTop.IsChecked = $on; $miTop.Checked = $on
    Save-Settings
    try { Update-Topmost } catch { $Window.Topmost = $on }
}

function Exit-App([switch]$Restart) {
    $script:Exiting = $true
    try { $script:Timer.Stop() } catch {}
    try { $script:DSync.Stop = $true; if ($script:DSync.Pipe) { $script:DSync.Pipe.Dispose() } } catch {}
    foreach ($ni in $script:Tray.Values) {
        $ni.Visible = $false
        if ($ni.Tag -is [System.IntPtr]) { [void][RBW.Native]::DestroyIcon($ni.Tag) }
        $ni.Dispose()
    }
    if ($Restart) {
        try { $script:Mutex.ReleaseMutex(); $script:Mutex.Dispose() } catch {}
        try { $t = Get-LaunchTarget; if ($t.Args) { Start-Process -FilePath $t.Target -ArgumentList $t.Args } else { Start-Process -FilePath $t.Target } } catch {}
    }
    $script:App.Shutdown()
}

$wTop.Add_Click({ Set-Topmost $wTop.IsChecked })
$wHide.Add_Click({ Toggle-Widget })
$wExit.Add_Click({ Exit-App })
$miShow.Add_Click({ Toggle-Widget })
$miTop.Add_Click({ Set-Topmost $miTop.Checked })
$miExit.Add_Click({ Exit-App })

$Window.Add_Loaded({
    $wa = [System.Windows.SystemParameters]::WorkArea
    if ($null -ne $script:Settings.Left -and $null -ne $script:Settings.Top -and
        $script:Settings.Left -ge ([System.Windows.SystemParameters]::VirtualScreenLeft - 50) -and
        $script:Settings.Left -lt ([System.Windows.SystemParameters]::VirtualScreenLeft + [System.Windows.SystemParameters]::VirtualScreenWidth - 50) -and
        $script:Settings.Top -ge ([System.Windows.SystemParameters]::VirtualScreenTop - 50) -and
        $script:Settings.Top -lt ([System.Windows.SystemParameters]::VirtualScreenTop + [System.Windows.SystemParameters]::VirtualScreenHeight - 50)) {
        $Window.Left = $script:Settings.Left; $Window.Top = $script:Settings.Top
    } else {
        $Window.Left = $wa.Right - $Window.ActualWidth - 10
        $Window.Top  = $wa.Top + 10
    }
    if ($script:Settings.Mode -eq 'strip') { Position-Island }
})

# ================== ВИГЛЯД: СМУЖКА / РОЗКРИТИЙ ==================
$FullW      = 800
$Window.Width = $FullW + 28
$StripH     = 20
$AnimMs     = 280
$PadFull    = New-Object System.Windows.Thickness(16, 12, 16, 12)
$PadStrip   = New-Object System.Windows.Thickness(12, 5, 12, 5)
$Ease = New-Object System.Windows.Media.Animation.QuarticEase
$Ease.EasingMode = [System.Windows.Media.Animation.EasingMode]::EaseOut
$P_Height  = [System.Windows.FrameworkElement]::HeightProperty
$P_Width   = [System.Windows.FrameworkElement]::WidthProperty
$P_Opacity = [System.Windows.UIElement]::OpacityProperty
$P_Padding = [System.Windows.Controls.Border]::PaddingProperty
$script:Expanded = $true
$script:LayoutReady = $false
$script:LastTargets = ''

function Anim($el, $prop, $to, [bool]$animate) {
    if ($animate) {
        if ($to -is [System.Windows.Thickness]) { $a = New-Object System.Windows.Media.Animation.ThicknessAnimation }
        else { $a = New-Object System.Windows.Media.Animation.DoubleAnimation; $to = [double]$to }
        $a.To = $to
        $a.Duration = New-Object System.Windows.Duration([TimeSpan]::FromMilliseconds($AnimMs))
        $a.EasingFunction = $Ease
        $el.BeginAnimation($prop, $a)
    } else {
        $el.BeginAnimation($prop, $null)
        $el.SetValue($prop, $(if ($to -is [System.Windows.Thickness]) { $to } else { [double]$to }))
    }
}
# висота розкритого віджета = висота блоку батареї
function Get-BatteryHeight {
    $BatteryCol.Measure((New-Object System.Windows.Size(230, [double]::PositiveInfinity)))
    return [Math]::Max(110, [Math]::Ceiling($BatteryCol.DesiredSize.Height))
}
function Get-FullHeight {
    $bh = Get-BatteryHeight
    $CenterCol.Height = $bh
    $CalCol.Height = $bh
    $DetailsContent.Measure((New-Object System.Windows.Size(($FullW - 34), [double]::PositiveInfinity)))
    return [Math]::Ceiling($DetailsContent.DesiredSize.Height)
}
function Get-StripWidth {
    $inf = New-Object System.Windows.Size([double]::PositiveInfinity, [double]::PositiveInfinity)
    $LeftItems.Measure($inf); $RightItems.Measure($inf); $StripCenter.Measure($inf)
    $side = [Math]::Max($LeftItems.DesiredSize.Width, $RightItems.DesiredSize.Width)
    $w = 2 * $side + $StripCenter.DesiredSize.Width + 24 + 2
    $min = if ($script:MicOnlyMode) { 56 } else { 150 }
    return [Math]::Max($min, [Math]::Min($FullW, [Math]::Ceiling($w)))
}
function Get-NoticeWidth {
    $NoticeText.Measure((New-Object System.Windows.Size([double]::PositiveInfinity, [double]::PositiveInfinity)))
    return [Math]::Max(200, [Math]::Ceiling($NoticeText.DesiredSize.Width) + 18 + 10 + 24 + 2 + 40)
}
function Position-Island {
    $wa = [System.Windows.SystemParameters]::WorkArea
    $Window.Left = $wa.Left + ($wa.Width - $Window.Width) / 2
    $Window.Top  = $wa.Top
}

function Apply-Visual([bool]$expanded, [bool]$animate) {
    $script:Expanded = $expanded
    $island = ($script:Settings.Mode -eq 'strip')
    if ($island) {
        # острівець: верхній край зливається з краєм екрана, як Dynamic Island
        $r = if ($expanded) { 22 } elseif ($script:NoticeActive) { 18 } else { 14 }
        $Card.Margin = New-Object System.Windows.Thickness(14, 0, 14, 14)
        $Card.BorderThickness = New-Object System.Windows.Thickness(1, 0, 1, 1)
        $Card.CornerRadius = New-Object System.Windows.CornerRadius(0, 0, $r, $r)
        $Card.Background = Brush (Get-CardBg $true)
        $PinBtn.Visibility = 'Collapsed'
    } else {
        $Card.Margin = New-Object System.Windows.Thickness(14)
        $Card.BorderThickness = New-Object System.Windows.Thickness(1)
        $Card.CornerRadius = New-Object System.Windows.CornerRadius(15)
        $Card.Background = Brush (Get-CardBg $false)
        $PinBtn.Visibility = 'Visible'
    }
    $fullH = Get-FullHeight
    $Window.Height = $fullH + 24 + 2 + 28 + 6
    $isNotice = ($script:NoticeActive -and -not $expanded -and $island)
    $nH = 0; $nO = 0
    if ($expanded) { $dH = $fullH; $dO = 1; $sH = 0; $sO = 0; $w = $FullW; $pad = $PadFull }
    elseif ($isNotice) { $dH = 0; $dO = 0; $sH = 0; $sO = 0; $nH = $NoticeH; $nO = 1; $w = Get-NoticeWidth; $pad = $PadStrip }
    else { $dH = 0; $dO = 0; $sH = $StripH; $sO = 1; $w = Get-StripWidth; $pad = $PadStrip }
    $script:LastTargets = "$expanded|$isNotice|$dH|$w"
    Anim $Details $P_Height  $dH $animate
    Anim $Details $P_Opacity $dO $animate
    Anim $Strip   $P_Height  $sH $animate
    Anim $Strip   $P_Opacity $sO $animate
    Anim $Notice  $P_Height  $nH $animate
    Anim $Notice  $P_Opacity $nO $animate
    Anim $Card    $P_Width   $w  $animate
    Anim $Card    $P_Padding $pad $animate
    if ($island) { Position-Island }
}

# викликається після кожного оновлення даних: якщо розмір вмісту змінився — плавно підлаштувати
$script:LastBatH = -1
function Sync-Layout {
    if (-not $script:LayoutReady) { return }
    $bh = Get-BatteryHeight
    if ($bh -ne $script:LastBatH) { $script:LastBatH = $bh; try { Render-Calendar } catch {}; return }
    $fullH = Get-FullHeight
    $isNotice = ($script:NoticeActive -and -not $script:Expanded -and $script:Settings.Mode -eq 'strip')
    $w = if ($script:Expanded) { $FullW } elseif ($isNotice) { Get-NoticeWidth } else { Get-StripWidth }
    $dH = if ($script:Expanded) { $fullH } else { 0 }
    if ("$($script:Expanded)|$isNotice|$dH|$w" -ne $script:LastTargets) { Apply-Visual $script:Expanded $true }
}

# наведення мишки (з невеликою затримкою, щоб не смикалось)
$script:HoverTarget = $false
$script:HoverTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:HoverTimer.Add_Tick({
    $script:HoverTimer.Stop()
    if ($script:Settings.Mode -ne 'strip') { return }
    $want = [bool]$script:HoverTarget
    if (-not $want -and ($Card.IsMouseOver -or $ctx.IsOpen)) { return }
    if ($want -ne $script:Expanded) { Apply-Visual $want $true }
})
function Start-Hover([bool]$want, [int]$delayMs) {
    $script:HoverTarget = $want
    $script:HoverTimer.Stop()
    $script:HoverTimer.Interval = [TimeSpan]::FromMilliseconds($delayMs)
    $script:HoverTimer.Start()
}
$Card.Add_MouseEnter({ Start-Hover $true 120 })
$Card.Add_MouseLeave({ Start-Hover $false 450 })
$ctx.Add_Closed({ if (-not $Card.IsMouseOver) { Start-Hover $false 300 } })

# перемикання вигляду
$wModeStrip = New-Object System.Windows.Controls.MenuItem; $wModeStrip.Header = 'Вигляд: острівець зверху (розкривається при наведенні)'; $wModeStrip.IsCheckable = $true
$wModeFull  = New-Object System.Windows.Controls.MenuItem; $wModeFull.Header  = 'Вигляд: плаваюча картка (завжди розкрита)'; $wModeFull.IsCheckable = $true
$ctx.Items.Insert(0, (New-Object System.Windows.Controls.Separator))
$ctx.Items.Insert(0, $wModeFull)
$ctx.Items.Insert(0, $wModeStrip)

$miModeStrip = New-Object System.Windows.Forms.ToolStripMenuItem('Вигляд: острівець зверху')
$miModeFull  = New-Object System.Windows.Forms.ToolStripMenuItem('Вигляд: плаваюча картка')
$TrayMenu.Items.Insert(1, $miModeStrip)
$TrayMenu.Items.Insert(2, $miModeFull)
$TrayMenu.Items.Insert(3, (New-Object System.Windows.Forms.ToolStripSeparator))
$miStyleIcon = New-Object System.Windows.Forms.ToolStripMenuItem('Іконки в треї: значок пристрою')
$miStyleNum  = New-Object System.Windows.Forms.ToolStripMenuItem('Іконки в треї: цифри')
$TrayMenu.Items.Insert(4, $miStyleIcon)
$TrayMenu.Items.Insert(5, $miStyleNum)
$TrayMenu.Items.Insert(6, (New-Object System.Windows.Forms.ToolStripSeparator))
function Set-TrayStyle([string]$st) {
    $script:Settings.TrayStyle = $st
    $miStyleIcon.Checked = ($st -ne 'number'); $miStyleNum.Checked = ($st -eq 'number')
    Save-Settings
    $script:IconKeys = @{}
    Refresh-All
}
$miStyleIcon.Add_Click({ Set-TrayStyle 'icon' })
$miStyleNum.Add_Click({ Set-TrayStyle 'number' })
$miStyleIcon.Checked = ("$($script:Settings.TrayStyle)" -ne 'number'); $miStyleNum.Checked = -not $miStyleIcon.Checked

function Set-Mode([string]$mode, [bool]$animate) {
    $script:Settings.Mode = $mode
    $isStrip = ($mode -eq 'strip')
    $wModeStrip.IsChecked = $isStrip; $wModeFull.IsChecked = -not $isStrip
    $miModeStrip.Checked  = $isStrip; $miModeFull.Checked  = -not $isStrip
    Save-Settings
    try { Update-Topmost; Update-MicWindow } catch {}
    if ($isStrip) { Apply-Visual ([bool]$Card.IsMouseOver) $animate }
    else {
        Apply-Visual $true $animate
        if ($script:LayoutReady) {
            $wa = [System.Windows.SystemParameters]::WorkArea
            if ($null -ne $script:Settings.Left) { $Window.Left = $script:Settings.Left; $Window.Top = $script:Settings.Top }
            else { $Window.Left = $wa.Right - $Window.Width - 10; $Window.Top = $wa.Top + 10 }
        }
    }
}
$wModeStrip.Add_Click({ Set-Mode 'strip' $true })
$wModeFull.Add_Click({ Set-Mode 'full' $true })
$miModeStrip.Add_Click({ Set-Mode 'strip' $true })
$miModeFull.Add_Click({ Set-Mode 'full' $true })

# ================== ПОГОДА І КАЛЕНДАР ==================
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}
Add-Type -AssemblyName Microsoft.VisualBasic
$UkCulture = [Globalization.CultureInfo]::GetCultureInfo('uk-UA')
$CI        = [Globalization.CultureInfo]::InvariantCulture
$CalFile   = Join-Path $Root 'calendars.txt'

# --- асинхронне завантаження (не блокує віджет) ---
$script:Pending = @{}
function Start-Download([string]$url, [scriptblock]$onDone, [scriptblock]$onError, $tag = $null) {
    $wc = New-Object System.Net.WebClient
    $wc.Encoding = [System.Text.Encoding]::UTF8
    $wc.Headers['User-Agent'] = 'RazerBatteryWidget/1.0'
    $script:Pending[$wc.GetHashCode()] = @{ Done = $onDone; Error = $onError; Tag = $tag }
    $wc.Add_DownloadStringCompleted({
        param($sender, $e)
        $h = $script:Pending[$sender.GetHashCode()]
        $script:Pending.Remove($sender.GetHashCode())
        try {
            if ($e.Error -or $e.Cancelled) { if ($h.Error) { & $h.Error $h.Tag } }
            else { & $h.Done $e.Result $h.Tag }
        } catch {}
        $sender.Dispose()
    })
    $wc.DownloadStringAsync([Uri]$url)
}

# --- ПОГОДА (open-meteo.com, без ключа) ---
function Get-WeatherInfo([int]$code) {
    $sun = [string][char]0x2600; $part = [string][char]0x26C5; $cloud = [string][char]0x2601
    $rain = [string][char]0x2602; $snow = [string][char]0x2744; $storm = [string][char]0x26A1
    if ($code -eq 0) { return @($sun, 'Ясно') }
    if ($code -le 2) { return @($part, 'Мінлива хмарність') }
    if ($code -eq 3) { return @($cloud, 'Хмарно') }
    if ($code -le 48) { return @($cloud, 'Туман') }
    if ($code -le 57) { return @($rain, 'Мряка') }
    if ($code -le 67) { return @($rain, 'Дощ') }
    if ($code -le 77) { return @($snow, 'Сніг') }
    if ($code -le 82) { return @($rain, 'Злива') }
    if ($code -le 86) { return @($snow, 'Снігопад') }
    return @($storm, 'Гроза')
}
function Refresh-Weather {
    $lat = ([double]$script:Settings.WeatherLat).ToString($CI)
    $lon = ([double]$script:Settings.WeatherLon).ToString($CI)
    $url = "https://api.open-meteo.com/v1/forecast?latitude=$lat&longitude=$lon&current=temperature_2m,apparent_temperature,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1"
    Start-Download $url {
        param($json)
        $w = $json | ConvertFrom-Json
        $info = Get-WeatherInfo ([int]$w.current.weather_code)
        $WeatherIcon.Text = $info[0]
        $WeatherTemp.Text = "$([Math]::Round([double]$w.current.temperature_2m))°"
        $WeatherText.Text = "$($info[1]), відчув. $([Math]::Round([double]$w.current.apparent_temperature))°"
        $hi = [Math]::Round([double]$w.daily.temperature_2m_max[0]); $lo = [Math]::Round([double]$w.daily.temperature_2m_min[0])
        $WeatherCity.Text = "$($script:Settings.WeatherCity) · $([char]0x2191)$hi° $([char]0x2193)$lo°"
        Sync-Layout
    } {
        if (-not $WeatherTemp.Text) { $WeatherText.Text = 'Погода недоступна (немає інтернету?)' }
    }
}
function Set-WeatherCity {
    $name = [Microsoft.VisualBasic.Interaction]::InputBox('Введи місто для погоди (наприклад: Riihimäki, Hyvinkää, Київ):', 'Погода — місто', "$($script:Settings.WeatherCity)")
    if (-not $name) { return }
    $url = 'https://geocoding-api.open-meteo.com/v1/search?count=1&language=uk&name=' + [Uri]::EscapeDataString($name)
    Start-Download $url {
        param($json)
        $g = $json | ConvertFrom-Json
        if (-not $g.results) { [System.Windows.MessageBox]::Show('Місто не знайдено.', 'Погода') | Out-Null; return }
        $r = $g.results[0]
        $script:Settings.WeatherLat = [double]$r.latitude
        $script:Settings.WeatherLon = [double]$r.longitude
        $script:Settings.WeatherCity = "$($r.name)"
        Save-Settings
        Refresh-Weather
    } { [System.Windows.MessageBox]::Show('Не вдалося знайти місто (немає інтернету?).', 'Погода') | Out-Null }
}

# --- КАЛЕНДАР (секретна iCal-адреса Google Календаря) ---
$TzMap = @{
    'Europe/Kiev' = 'FLE Standard Time'; 'Europe/Kyiv' = 'FLE Standard Time'; 'Europe/Helsinki' = 'FLE Standard Time'
    'Europe/Riga' = 'FLE Standard Time'; 'Europe/Tallinn' = 'FLE Standard Time'; 'Europe/Vilnius' = 'FLE Standard Time'
    'Europe/Sofia' = 'FLE Standard Time'; 'Europe/Stockholm' = 'W. Europe Standard Time'; 'Europe/Berlin' = 'W. Europe Standard Time'
    'Europe/Oslo' = 'W. Europe Standard Time'; 'Europe/Warsaw' = 'Central European Standard Time'; 'Europe/Paris' = 'Romance Standard Time'
    'Europe/London' = 'GMT Standard Time'; 'Europe/Moscow' = 'Russian Standard Time'; 'UTC' = 'UTC'; 'Etc/UTC' = 'UTC'; 'Etc/GMT' = 'UTC'
}
$script:TzCache = @{}
function Get-Tz([string]$id) {
    if (-not $id) { return [TimeZoneInfo]::Local }
    if ($script:TzCache.ContainsKey($id)) { return $script:TzCache[$id] }
    $tz = [TimeZoneInfo]::Local
    try { $tz = [TimeZoneInfo]::FindSystemTimeZoneById($id) }
    catch { if ($TzMap.ContainsKey($id)) { try { $tz = [TimeZoneInfo]::FindSystemTimeZoneById($TzMap[$id]) } catch {} } }
    $script:TzCache[$id] = $tz
    return $tz
}
function Parse-IcsDate([string]$params, [string]$val) {
    $val = $val.Trim()
    if ($val -match '^\d{8}$') {
        return @{ Wall = [datetime]::ParseExact($val, 'yyyyMMdd', $CI); AllDay = $true; Tz = $null; Utc = $false }
    }
    if ($val -match '^(\d{8}T\d{6})Z$') {
        return @{ Wall = [datetime]::ParseExact($Matches[1], "yyyyMMdd'T'HHmmss", $CI); AllDay = $false; Tz = [TimeZoneInfo]::Utc; Utc = $true }
    }
    if ($val -match '^\d{8}T\d{6}$') {
        $tzid = ''
        if ($params -match 'TZID="?([^";:]+)') { $tzid = $Matches[1] }
        return @{ Wall = [datetime]::ParseExact($val, "yyyyMMdd'T'HHmmss", $CI); AllDay = $false; Tz = (Get-Tz $tzid); Utc = $false }
    }
    return $null
}
function To-Local([datetime]$wall, $tz, [bool]$allDay) {
    if ($allDay) { return $wall }
    $w = [datetime]::SpecifyKind($wall, [DateTimeKind]::Unspecified)
    try {
        if ($tz -eq [TimeZoneInfo]::Utc) { return [TimeZoneInfo]::ConvertTimeFromUtc($w, [TimeZoneInfo]::Local) }
        return [TimeZoneInfo]::ConvertTime($w, $tz, [TimeZoneInfo]::Local)
    } catch { return $w }
}
function Parse-Duration([string]$v) {
    $m = [regex]::Match($v, '^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$')
    if (-not $m.Success) { return [TimeSpan]::Zero }
    $n = { param($i) if ($m.Groups[$i].Success) { [int]$m.Groups[$i].Value } else { 0 } }
    return New-Object TimeSpan(((& $n 2) * 7 + (& $n 3)), (& $n 4), (& $n 5), (& $n 6))
}
function Unescape-Ics([string]$v) { return ($v -replace '\\[nN]', ' ' -replace '\\,', ',' -replace '\\;', ';' -replace '\\\\', '\').Trim() }

$DayMap = @{ SU = 0; MO = 1; TU = 2; WE = 3; TH = 4; FR = 5; SA = 6 }
function Expand-RRule([datetime]$start, [string]$rule, $tz, [bool]$allDay, [datetime]$winFrom, [datetime]$winTo) {
    $p = @{}
    foreach ($part in $rule.Split(';')) { $kv = $part.Split('=', 2); if ($kv.Count -eq 2) { $p[$kv[0].ToUpperInvariant()] = $kv[1] } }
    $freq = "$($p['FREQ'])".ToUpperInvariant()
    $interval = 1; if ($p['INTERVAL']) { $interval = [Math]::Max(1, [int]$p['INTERVAL']) }
    $count = [int]::MaxValue; if ($p['COUNT']) { $count = [int]$p['COUNT'] }
    $until = [datetime]::MaxValue
    if ($p['UNTIL']) {
        $u = Parse-IcsDate '' $p['UNTIL']
        if ($u) {
            if ($u.AllDay) { $until = $u.Wall.AddDays(1).AddSeconds(-1) }
            elseif ($u.Utc -and $tz -and $tz -ne [TimeZoneInfo]::Utc) {
                try { $until = [TimeZoneInfo]::ConvertTimeFromUtc([datetime]::SpecifyKind($u.Wall, 'Utc'), $tz) } catch { $until = $u.Wall }
            } else { $until = $u.Wall }
        }
    }
    $lo = $winFrom.AddDays(-2); $hi = $winTo.AddDays(2)
    $tod = $start.TimeOfDay
    $out = New-Object System.Collections.ArrayList
    $n = 0; $guard = 0; $stop = $false

    if ($freq -eq 'DAILY') {
        $d = $start
        while (-not $stop -and $guard -lt 200000) {
            if ($d -gt $until -or $n -ge $count -or $d -gt $hi) { break }
            $n++
            if ($d -ge $lo) { [void]$out.Add($d) }
            $d = $d.AddDays($interval); $guard++
        }
    }
    elseif ($freq -eq 'WEEKLY') {
        $dows = @()
        if ($p['BYDAY']) { foreach ($t in $p['BYDAY'].Split(',')) { $k2 = $t.Substring([Math]::Max(0, $t.Length - 2)).ToUpperInvariant(); if ($DayMap.ContainsKey($k2)) { $dows += $DayMap[$k2] } } }
        if ($dows.Count -eq 0) { $dows = @([int]$start.DayOfWeek) }
        $offs = @($dows | ForEach-Object { ($_ + 6) % 7 } | Sort-Object -Unique)
        $weekStart = $start.Date.AddDays( - (([int]$start.DayOfWeek + 6) % 7))
        $k = 0
        while (-not $stop -and $guard -lt 200000) {
            $ws = $weekStart.AddDays(7 * $interval * $k)
            if ($ws -gt $hi -or $ws -gt $until) { break }
            foreach ($o in $offs) {
                $occ = $ws.AddDays($o) + $tod
                if ($occ -lt $start) { continue }
                if ($occ -gt $until -or $n -ge $count) { $stop = $true; break }
                $n++
                if ($occ -ge $lo -and $occ -le $hi) { [void]$out.Add($occ) }
            }
            $k++; $guard++
        }
    }
    elseif ($freq -eq 'MONTHLY') {
        $first = $start.Date.AddDays(1 - $start.Day)
        $k = 0
        while (-not $stop -and $guard -lt 5000) {
            $base = $first.AddMonths($interval * $k)
            if ($base -gt $hi -or $base -gt $until) { break }
            $dim = [datetime]::DaysInMonth($base.Year, $base.Month)
            $cands = New-Object System.Collections.ArrayList
            if ($p['BYDAY']) {
                foreach ($t in $p['BYDAY'].Split(',')) {
                    if ($t -match '^([+-]?\d+)?(MO|TU|WE|TH|FR|SA|SU)$') {
                        $ord = 0; if ($Matches[1]) { $ord = [int]$Matches[1] }
                        $dw = $DayMap[$Matches[2]]
                        $days = @(); for ($i = 0; $i -lt $dim; $i++) { $dd = $base.AddDays($i); if ([int]$dd.DayOfWeek -eq $dw) { $days += $dd } }
                        if ($ord -gt 0 -and $ord -le $days.Count) { [void]$cands.Add($days[$ord - 1]) }
                        elseif ($ord -lt 0 -and -$ord -le $days.Count) { [void]$cands.Add($days[$days.Count + $ord]) }
                        elseif ($ord -eq 0) { foreach ($dd in $days) { [void]$cands.Add($dd) } }
                    }
                }
            } elseif ($p['BYMONTHDAY']) {
                foreach ($t in $p['BYMONTHDAY'].Split(',')) { $md = [int]$t; if ($md -lt 0) { $md = $dim + $md + 1 }; if ($md -ge 1 -and $md -le $dim) { [void]$cands.Add($base.AddDays($md - 1)) } }
            } elseif ($start.Day -le $dim) { [void]$cands.Add($base.AddDays($start.Day - 1)) }
            foreach ($c in ($cands | Sort-Object)) {
                $occ = $c + $tod
                if ($occ -lt $start) { continue }
                if ($occ -gt $until -or $n -ge $count) { $stop = $true; break }
                $n++
                if ($occ -ge $lo -and $occ -le $hi) { [void]$out.Add($occ) }
            }
            $k++; $guard++
        }
    }
    elseif ($freq -eq 'YEARLY') {
        $k = 0
        while ($guard -lt 500) {
            $occ = $start.AddYears($interval * $k)
            if ($occ -gt $until -or $n -ge $count -or $occ -gt $hi) { break }
            $n++
            if ($occ -ge $lo) { [void]$out.Add($occ) }
            $k++; $guard++
        }
    }
    return ,$out
}

$RxProp = New-Object System.Text.RegularExpressions.Regex('^([A-Za-z\-]+)((?:;[^:;=]+=(?:"[^"]*"|[^:;]*))*):(.*)$')
function Parse-Ics([string]$text, [datetime]$from, [datetime]$to, [string]$calName) {
    $raw = $text -replace "`r`n[ `t]", '' -replace "`n[ `t]", ''
    $events = New-Object System.Collections.ArrayList
    $cur = $null
    foreach ($line in ($raw -split "`r?`n")) {
        if ($line -eq 'BEGIN:VEVENT') { $cur = @{ ExDates = New-Object System.Collections.ArrayList }; continue }
        if ($line -eq 'END:VEVENT') { if ($cur) { [void]$events.Add($cur) }; $cur = $null; continue }
        if ($null -eq $cur) { continue }
        $m = $RxProp.Match($line)
        if (-not $m.Success) { continue }
        $name = $m.Groups[1].Value.ToUpperInvariant(); $params = $m.Groups[2].Value; $val = $m.Groups[3].Value
        if ($name -eq 'DTSTART') { $cur.Start = Parse-IcsDate $params $val }
        elseif ($name -eq 'DTEND') { $cur.End = Parse-IcsDate $params $val }
        elseif ($name -eq 'DURATION') { $cur.Duration = Parse-Duration $val }
        elseif ($name -eq 'SUMMARY') { $cur.Title = Unescape-Ics $val }
        elseif ($name -eq 'LOCATION') { $cur.Location = Unescape-Ics $val }
        elseif ($name -eq 'RRULE') { $cur.RRule = $val }
        elseif ($name -eq 'EXDATE') { foreach ($v in $val.Split(',')) { $d = Parse-IcsDate $params $v; if ($d) { [void]$cur.ExDates.Add((To-Local $d.Wall $d.Tz $d.AllDay)) } } }
        elseif ($name -eq 'RECURRENCE-ID') { $cur.RecId = Parse-IcsDate $params $val }
        elseif ($name -eq 'UID') { $cur.Uid = $val }
        elseif ($name -eq 'STATUS') { $cur.Status = $val.ToUpperInvariant() }
    }

    # перенесені/змінені окремі повторення серії
    $overrides = @{}
    foreach ($e in $events) {
        if (-not $e.Start) { continue }
        if ($e.End) { $e.Dur = $e.End.Wall - $e.Start.Wall }
        elseif ($e.Duration) { $e.Dur = $e.Duration }
        elseif ($e.Start.AllDay) { $e.Dur = [TimeSpan]::FromDays(1) }
        else { $e.Dur = [TimeSpan]::Zero }
        if ($e.RecId) {
            $k = "$($e.Uid)"
            if (-not $overrides.ContainsKey($k)) { $overrides[$k] = New-Object System.Collections.ArrayList }
            [void]$overrides[$k].Add((To-Local $e.RecId.Wall $e.RecId.Tz $e.RecId.AllDay))
        }
    }

    $result = New-Object System.Collections.ArrayList
    foreach ($e in $events) {
        if (-not $e.Start) { continue }
        $ad = [bool]$e.Start.AllDay
        $walls = if ($e.RRule -and -not $e.RecId) { Expand-RRule $e.Start.Wall $e.RRule $e.Start.Tz $ad $from $to } else { @($e.Start.Wall) }
        if ("$($e.Status)" -eq 'CANCELLED') { continue }
        foreach ($wall in $walls) {
            $s = To-Local $wall $e.Start.Tz $ad
            if ($e.RRule -and -not $e.RecId) {
                if ($e.ExDates -contains $s) { continue }
                if ($overrides.ContainsKey("$($e.Uid)") -and ($overrides["$($e.Uid)"] -contains $s)) { continue }
            }
            $en = if ($ad) { $s + $e.Dur } else { To-Local ($wall + $e.Dur) $e.Start.Tz $false }
            if (($en -gt $from -or $s -ge $from) -and $s -lt $to) {
                [void]$result.Add([pscustomobject]@{ Start = $s; End = $en; AllDay = $ad; Title = "$($e.Title)"; Location = "$($e.Location)"; Calendar = $calName })
            }
        }
    }
    return ,$result
}

function Get-CalendarUrls {
    if (-not (Test-Path $CalFile)) { return @() }
    return @(Get-Content $CalFile -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^https?://' })
}

$script:CalData = @{}      # url -> list of events
$script:CalError = $false
function Refresh-Calendar {
    $urls = Get-CalendarUrls
    foreach ($k in @($script:CalData.Keys)) { if ($urls -notcontains $k) { $script:CalData.Remove($k) } }
    if ($urls.Count -eq 0) { Render-Calendar; return }
    foreach ($u in $urls) {
        Start-Download $u {
            param($ics, $url)
            $from = [DateTime]::Today; $to = $from.AddDays(8)
            $script:CalData[$url] = Parse-Ics $ics $from $to ''
            $script:CalError = $false
            Render-Calendar
        } { $script:CalError = $true; Render-Calendar } $u
    }
}

$C_Accent = '#4C8DFF'
function Fit-Calendar {
    $avail = (Get-BatteryHeight) - 18
    $sz = New-Object System.Windows.Size(260, [double]::PositiveInfinity)
    $CalRows.Measure($sz)
    while ($CalRows.Children.Count -gt 2 -and $CalRows.DesiredSize.Height -gt $avail) {
        $CalRows.Children.RemoveAt($CalRows.Children.Count - 1)
        $last = $CalRows.Children[$CalRows.Children.Count - 1]
        if ($last -is [System.Windows.Controls.TextBlock]) { $CalRows.Children.RemoveAt($CalRows.Children.Count - 1) }
        $CalRows.Measure($sz)
    }
}
function Render-Calendar {
    $CalRows.Children.Clear()
    $urls = Get-CalendarUrls
    if ($urls.Count -eq 0) {
        $CalHint.Text = "Календар ще не підключено. Права кнопка на іконці в треї $([char]0x2192) «Календар: додати iCal-посилання»."
        $CalHint.Visibility = 'Visible'
        Sync-Layout; return
    }
    $now = [DateTime]::Now
    $all = @($script:CalData.Values | ForEach-Object { $_ } | Where-Object { $_.End -gt $now -or ($_.AllDay -and $_.Start.Date -ge $now.Date) } | Sort-Object @{ Expression = { $_.Start } }, @{ Expression = { -not $_.AllDay } })
    $shown = @($all | Select-Object -First 7)
    if ($shown.Count -eq 0) {
        $CalHint.Text = if ($script:CalError -and $script:CalData.Count -eq 0) { 'Не вдалося завантажити календар. Перевір інтернет або посилання.' } elseif ($script:CalData.Count -eq 0) { 'Завантаження…' } else { 'На найближчий тиждень подій немає.' }
        $CalHint.Visibility = 'Visible'
        Sync-Layout; return
    }
    $CalHint.Visibility = 'Collapsed'
    $lastDay = $null
    foreach ($ev in $shown) {
        $day = $ev.Start.Date
        if ($day -lt $now.Date) { $day = $now.Date }
        if ($day -ne $lastDay) {
            $lastDay = $day
            $hdr = New-Object System.Windows.Controls.TextBlock
            $label = if ($day -eq $now.Date) { 'Сьогодні' } elseif ($day -eq $now.Date.AddDays(1)) { 'Завтра' } else { $day.ToString('dddd', $UkCulture) }
            $hdr.Text = "$label · $($day.ToString('d MMMM', $UkCulture))"
            $hdr.Foreground = Brush '#A9ADB5'; $hdr.FontSize = 11; $hdr.FontWeight = 'SemiBold'
            $hdr.Margin = New-Object System.Windows.Thickness(0, 6, 0, 2)
            [void]$CalRows.Children.Add($hdr)
        }
        $isNow = (-not $ev.AllDay -and $ev.Start -le $now -and $ev.End -gt $now)
        $g = New-Object System.Windows.Controls.Grid
        $g.Margin = New-Object System.Windows.Thickness(0, 3, 0, 3)
        $c0 = New-Object System.Windows.Controls.ColumnDefinition; $c0.Width = New-Object System.Windows.GridLength(3)
        $c1 = New-Object System.Windows.Controls.ColumnDefinition
        [void]$g.ColumnDefinitions.Add($c0); [void]$g.ColumnDefinitions.Add($c1)
        $bar = New-Object System.Windows.Controls.Border
        $bar.CornerRadius = New-Object System.Windows.CornerRadius(1.5)
        $bar.Background = Brush $(if ($isNow) { $C_Green } else { $C_Accent })
        [void]$g.Children.Add($bar)
        $sp = New-Object System.Windows.Controls.StackPanel
        $sp.Margin = New-Object System.Windows.Thickness(9, 0, 0, 0)
        [System.Windows.Controls.Grid]::SetColumn($sp, 1)
        $t1 = New-Object System.Windows.Controls.TextBlock
        $t1.Text = $ev.Title; $t1.Foreground = Brush '#F2F3F5'; $t1.FontSize = 12.5; $t1.FontWeight = 'SemiBold'; $t1.TextTrimming = 'CharacterEllipsis'
        $t2 = New-Object System.Windows.Controls.TextBlock
        $time = if ($ev.AllDay) { 'Увесь день' } else { "$($ev.Start.ToString('HH:mm'))–$($ev.End.ToString('HH:mm'))" }
        if ($isNow) { $time = "Зараз · до $($ev.End.ToString('HH:mm'))" }
        if ($ev.Location) { $time += " · $($ev.Location)" }
        $t2.Text = $time; $t2.Foreground = Brush $(if ($isNow) { $C_Green } else { '#8A8F98' }); $t2.FontSize = 11; $t2.TextTrimming = 'CharacterEllipsis'
        [void]$sp.Children.Add($t1); [void]$sp.Children.Add($t2)
        [void]$g.Children.Add($sp)
        [void]$CalRows.Children.Add($g)
    }
    Fit-Calendar
    Sync-Layout
}

function Add-CalendarUrl {
    $u = [Microsoft.VisualBasic.Interaction]::InputBox("Встав «Секретну адресу у форматі iCal» з налаштувань Google Календаря:`n(Налаштування $([char]0x2192) твій календар $([char]0x2192) Інтеграція календаря)", 'Google Календар', '')
    $u = "$u".Trim()
    if (-not $u) { return }
    if ($u -notmatch '^https?://') { [System.Windows.MessageBox]::Show('Це не схоже на посилання.', 'Google Календар') | Out-Null; return }
    Add-Content -Path $CalFile -Value $u -Encoding UTF8
    $script:CalData = @{}
    Refresh-Calendar
}
function Clear-CalendarUrls {
    if (Test-Path $CalFile) { Remove-Item $CalFile -Force }
    $script:CalData = @{}
    Render-Calendar
}

$miCalAdd   = New-Object System.Windows.Forms.ToolStripMenuItem('Календар: додати iCal-посилання…')
$miCalClear = New-Object System.Windows.Forms.ToolStripMenuItem('Календар: відключити всі')
$miCity     = New-Object System.Windows.Forms.ToolStripMenuItem('Погода: змінити місто…')
$sepIdx = $TrayMenu.Items.IndexOf($miExit)
$TrayMenu.Items.Insert($sepIdx - 1, (New-Object System.Windows.Forms.ToolStripSeparator))
$TrayMenu.Items.Insert($sepIdx, $miCalAdd)
$TrayMenu.Items.Insert($sepIdx + 1, $miCalClear)
$TrayMenu.Items.Insert($sepIdx + 2, $miCity)
$miCalAdd.Add_Click({ Add-CalendarUrl })
$miCalClear.Add_Click({ Clear-CalendarUrls })
$miCity.Add_Click({ Set-WeatherCity })

# фонові оновлення: погода кожні 15 хв, календар кожні 10 хв
$script:NetTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:NetTimer.Interval = [TimeSpan]::FromSeconds(2)
$script:NetTick = 0
$script:NetTimer.Add_Tick({
    $script:NetTimer.Interval = [TimeSpan]::FromMinutes(1)
    try {
        if ($script:NetTick % 15 -eq 0) { Refresh-Weather }
        if ($script:NetTick % 10 -eq 0) { Refresh-Calendar }
        $script:NetTick++
    } catch {}
})

# ================== DISCORD: СТАН МІКРОФОНА ==================
# Працює через локальний RPC Discord (іменований канал discord-ipc-N) у фоновому потоці.
$script:DSync = [hashtable]::Synchronized(@{
    ClientId = "$($script:Settings.DiscordClientId)"; ClientSecret = "$($script:Settings.DiscordClientSecret)"
    Token = "$($script:Settings.DiscordToken)"; Refresh = "$($script:Settings.DiscordRefresh)"
    RedirectUri = 'http://localhost'
    Stop = $false; Mute = $false; Deaf = $false; Version = 0; Connected = $false
    Status = ''; LastError = ''; WantAuthorize = $false; TokenChanged = $false; Pipe = $null
})

$DiscordWorker = {
    param($sync)
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}
    function Send-Raw($pipe, [int]$op, [string]$json) {
        $b = [Text.Encoding]::UTF8.GetBytes($json)
        $hdr = New-Object byte[] 8
        [BitConverter]::GetBytes($op).CopyTo($hdr, 0)
        [BitConverter]::GetBytes($b.Length).CopyTo($hdr, 4)
        $pipe.Write($hdr, 0, 8); $pipe.Write($b, 0, $b.Length); $pipe.Flush()
    }
    function Send-Frame($pipe, [int]$op, $obj) { Send-Raw $pipe $op ($obj | ConvertTo-Json -Depth 6 -Compress) }
    function Read-Exact($pipe, [int]$n) {
        $buf = New-Object byte[] $n; $r = 0
        while ($r -lt $n) { $k = $pipe.Read($buf, $r, $n - $r); if ($k -le 0) { throw 'pipe closed' }; $r += $k }
        return ,$buf
    }
    function Read-Frame($pipe) {
        $h = Read-Exact $pipe 8
        $op = [BitConverter]::ToInt32($h, 0); $len = [BitConverter]::ToInt32($h, 4)
        $json = ''
        if ($len -gt 0) { $json = [Text.Encoding]::UTF8.GetString((Read-Exact $pipe $len)) }
        return @{ Op = $op; Json = $json }
    }
    function Read-Json($pipe) {
        while ($true) {
            $f = Read-Frame $pipe
            if ($f.Op -eq 3) { Send-Raw $pipe 4 $f.Json; continue }
            if ($f.Op -eq 2) { throw "Discord закрив з'єднання: $($f.Json)" }
            return ($f.Json | ConvertFrom-Json)
        }
    }
    function Nonce { [guid]::NewGuid().ToString() }
    function Get-Token([hashtable]$form) {
        $form.client_id = "$($sync.ClientId)"; $form.client_secret = "$($sync.ClientSecret)"
        $r = Invoke-RestMethod -Method Post -Uri 'https://discord.com/api/oauth2/token' -Body $form -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 20
        $sync.Token = "$($r.access_token)"; $sync.Refresh = "$($r.refresh_token)"; $sync.TokenChanged = $true
    }
    function Try-Auth($pipe) {
        Send-Frame $pipe 1 @{ cmd = 'AUTHENTICATE'; args = @{ access_token = "$($sync.Token)" }; nonce = (Nonce) }
        $r = Read-Json $pipe
        return ($r.evt -ne 'ERROR')
    }

    while (-not $sync.Stop) {
        if (-not $sync.ClientId) { $sync.Status = 'notconfigured'; Start-Sleep -Seconds 2; continue }
        $pipe = $null
        try {
            for ($i = 0; $i -lt 10 -and -not $pipe; $i++) {
                $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', "discord-ipc-$i", [System.IO.Pipes.PipeDirection]::InOut)
                try { $p.Connect(300); $pipe = $p } catch { $p.Dispose() }
            }
            if (-not $pipe) { $sync.Status = 'nodiscord'; Start-Sleep -Seconds 3; continue }
            $sync.Pipe = $pipe
            Send-Frame $pipe 0 @{ v = 1; client_id = "$($sync.ClientId)" }
            [void](Read-Json $pipe)   # READY

            $authed = $false
            if ($sync.Token) {
                $authed = Try-Auth $pipe
                if (-not $authed -and $sync.Refresh) {
                    try { Get-Token @{ grant_type = 'refresh_token'; refresh_token = "$($sync.Refresh)" }; $authed = Try-Auth $pipe } catch {}
                }
            }
            if (-not $authed) {
                if (-not $sync.WantAuthorize) { $sync.Status = 'needauth'; throw 'need auth' }
                $sync.Status = 'authorizing'
                Send-Frame $pipe 1 @{ cmd = 'AUTHORIZE'; args = @{ client_id = "$($sync.ClientId)"; scopes = @('rpc', 'rpc.voice.read') }; nonce = (Nonce) }
                $r = Read-Json $pipe            # чекає, поки користувач натисне «Авторизувати» в Discord
                $sync.WantAuthorize = $false
                if ($r.evt -eq 'ERROR') { $sync.LastError = "$($r.data.message)"; $sync.Status = 'denied'; throw 'denied' }
                try { Get-Token @{ grant_type = 'authorization_code'; code = "$($r.data.code)"; redirect_uri = "$($sync.RedirectUri)" } }
                catch { $sync.LastError = "Не вдалося отримати токен: $_"; $sync.Status = 'autherror'; throw 'token' }
                if (-not (Try-Auth $pipe)) { $sync.LastError = 'Discord не прийняв токен.'; $sync.Status = 'autherror'; throw 'auth' }
            }

            Send-Frame $pipe 1 @{ cmd = 'SUBSCRIBE'; evt = 'VOICE_SETTINGS_UPDATE'; args = @{}; nonce = (Nonce) }
            Send-Frame $pipe 1 @{ cmd = 'GET_VOICE_SETTINGS'; args = @{}; nonce = (Nonce) }
            $sync.Status = 'ok'
            while (-not $sync.Stop) {
                $m = Read-Json $pipe
                if (($m.cmd -eq 'GET_VOICE_SETTINGS' -or $m.evt -eq 'VOICE_SETTINGS_UPDATE') -and $m.data -and $null -ne $m.data.mute) {
                    $sync.Mute = [bool]$m.data.mute
                    $sync.Deaf = [bool]$m.data.deaf
                    $sync.Connected = $true
                    $sync.Version = $sync.Version + 1
                }
            }
        } catch {
            if ($sync.Status -eq 'ok' -or $sync.Status -eq 'authorizing') { $sync.Status = 'nodiscord' }
        } finally {
            $sync.Connected = $false
            if ($pipe) { try { $pipe.Dispose() } catch {} }
            $sync.Pipe = $null
        }
        $wait = if ($sync.Status -eq 'needauth' -or $sync.Status -eq 'denied' -or $sync.Status -eq 'autherror') { 5 } else { 2 }
        for ($t = 0; $t -lt $wait * 4 -and -not $sync.Stop -and -not $sync.WantAuthorize; $t++) { Start-Sleep -Milliseconds 250 }
    }
}

$script:DRunspace = [runspacefactory]::CreateRunspace()
$script:DRunspace.ApartmentState = 'MTA'
$script:DRunspace.Open()
$script:DPs = [powershell]::Create()
$script:DPs.Runspace = $script:DRunspace
[void]$script:DPs.AddScript($DiscordWorker).AddArgument($script:DSync)
[void]$script:DPs.BeginInvoke()

# --- значок мікрофона (перекреслений, якщо вимкнено) ---
function New-MicGlyph([bool]$off, [double]$size, [string]$color) {
    $g = New-Object System.Windows.Controls.Grid
    $g.Width = $size + 2; $g.Height = $size + 2
    $t = New-Object System.Windows.Controls.TextBlock
    $t.FontFamily = $IconFont; $t.FontSize = $size; $t.Text = [string][char]0xE720
    $t.Foreground = Brush $color; $t.HorizontalAlignment = 'Center'; $t.VerticalAlignment = 'Center'
    [void]$g.Children.Add($t)
    if ($off) {
        $l = New-Object System.Windows.Shapes.Line
        $l.X1 = 1; $l.Y1 = 1; $l.X2 = $size + 1; $l.Y2 = $size + 1
        $l.Stroke = Brush $color; $l.StrokeThickness = [Math]::Max(1.6, $size / 9)
        $l.StrokeStartLineCap = 'Round'; $l.StrokeEndLineCap = 'Round'
        [void]$g.Children.Add($l)
    }
    return $g
}

# --- окремий маленький острівець (для режиму «плаваюча картка» або коли віджет схований) ---
[xml]$micXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent" ResizeMode="NoResize"
        ShowInTaskbar="False" Topmost="True" ShowActivated="False" Width="420" Height="80"
        UseLayoutRounding="True" FontFamily="Segoe UI">
  <Border x:Name="MCard" HorizontalAlignment="Center" VerticalAlignment="Top" Margin="14,0,14,14"
          CornerRadius="0,0,14,14" Background="#E6050506" BorderBrush="#26FFFFFF" BorderThickness="1,0,1,1" Padding="14,5,14,6">
    <Border.Effect><DropShadowEffect BlurRadius="14" ShadowDepth="0" Opacity="0.35" Color="Black"/></Border.Effect>
    <StackPanel Orientation="Horizontal">
      <Border x:Name="MIconHost" VerticalAlignment="Center"/>
      <TextBlock x:Name="MText" FontSize="14" FontWeight="SemiBold" VerticalAlignment="Center" Margin="10,0,2,0"/>
    </StackPanel>
  </Border>
</Window>
'@
$MicWin    = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $micXaml))
$MIconHost = $MicWin.FindName('MIconHost')
$MText     = $MicWin.FindName('MText')
$MicWin.Add_SourceInitialized({
    try {
        $h = (New-Object System.Windows.Interop.WindowInteropHelper($MicWin)).Handle
        $ex = [RBW.Win]::GetLong($h, -20)
        [RBW.Win]::SetLong($h, -20, (($ex -bor 0x80 -bor 0x20 -bor 0x08000000) -band (-bnot 0x40000)))   # tool + click-through + no-activate
    } catch {}
})

$script:MicMuted = $false; $script:MicKnown = $false; $script:RawMute = $false; $script:RawDeaf = $false
$script:NoticeActive = $false
$script:MicOnlyMode = $false
$NoticeH = 30

function Use-Integrated { return ($script:Settings.Mode -eq 'strip' -and $Window.IsVisible) }

function Update-Topmost {
    $want = [bool]$script:Settings.Topmost -or ((Use-Integrated) -and ($script:MicMuted -or $script:NoticeActive))
    if ($Window.Topmost -ne $want) { $Window.Topmost = $want }
}

function Update-MicBadge {
    # мікрофон вимкнено: у згорнутому острівці лишається тільки значок перекресленого мікрофона
    $only = ($script:MicMuted -and $script:MicKnown)
    $script:MicOnlyMode = $only
    if ($only) {
        $MicBadgeHost.Child = New-MicGlyph $true 14 $C_Red
        $MicBadgeHost.Visibility = 'Visible'
        $MicBadgeHost.Margin = New-Object System.Windows.Thickness(0)
    } else {
        $MicBadgeHost.Child = $null
        $MicBadgeHost.Visibility = 'Collapsed'
    }
    $vis = if ($only) { 'Collapsed' } else { 'Visible' }
    $LeftItems.Visibility = $vis; $RightItems.Visibility = $vis; $StripClock.Visibility = $vis
    $StripCenter.Margin = if ($only) { New-Object System.Windows.Thickness(0) } else { New-Object System.Windows.Thickness(14, 0, 14, 0) }
    Sync-Layout
}

function Update-MicWindow {
    $need = (-not (Use-Integrated)) -and ($script:NoticeActive -or ($script:MicMuted -and $script:MicKnown))
    if (-not $need) { if ($MicWin.IsVisible) { $MicWin.Hide() }; return }
    if (-not $script:NoticeActive) {
        $MIconHost.Child = New-MicGlyph $true 14 $C_Red
        $MText.Visibility = 'Collapsed'
    } else { $MText.Visibility = 'Visible' }
    $wa = [System.Windows.SystemParameters]::WorkArea
    $MicWin.Left = $wa.Left + ($wa.Width - $MicWin.Width) / 2
    $MicWin.Top  = $wa.Top
    if (-not $MicWin.IsVisible) { $MicWin.Show() }
    $MicWin.Topmost = $false; $MicWin.Topmost = $true
}

$script:NoticeTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:NoticeTimer.Interval = [TimeSpan]::FromMilliseconds(2600)
$script:NoticeTimer.Add_Tick({
    $script:NoticeTimer.Stop()
    $script:NoticeActive = $false
    Update-Topmost
    if ((Use-Integrated) -and -not $script:Expanded) { Apply-Visual $false $true }
    Update-MicWindow
})

function Show-MicNotice([bool]$mute, [bool]$deaf) {
    $off = $mute -or $deaf
    $text = if ($deaf) { 'Звук вимкнено' } elseif ($mute) { 'Мікрофон вимкнено' } else { 'Мікрофон увімкнено' }
    $color = if ($off) { $C_Red } else { $C_Green }
    $script:NoticeActive = $true
    $NoticeText.Text = $text; $NoticeText.Foreground = Brush $color
    $NoticeIconHost.Child = New-MicGlyph $off 16 $color
    $MText.Text = $text; $MText.Foreground = Brush $color
    $MIconHost.Child = New-MicGlyph $off 15 $color
    Update-MicBadge
    Update-Topmost
    if ((Use-Integrated) -and -not $script:Expanded) { Apply-Visual $false $true }
    Update-MicWindow
    $script:NoticeTimer.Stop(); $script:NoticeTimer.Start()
}

function Connect-Discord {
    $id = [Microsoft.VisualBasic.Interaction]::InputBox("Встав Client ID свого застосунку Discord`n(discord.com/developers/applications $([char]0x2192) твій застосунок $([char]0x2192) OAuth2):", 'Discord — мікрофон', "$($script:Settings.DiscordClientId)")
    $id = "$id".Trim(); if (-not $id) { return }
    $sec = [Microsoft.VisualBasic.Interaction]::InputBox('Встав Client Secret (там само, кнопка Reset Secret):', 'Discord — мікрофон', '')
    $sec = "$sec".Trim(); if (-not $sec) { return }
    $script:Settings.DiscordClientId = $id; $script:Settings.DiscordClientSecret = $sec
    $script:Settings.DiscordToken = ''; $script:Settings.DiscordRefresh = ''
    Save-Settings
    $s = $script:DSync
    $s.ClientId = $id; $s.ClientSecret = $sec; $s.Token = ''; $s.Refresh = ''; $s.LastError = ''; $s.Status = ''
    $s.WantAuthorize = $true
    $script:ReportedError = $false
    try { if ($s.Pipe) { $s.Pipe.Dispose() } } catch {}
    [System.Windows.MessageBox]::Show("Зараз у Discord з'явиться вікно із запитом доступу — натисни «Авторизувати».", 'Discord — мікрофон') | Out-Null
}
function Disconnect-Discord {
    foreach ($k in 'DiscordClientId', 'DiscordClientSecret', 'DiscordToken', 'DiscordRefresh') { $script:Settings[$k] = '' }
    Save-Settings
    $s = $script:DSync; $s.ClientId = ''; $s.ClientSecret = ''; $s.Token = ''; $s.Refresh = ''
    try { if ($s.Pipe) { $s.Pipe.Dispose() } } catch {}
}

$miDiscord    = New-Object System.Windows.Forms.ToolStripMenuItem('Discord: підключити мікрофон…')
$miDiscordOff = New-Object System.Windows.Forms.ToolStripMenuItem('Discord: відключити')
$exitIdx = $TrayMenu.Items.IndexOf($miExit)
$TrayMenu.Items.Insert($exitIdx - 1, $miDiscord)
$TrayMenu.Items.Insert($exitIdx, $miDiscordOff)
$miDiscord.Add_Click({ Connect-Discord })
$miDiscordOff.Add_Click({ Disconnect-Discord })

$script:MicVersion = -1
$script:ReportedError = $false
$script:MicTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:MicTimer.Interval = [TimeSpan]::FromMilliseconds(150)
$script:MicTimer.Add_Tick({
    $s = $script:DSync
    try {
        if ($s.TokenChanged) {
            $s.TokenChanged = $false
            $script:Settings.DiscordToken = "$($s.Token)"; $script:Settings.DiscordRefresh = "$($s.Refresh)"
            Save-Settings
        }
        if (-not $s.Connected) {
            if ($script:MicKnown) {
                $script:MicKnown = $false; $script:MicMuted = $false
                Update-MicBadge; Update-Topmost; Update-MicWindow
            }
        } elseif ($s.Version -ne $script:MicVersion) {
            $script:MicVersion = $s.Version
            $m = [bool]$s.Mute; $d = [bool]$s.Deaf
            $changed = $script:MicKnown -and (($m -ne $script:RawMute) -or ($d -ne $script:RawDeaf))
            $script:RawMute = $m; $script:RawDeaf = $d
            $script:MicMuted = $m -or $d
            $script:MicKnown = $true
            if ($changed) { Show-MicNotice $m $d } else { Update-MicBadge; Update-Topmost; Update-MicWindow }
        }

        $st = "$($s.Status)"
        $miDiscord.Text = switch ($st) {
            'ok'          { "Discord: підключено $([char]0x2713) (перепідключити…)" }
            'nodiscord'   { 'Discord: не запущений (перепідключити…)' }
            'authorizing' { 'Discord: чекаю підтвердження в Discord…' }
            'needauth'    { 'Discord: потрібно підключити знову…' }
            default       { 'Discord: підключити мікрофон…' }
        }
        $miDiscordOff.Visible = [bool]$s.ClientId
        if (($st -eq 'denied' -or $st -eq 'autherror') -and -not $script:ReportedError) {
            $script:ReportedError = $true
            [System.Windows.MessageBox]::Show("Не вдалося підключитися до Discord.`n$($s.LastError)", 'Discord — мікрофон') | Out-Null
        }
    } catch {}
})

# ================== ЗАСТОСУНОК: ІКОНКА, ЗАПУСКАЧ, ЯРЛИКИ ==================
$AppName       = 'Razer Island'
$AppExe        = Join-Path $Root "$AppName.exe"
$AppIco        = Join-Path $Root 'app.ico'
$AppVbs        = Join-Path $Root 'Запустити.vbs'
$StartMenuLnk  = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
$StartMenuSet  = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName — налаштування.lnk"
$DesktopLnk    = Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"

function New-AppIcon([string]$path) {
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $images = New-Object System.Collections.ArrayList
    foreach ($sz in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($sz, $sz)
        $gr = [System.Drawing.Graphics]::FromImage($bmp)
        $gr.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $gr.Clear([System.Drawing.Color]::Transparent)
        $bgPath = New-RoundRect 0 0 ($sz - 1) ($sz - 1) ([Math]::Max(3, $sz * 0.22))
        $gr.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#141619'))), $bgPath)
        # «острівець» зверху
        $pill = New-RoundRect ($sz * 0.28) ($sz * 0.14) ($sz * 0.44) ([Math]::Max(2, $sz * 0.12)) ([Math]::Max(1, $sz * 0.06))
        $gr.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#3A3E46'))), $pill)
        # батарейка Razer-зеленого кольору
        $green = [System.Drawing.ColorTranslator]::FromHtml('#44D62C')
        $pw = [Math]::Max(1.2, $sz * 0.06)
        $bx = $sz * 0.17; $by = $sz * 0.42; $bw = $sz * 0.58; $bh = $sz * 0.32
        $pen = New-Object System.Drawing.Pen($green, $pw)
        $gr.DrawPath($pen, (New-RoundRect $bx $by $bw $bh ([Math]::Max(1, $sz * 0.05))))
        $gb = New-Object System.Drawing.SolidBrush($green)
        $gr.FillRectangle($gb, ($bx + $bw + $pw * 0.6), ($by + $bh * 0.3), [Math]::Max(1, $sz * 0.06), ($bh * 0.4))
        $ins = $pw * 1.6
        $gr.FillRectangle($gb, ($bx + $ins), ($by + $ins), (($bw - 2 * $ins) * 0.7), ($bh - 2 * $ins))
        $gr.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        [void]$images.Add(@{ Size = $sz; Data = $ms.ToArray() })
    }
    $out = New-Object System.IO.MemoryStream
    $bw2 = New-Object System.IO.BinaryWriter($out)
    $bw2.Write([uint16]0); $bw2.Write([uint16]1); $bw2.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($im in $images) {
        $d = if ($im.Size -ge 256) { 0 } else { $im.Size }
        $bw2.Write([byte]$d); $bw2.Write([byte]$d); $bw2.Write([byte]0); $bw2.Write([byte]0)
        $bw2.Write([uint16]1); $bw2.Write([uint16]32)
        $bw2.Write([uint32]$im.Data.Length); $bw2.Write([uint32]$offset)
        $offset += $im.Data.Length
    }
    foreach ($im in $images) { $bw2.Write([byte[]]$im.Data) }
    $bw2.Flush()
    [System.IO.File]::WriteAllBytes($path, $out.ToArray())
}

function Build-Launcher {
    $code = @'
using System;
using System.Diagnostics;
using System.IO;
static class RazerIslandLauncher {
    [STAThread]
    static void Main(string[] args) {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string ps1 = Path.Combine(dir, "RazerBatteryWidget.ps1");
        string extra = args.Length > 0 ? " " + string.Join(" ", args) : "";
        ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File \"" + ps1 + "\"" + extra);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        psi.WorkingDirectory = dir;
        Process.Start(psi);
    }
}
'@
    $prov = New-Object Microsoft.CSharp.CSharpCodeProvider
    $cp = New-Object System.CodeDom.Compiler.CompilerParameters
    $cp.GenerateExecutable = $true
    $cp.OutputAssembly = $AppExe
    $cp.CompilerOptions = '/target:winexe /optimize+ /win32icon:"' + $AppIco + '"'
    [void]$cp.ReferencedAssemblies.Add('System.dll')
    $res = $prov.CompileAssemblyFromSource($cp, $code)
    if ($res.Errors.HasErrors) { Write-Log ('Не вдалося зібрати exe: ' + (($res.Errors | ForEach-Object { $_.ToString() }) -join '; ')) }
}

function Get-LaunchTarget {
    if (Test-Path $AppExe) { return @{ Target = $AppExe; Args = '' } }
    return @{ Target = "$env:WINDIR\System32\wscript.exe"; Args = '"' + $AppVbs + '"' }
}
function New-AppShortcut([string]$lnkPath, [string]$extraArgs, [string]$desc) {
    $t = Get-LaunchTarget
    $sh = New-Object -ComObject WScript.Shell
    $lnk = $sh.CreateShortcut($lnkPath)
    $lnk.TargetPath = $t.Target
    $lnk.Arguments = ($t.Args + ' ' + $extraArgs).Trim()
    $lnk.WorkingDirectory = $Root
    if (Test-Path $AppIco) { $lnk.IconLocation = "$AppIco,0" }
    $lnk.Description = $desc
    $lnk.Save()
}
function Set-DesktopShortcut([bool]$on) {
    $script:Settings.DesktopShortcut = $on; Save-Settings
    try {
        if ($on) { New-AppShortcut $DesktopLnk '' 'Заряд Razer, годинник, погода, розклад і мікрофон Discord' }
        elseif (Test-Path $DesktopLnk) { Remove-Item $DesktopLnk -Force }
    } catch { Write-Log "Ярлик на робочому столі: $_" }
}
function Install-App {
    try { if (-not (Test-Path $AppIco)) { New-AppIcon $AppIco } } catch { Write-Log "Іконка: $_" }
    try { if (-not (Test-Path $AppExe)) { Build-Launcher } } catch { Write-Log "Запускач: $_" }
    try {
        if (-not (Test-Path $StartMenuLnk)) { New-AppShortcut $StartMenuLnk '' 'Заряд Razer, годинник, погода, розклад і мікрофон Discord' }
        if (-not (Test-Path $StartMenuSet)) { New-AppShortcut $StartMenuSet '-OpenSettings' "Налаштування $AppName" }
        if ([bool]$script:Settings.DesktopShortcut -and -not (Test-Path $DesktopLnk)) { New-AppShortcut $DesktopLnk '' 'Razer Island' }
    } catch { Write-Log "Ярлики: $_" }
}
Install-App

# фон картки з урахуванням налаштованої непрозорості
function Get-CardBg([bool]$island) {
    $op = [double]$script:Settings.BgOpacity
    if ($op -lt 0.3 -or $op -gt 1) { $op = 0.8 }
    $hex = '{0:X2}' -f [int][Math]::Round(255 * $op)
    if ($island) { return "#$($hex)050506" } else { return "#$($hex)141619" }
}

# ================== ВІКНО НАЛАШТУВАНЬ І МАЙСТЕР ПЕРШОГО ЗАПУСКУ ==================
[xml]$setXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Razer Island — налаштування" Width="760" Height="560" MinWidth="680" MinHeight="480"
        WindowStartupLocation="CenterScreen" Background="#141619" FontFamily="Segoe UI" FontSize="13"
        UseLayoutRounding="True">
  <Window.Resources>
    <Style TargetType="TextBlock">
      <Setter Property="Foreground" Value="#D6D9DE"/>
      <Setter Property="TextWrapping" Value="Wrap"/>
    </Style>
    <Style TargetType="Hyperlink">
      <Setter Property="Foreground" Value="#6EA8FF"/>
    </Style>
    <Style TargetType="Button">
      <Setter Property="Foreground" Value="#F2F3F5"/>
      <Setter Property="Background" Value="#2A2D33"/>
      <Setter Property="Padding" Value="16,8"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="HorizontalContentAlignment" Value="Center"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="b" Background="{TemplateBinding Background}" CornerRadius="8" Padding="{TemplateBinding Padding}">
              <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Opacity" Value="0.85"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter TargetName="b" Property="Opacity" Value="0.35"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key="Primary" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
      <Setter Property="Background" Value="#44D62C"/>
      <Setter Property="Foreground" Value="#0B0C0E"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>
    <Style x:Key="Nav" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="HorizontalContentAlignment" Value="Left"/>
      <Setter Property="Padding" Value="12,9"/>
      <Setter Property="Margin" Value="0,1"/>
    </Style>
    <Style TargetType="TextBox">
      <Setter Property="Background" Value="#1E2126"/>
      <Setter Property="Foreground" Value="#F2F3F5"/>
      <Setter Property="CaretBrush" Value="#F2F3F5"/>
      <Setter Property="BorderBrush" Value="#33FFFFFF"/>
      <Setter Property="Padding" Value="8,6"/>
    </Style>
    <Style TargetType="PasswordBox">
      <Setter Property="Background" Value="#1E2126"/>
      <Setter Property="Foreground" Value="#F2F3F5"/>
      <Setter Property="CaretBrush" Value="#F2F3F5"/>
      <Setter Property="BorderBrush" Value="#33FFFFFF"/>
      <Setter Property="Padding" Value="8,6"/>
    </Style>
    <Style TargetType="CheckBox">
      <Setter Property="Foreground" Value="#E6E8EB"/>
      <Setter Property="Margin" Value="0,5"/>
    </Style>
    <Style TargetType="RadioButton">
      <Setter Property="Foreground" Value="#E6E8EB"/>
      <Setter Property="Margin" Value="0,5"/>
    </Style>
    <Style x:Key="H1" TargetType="TextBlock">
      <Setter Property="Foreground" Value="#F2F3F5"/>
      <Setter Property="FontSize" Value="22"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Margin" Value="0,0,0,10"/>
    </Style>
    <Style x:Key="H2" TargetType="TextBlock">
      <Setter Property="Foreground" Value="#8A8F98"/>
      <Setter Property="FontSize" Value="11"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Margin" Value="0,16,0,4"/>
    </Style>
    <Style x:Key="Box" TargetType="Border">
      <Setter Property="Background" Value="#1B1E23"/>
      <Setter Property="CornerRadius" Value="10"/>
      <Setter Property="Padding" Value="14,12"/>
      <Setter Property="Margin" Value="0,6,0,0"/>
    </Style>
  </Window.Resources>

  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="Auto"/>
      <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>

    <!-- бічне меню (режим налаштувань) -->
    <Border x:Name="Sidebar" Grid.Column="0" Width="190" Background="#0F1114" Padding="12,16">
      <StackPanel>
        <StackPanel Orientation="Horizontal" Margin="6,0,0,18">
          <Ellipse Width="9" Height="9" Fill="#44D62C" VerticalAlignment="Center" Margin="0,0,8,0"/>
          <TextBlock Text="Razer Island" Foreground="#F2F3F5" FontSize="15" FontWeight="SemiBold"/>
        </StackPanel>
        <Button x:Name="N1" Style="{StaticResource Nav}" Tag="1" Content="Батарея"/>
        <Button x:Name="N2" Style="{StaticResource Nav}" Tag="2" Content="Вигляд"/>
        <Button x:Name="N3" Style="{StaticResource Nav}" Tag="3" Content="Погода"/>
        <Button x:Name="N4" Style="{StaticResource Nav}" Tag="4" Content="Календар"/>
        <Button x:Name="N5" Style="{StaticResource Nav}" Tag="5" Content="Мікрофон Discord"/>
        <Button x:Name="N6" Style="{StaticResource Nav}" Tag="6" Content="Система"/>
        <Button x:Name="NWizard" Style="{StaticResource Nav}" Margin="0,18,0,0" Foreground="#8A8F98" Content="Пройти майстер ще раз"/>
      </StackPanel>
    </Border>

    <Grid Grid.Column="1" Margin="28,22,28,18">
      <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="*"/>
        <RowDefinition Height="Auto"/>
      </Grid.RowDefinitions>

      <StackPanel Grid.Row="0">
        <TextBlock x:Name="StepText" Foreground="#44D62C" FontSize="11" FontWeight="SemiBold" Margin="0,0,0,4"/>
        <Border x:Name="StepBar" Height="3" Background="#22FFFFFF" CornerRadius="1.5" Margin="0,0,0,14">
          <Border x:Name="StepFill" Height="3" Background="#44D62C" CornerRadius="1.5" HorizontalAlignment="Left"/>
        </Border>
      </StackPanel>

      <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
        <Grid x:Name="Pages">

          <!-- 0: Вітання -->
          <StackPanel x:Name="P0">
            <TextBlock Style="{StaticResource H1}" Text="Вітаю в Razer Island"/>
            <TextBlock>Це віджет-острівець угорі екрана, як Dynamic Island в айфоні. Він показує:</TextBlock>
            <Border Style="{StaticResource Box}">
              <TextBlock LineHeight="22">
                • заряд бездротової мишки й навушників Razer (і в треї біля годинника);<LineBreak/>
                • годинник, дату й погоду;<LineBreak/>
                • твій розклад із Google Календаря;<LineBreak/>
                • стан мікрофона в Discord — вимкнений мікрофон видно поверх усіх вікон.
              </TextBlock>
            </Border>
            <TextBlock Margin="0,14,0,0">Налаштування займе 2–3 хвилини. Будь-який крок можна пропустити й повернутися до нього пізніше: права кнопка на іконці в треї → «Налаштування».</TextBlock>
          </StackPanel>

          <!-- 1: Батарея -->
          <StackPanel x:Name="P1">
            <TextBlock Style="{StaticResource H1}" Text="Батарея Razer"/>
            <TextBlock>Заряд береться з програми Razer Synapse 4 — вона має бути встановлена й запущена (зазвичай вона сама стартує з Windows).</TextBlock>
            <TextBlock Style="{StaticResource H2}" Text="ЗНАЙДЕНІ ПРИСТРОЇ"/>
            <Border Style="{StaticResource Box}">
              <TextBlock x:Name="BatStatus" LineHeight="22"/>
            </Border>
            <StackPanel Orientation="Horizontal" Margin="0,10,0,0">
              <Button x:Name="BtnBatRefresh" Content="Перевірити знову"/>
            </StackPanel>
            <TextBlock Margin="0,14,0,0" Foreground="#8A8F98" FontSize="12">
              Немає Synapse? Завантаж його з сайту Razer:
              <Hyperlink NavigateUri="https://www.razer.com/synapse-4">razer.com/synapse-4</Hyperlink>.
              Заряд мишки оновлюється приблизно раз на 1–2 хвилини, навушників — рідше: так часто його записує сам Synapse.
            </TextBlock>
          </StackPanel>

          <!-- 2: Вигляд -->
          <StackPanel x:Name="P2">
            <TextBlock Style="{StaticResource H1}" Text="Вигляд"/>
            <TextBlock Style="{StaticResource H2}" Text="ФОРМА" Margin="0,0,0,4"/>
            <RadioButton x:Name="RbIsland" GroupName="mode" Content="Острівець зверху по центру — розкривається, коли наводиш мишку"/>
            <RadioButton x:Name="RbCard" GroupName="mode" Content="Плаваюча картка — завжди розкрита, можна перетягувати"/>
            <TextBlock Style="{StaticResource H2}" Text="ПОВЕДІНКА"/>
            <CheckBox x:Name="CbTop" Content="Завжди поверх усіх вікон"/>
            <CheckBox x:Name="CbPin" Content="Закріпити картку на місці (щоб випадково не перетягнути)"/>
            <TextBlock Style="{StaticResource H2}" Text="НЕПРОЗОРІСТЬ ФОНУ"/>
            <Grid>
              <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="56"/></Grid.ColumnDefinitions>
              <Slider x:Name="SlOpacity" Minimum="40" Maximum="100" TickFrequency="5" IsSnapToTickEnabled="True" VerticalAlignment="Center"/>
              <TextBlock x:Name="LblOpacity" Grid.Column="1" HorizontalAlignment="Right" VerticalAlignment="Center" Foreground="#F2F3F5"/>
            </Grid>
            <TextBlock Style="{StaticResource H2}" Text="ІКОНКИ В ТРЕЇ"/>
            <RadioButton x:Name="RbTrayIcon" GroupName="tray" Content="Значок пристрою + батарейка"/>
            <RadioButton x:Name="RbTrayNum" GroupName="tray" Content="Відсоток заряду цифрами + батарейка"/>
            <TextBlock Margin="0,8,0,0" Foreground="#8A8F98" FontSize="12">Якщо іконок не видно біля годинника — Windows сховала їх під стрілочку ^. Перетягни їх на панель або увімкни в Параметри → Персоналізація → Панель завдань → Інші значки.</TextBlock>
          </StackPanel>

          <!-- 3: Погода -->
          <StackPanel x:Name="P3">
            <TextBlock Style="{StaticResource H1}" Text="Погода"/>
            <TextBlock>Введи своє місто — погода оновлюється кожні 15 хвилин (дані open-meteo.com, без реєстрації).</TextBlock>
            <Grid Margin="0,14,0,0">
              <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
              <TextBox x:Name="TbCity"/>
              <Button x:Name="BtnCity" Grid.Column="1" Margin="8,0,0,0" Content="Знайти"/>
            </Grid>
            <TextBlock x:Name="CityStatus" Margin="0,10,0,0" Foreground="#8A8F98"/>
          </StackPanel>

          <!-- 4: Календар -->
          <StackPanel x:Name="P4">
            <TextBlock Style="{StaticResource H1}" Text="Google Календар"/>
            <TextBlock>Щоб бачити розклад, віджету потрібне секретне посилання на календар (без входу в акаунт):</TextBlock>
            <Border Style="{StaticResource Box}">
              <TextBlock LineHeight="22">
                1. Відкрий <Hyperlink NavigateUri="https://calendar.google.com/calendar/r/settings">налаштування Google Календаря</Hyperlink>.<LineBreak/>
                2. Зліва в розділі «Налаштування моїх календарів» вибери свій календар.<LineBreak/>
                3. Прокрути до «Інтеграція календаря» і скопіюй «Секретна адреса у форматі iCal».<LineBreak/>
                4. Встав її нижче. Можна кілька календарів — по одному посиланню в рядку.
              </TextBlock>
            </Border>
            <TextBox x:Name="TbCal" Margin="0,12,0,0" AcceptsReturn="True" Height="74" TextWrapping="NoWrap" VerticalScrollBarVisibility="Auto" FontFamily="Consolas" FontSize="12"/>
            <StackPanel Orientation="Horizontal" Margin="0,10,0,0">
              <Button x:Name="BtnCalTest" Content="Зберегти й перевірити"/>
            </StackPanel>
            <TextBlock x:Name="CalStatus" Margin="0,10,0,0" Foreground="#8A8F98"/>
            <TextBlock Margin="0,10,0,0" Foreground="#FFB347" FontSize="12">Нікому не показуй це посилання — за ним видно весь календар. Воно зберігається тільки на цьому комп'ютері.</TextBlock>
          </StackPanel>

          <!-- 5: Discord -->
          <StackPanel x:Name="P5">
            <TextBlock Style="{StaticResource H1}" Text="Мікрофон у Discord"/>
            <TextBlock>Коли вимикаєш мікрофон у Discord, острівець показує це поверх усіх вікон. Discord дозволяє це лише через твій власний «застосунок» — його створення займає 2 хвилини:</TextBlock>
            <Border Style="{StaticResource Box}">
              <TextBlock LineHeight="22">
                1. Відкрий <Hyperlink NavigateUri="https://discord.com/developers/applications">Discord Developer Portal</Hyperlink> → «New Application» → будь-яка назва → «Create».<LineBreak/>
                2. Зліва відкрий «OAuth2» і скопіюй «Client ID».<LineBreak/>
                3. Там само натисни «Reset Secret» і скопіюй «Client Secret».<LineBreak/>
                4. У «Redirects» натисни «Add Redirect», впиши <Run Foreground="#F2F3F5" FontFamily="Consolas">http://localhost</Run> і натисни «Save Changes».<LineBreak/>
                5. Встав Client ID і Client Secret нижче й натисни «Підключити». У Discord з'явиться вікно — натисни «Авторизувати».
              </TextBlock>
            </Border>
            <TextBlock Style="{StaticResource H2}" Text="CLIENT ID"/>
            <TextBox x:Name="TbDiscordId" FontFamily="Consolas"/>
            <TextBlock Style="{StaticResource H2}" Text="CLIENT SECRET"/>
            <PasswordBox x:Name="PbDiscordSecret"/>
            <StackPanel Orientation="Horizontal" Margin="0,12,0,0">
              <Button x:Name="BtnDiscord" Style="{StaticResource Primary}" Content="Підключити"/>
              <Button x:Name="BtnDiscordOff" Margin="8,0,0,0" Content="Відключити"/>
            </StackPanel>
            <TextBlock x:Name="DiscordStatus" Margin="0,10,0,0" Foreground="#8A8F98"/>
          </StackPanel>

          <!-- 6: Система -->
          <StackPanel x:Name="P6">
            <TextBlock Style="{StaticResource H1}" Text="Система"/>
            <CheckBox x:Name="CbAuto" Content="Запускати разом з Windows"/>
            <CheckBox x:Name="CbDesktop" Content="Ярлик «Razer Island» на робочому столі"/>
            <Border Style="{StaticResource Box}" Margin="0,14,0,0">
              <TextBlock x:Name="SysInfo" LineHeight="22" Foreground="#A9ADB5" FontSize="12"/>
            </Border>
          </StackPanel>

          <!-- 7: Готово -->
          <StackPanel x:Name="P7">
            <TextBlock Style="{StaticResource H1}" Text="Готово!"/>
            <Border Style="{StaticResource Box}">
              <TextBlock LineHeight="22">
                • Наведи мишку на острівець угорі екрана — він розкриється.<LineBreak/>
                • Права кнопка на острівці або на іконці в треї → «Налаштування», щоб щось змінити.<LineBreak/>
                • Razer Island тепер є в меню «Пуск» — його можна закріпити на початковому екрані.
              </TextBlock>
            </Border>
          </StackPanel>
        </Grid>
      </ScrollViewer>

      <Grid Grid.Row="2" Margin="0,16,0,0">
        <Button x:Name="BtnSkip" HorizontalAlignment="Left" Background="Transparent" Foreground="#8A8F98" Content="Пропустити налаштування"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
          <Button x:Name="BtnBack" Content="Назад"/>
          <Button x:Name="BtnNext" Style="{StaticResource Primary}" Margin="8,0,0,0" Content="Далі"/>
          <Button x:Name="BtnClose" Style="{StaticResource Primary}" Margin="8,0,0,0" Content="Закрити"/>
        </StackPanel>
      </Grid>
    </Grid>
  </Grid>
</Window>
'@

$script:SetWin = $null
$script:SetWizard = $false
$script:SetPage = 0
$script:SetLoading = $false
function SetCtl([string]$n) { return $script:SetWin.FindName($n) }

function Update-BatteryStatusUI {
    if (-not $script:SetWin) { return }
    $lines = @()
    foreach ($d in ($script:Known.Values | Sort-Object Name)) {
        $lines += "$([char]0x2713)  $($d.Name) — $($d.Level)%"
    }
    if ($lines.Count -eq 0) {
        (SetCtl 'BatStatus').Text = "Поки не бачу даних від Synapse.`nПереконайся, що Razer Synapse 4 запущений, а мишка й навушники ввімкнені, і натисни «Перевірити знову»."
        (SetCtl 'BatStatus').Foreground = Brush '#FFB347'
    } else {
        (SetCtl 'BatStatus').Text = ($lines -join "`n")
        (SetCtl 'BatStatus').Foreground = Brush '#D6D9DE'
    }
}
function Get-DiscordStatusText {
    $s = $script:DSync
    switch ("$($s.Status)") {
        'ok'            { return "$([char]0x2713) Підключено. Стан мікрофона відстежується." }
        'nodiscord'     { return 'Discord не запущений — підключення відбудеться автоматично, коли він запуститься.' }
        'authorizing'   { return 'Чекаю підтвердження: відкрий Discord і натисни «Авторизувати».' }
        'needauth'      { return 'Потрібно підключити знову: натисни «Підключити».' }
        'denied'        { return "Доступ не надано: $($s.LastError)" }
        'autherror'     { return "Помилка: $($s.LastError)" }
        'notconfigured' { return 'Не підключено.' }
        default         { return $(if ($s.ClientId) { 'Підключаюся…' } else { 'Не підключено.' }) }
    }
}
function Update-SysInfoUI {
    $exeInfo = if (Test-Path $AppExe) { "Програма: $AppExe" } else { "Програма: $AppVbs" }
    (SetCtl 'SysInfo').Text = "$exeInfo`nНалаштування й журнал помилок: $Root`nЯрлики «$AppName» і «$AppName — налаштування» є в меню «Пуск»."
}

function Save-CalendarFromUI {
    $txt = "$((SetCtl 'TbCal').Text)"
    $urls = @($txt -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $old = @(Get-CalendarUrls)
    if (($urls -join '|') -eq ($old -join '|')) { return $false }
    if ($urls.Count -gt 0) { Set-Content -Path $CalFile -Value $urls -Encoding UTF8 }
    elseif (Test-Path $CalFile) { Remove-Item $CalFile -Force }
    $script:CalData = @{}
    Refresh-Calendar
    return $true
}

function Load-SettingsUI {
    $script:SetLoading = $true
    $isStrip = ("$($script:Settings.Mode)" -ne 'full')
    (SetCtl 'RbIsland').IsChecked = $isStrip; (SetCtl 'RbCard').IsChecked = -not $isStrip
    (SetCtl 'CbTop').IsChecked = [bool]$script:Settings.Topmost
    (SetCtl 'CbPin').IsChecked = [bool]$script:Settings.Pinned
    (SetCtl 'CbPin').IsEnabled = -not $isStrip
    $op = [double]$script:Settings.BgOpacity; if ($op -lt 0.3 -or $op -gt 1) { $op = 0.8 }
    (SetCtl 'SlOpacity').Value = [Math]::Round($op * 100)
    (SetCtl 'LblOpacity').Text = "$([Math]::Round($op * 100))%"
    $num = ("$($script:Settings.TrayStyle)" -eq 'number')
    (SetCtl 'RbTrayIcon').IsChecked = -not $num; (SetCtl 'RbTrayNum').IsChecked = $num
    (SetCtl 'TbCity').Text = "$($script:Settings.WeatherCity)"
    (SetCtl 'CityStatus').Text = "Зараз: $($script:Settings.WeatherCity)"
    (SetCtl 'TbCal').Text = ((Get-CalendarUrls) -join "`r`n")
    (SetCtl 'TbDiscordId').Text = "$($script:Settings.DiscordClientId)"
    (SetCtl 'PbDiscordSecret').Password = "$($script:Settings.DiscordClientSecret)"
    (SetCtl 'DiscordStatus').Text = Get-DiscordStatusText
    (SetCtl 'CbAuto').IsChecked = [bool](Get-Autostart)
    (SetCtl 'CbDesktop').IsChecked = (Test-Path $DesktopLnk)
    Update-BatteryStatusUI
    Update-SysInfoUI
    Update-CalStatusUI
    $script:SetLoading = $false
}

function Update-CalStatusUI {
    if (-not $script:SetWin) { return }
    $urls = @(Get-CalendarUrls)
    $tb = SetCtl 'CalStatus'
    if ($urls.Count -eq 0) { $tb.Text = 'Календар ще не підключено.'; $tb.Foreground = Brush '#8A8F98'; return }
    $loaded = @($script:CalData.Keys).Count
    if ($loaded -eq 0) {
        if ($script:CalError) { $tb.Text = 'Не вдалося завантажити. Перевір посилання та інтернет.'; $tb.Foreground = Brush $C_Red }
        else { $tb.Text = 'Завантажую…'; $tb.Foreground = Brush '#8A8F98' }
        return
    }
    $cnt = @($script:CalData.Values | ForEach-Object { $_ }).Count
    $tb.Text = "$([char]0x2713) Підключено календарів: $loaded з $($urls.Count). Подій на найближчий тиждень: $cnt."
    $tb.Foreground = Brush $C_Green
}

$PageTitles = @('Вітання', 'Батарея', 'Вигляд', 'Погода', 'Календар', 'Мікрофон Discord', 'Система', 'Готово')
function Show-SetPage([int]$i) {
    if ($script:SetPage -eq 4 -and $i -ne 4) { try { [void](Save-CalendarFromUI) } catch {} }
    for ($k = 0; $k -le 7; $k++) { (SetCtl "P$k").Visibility = $(if ($k -eq $i) { 'Visible' } else { 'Collapsed' }) }
    $script:SetPage = $i
    for ($k = 1; $k -le 6; $k++) { (SetCtl "N$k").Background = Brush $(if ($k -eq $i) { '#2A2D33' } else { '#00000000' }) }
    if ($script:SetWizard) {
        (SetCtl 'StepText').Text = "КРОК $($i + 1) З 8 · $($PageTitles[$i].ToUpper())"
        (SetCtl 'StepFill').Width = [Math]::Max(6, (SetCtl 'StepBar').ActualWidth * ($i + 1) / 8)
        (SetCtl 'BtnBack').IsEnabled = ($i -gt 0)
        (SetCtl 'BtnNext').Content = $(if ($i -eq 7) { 'Почати' } elseif ($i -eq 0) { 'Почнімо' } else { 'Далі' })
        (SetCtl 'BtnSkip').Visibility = $(if ($i -lt 7) { 'Visible' } else { 'Collapsed' })
    }
    if ($i -eq 1) { Update-BatteryStatusUI }
    if ($i -eq 6) { Update-SysInfoUI }
}

function Finish-Wizard {
    try { [void](Save-CalendarFromUI) } catch {}
    $script:Settings.FirstRunDone = $true
    Save-Settings
    $script:SetWin.Hide()
    if (-not $Window.IsVisible) { Toggle-Widget }
}

function Open-Settings([int]$page = 2, [switch]$Wizard) {
    if (-not $script:SetWin) { New-SettingsWindow }
    $script:SetWizard = [bool]$Wizard
    $wiz = $script:SetWizard
    (SetCtl 'Sidebar').Visibility = $(if ($wiz) { 'Collapsed' } else { 'Visible' })
    (SetCtl 'StepText').Visibility = $(if ($wiz) { 'Visible' } else { 'Collapsed' })
    (SetCtl 'StepBar').Visibility = $(if ($wiz) { 'Visible' } else { 'Collapsed' })
    foreach ($b in 'BtnBack', 'BtnNext', 'BtnSkip') { (SetCtl $b).Visibility = $(if ($wiz) { 'Visible' } else { 'Collapsed' }) }
    (SetCtl 'BtnClose').Visibility = $(if ($wiz) { 'Collapsed' } else { 'Visible' })
    $script:SetWin.Title = $(if ($wiz) { "$AppName — перше налаштування" } else { "$AppName — налаштування" })
    Load-SettingsUI
    $script:SetPage = -1
    if (-not $script:SetWin.IsVisible) { $script:SetWin.Show() }
    $script:SetWin.Activate() | Out-Null
    $script:SetWin.UpdateLayout()
    Show-SetPage $page
}

function New-SettingsWindow {
    $w = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $setXaml))
    $script:SetWin = $w
    try { $w.Icon = [System.Windows.Media.Imaging.BitmapFrame]::Create((New-Object Uri($AppIco))) } catch {}

    # посилання відкриваються в браузері
    $w.AddHandler([System.Windows.Documents.Hyperlink]::RequestNavigateEvent, [System.Windows.Navigation.RequestNavigateEventHandler] {
        param($s, $e)
        try { Start-Process $e.Uri.AbsoluteUri } catch {}
        $e.Handled = $true
    })
    # закриття вікна = сховати (у майстрі — вважати, що налаштування завершено)
    $w.Add_Closing({
        param($s, $e)
        if ($script:Exiting) { return }
        $e.Cancel = $true
        if ($script:SetWizard) { Finish-Wizard } else { try { [void](Save-CalendarFromUI) } catch {}; $script:SetWin.Hide() }
    })

    for ($k = 1; $k -le 6; $k++) { (SetCtl "N$k").Add_Click({ param($s, $e) Show-SetPage ([int]$s.Tag) }) }
    (SetCtl 'NWizard').Add_Click({ Open-Settings 0 -Wizard })
    (SetCtl 'BtnNext').Add_Click({ if ($script:SetPage -ge 7) { Finish-Wizard } else { Show-SetPage ($script:SetPage + 1) } })
    (SetCtl 'BtnBack').Add_Click({ if ($script:SetPage -gt 0) { Show-SetPage ($script:SetPage - 1) } })
    (SetCtl 'BtnSkip').Add_Click({ Finish-Wizard })
    (SetCtl 'BtnClose').Add_Click({ try { [void](Save-CalendarFromUI) } catch {}; $script:SetWin.Hide() })

    (SetCtl 'BtnBatRefresh').Add_Click({ $script:FileCache = @{}; Refresh-All; Update-BatteryStatusUI })

    (SetCtl 'RbIsland').Add_Checked({ if (-not $script:SetLoading) { Set-Mode 'strip' $true; (SetCtl 'CbPin').IsEnabled = $false } })
    (SetCtl 'RbCard').Add_Checked({ if (-not $script:SetLoading) { Set-Mode 'full' $true; (SetCtl 'CbPin').IsEnabled = $true } })
    (SetCtl 'CbTop').Add_Click({ Set-Topmost ([bool](SetCtl 'CbTop').IsChecked) })
    (SetCtl 'CbPin').Add_Click({ Set-Pinned ([bool](SetCtl 'CbPin').IsChecked) })
    (SetCtl 'SlOpacity').Add_ValueChanged({
        if ($script:SetLoading) { return }
        $v = [Math]::Round((SetCtl 'SlOpacity').Value)
        (SetCtl 'LblOpacity').Text = "$v%"
        $script:Settings.BgOpacity = $v / 100.0
        Save-Settings
        $Card.Background = Brush (Get-CardBg ($script:Settings.Mode -eq 'strip'))
    })
    (SetCtl 'RbTrayIcon').Add_Checked({ if (-not $script:SetLoading) { Set-TrayStyle 'icon' } })
    (SetCtl 'RbTrayNum').Add_Checked({ if (-not $script:SetLoading) { Set-TrayStyle 'number' } })

    (SetCtl 'BtnCity').Add_Click({ Find-WeatherCity ("$((SetCtl 'TbCity').Text)".Trim()) })
    (SetCtl 'TbCity').Add_KeyDown({ param($s, $e) if ($e.Key -eq 'Return') { Find-WeatherCity ("$((SetCtl 'TbCity').Text)".Trim()) } })

    (SetCtl 'BtnCalTest').Add_Click({
        $changed = Save-CalendarFromUI
        if (-not $changed) { $script:CalData = @{}; Refresh-Calendar }
        Update-CalStatusUI
    })

    (SetCtl 'BtnDiscord').Add_Click({
        $id = "$((SetCtl 'TbDiscordId').Text)".Trim(); $sec = "$((SetCtl 'PbDiscordSecret').Password)".Trim()
        if (-not $id -or -not $sec) { (SetCtl 'DiscordStatus').Text = 'Встав і Client ID, і Client Secret.'; return }
        Connect-DiscordWith $id $sec
        (SetCtl 'DiscordStatus').Text = 'Підключаюся… Зараз у Discord з''явиться вікно — натисни «Авторизувати».'
    })
    (SetCtl 'BtnDiscordOff').Add_Click({ Disconnect-Discord; (SetCtl 'PbDiscordSecret').Password = ''; (SetCtl 'DiscordStatus').Text = 'Не підключено.' })

    (SetCtl 'CbAuto').Add_Click({ Set-Autostart ([bool](SetCtl 'CbAuto').IsChecked) })
    (SetCtl 'CbDesktop').Add_Click({ Set-DesktopShortcut ([bool](SetCtl 'CbDesktop').IsChecked) })
}

function Find-WeatherCity([string]$name) {
    if (-not $name) { return }
    if ($script:SetWin) { (SetCtl 'CityStatus').Text = 'Шукаю…' }
    $url = 'https://geocoding-api.open-meteo.com/v1/search?count=1&language=uk&name=' + [Uri]::EscapeDataString($name)
    Start-Download $url {
        param($json)
        $g = $json | ConvertFrom-Json
        if (-not $g.results) { if ($script:SetWin) { (SetCtl 'CityStatus').Text = 'Місто не знайдено. Спробуй латиницею, наприклад: Riihimaki.' }; return }
        $r = $g.results[0]
        $script:Settings.WeatherLat = [double]$r.latitude
        $script:Settings.WeatherLon = [double]$r.longitude
        $script:Settings.WeatherCity = "$($r.name)"
        Save-Settings
        Refresh-Weather
        if ($script:SetWin) {
            $country = if ($r.country) { ", $($r.country)" } else { '' }
            (SetCtl 'CityStatus').Text = "$([char]0x2713) Збережено: $($r.name)$country"
            (SetCtl 'TbCity').Text = "$($r.name)"
        }
    } { if ($script:SetWin) { (SetCtl 'CityStatus').Text = 'Не вдалося знайти місто (немає інтернету?).' } }
}

function Connect-DiscordWith([string]$id, [string]$sec) {
    $script:Settings.DiscordClientId = $id; $script:Settings.DiscordClientSecret = $sec
    $script:Settings.DiscordToken = ''; $script:Settings.DiscordRefresh = ''
    Save-Settings
    $s = $script:DSync
    $s.ClientId = $id; $s.ClientSecret = $sec; $s.Token = ''; $s.Refresh = ''; $s.LastError = ''; $s.Status = ''
    $s.WantAuthorize = $true
    $script:ReportedError = $true   # помилки показуються у вікні налаштувань
    try { if ($s.Pipe) { $s.Pipe.Dispose() } } catch {}
}

# статус Discord і календаря у відкритому вікні налаштувань
$script:SetStatusTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:SetStatusTimer.Interval = [TimeSpan]::FromMilliseconds(500)
$script:SetStatusTimer.Add_Tick({
    try {
        if ($script:SetWin -and $script:SetWin.IsVisible) {
            (SetCtl 'DiscordStatus').Text = Get-DiscordStatusText
            if ($script:SetPage -eq 4) { Update-CalStatusUI }
        }
    } catch {}
})

# --- спрощене меню трею: усе інше — у вікні налаштувань ---
$miSettings = New-Object System.Windows.Forms.ToolStripMenuItem('Налаштування…')
$miSettings.Font = New-Object System.Drawing.Font($miSettings.Font, [System.Drawing.FontStyle]::Bold)
$miSettings.Add_Click({ Open-Settings 2 })
$TrayMenu.Items.Clear()
[void]$TrayMenu.Items.Add($miSettings)
[void]$TrayMenu.Items.Add($miShow)
[void]$TrayMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$TrayMenu.Items.Add($miExit)

# меню на самому віджеті
$wSettings = New-Object System.Windows.Controls.MenuItem
$wSettings.Header = 'Налаштування…'; $wSettings.FontWeight = 'SemiBold'
$wSettings.Add_Click({ Open-Settings 2 })
$ctx.Items.Insert(0, (New-Object System.Windows.Controls.Separator))
$ctx.Items.Insert(0, $wSettings)

# ================== АВТОЗАПУСК ==================
$RunKey  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunName = 'RazerBatteryWidget'
$Launcher = Join-Path $Root 'Запустити.vbs'
function Get-Autostart { try { $null -ne (Get-ItemProperty -Path $RunKey -Name $RunName -ErrorAction Stop).$RunName } catch { $false } }
function Set-Autostart([bool]$on) {
    try {
        if ($on) {
            $t = Get-LaunchTarget
            Set-ItemProperty -Path $RunKey -Name $RunName -Value (('"' + $t.Target + '" ' + $t.Args).Trim())
        }
        else { Remove-ItemProperty -Path $RunKey -Name $RunName -ErrorAction SilentlyContinue }
    } catch {}
    $miAuto.Checked = Get-Autostart
}
if (-not [bool]$script:Settings.AutostartSet) {
    Set-Autostart $true
    $script:Settings.AutostartSet = $true
    Save-Settings
}
$miAuto.Checked = Get-Autostart
# автозапуск має вказувати на актуальний запускач (exe замість vbs)
try {
    if (Get-Autostart) {
        $cur = "$((Get-ItemProperty -Path $RunKey -Name $RunName).$RunName)"
        if ((Test-Path $AppExe) -and $cur -notlike "*$AppName.exe*") { Set-Autostart $true }
    }
} catch {}
$miAuto.Add_Click({ Set-Autostart $miAuto.Checked })

# ================== СТАРТ ==================
[System.Windows.Forms.Application]::EnableVisualStyles()
$script:App = New-Object System.Windows.Application
$script:App.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown
$script:App.Add_DispatcherUnhandledException({
    param($sender, $e)
    Write-Log ("Помилка під час роботи: " + $e.Exception.ToString())
    $e.Handled = $true
})

$script:LastMinute = -1
function Update-Clock {
    $now = [DateTime]::Now
    $t = $now.ToString('HH:mm'); $StripClock.Text = $t; $HeaderClock.Text = $t
    $d = $now.ToString('dddd, d MMMM', $UkCulture)
    $DateText.Text = $d.Substring(0, 1).ToUpper() + $d.Substring(1)
    if ($now.Minute -ne $script:LastMinute) {
        $first = ($script:LastMinute -eq -1)
        $script:LastMinute = $now.Minute
        if (-not $first) { try { Render-Calendar } catch {} }
    }
}
try { [System.Threading.SynchronizationContext]::SetSynchronizationContext((New-Object System.Windows.Threading.DispatcherSynchronizationContext([System.Windows.Threading.Dispatcher]::CurrentDispatcher))) } catch {}
Render-Calendar
Update-Clock
$script:ClockTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:ClockTimer.Interval = [TimeSpan]::FromSeconds(1)
$script:ClockTimer.Add_Tick({ Update-Clock })
$script:ClockTimer.Start()

Refresh-All
$startMode = if ("$($script:Settings.Mode)" -eq 'full') { 'full' } else { 'strip' }
Set-Mode $startMode $false
$script:LayoutReady = $true

$script:Timer = New-Object System.Windows.Threading.DispatcherTimer
$script:Timer.Interval = [TimeSpan]::FromSeconds($RefreshSec)
$script:Timer.Add_Tick({ try { Refresh-All } catch {} })
$script:Timer.Start()
$script:NetTimer.Start()
$script:MicTimer.Start()

$Window.Left = -10000; $Window.Top = -10000
$Window.Show()
if (-not [bool]$script:Settings.Visible) { $Window.Hide() }

$script:SetStatusTimer.Start()
try {
    if (-not [bool]$script:Settings.FirstRunDone) { Open-Settings 0 -Wizard }
    elseif ($OpenSettings) { Open-Settings 2 }
} catch { Write-Log "Вікно налаштувань: $_" }
Write-Log 'Віджет запущено.'
[void]$script:App.Run()
