using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using DnsPilot.Models;

namespace DnsPilot.Services;

/// <summary>One server's measurement. A server that did not answer is a result too, not an exception.</summary>
/// <remarks>
/// <paramref name="error"/> is a Lang key rather than English text, so the reason can be shown in
/// whichever language the app is running in. <paramref name="detail"/> carries the one parameter
/// some reasons need, such as the response code. Nothing in Services formats them: the reason is
/// only turned into words at the edge, in the UI, where the current language is known.
/// </remarks>
public sealed record LatencyResult(DnsServer Server, int? Milliseconds, string? Error, string? Detail = null)
{
    public bool Ok => Milliseconds.HasValue;

    public static LatencyResult Fail(DnsServer server, string error, string? detail = null)
        => new(server, null, error, detail);
}

/// <summary>
/// Measures how long a resolver takes to answer a real question.
/// </summary>
/// <remarks>
/// Two details decide whether the number means anything.
///
/// First, the query is a real DNS question on the wire rather than an ICMP echo: some resolvers
/// do not answer ping at all, and a ping would then rank a good server as a dead one. The
/// question is a small, well-known name that every public resolver has in cache or resolves
/// without effort.
///
/// Second, the minimum of several attempts is used rather than an average. The first packet to
/// a resolver pays for an ARP or neighbour lookup and any queue on the path; averaging keeps that
/// cost in the result, and the figure that is useful for choosing a server is how fast it is
/// when nothing is in the way. Two attempts is enough to leave the first one behind without
/// making the whole list slow to test.
///
/// Nothing here changes the machine. It only asks a question and waits for the answer, so it is
/// safe to run while another test is in progress.
/// </remarks>
public sealed class LatencyTester
{
    /// <summary>
    /// The name every server is asked about. Overridable so the target is not a magic string
    /// buried in the code.
    /// </summary>
    /// <remarks>
    /// example.com rather than something more specific, for two reasons.
    ///
    /// It is cached by essentially every resolver on earth, so the figure is dominated by the
    /// round trip to the server - which is the thing being ranked - rather than by how long a
    /// cold recursive resolution happened to take on this occasion.
    ///
    /// More importantly, the obvious choice for a connectivity probe is www.msftconnecttest.com,
    /// and that is precisely the name an ISP is likely to answer itself. Windows uses it to
    /// decide whether it has internet access, so a provider that wants its captive portal or its
    /// own resolver reached first has every reason to intercept it. On the network this was
    /// measured against, asking that name produced readings 30-40% higher and far more scattered
    /// than asking example.com from the same servers seconds later - which is the signature of
    /// an interception being timed rather than the resolver being measured. A latency figure
    /// that depends on the ISP's own plumbing is not a figure that can compare two resolvers.
    ///
    /// The name is deliberately not a Microsoft, Google or Cloudflare property either: none of
    /// them should be the referee in a contest between them.
    /// </remarks>
    public string ProbeName { get; init; } = "example.com";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// How many times to ask before giving up on a server.
    /// </summary>
    /// <remarks>
    /// Three rather than two, with the minimum taken. The first packet to a resolver pays for a
    /// neighbour lookup, and the second can still land behind a retransmission, so a single
    /// sample is often an outlier. Three is enough for the minimum to reflect the server rather
    /// than the path's mood, without turning a full sweep of the list into a wait - a server that
    /// answers stops asking immediately, so this only costs anything on servers that are dead.
    /// </remarks>
    public int Attempts { get; init; } = 3;

    /// <summary>
    /// The query type follows the family: an IPv6-only server that is asked for an A record can
    /// still answer, but asking for the family's own record type exercises the path the machine
    /// would actually use.
    /// </summary>
    private static ushort QueryTypeFor(DnsServer server)
        => server.Family == DnsFamily.IPv4 ? DnsWire.TypeA : DnsWire.TypeAAAA;

    public Task<LatencyResult> MeasureAsync(DnsServer server, CancellationToken ct = default)
        => MeasureAsync(server, PrimaryEndpoint(server), ct);

    /// <summary>Separates choosing where to ask from asking, so the endpoint choice is inspectable.</summary>
    public static IPEndPoint PrimaryEndpoint(DnsServer server)
    {
        if (!IPAddress.TryParse(server.Primary, out var ip))
            throw new ArgumentException($"'{server.Primary}' is not an address to ask.", nameof(server));
        return new IPEndPoint(ip, 53);
    }

