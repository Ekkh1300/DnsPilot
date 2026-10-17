# Diagnostic: list every UIA element whose name mentions DnsPilot, with class and rectangle,
# plus the taskbar's own buttons, so the tray-verification script can find the real icon.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement

Write-Output '--- elements whose name contains DnsPilot ---'
$nameCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, 'DnsPilot, click to open')
$els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
foreach ($el in $els) {
    $r = $el.Current.BoundingRectangle
    Write-Output ("name='{0}' class='{1}' type={2} rect=({3},{4} {5}x{6})" -f `
        $el.Current.Name, $el.Current.ClassName, $el.Current.ControlType.ProgrammaticName,
        [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
}

Write-Output ''
Write-Output '--- taskbar / tray area buttons (class Shell_TrayWnd descendants, top 60) ---'
$trayCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'Shell_TrayWnd')
$tray = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $trayCond)
if (-not $tray) { Write-Output 'no Shell_TrayWnd child'; exit }
$btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
$btns = $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
$i = 0
foreach ($b in $btns) {
    $r = $b.Current.BoundingRectangle
    Write-Output ("name='{0}' class='{1}' rect=({2},{3} {4}x{5})" -f `
        $b.Current.Name, $b.Current.ClassName, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
    $i++
    if ($i -ge 60) { break }
}
