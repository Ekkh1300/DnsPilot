using System;
using System.IO;
using System.Text;
using System.Windows;

using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Threading;

namespace DnsPilot;

public partial class App : Application
{
    private const string LogName = "dnspilot.log";

    /// <summary>
    /// Attaches before the window opens, so a failure during start-up is written down rather than
    /// becoming an unhandled-exception dialog that disappears and leaves no evidence of what
    /// happened.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;

        base.OnStartup(e);
        Log($"DnsPilot starting, version {typeof(App).Assembly.GetName().Version}");
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("unhandled: " + e.Exception);

        // The window may still be usable, and if it is not the message says so plainly. Swallowing
        // the exception without either showing it or recording it would be the worst of the three.
        MessageBox.Show(
            $"{Ui.Lang.T("error_running")}\n\n{e.Exception.Message}",
            Ui.Lang.T("app_name"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
        Log("handled and continued");
    }

    private static void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
        => Log("fatal: " + e.ExceptionObject);

    /// <summary>Appends to a small log next to the user's own data, with a size cap.</summary>
    internal static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DnsPilot");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, LogName);

            // No rotation scheme is needed for an app this small, but an unbounded file is one
            // that grows forever if something starts failing repeatedly - a megabyte of the same
            // exception every launch is a problem the user cannot fix. Truncate instead.
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                File.WriteAllText(path, string.Empty, Encoding.UTF8);

            File.AppendAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Logging must never be the thing that stops the app from running.
        }
    }
}
