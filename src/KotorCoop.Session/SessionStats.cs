namespace KotorCoop.Session;

public sealed record SessionStats
{
    public bool Connected { get; init; }

    public uint LocalPeerId { get; init; }

    public int PeerCount { get; init; }

    public double RoundTripMs { get; init; }

    public long PacketsSent { get; init; }

    public long PacketsReceived { get; init; }

    public long PacketsDropped { get; init; }

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

    public string Status { get; init; } = "idle";
}
