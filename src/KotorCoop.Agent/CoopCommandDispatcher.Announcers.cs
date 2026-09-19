using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>The per-tick diff announcers: peer joins/leaves, content-compatibility changes,
/// the persistent connection indicator, and whether a hosting player has a save to share.</summary>
public sealed partial class CoopCommandDispatcher
{
    /// <summary>Roster changes become lines on BOTH screens: "Malak joined the game" /
    /// "Malak left the game" (see <see cref="PeerAnnouncer"/> for why this exists).</summary>
    private readonly PeerAnnouncer _peerAnnouncer = new();

    /// <summary>A peer's <see cref="KotorCoop.Protocol.PlayerState.FlagAway"/> edge becomes a
    /// line too: "Malak left the world (main menu / loading)" / "Malak is back" — see
    /// <see cref="AwayAnnouncer"/> for why this is a separate diff from the roster one above.</summary>
    private readonly AwayAnnouncer _awayAnnouncer = new();

    /// <summary>A peer's <see cref="KotorCoop.Protocol.PlayerState.FlagInMinigame"/> edge:
    /// "Malak is in a minigame - waiting" / "Malak is back" — agent-side because the hide
    /// filter keeps the plugin from ever seeing the flag (see <see cref="MinigameAnnouncer"/>).</summary>
    private readonly MinigameAnnouncer _minigameAnnouncer = new();

    /// <summary>A peer's <see cref="KotorCoop.Protocol.PlayerState.FlagSoloSegment"/> edge:
    /// "Malak is fighting alone - spectating" / "Malak's fight is over" — see
    /// <see cref="SoloSegmentAnnouncer"/>.</summary>
    private readonly SoloSegmentAnnouncer _soloSegmentAnnouncer = new();

    /// <summary>Content differences become lines on screen too — see
    /// <see cref="CompatibilityAnnouncer"/> for why a degradation that is only logged is
    /// indistinguishable from a bug.</summary>
    private readonly CompatibilityAnnouncer _compatibilityAnnouncer = new();

    /// <summary>Behaviour-flag (coop.ini/CLI) differences become lines on screen too —
    /// docs/roadmap-seamless.md Tier 1 item 7. See <see cref="BehaviourAnnouncer"/>.</summary>
    private readonly BehaviourAnnouncer _behaviourAnnouncer = new();

    /// <summary>The persistent connection indicator (item 18) — see
    /// <see cref="ConnectionStatusAnnouncer"/> for why the <c>status</c> console verb was not
    /// enough on its own.</summary>
    private readonly ConnectionStatusAnnouncer _connectionAnnouncer = new();

    /// <summary>Whether a hosting player has anything to share yet — see
    /// <see cref="HostSaveAnnouncer"/> for the silent-no-op it closes.</summary>
    private readonly HostSaveAnnouncer _hostSaveAnnouncer = new();

    /// <summary>A hosting player's internet reachability (roadmap item 24) — see
    /// <see cref="PublicAddressAnnouncer"/>.</summary>
    private readonly PublicAddressAnnouncer _publicAddressAnnouncer = new();

    /// <summary>
    /// coop.ini's <c>status_toasts</c>, read once and cached rather than reloaded off disk
    /// every 150 ms tick. <c>null</c> means "not read yet"; the value itself is a plain bool
    /// once resolved, so there is no third state to confuse with "file absent" — an absent or
    /// unreadable coop.ini defaults the setting ON, the same default <see cref="CoopConfig"/>
    /// itself uses.
    /// </summary>
    private bool? _statusToastsEnabled;

    private bool StatusToastsEnabled => _statusToastsEnabled ??= loadConfig?.Invoke()?.StatusToasts ?? true;

    /// <summary>Diffs the admitted roster each tick and posts joins/leaves through the same
    /// trio every reply uses (stdout, console line, chat overlay) — which is what puts the
    /// line on the HOST's screen when a player is admitted.</summary>
    private void AnnouncePeerChanges()
    {
        if (manager.Current is not { } session)
        {
            _peerAnnouncer.Reset();
            _awayAnnouncer.Reset();
            _minigameAnnouncer.Reset();
            _soloSegmentAnnouncer.Reset();
            _kotor?.PublishSessionPeerCount(0);
            return;
        }

        IReadOnlyList<RemotePeer> peers = session.GetRemotePeers();
        _peerAnnouncer.Observe(peers, Reply);
        _awayAnnouncer.Observe(peers, Reply);
        _minigameAnnouncer.Observe(peers, Reply);
        _soloSegmentAnnouncer.Observe(peers, Reply);

        // The seamless-arrival gate's signal: written here, every tick, because THIS loop
        // never depends on the player resolve — the pump's RemoteCount does, and its 1 Hz
        // resolve is precisely why the load screen used to drop before the first sync.
        _kotor?.PublishSessionPeerCount(peers.Count);
    }

