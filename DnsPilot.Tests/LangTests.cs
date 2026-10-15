using System.Globalization;

using DnsPilot.Ui;

using Xunit;

namespace DnsPilot.Tests;

/// <summary>
/// The text table: every key in both languages, and the two things about Persian that are
/// invisible in a diff and obvious on screen.
/// </summary>
/// <remarks>
/// These run against every key rather than a sample. A gap in the table does not degrade
/// gracefully - Lang.T throws, so one unwritten key would take the whole window down the first
/// time that label was asked for, and a sweep is the only way to be sure none is missing.
/// </remarks>
public class LangTests : IDisposable
{
    public LangTests()
    {
        // The language is global and the tests run in one process, so it is pinned at the start
        // of each test and put back at the end rather than left wherever the last one stopped.
        Lang.Set(Language.English);
    }

    public void Dispose() => Lang.Set(Language.English);

    [Fact]
    public void Every_key_has_text_in_both_languages()
    {
        Assert.NotEmpty(Lang.Keys);

        foreach (var key in Lang.Keys)
        {
            Lang.Set(Language.English);
            var en = Lang.T(key);
            Assert.False(string.IsNullOrWhiteSpace(en), $"'{key}' has no English text");

            Lang.Set(Language.Persian);
            var fa = Lang.T(key);
            Assert.False(string.IsNullOrWhiteSpace(fa), $"'{key}' has no Persian text");
        }
    }

    [Fact]
    public void A_key_that_was_never_written_is_named_rather_than_shown()
    {
        // Returning the key itself would put "status_ready" in the status bar, which reads as a
        // working app with odd wording. Throwing turns it into a failure at the moment it happens.
        Assert.Throws<KeyNotFoundException>(() => Lang.T("no_such_key"));
    }

    [Fact]
    public void Persian_never_uses_the_Arabic_form_of_yeh()
    {
        // U+064A and U+06CC both render as a dotless yeh and a reader of either language can tell
        // them apart the moment they join a word. The Arabic form is wrong in Persian and this is
        // the kind of mistake that survives a code review because both characters look like "ي".
        Lang.Set(Language.Persian);

        foreach (var key in Lang.Keys)
        {
            var text = Lang.T(key);
            var index = text.IndexOf('\u064A');
            Assert.True(index < 0,
                $"'{key}' = '{text}' contains the Arabic yeh U+064A at index {index}");
        }
    }

    [Fact]
    public void Persian_text_actually_contains_the_Persian_yeh_somewhere()
    {
        // The counterpart: if the whole table were accidentally Arabic-form, or the Persian column
        // were copied from English, the check above would pass for the wrong reason.
        Lang.Set(Language.Persian);

        Assert.Contains(Lang.Keys, k => Lang.T(k).Contains('\u06CC'));
    }

    [Fact]
    public void The_two_languages_differ_for_the_labels_that_matter()
    {
        var translated = new[]
        {
            "adapter", "current", "action_apply", "action_test",
            "group_default", "group_family", "group_secure", "search",
        };

        foreach (var key in translated)
        {
            Lang.Set(Language.English);
            var en = Lang.T(key);
            Lang.Set(Language.Persian);
            var fa = Lang.T(key);

            Assert.NotEqual(en, fa);
        }
    }

    [Fact]
    public void Placeholders_are_filled_in()
    {
        Lang.Set(Language.English);
        Assert.Equal("Now using Google Public DNS", Lang.T("status_applied", "Google Public DNS"));

        Lang.Set(Language.Persian);
        Assert.Contains("Google Public DNS", Lang.T("status_applied", "Google Public DNS"));
    }

    [Fact]
    public void A_template_asked_for_without_its_argument_still_returns_words()
    {
        // string.Format throws when {0} has no value. Reporting the template keeps the status bar
        // readable instead of empty; the alternative is an empty bar and no clue why.
        Lang.Set(Language.English);
        var text = Lang.T("status_applied");

        Assert.Contains("Now using", text);
    }

    [Fact]
    public void Changing_the_language_announces_itself()
    {
        var raised = 0;
        void Handler() => raised++;

        Lang.Changed += Handler;
        try
        {
            Lang.Toggle();   // to Persian
            Assert.Equal(1, raised);
            Assert.True(Lang.IsRtl);

            Lang.Toggle();   // back to English
            Assert.Equal(2, raised);
            Assert.False(Lang.IsRtl);
        }
        finally
        {
            Lang.Changed -= Handler;
        }
    }

    [Fact]
    public void Setting_the_same_language_again_changes_nothing()
    {
        var raised = 0;
        void Handler() => raised++;

        Lang.Changed += Handler;
        try
        {
            Lang.Set(Language.English);
            Assert.Equal(0, raised);
        }
        finally
        {
            Lang.Changed -= Handler;
        }
    }

    [Fact]
    public void Switching_language_leaves_the_number_formatting_culture_alone()
    {
        // The UI culture and the formatting culture are different things. If a language switch
        // also moved CurrentCulture, latency would render in Persian digits while the IP
        // addresses beside it stayed Latin - two numerals in one row - and the change would
        // happen without any code touching the numbers at all.
        var before = CultureInfo.CurrentCulture;

        Lang.Set(Language.Persian);
        Assert.Equal(before, CultureInfo.CurrentCulture);

        Lang.Set(Language.English);
        Assert.Equal(before, CultureInfo.CurrentCulture);
    }

    [Fact]
    public void Latency_reads_the_same_number_in_either_language()
    {
        Lang.Set(Language.Persian);
        Assert.Equal("143 میلی‌ثانیه", Lang.T("latency_ms", 143));

        Lang.Set(Language.English);
        Assert.Equal("143 ms", Lang.T("latency_ms", 143));
    }
}
