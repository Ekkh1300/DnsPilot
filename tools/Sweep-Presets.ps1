# Measures every resolver in presets.json so the list can be pruned of servers that no longer
# answer. The list was converted from DNS Jumper's INI, which is extensive rather than curated, and
# it still carries providers that retired their public resolvers years ago. A dead entry is not
# harmless: it occupies a slot, and when one is applied the system falls back to the other and the
# user sees a resolution failure with nothing to explain it.
#
# Only ever sends queries. Changes nothing on the machine.

param(
    # One probe each, short timeout, to separate the dead from the merely slow without spending
    # three full timeouts on every address in a list that is mostly retired.
    [int]$ProbeTimeoutMs = 1500,

    # Three attempts on whatever answered, minimum kept - the same shape the app uses, so these
    # numbers are the numbers the app would show.
    [int]$FullTimeoutMs = 2500,
    [int]$Attempts      = 3,

    [string]$PresetsPath = 'E:\dnspilot\DnsPilot\Data\presets.json',
    [string]$OutPath     = 'E:\dnspilot\tools\sweep-results.json'
)

$ErrorActionPreference = 'Stop'

# example.com rather than a connectivity-probe name: cached by every resolver, so the reading is
# about the round trip, and no ISP has a reason to intercept it.
$ProbeName = 'example.com'

$AnyV4 = [System.Net.IPAddress]::Any
$AnyV6 = [System.Net.IPAddress]::IPv6Any


function New-DnsQueryBytes {
    param([string]$Name, [uint16]$QType, [uint16]$Id)

    $buf = New-Object System.Collections.Generic.List[byte]

    $buf.Add([byte](($Id -shr 8) -band 0xFF))
    $buf.Add([byte]($Id -band 0xFF))
    $buf.Add(0x01); $buf.Add(0x00)      # flags: standard query, recursion desired
    $buf.Add(0x00); $buf.Add(0x01)      # one question
    $buf.Add(0x00); $buf.Add(0x00)      # no answers
    $buf.Add(0x00); $buf.Add(0x00)      # no authority
    $buf.Add(0x00); $buf.Add(0x00)      # no additional

    foreach ($label in $Name.TrimEnd('.').Split('.', [StringSplitOptions]::RemoveEmptyEntries)) {
        $b = [System.Text.Encoding]::ASCII.GetBytes($label)
        $buf.Add([byte]$b.Count)
        foreach ($x in $b) { $buf.Add($x) }
    }

    $buf.Add(0x00)                      # root label ends the name
    $buf.Add([byte](($QType -shr 8) -band 0xFF))
    $buf.Add([byte]($QType -band 0xFF))
    $buf.Add(0x00); $buf.Add(0x01)      # class IN

    return $buf.ToArray()
}


function Measure-Dns {
    param([string]$Server, [string]$Name, [int]$TimeoutMs, [uint16]$QType = 1)

    $isV6   = $Server.Contains(':')
    $id     = [uint16](Get-Random -Minimum 1 -Maximum 65535)
    $query  = New-DnsQueryBytes -Name $Name -QType $QType -Id $id
    $bind   = $AnyV4
    if ($isV6) { $bind = $AnyV6 }

    $udp = $null
    try {
        if ($isV6) { $udp = New-Object System.Net.Sockets.UdpClient([System.Net.Sockets.AddressFamily]::InterNetworkV6) }
        else       { $udp = New-Object System.Net.Sockets.UdpClient }

        $udp.Client.ReceiveTimeout = $TimeoutMs
        $udp.Connect($Server, 53)

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        [void]$udp.Send($query, $query.Length)

        $remote = New-Object System.Net.IPEndPoint -ArgumentList $bind, 0
        $null = $udp.Receive([ref]$remote)
        $sw.Stop()

        return [int]$sw.ElapsedMilliseconds
    }
    catch { return $null }
    finally { if ($udp) { $udp.Close() } }
}


# ---------------------------------------------------------------- load the list

$data = Get-Content $PresetsPath -Raw -Encoding UTF8 | ConvertFrom-Json

