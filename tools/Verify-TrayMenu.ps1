# Drives the running DnsPilot at its own tray icon, opens the tray menu with a real right-click,
# and photographs it - dark+English, and dark+Persian - so the fix for "the tray menu ignored
# the theme and the language" is checked against the real app, not a replica.
#
# The tray menu lives outside the window's tree (it is opened by hand from the notification
# icon), so the only path to it a scripted client has is the user's own gesture: a right-click
# on the notification icon, then reading the menu that opens back from the screen. Same
# integrity rule as Capture-Screenshots.ps1: DnsPilot is requireAdministrator, and UIA cannot
# see into a higher-integrity process, so the script relaunches itself elevated first.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File Verify-TrayMenu.ps1
# Output: E:\dnspilot\docs\debug\tray-menu\tray-menu-dark-{en,fa}.png

$ErrorActionPreference = 'Stop'

# The elevated re-entry runs in its own console, whose output the medium-integrity caller never
# sees; everything is mirrored to this log and the caller prints it after Wait returns.
# (The UAC prompt itself is the user's click.)
$logFile = 'E:\dnspilot\tools\tray-verify.log'
function Say([string]$text) {
    Write-Output $text
    Add-Content -LiteralPath $logFile -Value $text -Encoding UTF8
}
if (Test-Path -LiteralPath $logFile) { Remove-Item -LiteralPath $logFile -Force -ErrorAction SilentlyContinue }

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "not elevated - relaunching as administrator..."
    try {
        $p = Start-Process -FilePath 'powershell.exe' `
            -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"" `
            -Verb RunAs -PassThru -Wait
    } catch {
        Write-Output ("UAC prompt was declined or failed: {0}" -f $_.Exception.Message)
        exit 2
    }
    if ((Test-Path -LiteralPath $logFile) -and ($p.ExitCode -ne $null)) {
        Write-Output "--- elevated run output ---"
        Get-Content -LiteralPath $logFile -Encoding UTF8 | Write-Output
    } else {
        Write-Output "no output arrived from the elevated run (exit code $($p.ExitCode))"
    }
    exit $p.ExitCode
}

# The tray icon's tooltip text is what UIA sees as its name, and the app sets that text in the
# running language - so after switching to Persian the icon is no longer "DnsPilot, click to
# open" but the Persian sentence. Spelling Persian here is fragile (the Arabic and Persian
# forms of yeh look identical in a diff), so the search matches the product prefix instead.

$exe    = 'E:\dnspilot\DnsPilot\bin\Release\net8.0-windows\DnsPilot.exe'
$outDir = 'E:\dnspilot\docs\debug\tray-menu'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -Namespace Native -Name Win -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtra);
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtra);
"@

# --- building blocks ------------------------------------------------------------------------

function Start-Fresh {
    Get-Process DnsPilot -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill(); $_.WaitForExit(3000) } catch { }
    }
    Start-Process -FilePath $exe | Out-Null
    Start-Sleep -Seconds 3
    $p = Get-Process DnsPilot -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { throw 'DnsPilot window did not appear' }
    return $p
}

function Find-Button([System.Windows.Automation.AutomationElement]$root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-Button($el) {
    ($el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 800
}

function Close-Menu {
    # ESC closes an open ContextMenu, exactly as it closes one that the user opened by hand.
    [void][Native.Win]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)               # ESC down
    [void][Native.Win]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)               # ESC up
    Start-Sleep -Milliseconds 500
}

function Find-OpenMenu([int]$expectedPid) {
    # An open ContextMenu is a top-level window of the app; among that process's windows, the
    # one that matters is the element that actually holds the menu items it was opened with.
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $expectedPid)
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pCond)) {
        if (-not $el) { continue }
        $itemCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::MenuItem)
        $item = $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
        if ($item) { return $el }
    }
    return $null
}

function Shoot($el, [string]$file) {
    # Screenshot the region of the desktop where the menu opened - a screen capture, because
    # the menu is what the user's eye sees at that spot, border and shadow included.
    $r = $el.Current.BoundingRectangle
    if ($r -eq [System.Windows.Rect]::Empty) { throw 'open menu reported an empty rectangle' }

    $pad = 16
    $x = [int][Math]::Max(0, $r.X - $pad)
    $y = [int][Math]::Max(0, $r.Y - $pad)
    $w = [int][Math]::Ceiling($r.Width)  + 2 * $pad
    $h = [int][Math]::Ceiling($r.Height) + 2 * $pad
    if ($w -lt 60 -or $h -lt 40) { throw ("menu bitmap looked wrong: {0}x{1}" -f $w, $h) }

    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Say ("captured {0} ({1}x{2} at {3},{4})" -f $file, $w, $h, $x, $y)
}

