using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace DnsPilot.Services;

/// <summary>
/// Runs the two external commands this app needs - netsh and ipconfig.
///
/// Arguments are passed one per element through <see cref="ProcessStartInfo.ArgumentList"/>,
/// which makes Windows quote each of them. There is no cmd.exe and no string concatenation, so
/// a value containing a quote or an ampersand is passed through as data and never becomes a
/// second command. That is the whole reason this wrapper exists rather than a call to
/// Process.Start with a command line built by hand.
/// </summary>
public static class Shell
{
    public readonly record struct Result(int ExitCode, string Output)
    {
        public bool Ok => ExitCode == 0;
    }

    /// <summary>Only these two may ever be run. Checked by name so a caller cannot aim elsewhere.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "netsh.exe", "ipconfig.exe"
    };

    static Shell()
    {
        // The OEM code page below is not UTF-8 and not ASCII, and in .NET Core such code pages
        // are only reachable once this provider is registered. The provider is part of the
        // Windows desktop runtime, so this is one line of setup, not a dependency; if it fails
        // the decode falls back silently rather than throwing at the user.
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* the decode falls back to UTF-8 replacement, which is still text */ }
    }

    public static async Task<Result> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var name = Path.GetFileName(file);
        if (!Allowed.Contains(name))
            throw new ArgumentException($"'{name}' is not one of the commands DnsPilot is allowed to run.", nameof(file));

        foreach (var a in args)
            if (a.Contains('\0')) throw new ArgumentException("a null byte in an argument is never valid.", nameof(args));

        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };

        if (!p.Start()) return new Result(-1, "the command could not be started");

        // Read the raw bytes rather than letting Process decode them. netsh writes its output in
        // the console code page of the redirected pipe - the OEM code page of the system locale -
        // and forcing UTF-8 there turned a refusal on, say, a Persian or German Windows into
        // mojibake before it ever reached the details box. Decoding is done at the end, once the
        // complete byte stream is known, so the best decode can be chosen.
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();

        var pumps = new[]
        {
            PumpAsync(p.StandardOutput.BaseStream, stdout, ct),
            PumpAsync(p.StandardError.BaseStream, stderr, ct),
        };

        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            // Killing the child closes its pipe, so the pumps reach end-of-stream at once;
            // their cancellation exceptions are expected, not an error worth surface here.
            try { await Task.WhenAll(pumps).ConfigureAwait(false); } catch { /* cancelled */ }
            throw;
        }

        // Both pumps run to end-of-stream, which only happens once the child's pipe closes - so
        // returning from WhenAll already means every byte has been read. No async reader events
        // to flush afterwards.
        await Task.WhenAll(pumps).ConfigureAwait(false);

        var text = Join(Decode(stdout.ToArray()), Decode(stderr.ToArray()));
        return new Result(p.ExitCode, text);
    }

    /// <summary>Copies one output pipe into memory until the child stops writing.</summary>
    private static async Task PumpAsync(Stream source, MemoryStream target, CancellationToken ct)
    {
        var buffer = new byte[4096];
        int n;
        while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            target.Write(buffer, 0, n);
    }

    /// <summary>
    /// Chooses the decode for one pipe's bytes. Strict UTF-8 is tried first: it is the console
    /// code page when the system locale is set to UTF-8, and a machine-configured Windows can
    /// get there by the "Beta: Use Unicode UTF-8" setting. That failing, the bytes are the OEM
    /// code page the console was actually given - read from the installed system locale, not
    /// from the thread's culture, because a user running the app in Persian on a German system
    /// still writes netsh output in German.
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                return Encoding.GetEncoding(CultureInfo.InstalledUICulture.TextInfo.OEMCodePage).GetString(bytes);
            }
            catch
            {
                // Registered provider missing or an exotic code page: still better to show
                // replacement characters than to lose the reply entirely.
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }

    private static string Join(string stdout, string stderr)
    {
        if (stdout.Length == 0) return stderr;
        if (stderr.Length == 0) return stdout;
        return stdout + Environment.NewLine + stderr;
    }
}
