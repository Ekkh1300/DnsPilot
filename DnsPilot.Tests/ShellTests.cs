using DnsPilot.Services;

using Xunit;

namespace DnsPilot.Tests;

/// <summary>
/// Runs <see cref="Shell"/> against the real netsh that ships on the machine. The commands are
/// reads only - <c>show dnsservers</c> prints the current configuration and changes nothing -
/// because automated tests are not allowed to touch the machine's configuration. What is under
/// test is the runner itself: the arguments arrive as separate tokens, the process is trimmed of
/// a console window, the bytes come back decodeable, and the exit code means what Windows made
/// netsh return rather than what the pipe contained.
/// </summary>
public class ShellTests
{
    [Fact]
    public async Task Shown_Servers_Reads_Real_Netsh()
    {
        var result = await Shell.RunAsync("netsh.exe",
            new[] { "interface", "ipv4", "show", "dnsservers" });

        Assert.True(result.Ok, $"netsh exited {result.ExitCode}: {result.Output}");
        Assert.True(result.Output.Length > 0, "netsh produced no output at all");
    }

    [Fact]
    public async Task Rejects_Commands_Outside_The_Allowed_List()
    {
        // netstat is not on the list; the guard is what stops a caller from aiming the runner
        // at a program the app has no business starting.
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Shell.RunAsync("netstat.exe", new[] { "-ano" }));
    }

    [Fact]
    public async Task Netsh_Refused_Line_Still_Exits_Zero()
    {
        // Observed behaviour, twice confirmed: `set dnsservers` without its essential parameters
        // prints its usage text and STILL exits 0. The exit code alone can therefore not be the
        // app's success signal - which is why every mutating path in the view model reads the
        // adapter's configuration back from the registry before it claims anything happened.
        var result = await Shell.RunAsync("netsh.exe", new[] { "interface", "ipv4", "set", "dnsservers" });

        Assert.True(result.Ok, "netsh reported a non-zero exit for a refused line");
        Assert.True(result.Output.Length > 40, "the refusal names itself in text, not in the exit code");
    }
}