    /// <summary>
    /// Puts content-compatibility findings on the player's screen, through the same trio
    /// every reply uses. Without this the per-channel mod policy is invisible where it
    /// matters: a channel switched off for safety looks exactly like a channel that broke,
    /// and the one place the verdict was visible — the agent's console readout — is not
    /// where a player is looking.
    /// </summary>
    private void AnnounceCompatibilityChanges()
    {
        if (manager.Current is not { } session)
        {
            _compatibilityAnnouncer.Reset();
            _behaviourAnnouncer.Reset();
            return;
        }

        _compatibilityAnnouncer.Observe(session.Compatibility, Reply);
        _behaviourAnnouncer.Observe(session.BehaviourWarnings, Reply);
    }

    /// <summary>
    /// The persistent connection indicator (item 18, roadmap-seamless.md): posts
    /// "co-op: connected, N players" / "reconnecting..." / "connection failed" /
    /// "disconnected" through the same queued-text path <see cref="AnnounceCompatibilityChanges"/>
    /// uses, reposted only on change, so a player who is not actively typing <c>status</c> at
    /// the console still finds out when their partner's link drops.
    ///
    /// The compact status byte + peer count also rides <c>SharedBlock.OffsetConnectStatus</c>
    /// (<see cref="ConnectionSnapshot.Pack"/>) UNCONDITIONALLY, regardless of the
    /// <c>status_toasts</c> gate below — that gate is about what appears on screen, not about
    /// whether the block's own state is kept current.
    /// </summary>
    private void AnnounceConnectionStatus()
    {
        if (manager.Current is not { } session)
        {
            _connectionAnnouncer.Reset();
            _kotor?.SetConnectStatus(new ConnectionSnapshot(ConnectionIndicatorState.Disconnected, 0).Pack());
            return;
        }

        SessionStats stats = session.GetStats();

        // See ConnectionPhaseMapping for the full reasoning (and the 2026-08-27 live failure
        // that motivated it): manager.Phase alone cannot tell "the very first handshake" apart
        // from "giving up and re-Helloing after an outage" — SessionManager.Poll sets
        // SessionPhase.Connecting for both the instant stats.Connected goes false. The live
        // stats.Reconnecting flag (CoopClient only) is what actually distinguishes them.
        ConnectionIndicatorState state = ConnectionPhaseMapping.Resolve(manager.Phase, stats);

        ConnectionSnapshot snapshot = new(state, stats.PeerCount);
        _kotor?.SetConnectStatus(snapshot.Pack());

        if (StatusToastsEnabled)
        {
            _connectionAnnouncer.Observe(snapshot, Reply);
        }
    }

    /// <summary>
    /// Tells a HOST whether they currently have a save to share — see
    /// <see cref="HostSaveAnnouncer"/>. Unconditional (not gated by <see cref="StatusToastsEnabled"/>):
    /// this is actionable onboarding information, not a toast a new player would know to
    /// re-enable, and its silence is exactly the bug it exists to close.
    /// </summary>
    private void AnnounceHostSaveAvailability()
    {
        if (manager.Current is not CoopHost || string.IsNullOrEmpty(gameDirectory))
        {
            _hostSaveAnnouncer.Reset();
            return;
        }

        string? saveDirectory = SaveDigest.FindSessionSave(gameDirectory);
        _hostSaveAnnouncer.Observe(
            saveDirectory is not null,
            saveDirectory is not null ? Path.GetFileName(saveDirectory) : null,
            Reply);
    }

    /// <summary>
    /// Roadmap item 24's "surface it": announces a hosting player's public IP:port (once STUN
    /// finds one) and UPnP mapping verdict — see <see cref="PublicAddressAnnouncer"/>.
    /// Unconditional like <see cref="AnnounceHostSaveAvailability"/>: this is the one piece of
    /// onboarding information that tells a host whether a remote friend can actually reach them
    /// at all, not a toast a returning player already knows to re-enable.
    /// </summary>
    private void AnnouncePublicAddress()
    {
        if (manager.Current is not CoopHost host)
        {
            _publicAddressAnnouncer.Reset();
            return;
        }

        _publicAddressAnnouncer.Observe(
            host.ListenEndPoint.Port, host.PublicEndPoint, host.UpnpStatus, host.Reachability, Reply);
    }

    /// <summary>
    /// A HOST's side of a join it turned away — a different build (<c>E_PROTO</c>), a wrong
    /// password — through the same trio a <c>W_CFG</c> line uses. The session queues and
    /// rate-limits these (<see cref="RejectedJoinNotices"/>); this only drains them. Nothing
    /// to reset on session end: the queue dies with the host.
    /// </summary>
    private void AnnounceRejectedJoins()
    {
        if (manager.Current is not CoopHost host)
        {
            return;
        }

        foreach (string line in host.DrainRejectedJoinNotices())
        {
            Reply($"co-op: {line}");
        }
    }
}
