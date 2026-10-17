using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace DnsPilot.Ui;

/// <summary>The two languages the app ships. English is the default, so its strings are authoritative.</summary>
public enum Language { English, Persian }

/// <summary>
/// Every visible string in the app, in both languages, in one place.
/// </summary>
/// <remarks>
/// A dictionary rather than .resx because this is one window: satellite assemblies and a
/// designer buy nothing at this size, while a key that nobody wrote throws here - at the moment
/// the text is asked for - instead of quietly rendering the key itself in the window.
///
/// Keys use underscores rather than dots. The view model exposes this dictionary through an
/// indexer so XAML can say <c>{Binding [action_apply]}</c>, and a dot inside indexer brackets is
/// exactly the shape that WPF's path parser may read as property traversal. If it did, the label
/// would not error - it would render empty, and an empty label looks like an unpopulated view
/// rather than a broken binding. Underscores cost nothing and remove the question.
///
/// Persian is written here as plain UTF-8 source. Building Persian from character codes is a
/// workaround for a shell that mangles encoding, not a way to store text. The Persian yeh is
/// U+06CC (ی), not U+064A (ي): the two look similar in isolation but U+064A joins as the
/// Arabic-form yeh and makes every affected word read wrong to a Persian reader.
/// </remarks>
public static class Lang
{
    public static Language Current { get; private set; } = Language.English;

    public static bool IsRtl => Current == Language.Persian;

    /// <summary>Raised after the language has changed, for anything that caches text.</summary>
    public static event Action? Changed;

    public static void Set(Language language)
    {
        if (Current == language) return;
        Current = language;
        Thread.CurrentThread.CurrentUICulture = language == Language.Persian
            ? new CultureInfo("fa-IR")
            : new CultureInfo("en-US");
        Changed?.Invoke();
    }

    public static void Toggle() => Set(Current == Language.English ? Language.Persian : Language.English);

    /// <summary>Every key in the table. Exists so a test can sweep all of them rather than spot-check.</summary>
    public static IReadOnlyCollection<string> Keys => Text.Keys;

    /// <summary>Looks up a key. Throws rather than returning the key, so a gap is impossible to miss.</summary>
    public static string T(string key)
    {
        if (!Text.TryGetValue(key, out var pair))
            throw new KeyNotFoundException($"no text was written for '{key}'.");
        return Current == Language.Persian ? pair.Fa : pair.En;
    }

    /// <summary>As <see cref="T"/>, with the template's placeholders filled in.</summary>
    public static string T(string key, params object[] args)
    {
        var template = T(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            // A template whose placeholders do not match its arguments is a text bug, not a
            // reason to show the user an empty status line.
            return template;
        }
    }

