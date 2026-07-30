using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// What the agent loop needs from a session, whether it is hosting or joining.
/// Deliberately poll-based: a game loop wants to ask "where is everyone right now",
/// not to be called back on a socket thread.
/// </summary>
public interface ICoopSession : IDisposable
{
    uint LocalPeerId { get; }

    bool IsConnected { get; }

    string LocalName { get; }

    EventFlagStore Flags { get; }

    SessionStats GetStats();

    /// <summary>Publishes the local avatar. Sent on the next tick.</summary>
    void PublishLocalState(in PlayerState state);

    /// <summary>Every remote peer, interpolated to the current render time.</summary>
    IReadOnlyList<RemotePeer> GetRemotePeers();

    /// <summary>Requests a world flag change. Delivered reliably.</summary>
    void SetFlag(uint flagId, sbyte value);

    /// <summary>Items currently lying on the ground, shared across the session.</summary>
    GroundItemStore Items { get; }

    /// <summary>
    /// Announces an item dropped by the local player. Returns the minted item id, or 0
    /// when the drop was refused (not connected yet, or the ground is full).
    /// </summary>
    uint DropItem(string resref, string module, Vector3f position);

    /// <summary>Announces that a ground item was picked up (or otherwise vanished) here.</summary>
    void RemoveItem(uint itemId);

    /// <summary>
    /// True when this peer owns enemy state. Exactly one peer does — the host — and it is
    /// the only one whose fights actually kill anything; everyone else mirrors it.
    /// </summary>
    bool IsEnemyAuthority { get; }

    /// <summary>Host only: publish the authoritative enemy list. Ignored on a client.</summary>
    void PublishEnemies(IReadOnlyList<EnemyState> enemies);

    /// <summary>Client only: the host's latest enemy list. Empty on the host.</summary>
    IReadOnlyList<EnemyState> GetEnemies();

    /// <summary>
    /// Client only: report this instance's local view of enemy health so the host can
    /// merge the damage in. Without this a client's fighting is cosmetic — its copies
    /// are pinned at 1 hp and the next mirror would erase its damage.
    /// </summary>
    void ReportEnemies(string module, IReadOnlyList<EnemyState> enemies);

    /// <summary>
    /// Host only: every client's latest enemy report for the given module, fresh ones
    /// only. The host applies each as a floor (min-merge) on its own enemies.
    /// </summary>
    IReadOnlyList<EnemyState> GetEnemyReports(string module);

    /// <summary>The shared quest journal, merged upward across every player.</summary>
    JournalStore Journal { get; }

    /// <summary>Announces a quest journal advance. Delivered reliably.</summary>
    void SetJournalEntry(string questTag, int state);

    /// <summary>
    /// The replication channels this session is currently allowed to use, after content
    /// compatibility with the peer(s) has been judged. Starts at <see
    /// cref="SessionCapabilities.All"/> and narrows if a mod mismatch is detected. The
    /// agent's pump consults this before each channel so a differently-modded pair still
    /// plays, minus whatever their difference makes unsafe.
    /// </summary>
    SessionCapabilities Capabilities { get; }

    /// <summary>The current compatibility verdict, for surfacing on the dashboard. Null
    /// until a peer has been evaluated.</summary>
    CompatibilityVerdict? Compatibility { get; }

    Task RunAsync(CancellationToken cancellationToken);
}

public sealed record SessionSettings
{
    public string PlayerName { get; init; } = "player";

    public int Port { get; init; } = ProtocolConstants.DefaultPort;

    public int TickRate { get; init; } = ProtocolConstants.DefaultTickRate;

    public int InterpolationDelayMs { get; init; } = ProtocolConstants.DefaultInterpolationDelayMs;

    public int PeerTimeoutMs { get; init; } = ProtocolConstants.PeerTimeoutMs;

    /// <summary>
    /// This install's content fingerprint, announced to the peer so a mod mismatch that
    /// would corrupt shared state is caught at join time. Defaults to
    /// <see cref="ContentDigest.Unknown"/> (agents without game-file access, and the mock
    /// harness), which compares as "unchecked" and never blocks a session.
    /// </summary>
    public ContentDigest Digest { get; init; } = ContentDigest.Unknown;

    /// <summary>Whether to admit a peer whose save differs, degrading to id-independent
    /// channels instead of refusing. Mirrors <see cref="ContentCompatibility.AllowDifferentSave"/>.</summary>
    public bool AllowDifferentSave { get; init; }
}
