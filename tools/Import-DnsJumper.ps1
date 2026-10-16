# Converts DNS Jumper's DnsJumper.ini into DnsPilot's presets.json.
#
# Run once, by hand, while developing. The INI is UTF-16 LE with a BOM; reading it as UTF-8 or
# ANSI silently corrupts every non-ASCII name, so the encoding is checked rather than assumed.
#
# Two things in the source need normalising, both found by looking at the data rather than at
# its format:
#   - some IPv6 rows put a space after the comma ("a, b"), which would then be written to the
#     registry as a server address and refused;
#   - IPv4 rows end in ",True" and IPv6 rows do not, so the field count differs by group.
# The country prefix ("US - ", "AU - ") is split off, because in the UI the country is metadata
# about a server and not part of its name.

# -Source and -Destination are the only machine-specific values in this repository, and neither
# says anything about DnsPilot: the INI is a file downloaded from DNS Jumper (not redistributed
# here), and where it happens to sit on this machine is a fact about this machine. They are
# parameters rather than constants so the paths are visible at the call site and can be
# overridden without editing the script:
#
#   .\Import-DnsJumper.ps1 -Source D:\DnsJumper.ini -Destination ..\DnsPilot\Data\presets.json
#
# The defaults below are this developer's layout, kept so the script still runs unchanged here.
param(
    [Parameter(Mandatory = $false)]
    [string]$Source = 'C:\dns\DNS.Jumper.2.3\DnsJumper.ini',

    [Parameter(Mandatory = $false)]
    [string]$Destination = 'E:\dnspilot\DnsPilot\Data\presets.json'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Source)) {
    throw "source INI not found at '$Source'. It is not part of this repository - pass -Source <path to DnsJumper.ini>."
}
$src = $Source
$out = $Destination

# The published list is extensive but not maintained, and it still carries providers that stopped
# operating public resolvers years ago. Swept on 2026-10-15: five probes each, 2.5 s apart, and
# these fifteen did not answer once. Applied as DNS they would leave the machine with no working
# resolver at all, so they are removed rather than left to be discovered by a user.
#
# Only addresses actually proven dead are listed here. Two rules keep this honest:
#
#   - an entry is dropped only when every address it has is retired. A server with one working
#     address is still usable, so if one survives the entry stays and the dead address leaves it.
#   - the IPv6 addresses in the source were never checked at all. The machine the sweep ran on has
#     no IPv6 connectivity, so every IPv6 address failed with NetworkUnreachable no matter what -
#     including Google's and Cloudflare's, which are certainly live. Those are absent from this
#     table because "untested" is not evidence of "dead", and they stay in the list.
$retired = @{
    # Norton ConnectSafe Basic / Family / Secure. Avast discontinued the service and moved the
    # netblocks to unrelated infrastructure.
    '199.85.126.10'  = 'Norton ConnectSafe Basic - service discontinued'
    '199.85.127.10'  = 'Norton ConnectSafe Basic - service discontinued'
    '199.85.126.30'  = 'Norton ConnectSafe Family - service discontinued'
    '199.85.127.30'  = 'Norton ConnectSafe Family - service discontinued'
    '199.85.126.20'  = 'Norton ConnectSafe Secure - service discontinued'
    '199.85.127.20'  = 'Norton ConnectSafe Secure - service discontinued'
    '198.153.192.1'  = 'Norton DNS - service discontinued'
    '198.153.194.1'  = 'Norton DNS - service discontinued'

    # Sprint's public resolvers went away with the acquisition; Sprintlink is the same dead block.
    '204.97.212.10'  = 'Sprint / Sprintlink - resolvers withdrawn'
    '204.117.214.10' = 'Sprint - resolvers withdrawn'
    '199.2.252.10'   = 'Sprintlink - resolvers withdrawn'

    # Censurfridns and FDN, IPv4 only. Both also appear with IPv6 addresses that remain unverified,
    # so those entries survive with just the dead IPv4 address removed.
    '89.233.43.71'   = 'Censurfridns - no longer answers'
    '91.239.100.100' = 'Censurfridns - no longer answers'
    '80.67.169.12'   = 'FDN - no longer answers'
    '80.67.169.40'   = 'FDN - no longer answers'
}

$dropped = @()
$trimmed = @()
$seen    = @()

$bytes = [System.IO.File]::ReadAllBytes($src)
if (-not ($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE)) { throw "expected UTF-16 LE BOM, found something else" }
$text = [System.Text.Encoding]::Unicode.GetString($bytes)