    private static readonly Dictionary<string, (string En, string Fa)> Text = new()
    {
        ["app_name"]            = ("DnsPilot", "DnsPilot"),
        ["app_tagline"]         = ("Pick the resolver your machine asks", "سرور DNS مورد انتخاب ماشین شما"),

        ["adapter"]             = ("Network adapter", "کارت شبکه"),
        ["adapter_none"]        = ("No usable network adapter was found", "کارت شبکه‌ای پیدا نشد"),
        ["adapter_none_hint"]   = ("Connect to a network, then open this window again.", "به شبکه وصل شوید و این پنجره را دوباره باز کنید."),

        ["family"]              = ("Address family", "نسخه آدرس"),
        ["family_ipv4"]         = ("IPv4", "IPv4"),
        ["family_ipv6"]         = ("IPv6", "IPv6"),
        ["family_unavailable"]  = ("This adapter is not configured for that family", "این کارت شبکه برای این نسخه تنظیم نشده است"),

        ["current"]             = ("Currently using", "در حال حاضر"),
        ["current_dhcp"]        = ("Automatic, from the router", "خودکار، از روتر"),
        ["current_none"]        = ("Nothing set", "چیزی تنظیم نشده"),
        ["current_static"]      = ("Set by hand", "دستی تنظیم شده"),
        ["current_dhcp_servers"] = ("{0} (automatic)", "{0} (خودکار)"),

        ["group_default"]       = ("General", "عمومی"),
        ["group_family"]        = ("Family", "خانوادگی"),
        ["group_secure"]        = ("Secure", "امن"),
        ["group_custom"]        = ("Mine", "سفارشی"),
        ["group_custom_empty"]  = ("You have not added a server yet", "هنوز سروری اضافه نکرده‌اید"),
        ["group_empty"]         = ("This list has no servers for that address family", "این فهرست برای این نسخه آدرس سروری ندارد"),

        ["search"]              = ("Find a server", "جستجوی سرور"),
        ["search_none"]         = ("No server matches that", "سروری با این عبارت پیدا نشد"),

        ["action_apply"]        = ("Use this server", "استفاده از این سرور"),
        ["action_apply_dhcp"]   = ("Use automatic", "استفاده از خودکار"),
        ["action_test"]         = ("Test speed", "تست سرعت"),
        ["action_flush"]        = ("Clear cache", "پاک کردن کش"),
        ["action_restore"]      = ("Restore original", "بازگردانی حالت اصلی"),
        ["action_add"]          = ("Add server", "افزودن سرور"),
        ["action_delete"]       = ("Remove", "حذف"),
        ["action_cancel"]       = ("Cancel", "انصراف"),
        ["action_save"]         = ("Save", "ذخیره"),

        ["latency_untested"]    = ("—", "—"),
        ["latency_failed"]      = ("no answer", "بدون پاسخ"),
        ["latency_ms"]          = ("{0} ms", "{0} میلی‌ثانیه"),
        ["latency_colon"]       = ("{0} ms", "{0} میلی‌ثانیه"),

        // Why a server did not answer. These are worth distinguishing rather than collapsing into
        // one "no answer": a machine with no IPv6 connectivity reports the same blank result for
        // every IPv6 server in the list, and reading that as "all these servers are dead" is both
        // wrong and alarming. In that case the fault is the machine's, and the words should say so.
        //
        // Kept to a token rather than a phrase because this string sits in a fixed-width column
        // beside server names that already run to thirty-five characters, and the window is
        // deliberately small. Each one is self-explanatory on its own, so no second line is
        // needed to unpack it.
        ["latency_reason_no_answer"]        = ("no answer", "بدون پاسخ"),
        ["latency_reason_no_route"]         = ("no route", "بدون مسیر"),
        ["latency_reason_refused"]          = ("refused", "رد شد"),
        ["latency_reason_host_unreachable"] = ("unreachable", "در دسترس نیست"),
        ["latency_reason_bad_address"]      = ("bad address", "آدرس نامعتبر"),
        ["latency_reason_rcode"]            = ("error {0}", "خطا {0}"),
        ["latency_reason_truncated"]        = ("truncated", "ناقص"),
        ["latency_reason_unknown"]          = ("unreadable", "نامفهوم"),

        ["add_title"]           = ("Add your own server", "افزودن سرور دلخواه"),
        ["add_name"]            = ("Name", "نام"),
        ["add_name_hint"]       = ("Something you will recognise later", "هرچیزی که بعداً بشناسید"),
        ["add_primary"]         = ("Primary address", "آدرس اصلی"),
        ["add_secondary"]       = ("Secondary address, if it has one", "آدرس فرعی، اگر دارد"),
        ["add_invalid"]         = ("That is not a valid IP address", "این یک آدرس IP معتبر نیست"),
        ["add_mismatch"]        = ("The address does not match its family", "آدرس با نسخه‌اش هم‌خوان نیست"),
        ["add_duplicate"]       = ("That address is already in the list", "این آدرس از قبل در فهرست هست"),
        ["add_loopback"]        = ("A loopback address would point the machine at itself", "آدرس حلقه برگشت، ماشین را به خودش اشاره می‌کند"),
        ["add_same"]            = ("Primary and secondary are the same address", "آدرس اصلی و فرعی یکسان‌اند"),

        ["status_ready"]        = ("Ready", "آماده"),
        ["status_working"]      = ("Applying…", "در حال اعمال…"),
        ["status_applied"]      = ("Now using {0}", "اکنون از {0} استفاده می‌شود"),
        ["status_dhcp"]         = ("Back to automatic", "بازگشت به خودکار"),
        // Said when netsh claimed success and the registry said otherwise: with the exit code
        // unreliable (a refused line still exits 0), what marks a real application is the
        // configuration read back from the adapter, and its absence gets its own sentence.
        ["status_not_applied"]  = ("The change did not take effect", "تغییر اعمال نشد"),
        ["detail_not_applied"]  = ("Windows still reports this configuration: {0}", "ویندوز همچنان این پیکربندی را گزارش می‌کند: {0}"),
        ["detail_state_none"]   = ("no configuration for this family", "هیچ پیکربندی‌ای برای این خانواده نیست"),
        ["detail_state_dhcp"]   = ("DHCP (automatic)", "DHCP (خودکار)"),
        ["status_flushed"]      = ("Resolver cache cleared", "کش DNS پاک شد"),
        ["status_restored"]     = ("Restored to what it was before", "حالت قبلی بازگردانده شد"),
        ["status_nothing_restore"] = ("Nothing has been recorded to restore", "چیزی برای بازگردانی ثبت نشده است"),
        ["status_failed"]       = ("Windows refused that change", "ویندوز این تغییر را نپذیرفت"),
        ["status_no_adapter"]   = ("No adapter is selected", "کارت شبکه‌ای انتخاب نشده است"),
        ["status_nothing_selected"] = ("Choose a server first", "ابتدا یک سرور انتخاب کنید"),
        ["status_testing"]      = ("Measuring {0} of {1} servers…", "در حال اندازه‌گیری {0} از {1} سرور…"),
        ["status_test_done"]    = ("{0} answered, {1} did not", "{0} پاسخ داد، {1} پاسخ نداد"),
        ["status_saved"]        = ("Saved", "ذخیره شد"),

        ["language"]            = ("Language", "زبان"),
        ["theme"]               = ("Theme", "پوسته"),
        // The tray icon opens the window on a single click, which is what the tip must say;
        // "double-click" described a click pattern the code never had.
        ["tray_show"]           = ("Show window", "نمایش پنجره"),
        ["tray_exit"]           = ("Exit", "خروج"),
        ["tray_tip"]            = ("DnsPilot, click to open", "DnsPilot، برای باز کردن کلیک کنید"),

        ["error_detail"]        = ("Details", "جزئیات"),
        ["error_running"]       = ("DnsPilot could not run its helper", "اجرای ابزار کمکی DnsPilot ممکن نشد"),
        ["error_custom_read"]   = ("Your saved servers had a problem", "در سرورهای ذخیره‌شده شما مشکلی پیش آمد"),
        // The three reasons behind "error_custom_read", spoken separately so the sentence can
        // say which file failed, and how much of it was lost. The unreadable case carries the
        // exception message Windows wrote, which is phrased in the system language.
        ["custom_skipped"]      = ("{0} of your saved servers were skipped, because their addresses are not valid", "{0} سرور از سرورهای ذخیره‌شدهٔ شما نادیده گرفته شد، چون آدرس‌هایش معتبر نیستند"),
        ["custom_unreadable"]   = ("the file could not be read ({0})", "فایل خواندنی نبود ({0})"),
        ["error_custom_save"]   = ("Your server could not be saved", "سرور شما ذخیره نشد"),
        ["error_preset_read"]   = ("The built-in server list could not be loaded", "فهرست سرورهای داخلی بارگذاری نشد"),
        ["error_adapter_gone"]  = ("That adapter is no longer available", "آن کارت دیگر در دسترس نیست"),
        ["error_ipv6_unsupported"] = ("Windows has no IPv6 configuration for that adapter", "ویندوز پیکربندی IPv6 برای آن کارت ندارد"),
    };
}
