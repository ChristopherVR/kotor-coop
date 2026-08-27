using System.Buffers.Binary;
using System.Net;

namespace KotorCoop.Session;

/// <summary>
/// A minimal RFC 5389 STUN Binding Request/Response codec — just enough to ask a public STUN
/// server "what does the internet see me as", not a general STUN/TURN/ICE stack. No
/// authentication, no MESSAGE-INTEGRITY/FINGERPRINT attributes: a bare Binding Request needs
/// neither, and every public STUN server answers one unauthenticated. IPv4 only, matching
/// this project's game socket (see <see cref="TryParseBindingResponse"/>'s doc comment).
///
/// Pure and socket-free by design: <see cref="BuildBindingRequest"/>/
/// <see cref="TryParseBindingResponse"/> operate on plain byte spans, so they are testable
/// against hand-built or captured bytes with no network at all. The socket wiring that sends
/// a request from the game's own port and waits for the matching reply lives in
/// <see cref="UdpChannel.SendStunRequestAsync"/> and <see cref="PublicAddressDiscovery"/>.
/// </summary>
public static class StunClient
{
    private const ushort MessageTypeBindingRequest = 0x0001;
    private const ushort MessageTypeBindingSuccess = 0x0101;

    /// <summary>The fixed magic cookie every STUN message (RFC 5389) carries at byte offset 4,
    /// and the XOR key for XOR-MAPPED-ADDRESS. Also what <see cref="LooksLikeStunMessage"/>
    /// checks for to recognise a STUN reply arriving on a socket that also carries game
    /// traffic — the co-op wire's own magic (<c>ProtocolConstants.Magic</c>, 2 bytes at offset
    /// 0) cannot collide with a 4-byte value at offset 4.</summary>
    private const uint MagicCookie = 0x2112A442;

    private const ushort AttrMappedAddress = 0x0001; // RFC 3489 fallback a few old servers still send
    private const ushort AttrXorMappedAddress = 0x0020;
    private const byte FamilyIPv4 = 0x01;

    /// <summary>STUN's fixed 20-byte header: 2 bytes type, 2 bytes body length, 4 bytes magic
    /// cookie, 12 bytes transaction id.</summary>
    public const int HeaderLength = 20;

    public const int TransactionIdLength = 12;

    /// <summary>Builds a bare 20-byte Binding Request (no attributes) with a fresh random
    /// transaction id, which the caller must keep to match the response.</summary>
    public static byte[] BuildBindingRequest(out byte[] transactionId)
    {
        transactionId = new byte[TransactionIdLength];
        Random.Shared.NextBytes(transactionId);

        byte[] message = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0, 2), MessageTypeBindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2, 2), 0); // no attributes
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4, 4), MagicCookie);
        transactionId.CopyTo(message.AsSpan(8, TransactionIdLength));
        return message;
    }

    /// <summary>
    /// True when <paramref name="data"/> carries the STUN magic cookie where a STUN header
    /// would put it. Used to pull a STUN reply out of a socket that also carries ordinary co-op
    /// traffic before it is mistaken for a malformed game packet — see
    /// <see cref="UdpChannel"/>'s receive loop.
    /// </summary>
    public static bool LooksLikeStunMessage(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderLength && BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)) == MagicCookie;

    /// <summary>
    /// Parses a Binding Success Response matching <paramref name="expectedTransactionId"/> and
    /// returns the reflexive (public) endpoint from its XOR-MAPPED-ADDRESS attribute, falling
    /// back to a plain MAPPED-ADDRESS for the rare server that still answers the older RFC 3489
    /// form. Returns null for an error response, a transaction mismatch, a malformed message,
    /// or one carrying neither address attribute.
    ///
    /// IPv4 only: an XOR-MAPPED-ADDRESS carrying family 0x02 (IPv6) is skipped rather than
    /// decoded (RFC 5389 §15.2's IPv6 form XORs against cookie+transaction-id, 16 bytes, and
    /// this project's game socket is IPv4-only — see docs/research/network-topology.md's
    /// "Internet play" section for the IPv6-untested note).
    /// </summary>
    public static IPEndPoint? TryParseBindingResponse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expectedTransactionId)
    {
        if (data.Length < HeaderLength
            || BinaryPrimitives.ReadUInt16BigEndian(data[..2]) != MessageTypeBindingSuccess
            || BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)) != MagicCookie
            || expectedTransactionId.Length != TransactionIdLength
            || !data.Slice(8, TransactionIdLength).SequenceEqual(expectedTransactionId))
        {
            return null;
        }

        int bodyLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
        int end = Math.Min(HeaderLength + bodyLength, data.Length);
        int offset = HeaderLength;

        IPEndPoint? fallback = null;

        // Walk the TLV attributes. XOR-MAPPED-ADDRESS wins outright when found (returned
        // immediately) — it is the form every modern server sends and the one that survives a
        // NAT rewriting addresses in transit, which is the entire reason it exists.
        while (offset + 4 <= end)
        {
            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            ushort attrLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
            int valueStart = offset + 4;

            if (valueStart + attrLength > end)
            {
                break; // truncated attribute — stop rather than read past the message
            }

            ReadOnlySpan<byte> value = data.Slice(valueStart, attrLength);

            if (attrType == AttrXorMappedAddress
                && TryParseXorMappedAddress(value, out IPEndPoint? xorMapped))
            {
                return xorMapped;
            }

            if (attrType == AttrMappedAddress && fallback is null)
            {
                fallback = TryParseMappedAddress(value);
            }

            // Attributes are padded to a 4-byte boundary (RFC 5389 §15).
            offset = valueStart + ((attrLength + 3) & ~3);
        }

        return fallback;
    }

    private static bool TryParseXorMappedAddress(ReadOnlySpan<byte> value, out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (value.Length < 8 || value[1] != FamilyIPv4)
        {
            return false;
        }

        ushort xPort = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2, 2));
        ushort port = (ushort)(xPort ^ (MagicCookie >> 16));

        uint xAddress = BinaryPrimitives.ReadUInt32BigEndian(value.Slice(4, 4));
        uint address = xAddress ^ MagicCookie;

        byte[] addressBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(addressBytes, address);

        endpoint = new IPEndPoint(new IPAddress(addressBytes), port);
        return true;
    }

    private static IPEndPoint? TryParseMappedAddress(ReadOnlySpan<byte> value)
    {
        if (value.Length < 8 || value[1] != FamilyIPv4)
        {
            return null;
        }

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2, 2));
        return new IPEndPoint(new IPAddress(value.Slice(4, 4)), port);
    }
}
