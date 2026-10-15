using System.Text;

using DnsPilot.Services;

using Xunit;

namespace DnsPilot.Tests;

/// <summary>
/// The wire format, checked byte by byte against the layout it is supposed to produce.
/// </summary>
/// <remarks>
/// These are the tests that carry the most weight in this project. The latency figure the whole
/// test feature is built on comes from this code, and a query that is subtly malformed does not
/// fail loudly - it times out, and a timeout looks exactly like "this server is slow" or "this
/// server is dead". Verifying the bytes is the only way to tell those apart.
/// </remarks>
public class DnsWireTests
{
    [Fact]
    public void Query_has_the_expected_header()
    {
        var query = DnsWire.BuildQuery(0x1234, "example.com", DnsWire.TypeA);

        // 12 header, then two labels (1+7 for "example", 1+3 for "com"), the root label, and
        // 4 bytes of type and class.
        Assert.Equal(29, query.Length);

        Assert.Equal(0x12, query[0]);   // id, big-endian
        Assert.Equal(0x34, query[1]);

        Assert.Equal(0x01, query[2]);   // flags: RD set, nothing else
        Assert.Equal(0x00, query[3]);

        // Each count is a big-endian 16-bit value, so the count of one lives in the low byte.
        Assert.Equal(0x00, query[4]);   // one question
        Assert.Equal(0x01, query[5]);
        Assert.Equal(0x00, query[6]);   // no answers
        Assert.Equal(0x00, query[8]);   // no authority
        Assert.Equal(0x00, query[10]);  // no additional
    }

    [Fact]
    public void Query_writes_the_name_as_length_prefixed_labels()
    {
        var query = DnsWire.BuildQuery(1, "www.example.com", DnsWire.TypeA);

        // The name is written as a length byte followed by that many characters, repeated.
        Assert.Equal(3, query[12]);                       // "www" is three bytes long
        Assert.Equal((byte)'w', query[13]);
        Assert.Equal(7, query[16]);                       // "example"
        Assert.Equal((byte)'e', query[17]);
        Assert.Equal(3, query[24]);                       // "com"
        Assert.Equal((byte)'c', query[25]);
        Assert.Equal(0, query[28]);                       // root label terminates the name
        Assert.Equal(0x00, query[29]);                    // qtype is 1 for A
        Assert.Equal(0x01, query[30]);
        Assert.Equal(0x00, query[31]);                    // qclass is 1 for IN
        Assert.Equal(0x01, query[32]);
    }

    [Fact]
    public void Query_encodes_AAAA_for_the_v6_family()
    {
        var query = DnsWire.BuildQuery(1, "example.com", DnsWire.TypeAAAA);

        // qtype sits immediately after the terminating root label: 28 is 0x001C.
        var qtypeOffset = query.Length - 4;
        Assert.Equal(0x00, query[qtypeOffset]);
        Assert.Equal(0x1C, query[qtypeOffset + 1]);
    }

