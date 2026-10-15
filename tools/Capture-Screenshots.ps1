# Captures the four states DnsPilot ships in: light and dark, English and Persian.
#
# The app keeps theme and language in memory only - there is no settings file - so the only
# honest way to photograph a state is to reach it the way a user does, by pressing the buttons
# in the running window. That is what the two AutomationProperties.AutomationId values in
# MainWindow.xaml are for: clicking by coordinate would break the moment the window is resized
# or a language change reflows the header, and clicking by name survives both.
#
# The script relaunches itself elevated. DnsPilot carries a requireAdministrator manifest, so it
# always runs at High integrity, and Windows blocks UI Automation from reaching into a
# higher-integrity process - the window is visible but reports zero descendants, which looks
# exactly like the automation ids are missing. Running at the same integrity as the app is what
# makes the search work.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File Capture-Screenshots.ps1
# Output: E:\dnspilot\docs\screenshots\dns-pilot-{light,dark}-{en,fa}.png

$ErrorActionPreference = 'Stop'

# Re-enter elevated, once, before doing anything that needs to see the app's controls.
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "not elevated - relaunching as administrator..."
    $p = Start-Process -FilePath 'powershell.exe' `
        -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"" `
        -Verb RunAs -PassThru -Wait
    exit $p.ExitCode
}

$exe      = 'E:\dnspilot\DnsPilot\bin\Release\net8.0-windows\DnsPilot.exe'
$outDir   = 'E:\dnspilot\docs\screenshots'
$apRoot   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# PrintWindow reads a window's own rendering rather than whatever happens to be on top of it,
# so a stray foreground window cannot end up in the shot. PW_RENDERFULLCONTENT (2) is what
# makes it capture GPU-composited content such as WPF's; without it the capture comes back
# blank for exactly this kind of app.
#
# The window handle comes from Get-Process.MainWindowHandle rather than from FindWindow. That
# is not a shortcut: FindWindow(null, "DnsPilot") returns 0 with ERROR_INVALID_NAME against a
# window that plainly exists and is plainly titled DnsPilot, and polling the process is both
# simpler and free of that quirk.
Add-Type -Namespace Native -Name Win -MemberDefinition @"
[DllImport("user32.dll", SetLastError = true)]
public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

[DllImport("user32.dll", SetLastError = true)]
public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

[DllImport("user32.dll")]
public static extern bool SetForegroundWindow(IntPtr hWnd);

[DllImport("user32.dll")]
public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }
"@

function New-WindowCapture([IntPtr]$hWnd) {
    $rect = New-Object Native.Win+RECT
    [void][Native.Win]::GetWindowRect($hWnd, [ref]$rect)
    $w = $rect.Right  - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { throw "window reported a ${w}x${h} rectangle" }

    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    try   { [void][Native.Win]::PrintWindow($hWnd, $hdc, 2) }
    finally { $g.ReleaseHdc($hdc); $g.Dispose() }
    return $bmp
}

function Find-Button([System.Windows.Automation.AutomationElement]$root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -eq $el) { throw "no control with AutomationId '$id'" }
    return $el
}

function Invoke-Button([System.Windows.Automation.AutomationElement]$el) {
    $pattern = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 600   # let the theme dictionary swap and the window repaint
}

function Wait-ForWindow([int]$ms) {
    $deadline = [DateTime]::Now.AddMilliseconds($ms)
    while ([DateTime]::Now -lt $deadline) {
        # MainWindowHandle is 0 until the window has been created and shown, so polling it also
        # waits for the window rather than merely for the process.
        $p = Get-Process DnsPilot -ErrorAction SilentlyContinue |
             Where-Object { $_.MainWindowHandle -ne 0 } |
             Select-Object -First 1
        if ($p) { return $p.MainWindowHandle }
        Start-Sleep -Milliseconds 150
    }
    throw "DnsPilot window did not appear within $ms ms"
}

if (-not (Test-Path $exe)) {
    Write-Host "missing: $exe"
    Write-Host "building Release first..."
    & (Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe') build `
        'E:\dnspilot\DnsPilot\DnsPilot.csproj' -c Release -v q -nologo | Out-Null
    if (-not (Test-Path $exe)) { throw "still missing after build: $exe" }
}

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# Every capture starts from a fresh process, so no state from the previous shot carries over.
function Start-Fresh {
    Get-Process DnsPilot -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill() } catch { }
        $_.WaitForExit(3000) | Out-Null
    }
    Start-Process $exe | Out-Null
    $h = Wait-ForWindow 15000
    Start-Sleep -Milliseconds 900   # first layout pass, adapter list, initial paint
    return $h
}

$shots = @(
    @{ Name = 'en-light'; Lang = $false; Theme = $false },
    @{ Name = 'en-dark';  Lang = $false; Theme = $true  },
    @{ Name = 'fa-light'; Lang = $true;  Theme = $false },
    @{ Name = 'fa-dark';  Lang = $true;  Theme = $true  }
)

$root = [System.Windows.Automation.AutomationElement]::RootElement

foreach ($shot in $shots) {
    $h = Start-Fresh

    [void][Native.Win]::ShowWindow($h, 9)          # SW_RESTORE
    [void][Native.Win]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 300

    # NativeWindowHandleProperty is typed Int32, so the handle goes across as Int32 rather than
    # Int64 - a window handle fits in 32 bits on both architectures, and WPF's automation layer
    # insists on it.
    $win = $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NativeWindowHandleProperty,
            $h.ToInt32())))
    if ($null -eq $win) { throw "could not reach the window through UI Automation" }

    # Dark first when the theme is wanted: the app starts light, so pressing once reaches dark.
    if ($shot.Theme) { Invoke-Button (Find-Button $win 'ThemeSwitch') }
    if ($shot.Lang)  { Invoke-Button (Find-Button $win 'LanguageSwitch') }

    Start-Sleep -Milliseconds 400
    $bmp = New-WindowCapture $h
    # Read the dimensions before disposing: a disposed Bitmap throws on Width.
    $w = $bmp.Width
    $hgt = $bmp.Height
    $path = Join-Path $outDir ("dns-pilot-{0}.png" -f $shot.Name)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    Write-Host ("{0,-10} {1,6:N0} KB  {2}x{3}" -f $shot.Name,
        ((Get-Item $path).Length / 1KB), $w, $hgt)
}

Get-Process DnsPilot -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

Write-Host ""
Write-Host "saved to $outDir"
