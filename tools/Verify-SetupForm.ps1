# Photographs the SETUP program's own window, in English and in Persian (/LANG= forces the
# language; the machine's real UI language is never touched). This is the evidence for the
# installer-side fixes: the app icon in the title bar and header, the DnsPilot wording in the
# header, the brand indigo, and the properly mirrored RTL layout for Persian.
#
# Launching the setup exe only opens its form - nothing is installed until Install is pressed,
# and the script kills both instances afterwards.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File Verify-SetupForm.ps1
# Output: E:\dnspilot\docs\debug\setup\setup-{en,fa}.png

$ErrorActionPreference = 'Stop'

$logFile = 'E:\dnspilot\tools\setup-verify.log'
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
    if (Test-Path -LiteralPath $logFile) {
        Write-Output "--- elevated run output ---"
        Get-Content -LiteralPath $logFile -Encoding UTF8 | Write-Output
    } else {
        Write-Output "no output arrived from the elevated run (exit code $($p.ExitCode))"
    }
    exit $p.ExitCode
}

$setup = 'E:\dnspilot\DnsPilot\dist\DnsPilot-Setup.exe'
$outDir = 'E:\dnspilot\docs\debug\setup'

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Native -Name Win -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, int nFlags);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
"@

function Shoot-Setup([string]$lang, [string]$file) {
    Say ("launching setup with /LANG={0}" -f $lang)
    Get-Process 'DnsPilot-Setup' -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill(); $_.WaitForExit(3000) } catch { }
    }
    Start-Process -FilePath $setup -ArgumentList "/LANG=$lang" | Out-Null
    Start-Sleep -Seconds 3

    $proc = Get-Process 'DnsPilot-Setup' -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $proc) { throw "setup window did not appear (lang=$lang)" }

    $h = $proc.MainWindowHandle
    $rect = New-Object Native.Win+RECT
    [void][Native.Win]::GetWindowRect($h, [ref]$rect)
    $w = $rect.Right - $rect.Left; $h2 = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h2 -le 0) { throw "setup window reported ${w}x${h2}" }

    # PrintWindow grabs the whole window - client area and title bar with its icon - which is
    # the pair of surfaces this verification is about.
    $bmp = New-Object System.Drawing.Bitmap($w, $h2)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][Native.Win]::PrintWindow($h, $hdc, 2)   # PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    Say ("captured {0} ({1}x{2})" -f $file, $w, $h2)
}

try {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    Shoot-Setup 'en' (Join-Path $outDir 'setup-en.png')
    Shoot-Setup 'fa' (Join-Path $outDir 'setup-fa.png')
    Say "done"
    exit 0
}
catch {
    Say ("FAIL: {0}" -f $_.Exception.Message)
    Get-Process 'DnsPilot-Setup' -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill() } catch { }
    }
    exit 1
}
finally {
    Get-Process 'DnsPilot-Setup' -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill(); $_.WaitForExit(3000) } catch { }
    }
}
