namespace KotorCoop.Protocol;

// The player text-chat relay. Matches PacketCodec.Chat.cs.

/// <summary>One received chat line, for a small on-screen/console log.</summary>
public sealed record ChatMessage(uint PeerId, string Name, string Text);

/// <summary>
/// A text message from one player. Sent to the host, which relays it to every other peer.
/// The sender does not receive its own back — it echoes locally on send.
/// </summary>
public sealed record ChatPacket(uint FromPeerId, string Name, string Text);
