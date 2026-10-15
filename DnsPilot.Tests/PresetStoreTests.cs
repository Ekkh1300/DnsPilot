using System.IO;

using DnsPilot.Models;
using DnsPilot.Services;

using Xunit;

namespace DnsPilot.Tests;

/// <summary>
/// The shipped resolver list, and the round trip a user's own list has to survive.
/// </summary>
/// <remarks>
/// The counts are asserted exactly rather than as a minimum. They were produced by a one-off
/// converter from DNS Jumper's INI, and that converter had a real bug - it read the trailing
/// ",True" flag as a DNS address for any row with a single server. An exact number is what makes
/// a future edit of either the source list or the converter fail here instead of shipping a list
/// with an address that is not an address.
/// </remarks>
public class PresetStoreTests
{
    [Fact]
    public void The_built_in_list_loads_with_all_six_groups()
    {
        var groups = PresetStore.LoadBuiltIn();

        Assert.Equal(6, groups.Count);
        Assert.Equal(3, groups.Count(g => g.Family == DnsFamily.IPv4));
        Assert.Equal(3, groups.Count(g => g.Family == DnsFamily.IPv6));
    }

    [Fact]
    public void An_entry_inherits_the_family_of_the_group_it_sits_in()
    {
        // The importer states the family once per group, not on all seventy-one entries. A
        // loader that gave every entry a default of IPv4 read each IPv6 address as belonging to
        // the wrong family, failed its check, and dropped it - so the window opened with three
        // groups and no IPv6 resolver at all. This is the test that says it must not happen
        // again, and it is deliberately about the data rather than about the loader.
        var v6 = PresetStore.LoadBuiltIn()
                          .Where(g => g.Family == DnsFamily.IPv6)
                          .SelectMany(g => g.Servers)
                          .ToList();

        Assert.Equal(24, v6.Count);
        Assert.All(v6, s => Assert.Equal(DnsFamily.IPv6, s.Family));
    }

    [Fact]
    public void Every_group_has_the_kind_it_claims()
    {
        var groups = PresetStore.LoadBuiltIn();

        foreach (var g in groups)
            Assert.Contains(g.Kind, new[] { "default", "family", "secure" });
    }

    [Fact]
    public void The_counts_are_what_the_importer_produced()
    {
        var groups = PresetStore.LoadBuiltIn();

        // Fifteen addresses the published list still carried were swept on 2026-10-15 and did not
        // answer once across five probes, which removed eight entries: Norton ConnectSafe
        // Basic/Family/Secure and Norton DNS, Sprint and Sprintlink, and the IPv4 halves of
        // Censurfridns and FDN. They are gone rather than left to be discovered by a user who
        // applies one and loses name resolution.
        //
        // The IPv6 counts are unchanged from the original import. They were never verified: the
        // machine the sweep ran on has no IPv6 connectivity, so all of them failed with
        // NetworkUnreachable whether they are live or not, and "untestable here" is not grounds
        // for deleting Google, Cloudflare and Quad9. If these numbers change, the retired table in
        // tools/Import-DnsJumper.ps1 changed - not the source list.
        Assert.Equal(26, Single(groups, "ipv4-default"));
        Assert.Equal(8,  Single(groups, "ipv4-family"));
        Assert.Equal(5,  Single(groups, "ipv4-secure"));
        Assert.Equal(13, Single(groups, "ipv6-default"));
        Assert.Equal(7,  Single(groups, "ipv6-family"));
        Assert.Equal(4,  Single(groups, "ipv6-secure"));

        static int Single(IReadOnlyList<PresetGroup> groups, string id)
            => groups.First(g => g.Id == id).Servers.Count;
    }

    [Fact]
    public void No_address_known_to_be_retired_is_still_shipped()
    {
        // Guards the sweep's actual output rather than a count. A count can still match while the
        // wrong entries were dropped, which is the failure that would quietly remove a working
        // resolver instead of a dead one.
        string[] retired =
        {
            "199.85.126.10", "199.85.127.10", "199.85.126.30", "199.85.127.30",
            "199.85.126.20", "199.85.127.20", "198.153.192.1", "198.153.194.1",
            "204.97.212.10", "204.117.214.10", "199.2.252.10",
            "89.233.43.71", "91.239.100.100", "80.67.169.12", "80.67.169.40",
        };

        var addresses = PresetStore.LoadBuiltIn()
            .SelectMany(g => g.Servers)
            .SelectMany(s => new[] { s.Primary, s.Secondary })
            .Where(a => !string.IsNullOrEmpty(a));

        foreach (var address in retired)
        {
            Assert.DoesNotContain(address, addresses);
        }
    }

