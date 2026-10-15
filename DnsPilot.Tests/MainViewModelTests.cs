using System.IO;

using DnsPilot.Models;
using DnsPilot.Services;
using DnsPilot.Ui;

using Xunit;

namespace DnsPilot.Tests;

/// <summary>
/// Validation, stores, and the rows the list shows.
/// </summary>
/// <remarks>
/// The validation tests are the ones that matter. Everything here ends up as an argument to a
/// netsh command that runs elevated, so the boundary between what a person can type and what a
/// privileged command receives is this method - and netsh's own refusal is localised and says
/// only that the command failed.
/// </remarks>
public class MainViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly MainViewModel _vm;

    public MainViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dnspilot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        Lang.Set(Language.English);
        _vm = new MainViewModel(new PresetStore(_dir), new BackupStore(_dir));
    }

    public void Dispose()
    {
        _vm.Dispose();
        Lang.Set(Language.English);
        try { Directory.Delete(_dir, recursive: true); } catch { /* already gone */ }
    }

    // --- adding a server ------------------------------------------------------------------

    [Theory]
    [InlineData("", "", "", DnsFamily.IPv4, "add_invalid")]
    [InlineData("x", "not-an-ip", "", DnsFamily.IPv4, "add_invalid")]
    [InlineData("x", "8.8.8.8", "also-not-an-ip", DnsFamily.IPv4, "add_invalid")]
    [InlineData("x", "8.8.8.8", "9.9.9.9", DnsFamily.IPv6, "add_mismatch")]
    [InlineData("x", "2001:4860:4860::8888", "", DnsFamily.IPv4, "add_mismatch")]
    [InlineData("x", "127.0.0.1", "", DnsFamily.IPv4, "add_loopback")]
    [InlineData("x", "8.8.8.8", "8.8.8.8", DnsFamily.IPv4, "add_same")]
    public void A_server_that_could_not_work_is_refused(string name, string primary,
                                                        string secondary, DnsFamily family, string expected)
    {
        var problem = _vm.TryAddCustom(name, primary, secondary, family);

        Assert.Equal(expected, problem);
        Assert.Empty(_vm.CustomServers);
    }

    [Fact]
    public void A_server_that_would_work_is_accepted_and_persisted()
    {
        var problem = _vm.TryAddCustom("My resolver", "9.9.9.9", "149.112.112.112", DnsFamily.IPv4);

        Assert.Null(problem);
        Assert.Single(_vm.CustomServers);

        // A second store over the same directory stands in for the next launch: what was saved
        // has to be what comes back, not merely what was added to a list in memory.
        var reloaded = new PresetStore(_dir).TryLoadCustom();
        Assert.Null(reloaded.Problem);
        Assert.Single(reloaded.Servers);
        Assert.Equal("9.9.9.9", reloaded.Servers[0].Primary);
        Assert.Equal("149.112.112.112", reloaded.Servers[0].Secondary);
    }

    [Fact]
    public void The_same_address_twice_is_refused()
    {
        Assert.Null(_vm.TryAddCustom("First", "8.8.8.8", "", DnsFamily.IPv4));
        Assert.Equal("add_duplicate", _vm.TryAddCustom("Second", "8.8.8.8", "", DnsFamily.IPv4));
        Assert.Single(_vm.CustomServers);
    }

    // --- the Use button's notification path --------------------------------------------------

    [Fact]
    public void Selecting_a_server_tells_the_use_button_to_re_check()
    {
        // The Use button is gated by ICommand.CanExecute, and a WPF Button only re-asks its command
        // when that command raises CanExecuteChanged. Selecting a row changed CanApply and raised an
        // ordinary property change, which nothing on the button binds to - so the button stayed
        // disabled through the one flow a user actually performs: press Test, then choose a
        // resolver. The test finishes with IsBusy false and the re-sort has cleared the selection,
        // so the button correctly disables; clicking a row then told nobody it could come back.
        //
        // Asserted on the notification rather than on the resulting CanExecute value: that value
        // also depends on whether the machine running the test happens to have a network adapter,
        // and a test whose result depends on the machine it ran on is not a test.
        Assert.Null(_vm.SelectedRow);

        bool raised = false;
        _vm.ApplyCommand.CanExecuteChanged += (_, _) => raised = true;

        var row = new ServerRow(new DnsServer("Google", "US", "8.8.8.8", "8.8.4.4", DnsFamily.IPv4));
        _vm.SelectedRow = row;

        Assert.True(raised, "selecting a server must raise CanExecuteChanged so the button re-queries");
        Assert.Same(row, _vm.SelectedRow);
    }

    [Fact]
    public void Choosing_an_adapter_tells_the_use_button_to_re_check()
    {
        // CanApply needs an adapter as well as a server, so the adapter setter owes the same
        // notification the selection setter does.
        bool raised = false;
        _vm.ApplyCommand.CanExecuteChanged += (_, _) => raised = true;

        _vm.SelectedAdapter = new AdapterInfo(
            "{test-guid}", "Ethernet", "Test adapter", true, true, false, null, null);

        Assert.True(raised, "choosing an adapter must raise CanExecuteChanged too");
    }

    [Fact]
    public void A_secondary_address_is_optional()
    {
        Assert.Null(_vm.TryAddCustom("Single", "1.1.1.1", "", DnsFamily.IPv4));
        Assert.Single(_vm.CustomServers);
        Assert.Equal("", _vm.CustomServers[0].Secondary);
    }

    [Fact]
    public void A_server_can_be_removed_and_is_gone_on_reload()
    {
        _vm.TryAddCustom("Temp", "8.8.4.4", "", DnsFamily.IPv4);
        Assert.Single(_vm.CustomServers);

        _vm.RemoveCustom(_vm.CustomServers[0]);

        Assert.Empty(_vm.CustomServers);
        Assert.Empty(new PresetStore(_dir).TryLoadCustom().Servers);
    }

    // --- the shipped list reaching the window ------------------------------------------------

    [Fact]
    public void The_window_starts_with_adapters_and_a_group()
    {
        // Read-only: nothing here touches configuration. The assertion is that construction
        // completed, since every one of these would otherwise fail on the first paint.
        Assert.NotNull(_vm.Groups);
        Assert.NotEmpty(_vm.Groups);
        Assert.Equal("default", _vm.SelectedGroup?.Kind);
        Assert.Equal("Ready", _vm.StatusText);
    }

    [Fact]
    public void Every_group_carries_a_label()
    {
        var labels = _vm.Groups.Select(g => g.Label).ToList();

        Assert.Equal(4, labels.Count);
        Assert.All(labels, l => Assert.False(string.IsNullOrWhiteSpace(l)));
        Assert.Equal(new[] { "General", "Family", "Secure", "Mine" }, labels);
    }

    [Fact]
    public void Selecting_a_group_marks_exactly_one_selected()
    {
        _vm.SelectGroupCommand.Execute("secure");

        Assert.Equal("secure", _vm.SelectedGroup?.Kind);
        Assert.Single(_vm.Groups, g => g.IsSelected);
    }

    [Fact]
    public void The_built_in_list_is_not_removable()
    {
        _vm.SelectGroupCommand.Execute("default");
        _vm.SelectedRow = _vm.VisibleServers.FirstOrDefault();

        Assert.False(_vm.CanRemoveSelected);
    }

    [Fact]
    public void The_language_indexer_returns_the_text_it_is_asked_for()
    {
        Lang.Set(Language.English);
        Assert.Equal("Use this server", _vm["action_apply"]);

        Lang.Set(Language.Persian);
        Assert.Equal("استفاده از این سرور", _vm["action_apply"]);
    }
}

