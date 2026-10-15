using System.Net;
using Microsoft.Win32;

namespace DnsPilot.Services;

using DnsPilot.Models;

/// <summary>What an adapter is currently set to ask.</summary>
public sealed record DnsState(IReadOnlyList<string> StaticServers, IReadOnlyList<string> DhcpServers)
{
    /// <summary>No static servers means Windows is taking them from DHCP.</summary>
    public bool IsDhcp => StaticServers.Count == 0;
    public IReadOnlyList<string> Effective => IsDhcp ? DhcpServers : StaticServers;
}

/// <summary>
/// Reads and writes the resolver an adapter uses.
///
/// Reading goes through the registry rather than netsh. netsh is perfectly good at changing
/// things, but its output is localised - on this machine it prints "Statically Configured DNS
/// Servers", and on a Persian or German Windows that sentence is different - so anything that
/// parsed it would break on most of the world's machines while working on the developer's. The
/// registry is a fixed set of values with fixed names, which is the same on every machine.
///
/// Writing goes through netsh, because it also arranges for the change to take effect; writing
/// the registry directly would set the value and leave Windows holding the old answer.
/// </summary>
public static class DnsService
{
    private static string KeyPath(string interfaceId, DnsFamily family)
    {
        var hive = family == DnsFamily.IPv4 ? "Tcpip" : "Tcpip6";
        return $@"SYSTEM\CurrentControlSet\Services\{hive}\Parameters\Interfaces\{interfaceId}";
    }

    /// <summary>
    /// Returns null when the adapter has no key at all, which is true of adapters Windows has
    /// not configured for that family. That is different from "set to DHCP" and the UI says so.
    /// </summary>
    public static DnsState? Read(string interfaceId, DnsFamily family)
    {
        if (string.IsNullOrWhiteSpace(interfaceId)) return null;

        using var key = Registry.LocalMachine.OpenSubKey(KeyPath(interfaceId, family));
        if (key is null) return null;

        return new DnsState(Split(key.GetValue("NameServer") as string),
                            Split(key.GetValue("DhcpNameServer") as string));
    }

    /// <summary>The registry stores these as one comma-separated string with no spaces.</summary>
    private static IReadOnlyList<string> Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Sets a resolver. The addresses are parsed before they are used, so a malformed value is
    /// refused here, where the message can name it, rather than by netsh, where the message is
    /// localised and says only that the command failed.
    /// </summary>
    public static async Task<Shell.Result> ApplyAsync(string adapterAlias, DnsFamily family,
                                                      string primary, string? secondary = null,
                                                      CancellationToken ct = default)
    {
        ValidateAlias(adapterAlias);
        var fam = family == DnsFamily.IPv4 ? "ipv4" : "ipv6";
        var expected = family == DnsFamily.IPv4 ? System.Net.Sockets.AddressFamily.InterNetwork
                                                : System.Net.Sockets.AddressFamily.InterNetworkV6;

        var p = Parse(primary, expected, nameof(primary));
        string? s = null;
        if (!string.IsNullOrWhiteSpace(secondary)) s = Parse(secondary, expected, nameof(secondary));

        // validate=no on the second address: an ISP resolver behind a captive portal can fail a
        // reachability probe, and Windows would then silently drop it and ask DHCP again - which
        // looks like the change did not take. The user asked for both; set both.
        //
        // netsh's `set dnsservers ... source=static` replaces the list rather than appending to
        // it, so there is nothing to clear first; the second address is only added when the
        // caller actually supplied one. Running the add with an empty address would make netsh
        // report a failure for a change that had in fact succeeded, which is worse than not
        // trying - the user would be told the switch failed and would try it again.
        var set = await Shell.RunAsync("netsh.exe", new[]
        {
            "interface", fam, "set", "dnsservers",
            $"name={adapterAlias}", "source=static", $"address={p}", "register=primary", "validate=no"
        }, ct).ConfigureAwait(false);

        if (!set.Ok) return set;
        if (s is null) return set;

        return await Shell.RunAsync("netsh.exe", new[]
        {
            "interface", fam, "add", "dnsservers",
            $"name={adapterAlias}", $"address={s}", "index=2", "validate=no"
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Puts the adapter back to taking its resolver from DHCP.</summary>
    public static Task<Shell.Result> UseDhcpAsync(string adapterAlias, DnsFamily family, CancellationToken ct = default)
    {
        ValidateAlias(adapterAlias);
        var fam = family == DnsFamily.IPv4 ? "ipv4" : "ipv6";
        return Shell.RunAsync("netsh.exe", new[]
        {
            "interface", fam, "set", "dnsservers",
            $"name={adapterAlias}", "source=dhcp"
        }, ct);
    }

    /// <summary>Clears the resolver cache. Every application on the machine benefits at once.</summary>
    public static Task<Shell.Result> FlushAsync(CancellationToken ct = default)
        => Shell.RunAsync("ipconfig.exe", new[] { "/flushdns" }, ct);

    /// <summary>Parses and range-checks one address, returning it unchanged when it is usable.</summary>
    private static string Parse(string value, System.Net.Sockets.AddressFamily expected, string which)
    {
        if (!IPAddress.TryParse(value, out var ip))
            throw new ArgumentException($"'{value}' is not an IP address ({which}).");
        if (ip.AddressFamily != expected)
            throw new ArgumentException($"'{value}' is not a {(expected == System.Net.Sockets.AddressFamily.InterNetwork ? "v4" : "v6")} address ({which}).");
        if (IPAddress.IsLoopback(ip))
            throw new ArgumentException($"'{value}' is a loopback address and would point the machine at itself.");
        return value;
    }

    /// <summary>
    /// The alias is what goes into netsh's name= argument. It comes from Windows, not from the
    /// user, but it is still checked as an alias that exists: a name with a quote in it is legal
    /// on NTFS and would otherwise make netsh look for an adapter that is not there.
    /// </summary>
    private static void ValidateAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
            throw new ArgumentException("an adapter name is required.", nameof(alias));
        if (alias.Contains('"') || alias.Contains('\n') || alias.Contains('\r'))
            throw new ArgumentException("that adapter name contains a character Windows will not accept.", nameof(alias));
    }
}
