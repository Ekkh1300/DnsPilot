using System.ComponentModel;
using System.Runtime.CompilerServices;

using DnsPilot.Models;

namespace DnsPilot.Ui;

/// <summary>
/// One resolver as the list shows it, with its measurement attached.
/// </summary>
/// <remarks>
/// Rows are their own objects rather than plain DnsServer values because the test fills results
/// in one at a time, from a background thread, while the list is on screen. Without a row object
/// per entry there is nothing to notify about, and the alternative - rebuilding the whole
/// collection when each answer arrives - would reorder the list under the user's cursor while
/// they are still deciding which row to pick.
///
/// "No answer" is carried as a real state, not as an absent measurement. A server that did not
/// respond is information the user needs in order to choose; showing it as a blank would read as
/// "not measured yet" and invite the same test to be run again.
/// </remarks>
public sealed class ServerRow : INotifyPropertyChanged
{
    private int? _milliseconds;
    private bool _failed;
    private bool _isCurrent;
    private string? _errorKey;
    private string? _errorDetail;

    public ServerRow(DnsServer server, bool isCustom = false)
    {
        Server = server;
        IsCustom = isCustom;
    }

    public DnsServer Server { get; }
    public bool IsCustom { get; }

    public string Name => Server.Name;
    public string Country => Server.Country;
    public bool HasCountry => !string.IsNullOrEmpty(Server.Country);
    public string Primary => Server.Primary;
    public string Secondary => Server.Secondary;
    public bool HasSecondary => Server.HasSecondary;

    /// <summary>True for the resolver currently configured on the adapter being shown.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent == value) return; _isCurrent = value; Raise(); }
    }

    public int? Milliseconds
    {
        get => _milliseconds;
        set { if (_milliseconds == value) return; _milliseconds = value; Raise(); Raise(nameof(LatencyText)); Raise(nameof(LatencyBrush)); }
    }

    public bool TestFailed
    {
        get => _failed;
        set { if (_failed == value) return; _failed = value; Raise(); Raise(nameof(LatencyText)); Raise(nameof(LatencyBrush)); }
    }

    /// <summary>
    /// Why the last test failed, as a Lang key, and the one value some reasons need.
    /// </summary>
    /// <remarks>
    /// Kept as a key rather than finished text so it can be rendered in the language the app is
    /// currently in, and re-rendered when that language changes.
    ///
    /// The alternative - one flat "no answer" for every failure - loses the distinction that
    /// matters most here. On a machine with no IPv6 connectivity, every IPv6 resolver fails
    /// identically and none of them is at fault; saying so would read as a verdict on twenty-four
    /// servers that are, as it happens, some of the most widely used in the world.
    /// </remarks>
    public string? ErrorKey
    {
        get => _errorKey;
        set { if (_errorKey == value) return; _errorKey = value; Raise(); Raise(nameof(LatencyText)); }
    }

    public string? ErrorDetail
    {
        get => _errorDetail;
        set { if (_errorDetail == value) return; _errorDetail = value; Raise(); Raise(nameof(LatencyText)); }
    }

    public bool IsTested => _milliseconds.HasValue;

    /// <summary>Measured, gave up, or not yet asked - three different things, three different words.</summary>
    public string LatencyText
    {
        get
        {
            if (_failed)
            {
                // Fall back to the generic wording when the tester gave no reason, so a row can
                // still be marked failed by hand without displaying a blank.
                return _errorKey is null
                    ? Lang.T("latency_failed")
                    : Lang.T(_errorKey, _errorDetail ?? string.Empty);
            }

            if (_milliseconds is null) return Lang.T("latency_untested");
            return Lang.T("latency_ms", _milliseconds.Value);
        }
    }

    /// <summary>
    /// Green below 50 ms, amber above 150, and the accent between them: the two ends are the
    /// judgement calls a user actually makes ("fine" and "too slow"), and the middle is left
    /// neutral so the scale does not turn into three shouting colours.
    /// </summary>
    public string LatencyBrush
    {
        get
        {
            if (_failed) return "Brush.Error";
            if (_milliseconds is null) return "Brush.Muted";
            if (_milliseconds <= 50) return "Brush.Good";
            if (_milliseconds > 150) return "Brush.Slow";
            return "Brush.Muted";
        }
    }

    /// <summary>Used for ordering after a test: measured first and fast first, unanswered last.</summary>
    public int SortKey => _milliseconds ?? int.MaxValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Re-reads the text-only properties after a language change.</summary>
    public void RefreshText()
    {
        Raise(nameof(LatencyText));
        Raise(nameof(LatencyBrush));
        Raise(nameof(IsCurrent));
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
