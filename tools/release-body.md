First release of DnsPilot — a small DNS switcher for Windows.

Pick a network adapter, pick a resolver, apply it. 470 px wide, runs
elevated, English by default with full Persian support.

## Install

Download `DnsPilot-Setup.exe` and run it. It is a single self-contained
executable — no Inno Setup, no WiX, no .NET runtime prerequisite to
install first.

Uninstalling returns every adapter's DNS to automatic (DHCP), so the
machine is never left on a resolver chosen inside an app that has just
been removed. The completion message states which of those two outcomes
actually happened.

## Highlights

- 63 preset resolvers in 6 groups, swept and re-verified rather than
  taken on trust. 138 addresses were measured; the 15 that no longer
  answer were retired with a documented reason.
- Latency testing over a hand-written DNS wire encoder. An answer counts
  only if it matches the transaction id that was sent and has the
  response bit set, which is what separates "this server answered me"
  from "something on the path answered instead".
- Failures report why — no answer, refused, no route, unreachable, bad
  address, server error — rather than one flat "failed". A machine with
  no IPv6 route fails every IPv6 resolver identically, and none of them
  is at fault; DnsPilot says so rather than marking them dead.
- Backup and restore of the adapter's original DNS settings.
- Cache flush from the main window.
- Closing the window hides to tray; exiting lives only in the tray menu.
- Light and dark themes, English and Persian.
- Its own icon in the title bar, taskbar and tray, drawn by
  `tools\New-Icon.ps1` rather than committed as binary artwork.

## Requirements

- Windows 10 or later
- Administrator rights (declared in the app manifest)

## Verifying the download

DnsPilot-Setup.exe — 66.9 MB (70,176,256 bytes)

MD5: 94B32765E31C1972A4F3D9F671B71120

The asset was replaced on Oct 17 2026 with a build that fixes three things the
first upload shipped with: server names rendered near-invisible in the dark
theme (they inherited the system's black text default), a tray menu whose items
carried no styling, and a Persian setup screen that came out as noise — the
installer's C# 5 compiler read the BOM-less UTF-8 source as ANSI, and the
RTL layout style it used mirrored the glyphs themselves. Two flags
(`/codepage:65001`, `/win32icon`) and a hand-written RTL mirror replace that.

The installer is built by `DnsPilot/installer/build.ps1`, which compiles
`Setup.cs` with the .NET Framework's own `csc.exe` and embeds the
compressed payload as a manifest resource. The checksum printed at the
end of that build is the one to compare against.

Rebuilding from source produces a different binary — the payload is
re-published and re-compressed each time — so compare against the build
you downloaded, not against a fresh rebuild.

