using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

using DnsPilot.Models;
using DnsPilot.Services;

using Xunit;

namespace DnsPilot.Tests;

/// <summary>
/// The measurement loop, driven against a resolver the test itself runs.
/// </summary>
/// <remarks>
/// A listener on 127.0.0.1 stands in for the resolver: the client sends, the listener counts or
/// answers, and nothing leaves the machine and nothing on it changes. These are the tests that
/// had been missing - the wire format is covered byte-by-byte by DnsWireTests, but how the loop
/// behaves around that wire (does it really ask three times, does a genuine reply become a
/// latency, does silence become "no answer") only proves itself with a socket on either end.
/// </remarks>
public class LatencyTesterTests
{
    /// <summary>A minimal UDP resolver: receives, counts, and may echo a reply.</summary>
    private sealed class FakeResolver : IDisposable
    {
        private readonly UdpClient _udp;

        private FakeResolver(UdpClient udp, IPEndPoint endpoint)
        {
            _udp = udp;
            Endpoint = endpoint;
        }

        public IPEndPoint Endpoint { get; }

        public int Received { get; private set; }

        /// <summary>True to answer every query with a matching-id response.</summary>
        public bool Reply { get; set; }

        /// <summary>True to stamp every reply with an id the client never asked with.</summary>
        public bool BadId { get; set; }

        public static FakeResolver Start(bool reply, bool badId = false)
        {
            // An ephemeral port picked by the system: no fixed port to collide with, and the
            // client is told where to send by the endpoint rather than by convention.
            var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var resolver = new FakeResolver(udp, (IPEndPoint)udp.Client.LocalEndPoint!)
            {
                Reply = reply,
                BadId = badId,
            };

            // Local copies: the receiving loop runs from a static context, so it reads the
            // parameters rather than the instance's own properties.
            bool answers = reply;
            bool corruptsId = badId;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var received = await udp.ReceiveAsync();
                        resolver.Received++;

                        if (!answers) continue;
                        // Echo the question with the QR bit and RA set, which is what DnsWire
                        // accepts as an answer for the id it sent.
                        var query = received.Buffer;
                        var response = new byte[query.Length];
                        Array.Copy(query, response, query.Length);
                        response[2] |= 0x80;    // QR: a response, not a question
                        response[3] |= 0x80;    // RA: recursion available
                        if (corruptsId)
                        {
                            response[0] = 0x44;
                            response[1] = 0x44;
                        }
                        await udp.SendAsync(response, response.Length, received.RemoteEndPoint);
                    }
                }
                catch (ObjectDisposedException) { /* stopped */ }
                catch (SocketException) { /* stopped */ }
                catch (OperationCanceledException) { /* stopped */ }
            });

            return resolver;
        }

        public void Dispose()
        {
            ((IDisposable)_udp).Dispose();
        }
    }

    private static DnsServer Server() => new("Local", "", "127.0.0.1", "", DnsFamily.IPv4);

    [Fact]
    public async Task A_live_server_reports_a_latency()
    {
        using var resolver = FakeResolver.Start(reply: true);
        var tester = new LatencyTester { Timeout = TimeSpan.FromMilliseconds(1500) };

        var result = await tester.MeasureAsync(Server(), resolver.Endpoint);

        Assert.True(result.Ok, $"expected an answer, got '{result.Error}'");
        Assert.NotNull(result.Milliseconds);
        Assert.True(result.Milliseconds >= 0);
        // Latency proportions: a localhost round trip is never 2.5 seconds, the timeout value.
        Assert.True(result.Milliseconds < 1500, $"localhost took {result.Milliseconds} ms?");
    }

    [Fact]
    public async Task Silence_becomes_no_answer_after_every_attempt()
    {
        // The listener counts and refuses to answer, so the result is failure - and the count
        // is the assertion: every attempt must actually have been sent, because a min-of-N
        // measurement is only a minimum if all N queries fire, including after an earlier one
        // succeeded. (There is no earlier success here, but a loop that broke after the first
        // attempt for any reason would leave this count short.)
        using var resolver = FakeResolver.Start(reply: false);
        var tester = new LatencyTester
        {
            Timeout = TimeSpan.FromMilliseconds(120),
            Attempts = 3,
        };

        var result = await tester.MeasureAsync(Server(), resolver.Endpoint);

        Assert.False(result.Ok);
        Assert.Equal("latency_reason_no_answer", result.Error);
        Assert.Equal(3, resolver.Received);
    }

    [Fact]
    public async Task A_wrong_id_is_not_believed()
    {
        // The listener answers quickly but stamps every reply with an id the client never sent.
        // A reply for a question that was never asked is exactly what an off-path forger or a
        // stray late datagram looks like, and accepting it would put someone else's answer in
        // this server's latency column.
        using var resolver = FakeResolver.Start(reply: true, badId: true);
        var tester = new LatencyTester
        {
            Timeout = TimeSpan.FromMilliseconds(250),
            Attempts = 1,
        };

        var result = await tester.MeasureAsync(Server(), resolver.Endpoint);

        Assert.False(result.Ok);
        Assert.Equal("latency_reason_unknown", result.Error);
    }
}
