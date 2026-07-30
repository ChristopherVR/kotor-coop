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

    public string Status { get; init; } = "idle";
}
