using System.Buffers.Binary;
using System.Text;

namespace DnsPilot.Services;

/// <summary>
/// Builds a DNS query and reads an answer back, by hand.
///
/// Written rather than taken from a package because the whole thing is forty lines and the two
/// properties that matter are then visible: the transaction id of the reply is matched against
/// the id that was sent, and the reply must actually be a reply. Accepting whatever datagram
/// arrives on the socket is how a forged answer gets believed - the id check is what makes the
/// measurement mean "this server answered" instead of "something answered".
/// </summary>
public static class DnsWire
{
    public const ushort TypeA    = 1;
    public const ushort TypeAAAA = 28;
    public const ushort ClassIn  = 1;

    /// <summary>
    /// Encodes a single-question query. The name is written as length-prefixed labels, which is
    /// what makes a dotted name into DNS's own representation.
    /// </summary>
    /// <remarks>
    /// Capacity is checked before each label is written rather than only at the end. Writing past
    /// a fixed buffer would throw from inside Array.Copy with a message about a destination
    /// buffer, which tells the caller nothing about the name that caused it - and this runs on
    /// whatever the user typed. The check also reserves room for the root label and the type and
    /// class that follow, so passing it guarantees the whole question fits.
    /// </remarks>
    public static byte[] BuildQuery(ushort id, string name, ushort qtype)
    {
        var buffer = new byte[512];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span[..2], id);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 0x0100); // standard query, recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 1);      // one question
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], 0);

        var offset = 12;
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
                throw new ArgumentException($"'{label}' is not a usable label in a DNS name.");

            // 1 length byte, the label, the terminating root, then qtype and qclass.
            if (offset + 1 + bytes.Length + 5 > buffer.Length)
                throw new ArgumentException($"'{name}' is too long for a DNS name.");

            buffer[offset++] = (byte)bytes.Length;
            bytes.CopyTo(buffer, offset);
            offset += bytes.Length;
        }

        buffer[offset++] = 0; // root label, which ends the name

        BinaryPrimitives.WriteUInt16BigEndian(span[offset..(offset + 2)], qtype);
        BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 2)..(offset + 4)], ClassIn);

        return buffer[..(offset + 4)];
    }

    /// <summary>Result of looking at a reply.</summary>
    public enum ReplyKind
    {
        /// <summary>Looks like an answer to our question.</summary>
        Answer,
        /// <summary>The id does not match, so it is not ours.</summary>
        WrongId,
        /// <summary>The QR bit is clear - this is a query, not an answer.</summary>
        NotAResponse,
        /// <summary>The server reported a failure.</summary>
        ServerError,
        TooShort,
        FormatError,
    }

    public static ReplyKind Inspect(ReadOnlySpan<byte> reply, ushort expectedId, out int rcode)
    {
        rcode = 0;
        if (reply.Length < 12) return ReplyKind.TooShort;

        var id     = BinaryPrimitives.ReadUInt16BigEndian(reply[..2]);
        var flags  = BinaryPrimitives.ReadUInt16BigEndian(reply[2..4]);
        rcode      = flags & 0x000F;

        if (id != expectedId) return ReplyKind.WrongId;
        if ((flags & 0x8000) == 0) return ReplyKind.NotAResponse;
        if (rcode != 0) return ReplyKind.ServerError;
        return ReplyKind.Answer;
    }

    /// <summary>A fresh id. Random rather than sequential so two queries in flight do not collide.</summary>
    public static ushort NewId() => (ushort)Random.Shared.Next(1, ushort.MaxValue);
}