function Find-TrayIconRect {
    # Returns the rectangle of the notification icon that can actually be clicked. The icon's
    # UIA name is the tooltip the app sets on it, in the app's current language - so the search
    # matches by the "DnsPilot" prefix over the two homes a tray icon can live in: the taskbar
    # strip and the overflow flyout. A flyout's elements go empty the moment it closes, so the
    # rectangle is captured in the same breath as the click that follows.
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)

    foreach ($attempt in 1, 2) {
        # Home 1: the taskbar itself. Home 2: the overflow flyout window, on either the Win10
        # or the Win11 island class. Each is scanned for buttons whose name starts with
        # DnsPilot and whose rectangle is really on screen.
        $homes = @()
        foreach ($className in @('Shell_TrayWnd', 'NotifyIconOverflowWindow', 'TopLevelWindowForOverflowXamlIsland')) {
            $c = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ClassNameProperty, $className)
            $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
            if ($el) { $homes += $el }
        }

        foreach ($trayHome in $homes) {
            foreach ($b in $trayHome.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
                $n = $b.Current.Name
                if (-not $n -or -not $n.StartsWith('DnsPilot')) { continue }
                # The taskbar's own application button is also named "DnsPilot - N running
                # window"; right-clicking THAT opens the shell jump list, not the app's tray
                # menu. The tray icon lives in the SystemTray classes instead.
                if ($b.Current.ClassName -and $b.Current.ClassName -match 'TaskListButton') { continue }
                $r = $b.Current.BoundingRectangle
                if ($r -ne [System.Windows.Rect]::Empty -and $r.Width -gt 0 -and $r.Height -gt 0) {
                    Say ("live icon found ('{0}') at ({1},{2}) {3}x{4}" -f `
                        $n, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
                    return $r
                }
            }
        }

        Say ("no clickable icon yet (attempt {0}); opening the hidden-icons flyout" -f $attempt)
        $cc = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'Show Hidden Icons')
        $chevron = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cc)
        if ($chevron) {
            ($chevron.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            Start-Sleep -Milliseconds 1200
        } else {
            Say "no 'Show Hidden Icons' button was found on this taskbar"
        }
    }

    # Last resort: a whole-desktop button scan. Slow, but it cannot miss a promoted icon
    # however the taskbar organises itself.
    foreach ($b in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
        $n = $b.Current.Name
        if (-not $n -or -not $n.StartsWith('DnsPilot')) { continue }
        $r = $b.Current.BoundingRectangle
        if ($r -ne [System.Windows.Rect]::Empty -and $r.Width -gt 0 -and $r.Height -gt 0 -and $r.Height -le 60) {
            Say ("live icon found (desktop scan) at ({0},{1})" -f [int]$r.X, [int]$r.Y)
            return $r
        }
    }
    throw 'tray icon not found, or only with an empty rectangle'
}

function Open-TrayMenu {
    $r = Find-TrayIconRect
    $cx = [int]($r.X + $r.Width  / 2)
    $cy = [int]($r.Y + $r.Height / 2)

    # A genuine right-click: move, press, release. The app opens its menu at the cursor.
    [void][Native.Win]::SetCursorPos($cx, $cy)
    Start-Sleep -Milliseconds 250
    Say ("right-clicking at {0},{1}" -f $cx, $cy)
    [Native.Win]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)   # RIGHTDOWN
    Start-Sleep -Milliseconds 80
    [Native.Win]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)   # RIGHTUP
    Start-Sleep -Milliseconds 800
}

function Close-App {
    Get-Process DnsPilot -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill(); $_.WaitForExit(3000) } catch { }
    }
}

# --- the run --------------------------------------------------------------------------------

try {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null

    Say "starting fresh app instance"
    Start-Fresh
    $proc = Get-Process DnsPilot -ErrorAction Stop | Select-Object -First 1
    $pidNum = $proc.Id

    $root = [System.Windows.Automation.AutomationElement]::RootElement

    # State 1: dark, English (default is light, so one theme click is all it takes)
    Say ("app running (pid {0}); switching to dark" -f $pidNum)
    $theme = Find-Button $root 'ThemeSwitch'
    if (-not $theme) { throw "ThemeSwitch button not found" }
    Invoke-Button $theme
    Start-Sleep -Seconds 1

    Say "opening tray menu (dark, English)"
    Open-TrayMenu
    $menu = Find-OpenMenu $pidNum
    if (-not $menu) { Close-Menu; throw 'ContextMenu window was not found after the right-click' }
    Shoot $menu (Join-Path $outDir 'tray-menu-dark-en.png')
    Close-Menu

    # State 2: dark, Persian (one language click from here)
    Say "switching to Persian"
    $lang = Find-Button $root 'LanguageSwitch'
    if (-not $lang) { throw "LanguageSwitch button not found" }
    Invoke-Button $lang
    Start-Sleep -Seconds 1

    Say "opening tray menu (dark, Persian)"
    Open-TrayMenu
    $menu = Find-OpenMenu $pidNum
    if (-not $menu) { Close-Menu; throw 'ContextMenu window was not found after the right-click (Persian)' }
    Shoot $menu (Join-Path $outDir 'tray-menu-dark-fa.png')
    Close-Menu

    Say "done"
    exit 0
}
catch {
    Say ("FAIL: {0}" -f $_.Exception.Message)
    Close-App
    exit 1
}
