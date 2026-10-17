# Drives the running app the way a user applies a resolver, end to end, and checks Windows
# state before and after. Two intended mutations only, both verified and both undone:
#
#   1. search for Cloudflare, select its row, press "Use this server", read the status line,
#      run `netsh interface ipv4 show dnsservers` and check 1.1.1.1 arrived;
#   2. the same for UltraDNS, and check the original 204.69.234.1 went back.
#
# The relaunch is elevated for the same reason Capture-Screenshots says: the app runs High
# integrity by manifest, and UIA cannot see a higher-integrity process's children.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File Verify-Apply.ps1
# Log:    E:\dnspilot\tools\apply-verify.log

$ErrorActionPreference = 'Stop'

$logFile = 'E:\dnspilot\tools\apply-verify.log'
function Say([object]$text) {
    $line = ($text | Out-String).TrimEnd()
    # Write-Host, not Write-Output: inside the helpers below any Write-Output would ride out
    # of the function as part of its return value and turn a single row object into an array.
    Write-Host $line
    Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
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

$exe = 'E:\dnspilot\DnsPilot\bin\Release\net8.0-windows\DnsPilot.exe'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Start-Fresh {
    Get-Process DnsPilot -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill() } catch { }
        $_.WaitForExit(3000) | Out-Null
    }
    Start-Process $exe | Out-Null
    $deadline = [DateTime]::Now.AddSeconds(15)
    while ([DateTime]::Now -lt $deadline) {
        $p = Get-Process DnsPilot -ErrorAction SilentlyContinue |
             Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($p) {
            Start-Sleep -Milliseconds 900   # first layout, adapter list, presets
            return $p.MainWindowHandle
        }
        Start-Sleep -Milliseconds 150
    }
    throw "DnsPilot window did not appear"
}

$root = [System.Windows.Automation.AutomationElement]::RootElement

function Get-AppWindow {
    $p = Get-Process DnsPilot -ErrorAction SilentlyContinue |
         Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { throw "app is not running" }
    $win = $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NativeWindowHandleProperty,
            $p.MainWindowHandle.ToInt32())))
    if ($null -eq $win) { throw "could not reach the window through UI Automation" }
    return $win
}

function Invoke-ApplyRetry([System.Windows.Automation.AutomationElement]$win) {
    # "The parameter is incorrect" from a captured element after the list has re-laid is a
    # stale-peer problem, not a refused click of the app's: re-find through a fresh window
    # root and try again. Three retries cover every re-layout observed here.
    for ($i = 0; $i -lt 3; $i++) {
        try {
            if ($i -gt 0) { $win = Get-AppWindow; Start-Sleep -Milliseconds 300 }
            $apply = Find-ById $win 'Apply'
            ($apply.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            Say ("clicked apply (attempt {0})" -f ($i + 1))
            return
        } catch {
            Say ("invoke attempt {0} failed: {1}" -f ($i + 1), $_.Exception.GetBaseException().Message)
            Start-Sleep -Milliseconds 400
        }
    }
    throw 'could not invoke the apply button'
}

function Find-ById([System.Windows.Automation.AutomationElement]$win, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -eq $el) { throw "no control with AutomationId '$id'" }
    return $el
}

function Set-Search([System.Windows.Automation.AutomationElement]$win, [string]$term) {
    $box = Find-ById $win 'SearchField'
    ($box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($term)
    Start-Sleep -Milliseconds 600   # the VM filters on the text change
    Say ("search set to '{0}'" -f $term)
}

function Select-Row([System.Windows.Automation.AutomationElement]$win, [string]$term) {
    $list = Find-ById $win 'ServerList'
    $itemCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
    Say ("rows realised under the list: {0}" -f $items.Count)
    if ($items.Count -eq 0) { throw "the list has no realised rows" }

    # Row items may not carry the server's words as their own Name, so both paths are tried:
    # the item's Name, and the name of any text inside its subtree.
    foreach ($r in $items) {
        $n = $r.Current.Name
        if ($n -and $n.Contains($term)) { return $r }
        $textCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)
        $texts = $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)
        foreach ($t in $texts) {
            if ($t.Current.Name -eq $term) { return $r }
        }
    }
    # Diagnostics rather than a guess: what the rows actually report, so the matching rule
    # can be corrected against the machine's real names.
    foreach ($r in $items) {
        $names = @()
        $textCond2 = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)
        foreach ($t in $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond2)) {
            $nm = $t.Current.Name
            if (-not $nm) { $nm = '?' }
            $names += $nm
        }
        Say ("unmatched row: '{0}' texts: {1}" -f $r.Current.Name, ($names -join ' | '))
    }
    throw "row '$term' not found among $($items.Count) rows"
}

function Select-Item([System.Windows.Automation.AutomationElement]$row) {
    ($row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 350
    Say ("selected: '{0}'" -f $row.Current.Name)
}

function Invoke-Apply([System.Windows.Automation.AutomationElement]$win) {
    $apply = Find-ById $win 'Apply'
    ($apply.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Say ("clicked apply")
}

function Wait-Status([System.Windows.Automation.AutomationElement]$win, [string[]]$prefixes) {
    $status = Find-ById $win 'StatusText'
    $deadline = [DateTime]::Now.AddSeconds(8)
    while ([DateTime]::Now -lt $deadline) {
        $t = $status.Current.Name
        if ($t) {
            foreach ($pfx in $prefixes) {
                if ($t.StartsWith($pfx)) {
                    Say ("status: {0}" -f $t)
                    return $t
                }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "status line never reached any of: $($prefixes -join ' | ') (last: '$($status.Current.Name)')"
}

function Show-Dns([string]$label) {
    $out = & netsh interface ipv4 show dnsservers 'name=vEthernet (Default Switch)' 2>&1
    Say ("netsh show ({0}): exit {1}" -f $label, $LASTEXITCODE)
    Say ($out | Out-String)
    return ($out -join "`n")
}

try {
    Say "starting fresh app"
    [void](Start-Fresh)
    $win = Get-AppWindow

    Say "--- before: current state ---"
    $before = Show-Dns 'before'

    # 1. Cloudflare
    Set-Search $win 'Cloudflare'
    $row = Select-Row $win 'Cloudflare'
    Select-Item $row
    Invoke-Apply $win
    $st = Wait-Status $win @('Now using', 'Windows refused')
    Start-Sleep -Milliseconds 500
    $after1 = Show-Dns 'after Cloudflare'
    $ok1 = $after1 -match '1\.1\.1\.1'
    Say ("STEP1 Cloudflare: status '{0}'; netsh shows 1.1.1.1 = {1}" -f $st, $ok1)

    # 2. back to UltraDNS
    Set-Search $win 'UltraDNS'
    $row2 = Select-Row $win 'UltraDNS'
    Select-Item $row2
    Invoke-ApplyRetry $win
    $st2 = Wait-Status $win @('Now using', 'Windows refused')
    Start-Sleep -Milliseconds 500
    $after2 = Show-Dns 'after UltraDNS'
    $ok2 = $after2 -match '204\.69\.234\.1'
    Say ("STEP2 UltraDNS: status '{0}'; netsh shows 204.69.234.1 = {1}" -f $st2, $ok2)

    if ($ok1 -and $ok2) {
        Say "RESULT: apply works end to end and reverts cleanly"
        exit 0
    }
    Say "RESULT: apply is broken - see the steps above"
    exit 1
}
catch {
    Say ("FAIL: {0}" -f $_.Exception.Message)
    exit 1
}
finally {
    Get-Process DnsPilot -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill() } catch { }
    }
}
