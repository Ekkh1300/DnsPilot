using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using DnsPilot.Models;
using DnsPilot.Services;

namespace DnsPilot.Ui;

/// <summary>
/// One row of the group selector.
/// </summary>
/// <remarks>
/// A class rather than a record, because the radio buttons bind to <see cref="IsSelected"/> one
/// way: the view model decides which tab is chosen and each tab says so. Keeping the state here
/// instead of binding IsChecked back into the view model means there is exactly one place that
/// decides, and clicking a tab cannot leave the highlight on two tabs or on neither.
/// </remarks>
public sealed class GroupTab : INotifyPropertyChanged
{
    private bool _isSelected;

    public GroupTab(string kind, string label)
    {
        Kind = kind;
        Label = label;
    }

    public string Kind { get; }
    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Everything the window can show or do.
/// </summary>
/// <remarks>
/// The indexer is what lets XAML say <c>{Binding [action_apply]}</c> for every label: the value
/// comes from <see cref="Lang"/>, and after a language change a single notification with an
/// empty property name makes WPF re-read every binding on this object. One raise replaces
/// raising a named property for all sixty-odd strings, which is where an approach like this
/// usually breaks - forget one and that label alone stays in the old language.
///
/// Operations that reach outside the process (netsh, ipconfig, the latency probe) are all async
/// and all funnel through one <see cref="IsBusy"/> flag. Two switches at once would be the real
/// hazard here: netsh is fast, but a user who clicks Apply twice while the first is in flight can
/// interleave two registry writes and end up with neither result.
/// </remarks>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PresetStore _presets;
    private readonly BackupStore _backups;
    private readonly LatencyTester _tester = new();

    /// <summary>Rows are cached by server identity so a measurement survives switching groups.</summary>
    private readonly Dictionary<DnsServer, ServerRow> _rows = new();

    private IReadOnlyList<PresetGroup> _builtIn = Array.Empty<PresetGroup>();
    private IReadOnlyList<DnsServer> _custom = Array.Empty<DnsServer>();
    private IReadOnlyList<AdapterInfo> _adapters = Array.Empty<AdapterInfo>();

    private AdapterInfo? _selectedAdapter;
    private DnsFamily _family = DnsFamily.IPv4;
    private GroupTab? _selectedGroup;
    private ServerRow? _selectedRow;
    private string _searchText = string.Empty;
    private string _statusKey = "status_ready";
    private object[] _statusArgs = Array.Empty<object>();
    private string? _statusDetail;
    private string? _errorText;
    private bool _isBusy;
    private bool _testing;

    public MainViewModel(PresetStore? presets = null, BackupStore? backups = null)
    {
        _presets = presets ?? new PresetStore();
        _backups = backups ?? new BackupStore();

        LoadPresets();
        LoadAdapters();
        RebuildGroups();

        ApplyCommand     = new RelayCommand(() => _ = ApplyAsync(), () => CanApply);
        ApplyDhcpCommand = new RelayCommand(() => _ = ApplyDhcpAsync(), () => !IsBusy);
        TestCommand      = new RelayCommand(() => _ = TestAsync(), () => !IsBusy && VisibleServers.Count > 0);
        FlushCommand     = new RelayCommand(() => _ = FlushAsync(), () => !IsBusy);
        RestoreCommand   = new RelayCommand(() => _ = RestoreAsync(), () => !IsBusy);
        RefreshCommand   = new RelayCommand(RefreshAdapters, () => !IsBusy);
        LanguageCommand  = new RelayCommand(() => { Lang.Toggle(); RaiseAll(); });
        ThemeCommand     = new RelayCommand(() =>
        {
            AppTheme.Toggle();
            Raise(nameof(ThemeGlyph));
            Raise(nameof(ThemeToggleText));
        });
        SelectGroupCommand = new RelayCommand(p => SelectGroup(p));
        RemoveCommand    = new RelayCommand(RemoveSelected, () => CanRemoveSelected && !IsBusy);

        Lang.Changed += OnLanguageChanged;
    }

    // --- text -------------------------------------------------------------------------------

