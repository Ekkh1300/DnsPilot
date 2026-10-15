# Compares latency readings for the same resolvers under different measurement choices.
#
# DnsPilot asks every server at once and asks about www.msftconnecttest.com. DNS Jumper asks one
# at a time. On this network the second choice matters twice over: a wireless adapter queues
# concurrent packets on the radio, and www.msftconnecttest.com is the exact name an Iranian ISP
# intercepts for its connectivity check - so it can be measuring the interception rather than
# the resolver.
#
# This measures the same servers both ways so the size of each effect is visible rather than
# assumed. It only ever sends queries; it changes nothing.

param(
    [int]$TimeoutMs = 3000,
    [int]$Rounds    = 3
)

$Servers = @(
    @{ Name = 'Google';        Ip = '8.8.8.8' },
    @{ Name = 'Cloudflare';    Ip = '1.1.1.1' },
    @{ Name = 'Quad9';         Ip = '9.9.9.9' },
    @{ Name = 'OpenDNS';       Ip = '208.67.222.222' },
    @{ Name = 'Current ISP 1'; Ip = '217.218.127.127' },
    @{ Name = 'Current ISP 2'; Ip = '5.200.200.200' },
    @{ Name = 'Router';        Ip = '192.168.1.254' }
)

function New-DnsQueryBytes {
    param([string]$Name, [uint16]$QType, [uint16]$Id)

    $buf = New-Object System.Collections.Generic.List[byte]

    $buf.Add([byte](($Id -shr 8) -band 0xFF))
    $buf.Add([byte]($Id -band 0xFF))

    $buf.Add(0x01); $buf.Add(0x00)          # flags: standard query, recursion desired
    $buf.Add(0x00); $buf.Add(0x01)          # one question
    $buf.Add(0x00); $buf.Add(0x00)          # no answers
    $buf.Add(0x00); $buf.Add(0x00)          # no authority
    $buf.Add(0x00); $buf.Add(0x00)          # no additional

    foreach ($label in $Name.TrimEnd('.').Split('.', [StringSplitOptions]::RemoveEmptyEntries)) {
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($label)
        $buf.Add([byte]$bytes.Count)
        foreach ($b in $bytes) { $buf.Add($b) }
    }

    $buf.Add(0x00)                          # root label ends the name
    $buf.Add([byte](($QType -shr 8) -band 0xFF))
    $buf.Add([byte]($QType -band 0xFF))
    $buf.Add(0x00); $buf.Add(0x01)          # class IN

    return $buf.ToArray()
}

function Measure-Dns {
    param([string]$Server, [string]$Name)

    $id   = Get-Random -Minimum 1 -Maximum 65535
    $query = New-DnsQueryBytes -Name $Name -QType 1 -Id ([uint16]$id)

    $udp = New-Object System.Net.Sockets.UdpClient
    try {
        $udp.Client.ReceiveTimeout = $TimeoutMs
        $udp.Connect($Server, 53)

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        [void]$udp.Send($query, $query.Length)

        $remote = New-Object System.Net.IPEndPoint -ArgumentList ([System.Net.IPAddress]::Any, 0)
        $null = $udp.Receive([ref]$remote)
        $sw.Stop()

        return [int]$sw.ElapsedMilliseconds
    }
    catch { return $null }
    finally { $udp.Close() }
}

function Measure-Many {
    param([string]$Server, [string]$Name, [int]$Count)

    $samples = @()
    for ($i = 0; $i -lt $Count; $i++) { $samples += Measure-Dns -Server $Server -Name $Name }

    $ok = @($samples | Where-Object { $_ -ne $null })
    if ($ok.Count -eq 0) { return $null }

    # Minimum, because the first packet to a resolver pays for a neighbour lookup, and the figure
    # that is useful for choosing is how fast it answers when nothing is in the way.
    return ($ok | Measure-Object -Minimum).Minimum
}

function Test-Matrix {
    param([string]$Name, [string]$Label)

    Write-Host ""
    Write-Host ("=== probe name: {0}  ({1}) ===" -f $Name, $Label)

    foreach ($s in $Servers) {
        $ms = Measure-Many -Server $s.Ip -Name $Name -Count $Rounds
        $shown = if ($ms -eq $null) { "no answer" } else { "{0,5} ms" -f $ms }
        Write-Host ("  {0,-14} {1,-16} {2}" -f $s.Name, $s.Ip, $shown)
    }
}

Write-Host "DnsPilot latency check  (timeout ${TimeoutMs}ms, $Rounds rounds, minimum of rounds)"
Write-Host ("local resolvers in use : " + ((Get-DnsClientServerAddress -AddressFamily IPv4 |
    Where-Object { $_.ServerAddresses } |
    ForEach-Object { $_.ServerAddresses }) -join ', '))

# The name DnsPilot currently asks about.
Test-Matrix -Name 'www.msftconnecttest.com' -Label 'Microsoft NCSI connectivity check'

# A neutral, universally cached name that no ISP has a reason to intercept.
Test-Matrix -Name 'example.com'             -Label 'neutral test domain'

# An IPv6 question, to confirm the same thing happens for the v6 entries.
Test-Matrix -Name 'www.msftconnecttest.com' -Label 'again, to show run-to-run variation'