/// <summary>Row behaviour that decides what the list says before and after a measurement.</summary>
public class ServerRowTests
{
    public ServerRowTests()
    {
        // Row text comes from Lang, which is global, so it is pinned rather than left to whatever
        // the previous test class left behind.
        Lang.Set(Language.English);
    }

    [Fact]
    public void An_untested_row_says_so_rather_than_showing_zero()
    {
        var row = new ServerRow(new DnsServer("Google", "US", "8.8.8.8", "8.8.4.4", DnsFamily.IPv4));

        Assert.False(row.IsTested);
        Assert.Equal("—", row.LatencyText);
        Assert.Equal("Brush.Muted", row.LatencyBrush);
        Assert.Equal(int.MaxValue, row.SortKey);   // sorts after every measured row
    }

    [Fact]
    public void A_row_that_did_not_answer_says_no_answer()
    {
        var row = new ServerRow(new DnsServer("Dead", "", "8.8.8.8", "", DnsFamily.IPv4))
        {
            TestFailed = true,
        };

        Assert.False(row.IsTested);
        Assert.Equal("no answer", row.LatencyText);
        Assert.Equal("Brush.Error", row.LatencyBrush);
    }

    [Fact]
    public void A_row_with_no_reason_given_falls_back_to_the_generic_wording()
    {
        // The tester may be replaced, or a row marked failed by hand. Either way the cell must not
        // end up blank, which would read as "not tested yet" and invite the test to be run again.
        var row = new ServerRow(new DnsServer("Dead", "", "8.8.8.8", "", DnsFamily.IPv4))
        {
            TestFailed = true,
            ErrorKey = null,
        };

        Assert.Equal("no answer", row.LatencyText);
    }