    /// <summary>Bound by XAML as {Binding [key]}. Throws on a key nobody wrote.</summary>
    public string this[string key] => Lang.T(key);

    // --- commands ---------------------------------------------------------------------------

    public RelayCommand ApplyCommand { get; }
    public RelayCommand ApplyDhcpCommand { get; }
    public RelayCommand TestCommand { get; }
    public RelayCommand FlushCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand LanguageCommand { get; }
    public RelayCommand ThemeCommand { get; }
    public RelayCommand SelectGroupCommand { get; }
    public RelayCommand RemoveCommand { get; }

    // --- adapters ---------------------------------------------------------------------------

    public IReadOnlyList<AdapterInfo> Adapters => _adapters;

    public bool HasAdapters => _adapters.Count > 0;

    public AdapterInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (ReferenceEquals(_selectedAdapter, value)) return;
            _selectedAdapter = value;

            // The family has to be re-checked on every adapter change: a USB Ethernet dongle may
            // have no IPv6 key while the adapter above it does, and leaving IPv6 selected would
            // put the window on a family with nothing to configure.
            if (value is not null && !value.Supports(_family))
                _family = value.SupportsIPv4 ? DnsFamily.IPv4 : DnsFamily.IPv6;

            RebuildGroups();
            RebuildVisibleServers();
            RaiseAll();

