namespace KotorCoop.Protocol;

public static class ProtocolConstants
{
    /// <summary>Two byte magic ('K','C') so stray traffic on the port is discarded cheaply.</summary>
    public const ushort Magic = 0x4B43;

    /// <summary>Bumped on any wire-format change. Mismatched peers are rejected at Hello.
    /// Version 3: PlayerState flags dword, journal channel, client enemy reports.
    /// Version 4: content digest on Hello/Welcome for mod-compatibility handshaking.
    /// Version 5: session password hash on Hello; party-member snapshot channel.
    /// Version 6: dialogue ownership token (snapshot + request channels).
    /// Version 7: text chat channel.
    /// Version 8: shared progression (party gold + PC experience).
    /// Version 9: world digest carries save identity + per-module GIT digest.
    /// Version 10: dialogue token carries the partner tag and the owner's reply picks, so a
    /// spectator runs the same conversation in the game's own UI instead of reading a
    /// transcript; the spectate line grew past the shared string cap.
    /// Version 11: the dialogue token carries the owner's pick LOG (each pick stamped with
    /// the line it was made on) so a spectator replays picks on the right node and a late
    /// joiner catches up.
    /// Version 12: the enemy snapshot carries presence (position, facing, animation) so a
    /// mirroring instance drives its copies to match the owner's fight instead of running
    /// a private one. Reports still carry health alone.</summary>
    public const byte Version = 12;

    /// <summary>Journal quest tags are resrefs plus a little slack.</summary>
    public const int MaxJournalTagLength = 32;

    /// <summary>
    /// Journal entries per snapshot datagram: tag (up to 33 bytes) + state (4) each, so
    /// 28 stays inside <see cref="MaxPacketSize"/>. A longer journal is sent as several
    /// snapshot packets in one burst.
    /// </summary>
    public const int MaxJournalEntriesPerPacket = 28;

    /// <summary>Odyssey resource references are at most 16 characters.</summary>
    public const int MaxResrefLength = 16;

    /// <summary>
    /// Ground items a session tracks at once. 16 is the most that still fits in a single
    /// item snapshot datagram with every string at maximum length (16 x 66 bytes + header).
    /// </summary>
    public const int MaxGroundItems = 16;

    /// <summary>
    /// Enemies the owner replicates. 29 bytes each in the snapshot (id, health, death,
    /// position, facing, animation), so 32 still fits one datagram with room to spare.
    /// </summary>
    public const int MaxEnemies = 32;

    /// <summary>Party companions the host replicates. KOTOR allows two active at once; a
    /// little headroom costs nothing.</summary>
    public const int MaxPartyMembers = 4;

    /// <summary>
    /// The spectate line carried on the dialogue token.
    ///
    /// Deliberately larger than <see cref="MaxStringLength"/>: real KOTOR dialogue runs to
    /// ~200 characters and the shared 63-byte cap cut the spectator's subtitle mid-word —
    /// observed live as "Trask: That door's locked. I don't have the codes to open it, but
    /// swit". The dialogue token is a single string in a packet of its own, so the extra
    /// bytes cannot crowd out a peer snapshot. Still one length byte, so 255 is the hard
    /// ceiling.
    /// </summary>
    public const int MaxDialogueLineLength = 220;

    /// <summary>magic(2) + version(1) + type(1)</summary>
    public const int HeaderSize = 4;

    /// <summary>Comfortably inside the smallest common path MTU, so we never fragment.</summary>
    public const int MaxPacketSize = 1200;

    /// <summary>Default cap for the short identity fields (names, resrefs, tags), chosen so
    /// a full snapshot with every one at maximum still fits a datagram. NOT the absolute
    /// limit — see <see cref="MaxLengthPrefixedString"/>.</summary>
    public const int MaxStringLength = 63;

    /// <summary>What a single length byte can express. The absolute upper bound on any
    /// length-prefixed string, whatever cap the field itself declares.</summary>
    public const int MaxLengthPrefixedString = 255;

    /// <summary>
    /// Per-field caps, chosen so a full <see cref="MaxPeers"/> snapshot with every string
    /// at maximum length still fits in one datagram. Without these the worst case is
    /// ~1265 bytes and every tick would be silently dropped once the session filled up.
    /// </summary>
    public const int MaxNameLength = 24;

    /// <summary>
    /// Odyssey resource references are 16 characters; 32 leaves room without bloating
    /// the snapshot.
    /// </summary>
    public const int MaxModuleLength = 32;

    /// <summary>Object tags identify the NPC a shared conversation is with. Odyssey tags are
    /// authored short; 32 matches the plugin's buffer.</summary>
    public const int MaxTagLength = 32;

    public const int DefaultPort = 27015;

    /// <summary>
    /// State broadcast rate. Raised from 20 Hz after play testing reported the peer lagging:
    /// once the phantom is position-driven rather than walked to a goal, the tick interval is
    /// visible latency rather than just goal freshness, so it is worth spending. The packets
    /// are small and the expensive world-state scanning runs on its own divided-down pass.
    /// </summary>
    public const int DefaultTickRate = 30;

    /// <summary>
    /// How far in the past remote peers are rendered. Remote motion is interpolated
    /// between two received snapshots rather than extrapolated, which trades a fixed
    /// latency for the absence of rubber-banding.
    ///
    /// This is now the dominant term in how far behind a peer looks, so it is set just above
    /// two tick intervals — enough that there is always a later snapshot to interpolate
    /// towards on a LAN, without paying for jitter that a LAN does not have.
    /// </summary>
    public const int DefaultInterpolationDelayMs = 80;

    public const int PeerTimeoutMs = 5000;

    public const int MaxPeers = 8;
}