    [Fact]
    public void Every_ipv6_server_that_the_sweep_could_not_reach_was_kept()
    {
        // The other half of the same decision, and the one that matters: a list pruned on the
        // strength of a machine with no IPv6 route would have emptied the IPv6 tab entirely.
        // Cloudflare and Google are the canaries because they are certainly live.
        var addresses = PresetStore.LoadBuiltIn()
            .SelectMany(g => g.Servers)
            .Select(s => s.Primary)
            .ToList();

        Assert.Contains("2606:4700:4700::1111", addresses);
        Assert.Contains("2001:4860:4860::8888", addresses);
        Assert.Contains("2620:fe::fe", addresses);
    }

    [Fact]
    public void Every_shipped_address_is_a_real_address()
    {
        var servers = PresetStore.LoadBuiltIn().SelectMany(g => g.Servers);

        foreach (var s in servers)
        {
            // IsValid checks both that it parses and that it belongs to the family it is filed
            // under - an IPv6 address filed as IPv4 would otherwise reach netsh and be refused
            // there, in a message that names neither the server nor the reason.
            Assert.True(s.IsValid, $"'{s.Name}' has primary '{s.Primary}' secondary '{s.Secondary}' family {s.Family}");
        }
    }

    [Fact]
    public void The_INI_boolean_flag_was_not_read_as_an_address()
    {
        // The specific regression: rows that have one server and no second address, such as
        // "74.82.42.42,,True", where the flag follows an empty field.
        var servers = PresetStore.LoadBuiltIn().SelectMany(g => g.Servers);

        Assert.DoesNotContain(servers, s => s.Primary is "True" or "False");
        Assert.DoesNotContain(servers, s => s.Secondary is "True" or "False");
    }

    [Fact]
    public void Addresses_that_look_empty_are_not_present()
    {
        var servers = PresetStore.LoadBuiltIn().SelectMany(g => g.Servers);

        Assert.DoesNotContain(servers, s => string.IsNullOrWhiteSpace(s.Primary));
        Assert.DoesNotContain(servers, s => s.Secondary is not null && s.Secondary.Trim() != s.Secondary);
    }

    [Fact]
    public void No_name_is_missing()
    {
        var servers = PresetStore.LoadBuiltIn().SelectMany(g => g.Servers);

        Assert.DoesNotContain(servers, s => string.IsNullOrWhiteSpace(s.Name));
    }

    // --- custom list ---------------------------------------------------------------------

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dnspilot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void A_fresh_install_has_no_custom_file_and_no_complaint()
    {
        var dir = TempDir();
        try
        {
            var store = new PresetStore(dir);
            var (servers, problem) = store.TryLoadCustom();

            Assert.Empty(servers);
            Assert.Null(problem);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Custom_servers_survive_a_write_and_read_back()
    {
        var dir = TempDir();
        try
        {
            var store = new PresetStore(dir);
            var original = new List<DnsServer>
            {
                new("My resolver", "IR", "9.9.9.9", "149.112.112.112", DnsFamily.IPv4),
                new("Second", "", "1.1.1.1", "", DnsFamily.IPv4),
            };

            store.SaveCustom(original);
            var (reloaded, problem) = store.TryLoadCustom();

            Assert.Null(problem);
            Assert.Equal(2, reloaded.Count);
            Assert.Equal(original[0].Name, reloaded[0].Name);
            Assert.Equal(original[0].Primary, reloaded[0].Primary);
            Assert.Equal(original[0].Secondary, reloaded[0].Secondary);
            Assert.Equal(original[1].Secondary, reloaded[1].Secondary);
            Assert.Equal(DnsFamily.IPv4, reloaded[0].Family);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_corrupt_file_is_reported_rather_than_thrown()
    {
        var dir = TempDir();
        try
        {
            var store = new PresetStore(dir);
            File.WriteAllText(store.CustomPath, "{ this is not json");

            // Throwing here would stop the window opening at all, with the built-in list
            // perfectly usable. The caller needs to know, not to crash.
            var (servers, problem) = store.TryLoadCustom();

            Assert.Empty(servers);
            Assert.NotNull(problem);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void An_invalid_entry_is_skipped_and_counted()
    {
        var dir = TempDir();
        try
        {
            var store = new PresetStore(dir);
            File.WriteAllText(store.CustomPath, """
            {
              "groups": [
                { "id": "custom", "family": "IPv4", "group": "custom", "servers": [
                  { "name": "good", "primary": "8.8.8.8", "secondary": "", "family": "IPv4" },
                  { "name": "bad",  "primary": "not-an-ip", "secondary": "", "family": "IPv4" },
                  { "name": "empty", "primary": "", "secondary": "", "family": "IPv4" }
                ]}
              ]
            }
            """);

            var (servers, problem) = store.TryLoadCustom();

            Assert.Single(servers);
            Assert.Equal("8.8.8.8", servers[0].Primary);
            Assert.NotNull(problem);
            Assert.Contains("skipped", problem!);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
