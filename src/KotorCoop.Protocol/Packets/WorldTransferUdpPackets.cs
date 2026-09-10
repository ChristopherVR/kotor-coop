namespace KotorCoop.Protocol;

// The UDP fallback for auto_adopt_world's world-transfer (PacketType.WorldTransferRequest
// through WorldTransferComplete). Matches PacketCodec.WorldTransferUdp.cs. See
// docs/research/network-topology.md's UDP world-transfer section for why this exists
// alongside KotorCoop.Session.WorldTransfer's TCP channel rather than replacing it.

/// <summary>How a <see cref="WorldTransferHeaderPacket"/> answers a
/// <see cref="WorldTransferRequestPacket"/> — the UDP mirror of the status byte
/// <c>KotorCoop.Session.WorldTransfer</c>'s TCP framing already uses.</summary>
public enum WorldTransferUdpStatus : byte
{
    Ok = 0,
    BadPassword = 1,
    NoWorld = 2,
}

/// <summary>client -&gt; host: begin a UDP world-transfer. <paramref name="RequestId"/> is
/// client-minted and echoed on every reply, so a resent request (the client has not yet seen a
/// header) is distinguishable from a stale one whose header already arrived.</summary>
public sealed record WorldTransferRequestPacket(uint RequestId, uint PasswordHash);

/// <summary>host -&gt; client: answers one <see cref="WorldTransferRequestPacket"/>.
/// <paramref name="TotalBytes"/>/<paramref name="TotalChunks"/> describe the flat payload
/// <see cref="WorldTransferChunkPacket"/> ships in pieces — 0 for both when
/// <paramref name="Status"/> is not <see cref="WorldTransferUdpStatus.Ok"/>, the same "the
/// identity trails the status regardless of the answer" shape
/// <c>KotorCoop.Session.WorldTransfer</c>'s TCP reply header uses.</summary>
public sealed record WorldTransferHeaderPacket(
    uint RequestId, WorldTransferUdpStatus Status, uint SaveIdentity, uint TotalBytes, ushort TotalChunks);

/// <summary>host -&gt; client: one slice of the flat payload. <paramref name="Payload"/>'s
/// length is whatever the datagram actually carried (fixed for every chunk but the last,
/// which is however many bytes remained) — there is no separate length field, the same way a
/// UDP datagram's own size already tells a receiver how much arrived.</summary>
public sealed record WorldTransferChunkPacket(uint RequestId, ushort ChunkIndex, byte[] Payload);

/// <summary>client -&gt; host: which chunks (of <see cref="WorldTransferHeaderPacket.TotalChunks"/>)
/// are still missing, as a bitmap — bit N set means chunk N has not arrived yet. Sent
/// periodically while a transfer is in progress; the host simply resends whatever bit is
/// still set, so a lost status datagram costs one resend cycle, never a stuck transfer.</summary>
public sealed record WorldTransferChunkStatusPacket(uint RequestId, byte[] MissingBitmap);

/// <summary>client -&gt; host: every chunk arrived and reassembled/validated cleanly. Lets the
/// host free its per-client transfer buffer instead of waiting out the transfer's own
/// timeout.</summary>
public sealed record WorldTransferCompletePacket(uint RequestId, bool Success);