$cur = $null
$groups = [ordered]@{}
foreach ($l in ($text -split "`r`n|`n")) {
    if ($l -match '^\[(.+)\]') { $cur = $Matches[1]; continue }
    if (-not $cur -or $cur -match '^Language|^Main') { continue }
    if ($l -match '^\s*$' -or $l -match '^\s*;') { continue }
    if ($l -match '^([^=;][^=]*)=(.*)$') {
        if (-not $groups.Contains($cur)) { $groups[$cur] = New-Object System.Collections.ArrayList }
        [void]$groups[$cur].Add(@{ Name = $Matches[1].Trim(); Value = $Matches[2].Trim() })
    }
}

function Esc([string]$s) { if ($null -eq $s) { return '' } return $s.Replace('\','\\').Replace('"','\"') }

function CountryAndName([string]$raw) {
    if ($raw -match '^([A-Z]{2})\s*-\s*(.+)$') { return @($Matches[1], $Matches[2].Trim()) }
    return @('', $raw.Trim())
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('  "_source": "Converted from DNS Jumper 2.3 (www.sordum.org) DnsJumper.ini. That list is extensive but unmaintained, and fifteen of its addresses no longer answer; they are removed here, with each one and the reason recorded in tools/Import-DnsJumper.ps1. The IPv6 entries were never verified, because the machine the sweep ran on had no IPv6 connectivity at all, so every one of them failed regardless of whether it is live - they are unverified rather than known-good, and the in-app test is what settles them. Everything else is listed as published.",')
[void]$sb.AppendLine('  "groups": [')

$defs = @(
    @{ Key='Ipv4_Default'; Family='IPv4'; Group='default' },
    @{ Key='Ipv4_Family';  Family='IPv4'; Group='family'  },
    @{ Key='Ipv4_Secure';  Family='IPv4'; Group='secure'  },
    @{ Key='Ipv6_Default'; Family='IPv6'; Group='default' },
    @{ Key='Ipv6_Family';  Family='IPv6'; Group='family'  },
    @{ Key='Ipv6_Secure';  Family='IPv6'; Group='secure'  }
)

$gi = 0
foreach ($d in $defs) {
    if (-not $groups.Contains($d.Key)) { continue }
    if ($gi -gt 0) { [void]$sb.AppendLine('    ,') }
    $gi++
    [void]$sb.AppendLine('    {')
    [void]$sb.AppendLine(('      "id": "{0}-{1}",' -f $d.Family.ToLower(), $d.Group))
    [void]$sb.AppendLine(('      "family": "{0}",' -f $d.Family))
    [void]$sb.AppendLine(('      "group": "{0}",' -f $d.Group))
    [void]$sb.AppendLine('      "servers": [')

    $si = 0
    foreach ($e in ($groups[$d.Key] | Sort-Object Name)) {
        # Trim, because "a, b" would otherwise become a second address with a leading space.
        $parts = @($e.Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        if ($parts.Count -lt 1) { continue }

        # The third field is a boolean "enabled" flag, not an address. Dropping empty fields
        # before reading positions would promote that flag into the secondary address for any
        # row with a single server - which is exactly what happened to Hurricane Electric, whose
        # "74.82.42.42,,True" produced the secondary address "True". The flag is removed by its
        # value, so it cannot depend on how many addresses came before it. IPv6 rows have no flag.
        if ($parts.Count -ge 2 -and ($parts[-1] -eq 'True' -or $parts[-1] -eq 'False')) {
            $parts = $parts[0..($parts.Count - 2)]
        }

        $primary = $parts[0]
        $secondary = if ($parts.Count -ge 2) { $parts[1] } else { '' }
        $cn = CountryAndName $e.Name

        # Every address the source offered, recorded before any filtering. The staleness check at
        # the end compares the retired table against this, because comparing it against the written
        # output would of course report every retired address as absent - that is what retired means.
        if ($primary)   { $seen += $primary }
        if ($secondary) { $seen += $secondary }

        # Strip retired addresses, keeping whatever survives. Recording which addresses went and
        # why is the point of doing it here rather than by hand: the next person to regenerate this
        # file from the same INI would otherwise put them straight back.
        $retiredHere = @()
        if ($retired.ContainsKey($primary)) { $retiredHere += $primary; $primary = '' }
        if ($secondary -and $retired.ContainsKey($secondary)) { $retiredHere += $secondary; $secondary = '' }

        # Promote a surviving secondary, so a server that lost one address keeps the other rather
        # than being dropped as though it had none.
        if (-not $primary -and $secondary) { $primary = $secondary; $secondary = '' }

        # Nothing left to talk to: skip the entry rather than write one with empty fields, which
        # would appear in the list as a server with no address to apply.
        if (-not $primary) {
            $dropped += ('{0,-28} every address retired ({1})' -f $cn[1], ($retiredHere -join ', '))
            continue
        }

        if ($retiredHere.Count) {
            $trimmed += ('{0,-28} kept {1}, dropped {2}' -f $cn[1], $primary, ($retiredHere -join ', '))
        }

        if ($si -gt 0) { [void]$sb.AppendLine('        ,') }
        $si++
        [void]$sb.AppendLine('        {')
        [void]$sb.AppendLine(('          "name": "{0}",' -f (Esc $cn[1])))
        [void]$sb.AppendLine(('          "country": "{0}",' -f (Esc $cn[0])))
        [void]$sb.AppendLine(('          "primary": "{0}",' -f (Esc $primary)))
        [void]$sb.AppendLine(('          "secondary": "{0}"' -f (Esc $secondary)))
        [void]$sb.AppendLine('        }')
    }

    [void]$sb.AppendLine('      ]')
    [void]$sb.AppendLine('    }')
}

[void]$sb.AppendLine('  ]')
[void]$sb.AppendLine('}')

New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
[System.IO.File]::WriteAllText($out, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))

# The conversion is checked rather than trusted: parse the file straight back and compare the
# counts, because a malformed JSON here only shows up as an empty list at runtime.
$json = Get-Content $out -Raw | ConvertFrom-Json
"  wrote $out"
$total = 0
foreach ($g in $json.groups) {
    "    {0,-16} {1,3} servers" -f $g.id, $g.servers.Count
    $total += $g.servers.Count
}
"  total: $total"
$bad = $json.groups | ForEach-Object { $_.servers } | Where-Object {
    -not ($_.primary -match '^[0-9a-fA-F:.]+$') -or ($_.secondary -and -not ($_.secondary -match '^[0-9a-fA-F:.]+$'))
}
"  entries with a non-address-looking value: " + @($bad).Count
if ($bad) { $bad | Select-Object -First 5 | ForEach-Object { "    " + $_.name + " = " + $_.primary } }

# The specific regression this shape invites: the INI's boolean flag being read as an address.
$flags = $json.groups | ForEach-Object { $_.servers } | Where-Object {
    $_.primary -match '^(True|False)$' -or $_.secondary -match '^(True|False)$'
}
"  addresses that are actually the True/False flag: " + @($flags).Count
if ($flags) { $flags | ForEach-Object { "    " + $_.name }; throw "the flag leaked into an address field" }

# And a sanity check on the format of every address, so a typo in the source is named here
# rather than becoming a set command Windows rejects later.
$badIp = $json.groups | ForEach-Object { $_.servers } | ForEach-Object {
    foreach ($v in @($_.primary, $_.secondary)) {
        if ($v) {
            $ip = $null
            if (-not [System.Net.IPAddress]::TryParse($v, [ref]$ip)) { "{0}: '{1}'" -f $_.name, $v }
        }
    }
}
"  values that are not parseable IP addresses: " + @($badIp).Count
if ($badIp) { $badIp | ForEach-Object { "    " + $_ } }

# And what the retired table actually did to the output. The point is that a shorter list is a
# silent difference otherwise - someone regenerating this file from the same INI has no way to see
# that anything was removed, or check the reasoning against a different network.
if ($trimmed.Count) {
    ""
    "entries kept, with a retired address removed:"
    $trimmed | ForEach-Object { "  $_" }
}
if ($dropped.Count) {
    ""
    "entries dropped entirely, because every address they had was retired:"
    $dropped | ForEach-Object { "  $_" }
}
""
"addresses retired: {0}   entries trimmed: {1}   entries dropped: {2}" -f `
    $retired.Count, $trimmed.Count, $dropped.Count

# Every retired address should have been matched by something the source contained. An address in
# the table that the INI no longer carries means the table has gone stale, which is harmless here
# but would mean a retired server had been quietly reintroduced by a later source drop.
$unused = @($retired.Keys | Where-Object { $seen -notcontains $_ })
if ($unused.Count) {
    ""
    "retired addresses no longer present in the source (table may be stale): $($unused.Count)"
    $unused | ForEach-Object { "    $_" }
} else {
    ""
    "every retired address was matched against the source: $($retired.Count) of $($retired.Count)"
}
