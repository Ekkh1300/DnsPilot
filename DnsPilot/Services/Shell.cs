using System.Diagnostics;
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
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };

        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        if (!p.Start()) return new Result(-1, "the command could not be started");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        string text;
        lock (output) text = output.ToString();
        return new Result(p.ExitCode, text);
    }
}