    [Fact]
    public void Query_refuses_a_label_that_cannot_be_encoded()
    {
        // Sixty-four bytes is one over the DNS limit for a single label. Sending it would be a
        // malformed query that no server has to answer, and the failure would then look like a
        // slow server rather than like a bad name.
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery(1, new string('a', 64) + ".com", DnsWire.TypeA));
    }

    [Fact]
    public void Query_refuses_a_name_that_would_not_fit()
    {
        // Nine 63-byte labels encode to 576 bytes before the root label, over the 512 the query
        // buffer allows. The refusal has to name the problem rather than fault on a copy.
        var name = string.Join(".", Enumerable.Repeat(new string('a', 63), 9));

        var ex = Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery(1, name, DnsWire.TypeA));
        Assert.Contains("too long", ex.Message);
    }

    /// <summary>Builds a reply by echoing the query with the response bit set - the minimum a
    /// server could send back that should still count as an answer.</summary>
    private static byte[] ReplyTo(byte[] query, ushort id, ushort flags)
    {
        var reply = new byte[query.Length];
        Array.Copy(query, reply, query.Length);
        reply[0] = (byte)(id >> 8);
        reply[1] = (byte)(id & 0xFF);
        reply[2] = (byte)(flags >> 8);
        reply[3] = (byte)(flags & 0xFF);
        return reply;
    }

    [Fact]
    public void A_reply_matching_the_id_is_accepted()
    {
        var query = DnsWire.BuildQuery(0x0F0F, "example.com", DnsWire.TypeA);
        var reply = ReplyTo(query, 0x0F0F, 0x8180);   // QR set, RD echoed, rcode 0

        var kind = DnsWire.Inspect(reply, 0x0F0F, out var rcode);

        Assert.Equal(DnsWire.ReplyKind.Answer, kind);
        Assert.Equal(0, rcode);
    }

    [Fact]
    public void A_reply_for_another_id_is_rejected()
    {
        var query = DnsWire.BuildQuery(1, "example.com", DnsWire.TypeA);
        var reply = ReplyTo(query, 0x7777, 0x8180);

        // This is the check that makes the measurement mean something. Accepting a datagram
        // because it arrived would let anything on the path answer for the resolver.
        Assert.Equal(DnsWire.ReplyKind.WrongId, DnsWire.Inspect(reply, 1, out _));
    }

    [Fact]
    public void A_query_that_is_not_a_response_is_rejected()
    {
        var query = DnsWire.BuildQuery(1, "example.com", DnsWire.TypeA);
        var reply = ReplyTo(query, 1, 0x0100);   // response bit clear

        Assert.Equal(DnsWire.ReplyKind.NotAResponse, DnsWire.Inspect(reply, 1, out _));
    }

    [Theory]
    [InlineData(1, "format error")]     // the server could not read the question
    [InlineData(2, "server failure")]   // the server failed while resolving
    [InlineData(3, "no such name")]     // the name does not exist
    public void A_nonzero_rcode_is_reported_as_a_server_error(int rcode, string because)
    {
        var query = DnsWire.BuildQuery(1, "example.com", DnsWire.TypeA);
        var reply = ReplyTo(query, 1, (ushort)(0x8180 | rcode));

        var kind = DnsWire.Inspect(reply, 1, out var seen);

        Assert.True(kind == DnsWire.ReplyKind.ServerError, because);
        Assert.Equal(rcode, seen);
    }

    [Fact]
    public void A_truncated_datagram_is_not_mistaken_for_an_answer()
    {
        var kind = DnsWire.Inspect(new byte[11], 1, out _);
        Assert.Equal(DnsWire.ReplyKind.TooShort, kind);
    }

    [Fact]
    public void Names_with_a_trailing_dot_are_encoded_the_same_as_without()
    {
        // Browsers and resolver settings disagree about whether a trailing dot belongs. Both
        // spellings have to reach the server as the same query.
        var withDot = DnsWire.BuildQuery(1, "example.com.", DnsWire.TypeA);
        var without = DnsWire.BuildQuery(1, "example.com", DnsWire.TypeA);

        Assert.Equal(without, withDot);
    }

    [Fact]
    public void Ids_are_within_the_range_a_query_can_carry()
    {
        for (var i = 0; i < 2000; i++)
        {
            var id = DnsWire.NewId();
            Assert.InRange(id, (ushort)1, ushort.MaxValue);
        }
    }

    [Fact]
    public void The_decoded_reply_is_not_confused_by_extra_bytes()
    {
        var query = DnsWire.BuildQuery(5, "example.com", DnsWire.TypeA);
        var reply = ReplyTo(query, 5, 0x8180);

        // A real answer carries records after the question; the header check must still be the
        // same whether or not anything follows it.
        var extended = new byte[reply.Length + 60];
        Array.Copy(reply, extended, reply.Length);

        Assert.Equal(DnsWire.ReplyKind.Answer, DnsWire.Inspect(extended, 5, out _));
    }
}
