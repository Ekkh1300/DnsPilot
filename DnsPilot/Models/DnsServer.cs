using System.Net;

namespace DnsPilot.Models;

/// <summary>Which addressing family a resolver serves. Windows keeps the two configured apart.</summary>
public enum DnsFamily { IPv4, IPv6 }

/// <summary>
/// One resolver the user can switch to. Both addresses are optional in the source data but a
/// server with no primary is not a server, so <see cref="IsValid"/> is what stands between the
/// preset file and a set command Windows would reject.
/// </summary>
public sealed record DnsServer(string Name, string Country, string Primary, string Secondary, DnsFamily Family)
{
    public bool HasSecondary => !string.IsNullOrWhiteSpace(Secondary);

    /// <summary>True only if every address this record carries parses as a real address.</summary>
    public bool IsValid
    {
        get
        {
            if (!IPAddress.TryParse(Primary, out var p)) return false;
            if (p.AddressFamily != (Family == DnsFamily.IPv4 ? System.Net.Sockets.AddressFamily.InterNetwork
                                                            : System.Net.Sockets.AddressFamily.InterNetworkV6))
                return false;
            if (!HasSecondary) return true;
            return IPAddress.TryParse(Secondary, out var s) && s.AddressFamily == p.AddressFamily;
        }
    }
}

/// <summary>A named collection of resolvers, e.g. "general", "family" or "secure".</summary>
public sealed record PresetGroup(string Id, DnsFamily Family, string Kind, IReadOnlyList<DnsServer> Servers);

/// <summary>The three built-in kinds of list, matching what DNS Jumper calls Default/Family/Secure.</summary>
public static class PresetKind
{
    public const string Default = "default";
    public const string Family  = "family";
    public const string Secure  = "secure";
}