            // Same reason as SelectedRow: CanApply needs an adapter as well as a server, and the
            // command raise is the only notification the button actually listens for.
            RaiseCommands();
        }
    }

    public bool CanIPv4 => _selectedAdapter?.SupportsIPv4 ?? false;
    public bool CanIPv6 => _selectedAdapter?.SupportsIPv6 ?? false;

    public DnsFamily Family
    {
        get => _family;
        set
        {
            if (_family == value) return;
            if (_selectedAdapter is not null && !_selectedAdapter.Supports(value)) return;
            _family = value;
            RebuildGroups();
            RebuildVisibleServers();
            RaiseAll();
        }
    }

    // --- group selector ---------------------------------------------------------------------

    public IReadOnlyList<GroupTab> Groups { get; private set; } = Array.Empty<GroupTab>();

    public GroupTab? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (ReferenceEquals(_selectedGroup, value)) return;
            _selectedGroup = value;
            RebuildVisibleServers();
            RaiseAll();
        }
    }

    // --- list -------------------------------------------------------------------------------

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value ?? string.Empty;
            RebuildVisibleServers();
            RaiseAll();
        }
    }

    public ObservableCollection<ServerRow> VisibleServers { get; } = new();

    public bool HasVisibleServers => VisibleServers.Count > 0;

    public bool ListIsEmpty => VisibleServers.Count == 0;

    /// <summary>Distinguishes "your list is empty" from "nothing matched that search".</summary>
    public string EmptyListText
        => !string.IsNullOrWhiteSpace(_searchText) ? Lang.T("search_none")
         : _selectedGroup?.Kind == "custom" ? Lang.T("group_custom_empty")
         : Lang.T("group_empty");

    public ServerRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (ReferenceEquals(_selectedRow, value)) return;
            _selectedRow = value;
            Raise();
            Raise(nameof(CanApply));
            Raise(nameof(SelectedRowName));
            Raise(nameof(CanRemoveSelected));

            // The Use button is gated by ICommand.CanExecute rather than by an IsEnabled binding,
            // so raising the property above changes nothing the button can observe - a Button only
            // re-asks its command when that command raises CanExecuteChanged. That raise used to
            // happen only when IsBusy moved or an action finished, which left the button dead in
            // the one case a user would notice: pressing Test, then choosing a resolver. The test
            // ends with IsBusy false, the re-sort has cleared the selection, so the button
            // correctly disables; clicking a row then changed CanApply and told nobody.
            RaiseCommands();
        }
    }

    public string SelectedRowName => _selectedRow?.Server.Name ?? string.Empty;

    /// <summary>Only the user's own entries can be removed - the built-in list is not editable.</summary>
    public bool CanRemoveSelected => _selectedRow?.IsCustom == true;

    /// <summary>The user's own servers, in the order they were added.</summary>
    public IReadOnlyList<DnsServer> CustomServers => _custom;

    public bool CanApply => !IsBusy && _selectedRow is not null && _selectedAdapter is not null;

    // --- current state ----------------------------------------------------------------------

    /// <summary>What the selected adapter is actually asking, for the selected family.</summary>
    public string CurrentServers
    {
        get
        {
            if (_selectedAdapter is null) return Lang.T("current_none");
            var state = _selectedAdapter.StateFor(_family);
            if (state is null) return Lang.T("family_unavailable");

            var list = state.Effective;
            if (list.Count == 0) return Lang.T("current_none");
            return string.Join("  ", list);
        }
    }

    public string CurrentMode
    {
        get
        {
            var state = _selectedAdapter?.StateFor(_family);
            if (state is null) return string.Empty;
            return state.IsDhcp ? Lang.T("current_dhcp") : Lang.T("current_static");
        }
    }

    // --- status -----------------------------------------------------------------------------

    public string StatusText => Lang.T(_statusKey, _statusArgs);
    public string? StatusDetail => _statusDetail;
    public bool HasDetail => !string.IsNullOrEmpty(_statusDetail);

    /// <summary>Non-null when something genuinely went wrong, so it can be coloured and persisted.</summary>
    public string? ErrorText
    {
        get => _errorText;
        private set { if (_errorText == value) return; _errorText = value; Raise(); Raise(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (_isBusy == value) return; _isBusy = value; Raise(); RaiseAllCommands(); }
    }

    public bool IsTesting
    {
        get => _testing;
        private set { if (_testing == value) return; _testing = value; Raise(); }
    }

    // --- loading ----------------------------------------------------------------------------

    private void LoadPresets()
    {
        try
        {
            _builtIn = PresetStore.LoadBuiltIn();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            // A missing or unreadable embedded list must not stop the window opening: the adapter,
            // the current DNS and the flush button all still work, and the message names what
            // failed. The alternative - throwing from the constructor - is a crash on start with
            // a stack trace nobody asked for.
            ErrorText = $"{Lang.T("error_preset_read")}: {ex.Message}";
            _builtIn = Array.Empty<PresetGroup>();
        }

        var (servers, problem) = _presets.TryLoadCustom();
        _custom = servers;
        if (problem is not null)
            ErrorText = $"{Lang.T("error_custom_read")}: {Lang.T(problem.Key, problem.Args)}";
    }

    private void LoadAdapters()
    {
        _adapters = AdapterService.GetAdapters();

        if (_selectedAdapter is null || !_adapters.Any(a => a.Id == _selectedAdapter.Id))
            _selectedAdapter = _adapters.FirstOrDefault();
        else
            _selectedAdapter = _adapters.First(a => a.Id == _selectedAdapter.Id);

        RebuildVisibleServers();
    }

    private void RebuildGroups()
    {
        var current = _selectedGroup?.Kind ?? "default";
        var tabs = new List<GroupTab>
        {
            new("default", Lang.T("group_default")),
            new("family",  Lang.T("group_family")),
            new("secure",  Lang.T("group_secure")),
            new("custom",  Lang.T("group_custom")),
        };

        var chosen = tabs.FirstOrDefault(t => t.Kind == current) ?? tabs[0];
        chosen.IsSelected = true;

        Groups = tabs;
        _selectedGroup = chosen;
    }

    /// <summary>Bound by the group radio buttons through CommandParameter.</summary>
    private void SelectGroup(object? kind)
    {
        if (kind is not string k) return;
        var tab = Groups.FirstOrDefault(t => t.Kind == k);
        if (tab is null) return;

        if (_selectedGroup?.Kind == k) return;

        _selectedGroup = tab;
        foreach (var t in Groups) t.IsSelected = ReferenceEquals(t, tab);
        RebuildVisibleServers();
        RaiseAll();
    }

    /// <summary>The other language's name, which is what the button must offer to switch to.</summary>
    public string SwitchLanguageText => Lang.Current == Language.English ? "فارسی" : "English";

    /// <summary>Crescent when light, sun when dark: shows what the click will give you.</summary>
    public string ThemeGlyph => AppTheme.Current == Theme.Light ? "☾" : "☀";

    /// <summary>
    /// The word on the theme button, naming the state it will switch to.
    /// </summary>
    /// <remarks>
    /// A crescent on its own proved too quiet to notice, which is a poor trade for a control that
    /// changes the whole window. The word is offered rather than the current state, exactly as the
    /// language button does - a control that says "English" while the app is in English is a
    /// setting, whereas one that says "فارسی" is an action.
    /// </remarks>
    public string ThemeToggleText
        => Lang.T(AppTheme.Current == Theme.Light ? "theme_dark" : "theme_light");

    private IEnumerable<DnsServer> ServersIn(string kind)
    {
        if (kind == "custom") return _custom.Where(s => s.Family == _family);
        return _builtIn.Where(g => g.Kind == kind && g.Family == _family).SelectMany(g => g.Servers);
    }

    private void RebuildVisibleServers()
    {
        var keep = _selectedRow?.Server;
        IEnumerable<DnsServer> source;

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            // Search across the whole family rather than inside the selected tab. Someone looking
            // for "cloudflare" should not have to first work out which of the three lists it is in.
            var term = _searchText.Trim();
            source = _builtIn.Where(g => g.Family == _family).SelectMany(g => g.Servers)
                       .Concat(_custom.Where(s => s.Family == _family))
                       .Where(s => s.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                                || s.Primary.Contains(term, StringComparison.OrdinalIgnoreCase)
                                || (s.HasSecondary && s.Secondary.Contains(term, StringComparison.OrdinalIgnoreCase))
                                || s.Country.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            source = ServersIn(_selectedGroup?.Kind ?? "default");
        }

        // After a test, fastest first; untested entries keep their order at the end. The window
        // between reorders is why SelectedRow is restored by server identity below - otherwise
        // choosing a fast one could leave a different row selected after the sort.
        var rows = source
            .Where(s => s.IsValid)
            .Select(GetRow)
            .OrderBy(r => r.SortKey)
            .ToList();

        VisibleServers.Clear();
        foreach (var row in rows) VisibleServers.Add(row);

        _selectedRow = keep is null ? null : rows.FirstOrDefault(r => r.Server.Equals(keep));
        MarkCurrentServers();

        RaiseAll();
    }

    private ServerRow GetRow(DnsServer server)
    {
        if (_rows.TryGetValue(server, out var existing)) return existing;
        var row = new ServerRow(server, isCustom: _custom.Contains(server));
        _rows[server] = row;
        return row;
    }

    /// <summary>Flags the row that matches what the adapter is set to right now.</summary>
    private void MarkCurrentServers()
    {
        var state = _selectedAdapter?.StateFor(_family);
        var current = state is null || state.IsDhcp ? new HashSet<string>() : state.StaticServers.ToHashSet();

        foreach (var row in VisibleServers)
            row.IsCurrent = row.Server.Primary is not null && current.Contains(row.Server.Primary);
    }

    /// <summary>
    /// Compares what the adapter reports now against what the change asked for. Addresses are
    /// compared parsed rather than as text, so an IPv6 that netsh re-formatted still matches the
    /// one the user picked; a missing, DHCP-driven or unparsable state matches nothing.
    /// </summary>
    private static bool SameResolvers(DnsState? state, IReadOnlyList<string> want)
    {
        if (state is null || state.IsDhcp || state.StaticServers.Count != want.Count) return false;
        for (var i = 0; i < want.Count; i++)
        {
            if (!System.Net.IPAddress.TryParse(state.StaticServers[i], out var reported)) return false;
            if (!System.Net.IPAddress.TryParse(want[i], out var asked)) return false;
            if (!reported.Equals(asked)) return false;
        }
        return true;
    }

    /// <summary>The configuration Windows says it holds, for the detail line of a refused change.</summary>
    private static string DescribeState(DnsState? state)
        => state is null ? Lang.T("detail_state_none")
         : state.IsDhcp ? Lang.T("detail_state_dhcp")
         : string.Join(" / ", state.StaticServers);

    // --- actions ----------------------------------------------------------------------------

    private async Task ApplyAsync()
    {
        var row = _selectedRow;
        var adapter = _selectedAdapter;
        if (row is null) { SetStatus("status_nothing_selected"); return; }
        if (adapter is null) { SetStatus("status_no_adapter"); return; }

        var want = row.HasSecondary
            ? new[] { row.Server.Primary, row.Server.Secondary }
            : new[] { row.Server.Primary };

        await RunAsync(async () =>
        {
            // Recorded before the change, not after. A run that fails part-way is exactly when
            // the original is most needed, and writing afterwards would record nothing.
            _backups.RememberIfAbsent(adapter, _family);

            var result = await DnsService.ApplyAsync(adapter.Alias, _family,
                                                     row.Server.Primary,
                                                     row.HasSecondary ? row.Server.Secondary : null);

            if (!result.Ok)
            {
                SetStatus("status_failed", result.Output);
                return false;
            }

            // netsh's exit code is not a promise: a line whose parameters netsh refuses still
            // exits 0 (observed on `set dnsservers` and `add dnsservers` alike). What decides
            // is what the adapter is now set to, read straight back from the registry.
            var after = DnsService.Read(adapter.Id, _family);
            if (!SameResolvers(after, want))
            {
                SetStatus("status_not_applied", Lang.T("detail_not_applied", DescribeState(after)));
                return false;
            }

            SetStatus("status_applied", row.Server.Name);
            return true;
        });
    }

    private async Task ApplyDhcpAsync()
    {
        var adapter = _selectedAdapter;
        if (adapter is null) { SetStatus("status_no_adapter"); return; }

        await RunAsync(async () =>
        {
            _backups.RememberIfAbsent(adapter, _family);
            var result = await DnsService.UseDhcpAsync(adapter.Alias, _family);
            if (!result.Ok) { SetStatus("status_failed", result.Output); return false; }

            var after = DnsService.Read(adapter.Id, _family);
            if (after is null || !after.IsDhcp)
            {
                SetStatus("status_not_applied", Lang.T("detail_not_applied", DescribeState(after)));
                return false;
            }

            SetStatus("status_dhcp");
            return true;
        });
    }

    private async Task RestoreAsync()
    {
        var adapter = _selectedAdapter;
        if (adapter is null) { SetStatus("status_no_adapter"); return; }

        var saved = _backups.Get(adapter.Id, _family);
        if (saved is null) { SetStatus("status_nothing_restore"); return; }

        await RunAsync(async () =>
        {
            Shell.Result result;
            if (saved.WasDhcp)
            {
                result = await DnsService.UseDhcpAsync(adapter.Alias, _family);
                var after = DnsService.Read(adapter.Id, _family);
                if (after is null || !after.IsDhcp)
                {
                    SetStatus("status_not_applied", Lang.T("detail_not_applied", DescribeState(after)));
                    return false;
                }
            }
            else
            {
                result = await DnsService.ApplyAsync(adapter.Alias, _family,
                                                     saved.Servers.FirstOrDefault() ?? "",
                                                     saved.Servers.Skip(1).FirstOrDefault());
                var want = saved.Servers.ToArray();
                var after = DnsService.Read(adapter.Id, _family);
                if (!SameResolvers(after, want))
                {
                    SetStatus("status_not_applied", Lang.T("detail_not_applied", DescribeState(after)));
                    return false;
                }
            }

            if (!result.Ok) { SetStatus("status_failed", result.Output); return false; }
            _backups.Forget(adapter.Id, _family);
            SetStatus("status_restored");
            return true;
        });
    }

    private async Task FlushAsync()
    {
        await RunAsync(async () =>
        {
            var result = await DnsService.FlushAsync();
            if (!result.Ok) { SetStatus("status_failed", result.Output); return false; }
            SetStatus("status_flushed");
            return true;
        });
    }

    private async Task TestAsync()
    {
        var targets = VisibleServers.Where(r => r.Server.IsValid).ToList();
        if (targets.Count == 0) { SetStatus("status_nothing_selected"); return; }

        IsBusy = true;
        IsTesting = true;
        SetStatus("status_testing", 0, targets.Count);
        ErrorText = null;

        var answered = 0;
        var missed = 0;

        // Reports arrive through Progress, which captures the UI thread's context at construction
        // and marshals each report back onto it: the tester measures from its own worker tasks,
        // and every completed measurement fills its row in as it lands, instead of all rows
        // appearing only when the whole sweep has finished. The wording of the status stays one
        // step ahead of what has already been reported - "0 of 63" reads as the list about to be
        // tested, not as progress that never moved.
        var progress = new Progress<LatencyResult>(r =>
        {
            if (!_rows.TryGetValue(r.Server, out var row)) return;
            if (r.Ok) { row.Milliseconds = r.Milliseconds; row.TestFailed = false; row.ErrorKey = null; row.ErrorDetail = null; answered++; }
            else { row.Milliseconds = null; row.TestFailed = true; row.ErrorKey = r.Error; row.ErrorDetail = r.Detail; missed++; }

            SetStatus("status_testing", answered + missed, targets.Count);
        });

        try
        {
            // The plain await (no ConfigureAwait(false)) puts the continuation back on the UI
            // thread, which also guarantees the dispatcher has drained every posted report
            // before the final status is set - Progress posts are queued ahead of this resumption,
            // so the "done" line can never be overwritten by a late progress line.
            var results = await _tester.MeasureAllAsync(targets.Select(r => r.Server),
                                                        progress: progress);

            SetStatus("status_test_done", answered, missed);
            RebuildVisibleServers();
        }
        catch (OperationCanceledException)
        {
            SetStatus("status_ready");
        }
        catch (Exception ex)
        {
            ErrorText = $"{Lang.T("error_running")}: {ex.Message}";
            SetStatus("status_failed", ex.ToString());
        }
        finally
        {
            IsTesting = false;
            IsBusy = false;
        }
    }

    /// <summary>Runs one privileged action with the busy flag, an error trap and a state refresh.</summary>
    private async Task RunAsync(Func<Task<bool>> action)
    {
        IsBusy = true;
        ErrorText = null;
        SetStatus("status_working");

        try
        {
            var ok = await action();
            LoadAdapters();          // re-read the registry, because that is now the truth
            MarkCurrentServers();
            RaiseAll();

            if (ok) RaiseCommands();
        }
        catch (ArgumentException ex)
        {
            ErrorText = ex.Message;
            SetStatus("status_failed", ex.Message);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // The usual cause is that Windows declined the change for want of elevation.
            ErrorText = $"{Lang.T("error_running")}: {ex.Message}";
            SetStatus("status_failed", ex.ToString());
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            SetStatus("status_failed", ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Re-reads the adapters. Public because a network change arrives from outside the window and
    /// has to reach the same code the refresh button uses - two paths to the same reload would
    /// mean one of them eventually forgot to update the selection.
    /// </summary>
    public void RefreshAdapters()
    {
        LoadAdapters();
        RaiseAll();
    }

    // --- custom servers ---------------------------------------------------------------------

    /// <summary>Returns an error key when the input is refused, or null when it was saved.</summary>
    public string? TryAddCustom(string name, string primary, string secondary, DnsFamily family)
    {
        primary = (primary ?? string.Empty).Trim();
        secondary = (secondary ?? string.Empty).Trim();

        if (!System.Net.IPAddress.TryParse(primary, out var p)) return "add_invalid";

        // Parsed once here rather than twice down the checks: the address object is what the
        // family comparison needs, and the original string is what the duplicate search and the
        // saved record need. Both live in this one block.
        System.Net.IPAddress? secondaryAddress = null;
        if (!string.IsNullOrEmpty(secondary))
        {
            if (!System.Net.IPAddress.TryParse(secondary, out var parsed)) return "add_invalid";
            secondaryAddress = parsed;
        }

        var expected = family == DnsFamily.IPv4 ? System.Net.Sockets.AddressFamily.InterNetwork
                                                : System.Net.Sockets.AddressFamily.InterNetworkV6;
        if (p.AddressFamily != expected) return "add_mismatch";
        if (secondaryAddress is not null && secondaryAddress.AddressFamily != expected) return "add_mismatch";
        if (secondaryAddress is not null &&
            string.Equals(primary, secondary, StringComparison.OrdinalIgnoreCase)) return "add_same";

        if (System.Net.IPAddress.IsLoopback(p)) return "add_loopback";

        if (_custom.Any(c => string.Equals(c.Primary, primary, StringComparison.OrdinalIgnoreCase)
                          || (!string.IsNullOrEmpty(secondary)
                              && string.Equals(c.Secondary, secondary, StringComparison.OrdinalIgnoreCase))))
            return "add_duplicate";

        var label = string.IsNullOrWhiteSpace(name) ? primary : name.Trim();
        var server = new DnsServer(label, string.Empty, primary, secondary, family);

        _custom = _custom.Append(server).ToList();
        try
        {
            _presets.SaveCustom(_custom);
        }
        catch (Exception ex)
        {
            _custom = _custom.Where(c => !c.Equals(server)).ToList();
            ErrorText = ex.Message;
            // A save failure is a different fact from a read one, and the two keys say so:
            // pointing the user at their saved list would send them looking for a file that is
            // not the problem.
            return "error_custom_save";
        }

        SetStatus("status_saved");
        RebuildGroups();
        RebuildVisibleServers();
        RaiseAll();
        return null;
    }

    public void RemoveCustom(ServerRow row)
    {
        if (row is null || !row.IsCustom) return;
        RemoveCustom(row.Server);
    }

    /// <summary>
    /// Removes one of the user's own servers by identity. Takes the server rather than the row
    /// because rows are rebuilt whenever the group or the language changes, so a row handed over
    /// from an earlier moment may no longer be the one in the list.
    /// </summary>
    public void RemoveCustom(DnsServer server)
    {
        if (!_custom.Contains(server)) return;

        _custom = _custom.Where(c => !c.Equals(server)).ToList();
        _rows.Remove(server);
        try
        {
            _presets.SaveCustom(_custom);
        }
        catch (Exception ex)
        {
            // The in-memory list has already changed, so the user sees it go. Telling them the
            // file did not take is the honest half: otherwise it returns on the next launch and
            // the removal appears not to have worked at all.
            ErrorText = ex.Message;
        }

        if (_selectedRow is not null && _selectedRow.Server.Equals(server)) _selectedRow = null;
        RebuildGroups();
        RebuildVisibleServers();
        RaiseAll();
    }

    private void RemoveSelected()
    {
        if (_selectedRow is { IsCustom: true } row) RemoveCustom(row.Server);
    }

    // --- plumbing ---------------------------------------------------------------------------

    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        // Only a refusal or a silent non-application carries a detail worth expanding: the raw
        // netsh output is what names the former, the read-back state what names the latter. For
        // every other status the arguments are the message's own substitutions and would read as
        // noise if shown twice.
        _statusDetail = (key == "status_failed" || key == "status_not_applied") && args.Length > 0
            ? args[^1]?.ToString() : null;
        Raise();
        Raise(nameof(StatusText));
        Raise(nameof(StatusDetail));
        Raise(nameof(HasDetail));
    }

    private void OnLanguageChanged()
    {
        RebuildGroups();
        foreach (var row in _rows.Values) row.RefreshText();
        RebuildVisibleServers();
        RaiseAll();
        Raise(nameof(SwitchLanguageText));
    }

    private void RaiseAllCommands()
    {
        ApplyCommand.RaiseCanExecuteChanged();
        ApplyDhcpCommand.RaiseCanExecuteChanged();
        TestCommand.RaiseCanExecuteChanged();
        FlushCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommands()
    {
        ApplyCommand.RaiseCanExecuteChanged();
        TestCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }

    /// <summary>An empty property name makes WPF re-read every binding on this object.</summary>
    private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose() => Lang.Changed -= OnLanguageChanged;
}
