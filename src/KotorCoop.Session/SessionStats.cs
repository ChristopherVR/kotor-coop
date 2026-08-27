namespace KotorCoop.Session;

public sealed record SessionStats
{
    public bool Connected { get; init; }

    /// <summary>
    /// CLIENT ONLY: true after the peer timeout has been given up on and this session is
    /// re-Hello-ing the SAME nonce to reclaim its identity — <c>CoopClient._reconnecting</c>,
    /// set the instant <c>ForgetSession</c> runs and cleared the instant a fresh Welcome
    /// answers. Always false for a host, which has no single link of its own to lose (an
    /// individual peer dropping only ever moves <see cref="PeerCount"/> — see
    /// docs/research/connect-flow.md's 2026-08-27 live pass). Distinct from <see cref="Connected"/>
    /// being merely false: a brand-new, never-yet-connected client is also not Connected, but
    /// is not Reconnecting either — see <c>ConnectionPhaseMapping</c>, the consumer this exists
    /// for.
    /// </summary>
    public bool Reconnecting { get; init; }

    public uint LocalPeerId { get; init; }

    public int PeerCount { get; init; }

    public double RoundTripMs { get; init; }

    public long PacketsSent { get; init; }

    public long PacketsReceived { get; init; }

    public long PacketsDropped { get; init; }

    /// <summary>Datagrams that carried our magic and a recognised type but a protocol
    /// version we do not speak — a peer on a different co-op build, counted separately from
    /// <see cref="PacketsDropped"/> because "wrong version" and "garbage" want different
    /// player-facing messages.</summary>
    public long VersionMismatches { get; init; }

    public long BytesSent { get; init; }

    public long BytesReceived { get; init; }

    /// <summary>Time since the last inbound snapshot. A rising value means the link is dead.</summary>
    public long LastInboundAgeMs { get; init; }

    public uint FlagVersion { get; init; }

    public int PendingFlagOps { get; init; }

    /// <summary>
    /// How many "the enemy owner says this creature is alive, but this instance's own engine
    /// already had it dead or gone" anomalies the plugin's liveness reconciliation has counted
    /// since it started — pushed in from <c>CoopAgent</c> via
    /// <see cref="ICoopSession.ReportLivenessAnomalyCount"/>, never computed by the session
    /// itself. 0 when the channel is off, unsupported, or has found nothing to complain about.
    /// See docs/research/creature-liveness.md.
    /// </summary>
    public uint LivenessAnomalies { get; init; }

    /// <summary>The deliberate playback delay behind live truth the avatar stream is buffered
    /// by (<c>SnapshotBuffer</c>, coop.ini <c>interp_ms</c>) — NOT a defect: it trades a fixed,
    /// bounded delay for smooth interpolation instead of a step on every network tick. `lagstats`
    /// surfaces it so a live round can tell "the deliberate 80 ms" from "something is actually
    /// stalled." See docs/research/lag-audit.md.</summary>
    public int PhantomInterpolationDelayMs { get; init; }

    /// <summary>The last few real gaps between successive state sends, OLDEST first — the
    /// ACTUAL send cadence this session is achieving, which can diverge from the configured
    /// tick rate under load. Empty until at least two sends have happened.</summary>
    public IReadOnlyList<long> RecentSendIntervalsMs { get; init; } = [];

    /// <summary>The last few real gaps between successive inbound packets, OLDEST first.</summary>
    public IReadOnlyList<long> RecentRecvIntervalsMs { get; init; } = [];

    public string Status { get; init; } = "idle";
}
