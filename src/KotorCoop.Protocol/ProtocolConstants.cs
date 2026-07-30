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
    /// Version 7: text chat channel.</summary>
    public const byte Version = 7;

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
    /// Enemies whose health the host replicates. 9 bytes each on the wire, so this stays
    /// comfortably inside one datagram.
    /// </summary>
    public const int MaxEnemies = 32;

    /// <summary>Party companions the host replicates. KOTOR allows two active at once; a
    /// little headroom costs nothing.</summary>
    public const int MaxPartyMembers = 4;

    /// <summary>The spectate line carried on the dialogue token. Capped at the wire string
    /// limit; a longer subtitle is simply truncated for the spectator.</summary>
    public const int MaxDialogueLineLength = MaxStringLength;

    /// <summary>magic(2) + version(1) + type(1)</summary>
    public const int HeaderSize = 4;

    /// <summary>Comfortably inside the smallest common path MTU, so we never fragment.</summary>
    public const int MaxPacketSize = 1200;

    /// <summary>Length-prefixed strings use a single byte prefix. Absolute upper bound.</summary>
    public const int MaxStringLength = 63;

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

    public const int DefaultPort = 27015;

    /// <summary>State broadcast rate. 20 Hz matches what the Souls games replicate at.</summary>
    public const int DefaultTickRate = 20;

    /// <summary>
    /// How far in the past remote peers are rendered. Remote motion is interpolated
    /// between two received snapshots rather than extrapolated, which trades a fixed
    /// latency for the absence of rubber-banding.
    /// </summary>
    public const int DefaultInterpolationDelayMs = 100;

    public const int PeerTimeoutMs = 5000;

    public const int MaxPeers = 8;
}
