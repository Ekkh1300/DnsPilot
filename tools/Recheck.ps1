# Two questions left before anything gets removed from the list.
#
# One: of the IPv4 addresses that failed a single 1.5 s probe, which fail consistently and which
# just dropped a packet. A resolver that misses one short probe is not proven dead - the whole
# list came from a published catalogue and a single run from one network is thin evidence for
# deleting entries other people might still need. Several of these are addresses of services that
# are certainly still running, where only one of a provider's pair answered.
#
# Two: what the socket actually reports when the address family has no route at all. "No answer"
# and "this machine cannot reach that family" are different facts, and the app should not report
# them with the same words.
#
# Only ever sends queries. Changes nothing.

param(
    [int]$Attempts = 5,
    [int]$TimeoutMs = 2500
)

$ProbeName = 'example.com'


function New-DnsQueryBytes {
    param([string]$Name, [uint16]$QType, [uint16]$Id)

    $buf = New-Object System.Collections.Generic.List[byte]

    $buf.Add([byte](($Id -shr 8) -band 0xFF))
    $buf.Add([byte]($Id -band 0xFF))
    $buf.Add(0x01); $buf.Add(0x00)
    $buf.Add(0x00); $buf.Add(0x01)
    $buf.Add(0x00); $buf.Add(0x00)
    $buf.Add(0x00); $buf.Add(0x00)
    $buf.Add(0x00); $buf.Add(0x00)

    foreach ($label in $Name.TrimEnd('.').Split('.', [StringSplitOptions]::RemoveEmptyEntries)) {
        $b = [System.Text.Encoding]::ASCII.GetBytes($label)
        $buf.Add([byte]$b.Count)
        foreach ($x in $b) { $buf.Add($x) }
    }

    $buf.Add(0x00)
    $buf.Add([byte](($QType -shr 8) -band 0xFF))
    $buf.Add([byte]($QType -band 0xFF))
    $buf.Add(0x00); $buf.Add(0x01)

    return $buf.ToArray()
}


# Returns the elapsed milliseconds, or $null, and reports the socket error code when it failed.
# Kept as one function so the timing and the diagnosis cannot drift apart.
function Measure-Dns {
    param([string]$Server, [string]$Name, [int]$TimeoutMs, [ref]$ErrorCode)

    $isV6  = $Server.Contains(':')
    $id    = [uint16](Get-Random -Minimum 1 -Maximum 65535)
    $query = New-DnsQueryBytes -Name $Name -QType 1 -Id $id

    $bind = [System.Net.IPAddress]::Any
    if ($isV6) { $bind = [System.Net.IPAddress]::IPv6Any }

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

        $ErrorCode.Value = $null
        return [int]$sw.ElapsedMilliseconds
    }
    catch {
        $ErrorCode.Value = $null
        if ($_.Exception -is [System.Net.Sockets.SocketException]) {
            $ErrorCode.Value = $_.Exception.SocketErrorCode.ToString()
        }
        elseif ($_.Exception.InnerException -is [System.Net.Sockets.SocketException]) {
            $ErrorCode.Value = $_.Exception.InnerException.SocketErrorCode.ToString()
        }
        else { $ErrorCode.Value = $_.Exception.GetType().Name }
        return $null
    }
    finally { if ($udp) { $udp.Close() } }
}


# ---------------------------------------------------------------------------
# Question one: the IPv4 addresses that failed the first sweep.

$Retry = @(
    '199.85.126.10', '199.85.127.10',              # Norton ConnectSafe Basic
    '199.85.126.30', '199.85.127.30',              # Norton ConnectSafe Family
    '199.85.126.20', '199.85.127.20',              # Norton ConnectSafe Secure
    '198.153.192.1',  '198.153.194.1',             # Norton DNS
    '89.233.43.71',   '91.239.100.100',            # Censurfridns
    '80.67.169.12',   '80.67.169.40',              # FDN
    '204.97.212.10',  '204.117.214.10',            # Sprint / Sprintlink
    '199.2.252.10',                               # Sprintlink
    '84.200.69.80',                               # DNS WATCH (its other address answered)
    '185.228.169.9',  '185.228.169.11'             # CleanBrowsing (their .168.x answered)
)

Write-Host ("=== IPv4 retest: {0} attempts each, {1} ms timeout ===" -f $Attempts, $TimeoutMs)
Write-Host ("{0,-17} {1,-8} {2,-10} {3}" -f 'address', 'replied', 'best', 'socket error when it failed')

$confirmedDead = @()
$intermittent  = @()

foreach ($addr in $Retry) {
    $replies = 0
    $best    = $null
    $lastErr = '-'

    for ($i = 0; $i -lt $Attempts; $i++) {
        $errCode = $null
        $ms = Measure-Dns -Server $addr -Name $ProbeName -TimeoutMs $TimeoutMs -ErrorCode ([ref]$errCode)
        if ($ms -ne $null) {
            $replies++
            if ($best -eq $null -or $ms -lt $best) { $best = $ms }
        }
        elseif ($errCode -ne $null) { $lastErr = $errCode }
    }

    $shown = if ($best -eq $null) { '-' } else { "{0} ms" -f $best }
    Write-Host ("{0,-17} {1,-8} {2,-10} {3}" -f $addr, "$replies/$Attempts", $shown, $lastErr)

    if ($replies -eq 0)        { $confirmedDead += $addr }
    elseif ($replies -lt $Attempts) { $intermittent += $addr }
}

Write-Host ""
if ($confirmedDead.Count) {
    Write-Host "answered never - consistent with retired:"
    $confirmedDead | ForEach-Object { "  $_" }
}
if ($intermittent.Count) {
    Write-Host "answered at least once - these are alive and the first sweep was wrong:"
    $intermittent | ForEach-Object { "  $_" }
}


# ---------------------------------------------------------------------------
# Question two: what does an unroutable address family actually report?

Write-Host ""
Write-Host "=== socket error for a family with no route on this machine ==="

foreach ($addr in '2606:4700:4700::1111', '2a02:6b8::feed:0ff', '2001:4860:4860::8888') {
    $errCode = $null
    $ms = Measure-Dns -Server $addr -Name $ProbeName -TimeoutMs $TimeoutMs -ErrorCode ([ref]$errCode)
    $shown = if ($ms -eq $null) { 'no answer' } else { "{0} ms" -f $ms }
    Write-Host ("  {0,-26} {1,-10} error={2}" -f $addr, $shown, $errCode)
}

Write-Host ""
Write-Host "=== a routable IPv4 address, same code path ==="
$errCode = $null
$ms = Measure-Dns -Server '8.8.8.8' -Name $ProbeName -TimeoutMs $TimeoutMs -ErrorCode ([ref]$errCode)
Write-Host ("  {0,-26} {1,-10} error={2}" -f '8.8.8.8', $(if ($ms -eq $null) { 'no answer' } else { "$ms ms" }), $errCode)