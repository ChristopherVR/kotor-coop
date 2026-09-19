using System.Security.Cryptography;

namespace KotorCoop.Session;

/// <summary>
/// The one datagram <see cref="ReachabilityCheck"/> sends to itself: a fixed 8-byte marker
/// followed by a random 16-byte nonce. Sent from a throwaway second socket to the host's OWN
/// public IP:port, so that if the router hairpins it, it arrives on the session's listener
/// looking like traffic from the internet. <see cref="UdpChannel"/>'s receive loop pulls it
/// out before the session ever sees it — exactly the way a STUN reply is intercepted — and a
/// probe nobody is waiting for is dropped silently rather than counted as a malformed game
/// packet.
///
/// Not a session packet: no <c>PacketType</c>, no protocol version, and the marker's first
/// two bytes ("KC") deliberately read as <c>0x434B</c>, not <c>ProtocolConstants.Magic</c>
/// (<c>0x4B43</c>), so even a probe that somehow reached the packet codec would be rejected
/// as garbage rather than mistaken for a Hello. Its first byte's top two bits are <c>01</c>,
/// so it can never be mistaken for a STUN message either (those start with <c>00</c>).
/// </summary>
public static class ReachabilityProbe
{
    public const int MarkerLength = 8;
    public const int NonceLength = 16;
    public const int Length = MarkerLength + NonceLength;

    public static ReadOnlySpan<byte> Marker => "KCPROBE1"u8;

    /// <summary>A fresh probe and the nonce it carries, for the waiter to match on.</summary>
    public static byte[] Build(out byte[] nonce)
    {
        nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] probe = new byte[Length];
        Marker.CopyTo(probe);
        nonce.CopyTo(probe, MarkerLength);
        return probe;
    }

    public static bool LooksLikeProbe(ReadOnlySpan<byte> data) =>
        data.Length == Length && data[..MarkerLength].SequenceEqual(Marker);

    public static bool Matches(ReadOnlySpan<byte> data, ReadOnlySpan<byte> nonce) =>
        LooksLikeProbe(data) && nonce.Length == NonceLength && data.Slice(MarkerLength, NonceLength).SequenceEqual(nonce);
}