    [Fact]
    public void An_unroutable_family_is_reported_as_this_machine_having_no_route()
    {
        // The distinction this exists for. On a machine with no IPv6 connectivity every IPv6
        // resolver fails identically and none of them is at fault, so the cell has to say so -
        // otherwise twenty-four of the most widely used resolvers in the world appear to be dead.
        var row = new ServerRow(new DnsServer("Cloudflare", "AU", "2606:4700:4700::1111", "", DnsFamily.IPv6))
        {
            TestFailed = true,
            ErrorKey = "latency_reason_no_route",
        };

        // Short by design: this sits in a fixed-width column next to server names, so the cell
        // holds a token and the colour carries the severity.
        Assert.Equal("no route", row.LatencyText);
        Assert.Equal("Brush.Error", row.LatencyBrush);
    }

    [Fact]
    public void A_reason_that_takes_a_value_gets_it()
    {
        var row = new ServerRow(new DnsServer("Broken", "", "8.8.8.8", "", DnsFamily.IPv4))
        {
            TestFailed = true,
            ErrorKey = "latency_reason_rcode",
            ErrorDetail = "2",
        };

        // "error 2" rather than the bare code alone: a number with nothing around it reads as a
        // latency, which is exactly the wrong impression in a column whose whole job is latency.
        Assert.Equal("error 2", row.LatencyText);
    }

    [Fact]
    public void A_reason_is_shown_in_the_language_the_app_is_in()
    {
        var row = new ServerRow(new DnsServer("Cloudflare", "AU", "2606:4700:4700::1111", "", DnsFamily.IPv6))
        {
            TestFailed = true,
            ErrorKey = "latency_reason_no_route",
        };

        try
        {
            Lang.Set(Language.Persian);

            // The detail must survive a language change rather than being baked in at the moment
            // the test failed, since the user can switch language after a test has already run.
            row.RefreshText();

            Assert.Equal("بدون مسیر", row.LatencyText);
        }
        finally
        {
            Lang.Set(Language.English);
        }
    }

    [Theory]
    [InlineData(10,  "Brush.Good")]   // fast, worth pointing at
    [InlineData(50,  "Brush.Good")]
    [InlineData(51,  "Brush.Muted")]  // neither end of the judgement, so no colour
    [InlineData(150, "Brush.Muted")]
    [InlineData(151, "Brush.Slow")]   // past the point where the user would care
    public void Latency_is_coloured_by_how_good_it_is(int ms, string expected)
    {
        var row = new ServerRow(new DnsServer("S", "", "8.8.8.8", "", DnsFamily.IPv4))
        {
            Milliseconds = ms,
        };

        Assert.Equal(expected, row.LatencyBrush);
        Assert.Equal($"{ms} ms", row.LatencyText);
    }

    [Fact]
    public void The_status_flips_when_a_measurement_lands()
    {
        var row = new ServerRow(new DnsServer("S", "", "8.8.8.8", "", DnsFamily.IPv4));
        var seen = new List<string?>();
        row.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        row.Milliseconds = 42;

        Assert.Contains(nameof(ServerRow.Milliseconds), seen);
        Assert.Contains(nameof(ServerRow.LatencyText), seen);
        Assert.Contains(nameof(ServerRow.LatencyBrush), seen);
        Assert.True(row.IsTested);
    }
}