    public async Task<LatencyResult> MeasureAsync(DnsServer server, IPEndPoint endpoint, CancellationToken ct = default)
    {
        if (!server.IsValid)
            return LatencyResult.Fail(server, "latency_reason_bad_address");

        var best = int.MaxValue;
        string? lastError = "latency_reason_no_answer";
        string? lastDetail = null;

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (ms, error, detail) = await QueryOnceAsync(endpoint, QueryTypeFor(server), ct).ConfigureAwait(false);

            if (ms is not null)
            {
                best = Math.Min(best, ms.Value);
                // One good measurement is the whole answer; further attempts cannot improve it.
                break;
            }

            if (error is not null)
            {
                lastError = error;
                lastDetail = detail;
            }
        }

        return best == int.MaxValue
            ? LatencyResult.Fail(server, lastError!, lastDetail)
            : new LatencyResult(server, best, null);
    }

    private async Task<(int? Ms, string? ErrorKey, string? Detail)> QueryOnceAsync(IPEndPoint endpoint, ushort qtype, CancellationToken ct)
    {
        var id = DnsWire.NewId();
        var query = DnsWire.BuildQuery(id, ProbeName, qtype);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        var watch = Stopwatch.StartNew();

        try
        {
            using var udp = new UdpClient(endpoint.AddressFamily);
            await udp.SendAsync(query, endpoint, timeout.Token).ConfigureAwait(false);

            var reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            watch.Stop();

            var kind = DnsWire.Inspect(reply.Buffer, id, out var rcode);
            return kind switch
            {
                DnsWire.ReplyKind.Answer       => ((int?)watch.ElapsedMilliseconds, (string?)null, (string?)null),
                DnsWire.ReplyKind.WrongId      => (null, "latency_reason_unknown", null),
                DnsWire.ReplyKind.NotAResponse => (null, "latency_reason_unknown", null),
                DnsWire.ReplyKind.ServerError  => (null, "latency_reason_rcode", rcode.ToString()),
                DnsWire.ReplyKind.TooShort     => (null, "latency_reason_truncated", null),
                _                              => (null, "latency_reason_unknown", null),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller stopped it, which is different from the server not answering
        }
        catch (OperationCanceledException)
        {
            return (null, "latency_reason_no_answer", null);
        }
        catch (SocketException ex)
        {
            // NetworkUnreachable is the one worth naming precisely. A machine with no route to the
            // address family fails every server in that family the same way, so reporting it as
            // "unreachable" reads as a verdict on the servers when it is a fact about this
            // machine - and on a network without IPv6 it would apply to all of them at once.
            return (null, ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "latency_reason_refused",
                SocketError.NetworkUnreachable => "latency_reason_no_route",
                SocketError.HostUnreachable => "latency_reason_host_unreachable",
                _ => "latency_reason_no_answer",
            }, null);
        }
        catch (ArgumentException)
        {
            return (null, "latency_reason_bad_address", null);
        }
    }

    /// <summary>
    /// Measures every server concurrently, up to a bounded number in flight at once.
    /// </summary>
    /// <remarks>
    /// Bounded rather than one-at-a-time because a full list is seventy-one servers and a slow
    /// link would otherwise make the sweep take minutes, but the bound is deliberately low at
    /// six.
    ///
    /// Every packet in flight shares one connection and one radio, so raising the bound does not
    /// merely fail to help - it can add the app's own queueing to each reading, and a figure that
    /// includes the cost of the sweep is not a figure about the resolver. Six keeps the sweep
    /// quick while leaving each query close to what a single deliberate probe would see.
    ///
    /// That also matters for comparability: the tools people use alongside this one measure
    /// sequentially, so a reading inflated by parallelism would make every server look worse than
    /// the same server measured elsewhere, with no way to tell from the number alone why.
    /// </remarks>
    public async Task<IReadOnlyList<LatencyResult>> MeasureAllAsync(IEnumerable<DnsServer> servers,
                                                                    int maxConcurrent = 6,
                                                                    IProgress<LatencyResult>? progress = null,
                                                                    CancellationToken ct = default)
    {
        var list = servers.ToList();
        var results = new LatencyResult[list.Count];
        using var gate = new SemaphoreSlim(maxConcurrent);

        var running = new List<Task>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var index = i;
            var server = list[index];

            running.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await MeasureAsync(server, ct).ConfigureAwait(false);
                    results[index] = result;
                    progress?.Report(result);
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }

        await Task.WhenAll(running).ConfigureAwait(false);
        return results;
    }
}
