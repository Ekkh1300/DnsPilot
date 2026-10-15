<p align="right"><a href="README.fa.md">فارسی</a></p>

# DnsPilot

A small, fast DNS switcher for Windows. Pick a network adapter, pick a resolver, apply it.

470 px wide. Runs elevated. English by default, with full Persian support.

| Light | Dark |
| --- | --- |
| ![DnsPilot in light theme, English](docs/screenshots/dns-pilot-en-light.png) | ![DnsPilot in dark theme, English](docs/screenshots/dns-pilot-en-dark.png) |

| فارسی (Light) | فارسی (Dark) |
| --- | --- |
| ![DnsPilot in light theme, Persian](docs/screenshots/dns-pilot-fa-light.png) | ![DnsPilot in dark theme, Persian](docs/screenshots/dns-pilot-fa-dark.png) |

## Install

Download `DnsPilot-Setup.exe` from the latest release and run it.

The installer is a single self-contained executable — no Inno Setup, no WiX, no runtime
prerequisite. Uninstalling returns **every** adapter's DNS to automatic (DHCP) rather than
leaving the machine on a resolver chosen inside an app that has just been removed, and it
reports which of those two outcomes actually happened.

## What it does

- **Apply a resolver** to a chosen adapter, or return to automatic.
- **63 preset resolvers** in 6 groups, imported from DNS Jumper's list and then re-verified
  against the network — see *The preset list* below.
- **Custom servers**, added and removed by hand.
- **Latency testing**, over a hand-written DNS wire encoder.
- **Backup and restore** of the adapter's original settings.
- **Cache flush**, straight from the main window.
- **Tray icon** — closing the window hides it; exiting lives only in the tray menu.

## How the latency test works

Most tools resolve a name and time the round trip. DnsPilot writes the DNS query packet
itself, then accepts an answer only if it matches the transaction id it sent **and** has the
response bit set. That check is what separates "this server answered me" from "something on
the path answered instead" — the difference matters on networks where a captive portal or an
ISP interceptor answers on port 53 for every name it does not recognise.

The wire format is validated in the test suite against .NET's own `DnsClient`, which returns
the same answers for the same packets.

Failures say *why*, rather than one flat "failed":

| Shown | Means |
| --- | --- |
| `no answer` | The server stayed silent through three attempts. |
| `no route` | Your machine has no route to that network. **The server is not at fault.** |
| `refused` | The server answered, and refused the query. |
| `unreachable` | The host was reachable but reported itself unreachable. |
| `bad address` | The name did not resolve to a usable address. |
| `error N` | The server returned a DNS error code. |

That second row is not decoration. This machine has no IPv6 connectivity, so *every* IPv6
resolver fails identically — reporting that as twenty-odd dead servers would be both wrong and
alarming. DnsPilot distinguishes "this server is down" from "you cannot reach that network",
because they call for different actions.

## The preset list

The 63 presets came from DNS Jumper's server list, and were then measured rather than trusted:

- **138 addresses** were swept.
- **15 had stopped answering** — zero replies across five attempts — and were retired, each
  with a documented reason in `tools/Import-DnsJumper.ps1`. Re-running the importer reproduces
  the list exactly.
- **Every IPv6 entry was kept**, including ones that failed from this machine. They failed
  because *this machine has no IPv6 route at all*, not because the servers are down. Google,
  Cloudflare and Quad9's IPv6 resolvers are certainly live; pruning them here would have been
  an error dressed up as tidiness.

## Building

Requires the .NET 8 SDK.

```powershell
dotnet build DnsPilot\DnsPilot.csproj -c Release
dotnet test  DnsPilot.Tests\DnsPilot.Tests.csproj
```

To produce the installer:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File DnsPilot\installer\build.ps1
```

The setup executable is compiled by `csc.exe` from the .NET Framework already present on
Windows, with the compressed payload embedded as a manifest resource — so it needs no
installer toolchain to build.

## Tests

70 tests, covering the DNS wire format, the preset store, and the view model — including the
failure-reason plumbing and the regression that stopped the **Use** button from enabling after
a test completed.

```powershell
dotnet test DnsPilot.Tests\DnsPilot.Tests.csproj
```

## Notes

- **Administrator rights are required**, declared in `app.manifest`. Changing an adapter's DNS
  is a system-wide setting and Windows does not let a non-elevated process do it.
- **DNS is read from the registry and written through `netsh`.** Reading from
  `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{GUID}` is the only
  reliable way to see what an adapter is actually using; `netsh` is what Windows itself uses to
  set it. Its output is localized, which is why nothing is parsed out of it.
- **The UI is never a VPN.** It changes which DNS server your machine asks. It does not
  encrypt, tunnel, or hide your traffic, and it does not claim to.

## License

MIT
