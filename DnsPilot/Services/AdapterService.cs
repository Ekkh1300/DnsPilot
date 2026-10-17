using System.Net.NetworkInformation;

using DnsPilot.Models;

namespace DnsPilot.Services;

/// <summary>
/// One network adapter as the app needs to see it: which one, what Windows calls it, and what
/// resolver it is on right now for each family.
/// </summary>
public sealed record AdapterInfo(
    string Id,          // the GUID Windows keys the registry entry by
    string Alias,       // the friendly name netsh takes as name=
    string Description, // the hardware description, for telling two "Ethernet" entries apart
    bool IsPhysical,
    bool SupportsIPv4,
    bool SupportsIPv6,
    DnsState? StateV4,
    DnsState? StateV6)
{
    public DnsState? StateFor(DnsFamily family) => family == DnsFamily.IPv4 ? StateV4 : StateV6;

    /// <summary>Both current addresses for the family, in one display string.</summary>
    public IReadOnlyList<string> EffectiveFor(DnsFamily family) => StateFor(family)?.Effective ?? Array.Empty<string>();

    public bool Supports(DnsFamily family) => family == DnsFamily.IPv4 ? SupportsIPv4 : SupportsIPv6;
}

/// <summary>
/// Finds the adapters that can actually be switched.
/// </summary>
/// <remarks>
/// The filter is the whole job here. Windows reports a great many interfaces - loopback, the
/// Teredo tunnel, each Hyper-V vEthernet, each VPN's own adapter - and offering all of them
/// means the user has to work out which row is the network they are on. So: anything not Up is
/// dropped, loopback is dropped, and a family is offered only when the adapter has that
/// family's registry key at all.
///
/// Virtual adapters are kept rather than hidden, because they are frequently the right answer -
/// someone on a VPN wants the VPN's adapter, and someone on a phone hotspot wants that. What
/// marks them is the hardware description, which is shown, instead of guessing from the name.
/// </remarks>
public static class AdapterService
{
    public static IReadOnlyList<AdapterInfo> GetAdapters()
    {
        var found = new List<AdapterInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            if (string.IsNullOrWhiteSpace(nic.Name)) continue;

            var id = nic.Id;

            // Both families are read straight from their registry keys rather than gated behind
            // NetworkInterface.Supports(). That call reports protocol bindings and is known to
            // under-report IPv6 on some adapters - a machine whose Tcpip6 key is real would have
            // its IPv6 half hidden from the list by a probe that said no. The key's existence is
            // the same thing netsh writes to and therefore the same thing the switcher can
            // change, so it is the better authority: DnsService.Read returns null exactly when
            // Windows has never configured the family on this adapter.
            var v4 = DnsService.Read(id, DnsFamily.IPv4);
            var v6 = DnsService.Read(id, DnsFamily.IPv6);

            // An adapter with neither a v4 nor a v6 key is not one anyone can set a resolver on.
            if (v4 is null && v6 is null) continue;

            found.Add(new AdapterInfo(
                Id: id,
                Alias: nic.Name,
                Description: nic.Description,
                IsPhysical: IsPhysical(nic),
                SupportsIPv4: v4 is not null,
                SupportsIPv6: v6 is not null,
                StateV4: v4,
                StateV6: v6));
        }

        // Physical adapters first: on a machine with a USB wireless stick and a Hyper-V switch,
        // the thing the user is actually using should be the first row and the default choice.
        return found
            .OrderByDescending(a => a.IsPhysical)
            .ThenBy(a => a.Alias, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Best available signal for "this is a real network card" without P/Invoking SetupAPI.</summary>
    private static bool IsPhysical(NetworkInterface nic)
    {
        var t = nic.NetworkInterfaceType;
        return t switch
        {
            NetworkInterfaceType.Wireless80211 => true,
            NetworkInterfaceType.Ethernet => true,
            NetworkInterfaceType.Ethernet3Megabit => true,
            NetworkInterfaceType.FastEthernetT => true,
            NetworkInterfaceType.FastEthernetFx => true,
            NetworkInterfaceType.GigabitEthernet => true,
            _ => false,
        };
    }

    public static AdapterInfo? Find(IEnumerable<AdapterInfo> adapters, string? id)
        => id is null ? null : adapters.FirstOrDefault(a => a.Id == id);
}