$targets = @()
foreach ($g in $data.groups) {
    foreach ($s in $g.servers) {
        if ($s.primary) {
            $targets += [pscustomobject]@{
                Group = $g.group; Family = $g.family; Name = $s.name
                Country = $s.country; Address = $s.primary; Role = 'primary'
            }
        }
        if ($s.secondary) {
            $targets += [pscustomobject]@{
                Group = $g.group; Family = $g.family; Name = $s.name
                Country = $s.country; Address = $s.secondary; Role = 'secondary'
            }
        }
    }
}

$unique = @($targets | Select-Object -ExpandProperty Address -Unique)
Write-Host ("{0} entries in the list, {1} distinct addresses to measure" -f $targets.Count, $unique.Count)


# ---------------------------------------------------------------- phase 1: who answers at all

$timings = @{}
$done    = 0
foreach ($addr in $unique) {
    $timings[$addr] = Measure-Dns -Server $addr -Name $ProbeName -TimeoutMs $ProbeTimeoutMs
    $done++
    Write-Host ("`r  probe {0}/{1}" -f $done, $unique.Count) -NoNewline
}
Write-Host ""

$dead = @($unique | Where-Object { $timings[$_] -eq $null })
Write-Host ("{0} answered, {1} did not" -f ($unique.Count - $dead.Count), $dead.Count)


# ---------------------------------------------------------------- phase 2: how fast are the live ones

$live = @($unique | Where-Object { $timings[$_] -ne $null })
$done = 0
foreach ($addr in $live) {
    $best = $null
    for ($i = 0; $i -lt $Attempts; $i++) {
        $ms = Measure-Dns -Server $addr -Name $ProbeName -TimeoutMs $FullTimeoutMs
        if ($ms -ne $null) {
            if ($best -eq $null -or $ms -lt $best) { $best = $ms }
        }
    }
    $timings[$addr] = $best      # null again if it answered once then stopped
    $done++
    Write-Host ("`r  timing {0}/{1}" -f $done, $live.Count) -NoNewline
}
Write-Host ""


# ---------------------------------------------------------------- report

$results = foreach ($t in $targets) {
    $ms = $timings[$t.Address]
    $state = 'dead'
    if ($ms -ne $null) {
        if ($ms -le 50)  { $state = 'fast' }
        elseif ($ms -le 150) { $state = 'ok' }
        else { $state = 'slow' }
    }

    [pscustomobject]@{
        group   = $t.Group
        family  = $t.Family
        name    = $t.Name
        country = $t.Country
        role    = $t.Role
        address = $t.Address
        ms      = $ms
        state   = $state
    }
}

$results | ConvertTo-Json -Depth 4 | Set-Content $OutPath -Encoding UTF8

Write-Host ""
foreach ($g in $data.groups) {
    $rows = @($results | Where-Object { $_.group -eq $g.group } | Sort-Object { if ($_.ms -eq $null) { 999999 } else { $_.ms } })

    Write-Host ("=== {0}  ({1}, {2}) ===" -f $g.group, $g.family, $rows.Count)
    foreach ($r in $rows) {
        $shown = if ($r.ms -eq $null) { "   no answer" } else { "{0,5} ms" -f $r.ms }
        Write-Host ("  {0,-11} {1,-4} {2,-30} {3,-18} {4}" -f $r.state, $r.country, $r.name, $r.address, $shown)
    }
    Write-Host ""
}

$deadRows = @($results | Where-Object { $_.state -eq 'dead' })
if ($deadRows.Count) {
    Write-Host ("=== did not answer, {0} entries across {1} distinct addresses ===" -f `
        $deadRows.Count, @(($deadRows | Select-Object -ExpandProperty address -Unique)).Count)
    $deadRows | Group-Object name | Sort-Object Name | ForEach-Object {
        Write-Host ("  {0,-34} {1}" -f $_.Name, (($_.Group | Select-Object -ExpandProperty address) -join ', '))
    }
}

Write-Host ""
Write-Host ("written to {0}" -f $OutPath)
