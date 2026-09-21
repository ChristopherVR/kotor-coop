using KotorCoop.Protocol;

namespace KotorCoop.Session;

// "One player races, everybody else waits" (docs/research/minigames.md §24).
//
// A KOTOR minigame swaps the racer's WHOLE AREA for a single-seat track — the swoop bike in
// tar_m03mg, the turret deck in m12ab — so for as long as one lasts there is no shared world
// for the other players to be standing in, and the track has no second seat to put them in.
// Rather than let them wander a world the racer cannot see (and rather than let the ordinary
// follow-the-leader warp drag them onto the track, which is what the 2026-08-29 live round
// watched the following instance die doing), they are held still with a line naming who is
// racing, and released the moment the race ends.
//
// The DECIDING is MinigameLockRule (KotorCoop.Protocol, pure, unit-tested); this file is the
// agent-side plumbing: the roster in, one shared-block write out, one log line per edge.
public sealed partial class CoopAgent
{
    /// <summary>The peer whose minigame this instance is currently locked for, or 0. Sticky
    /// across ticks so a second peer entering a minigame does not steal the notice.</summary>
    private uint _minigameLockOwner;

    /// <summary>When <see cref="_minigameLockOwner"/> was adopted — the timeout's clock.</summary>
    private long _minigameLockStartedAtMs;

    /// <summary>Scratch, reused every tick rather than allocated: the rule reads three fields
    /// per peer and a fresh list per tick at 30 Hz is pure garbage.</summary>
    private readonly List<MinigameLockPeer> _minigameLockPeers = [];

    /// <summary>
    /// True while this instance's own player is held for a peer's minigame — read by
    /// <see cref="Pumps.TransitionPump"/>'s follow decision, which must not warp a locked
    /// player anywhere, and exposed publicly so a readout can say why the player cannot move.
    /// </summary>
    public bool IsLockedForPeerMinigame => _minigameLockOwner != 0;

    /// <summary>The racing peer's published name while <see cref="IsLockedForPeerMinigame"/>,
    /// otherwise empty.</summary>
    public string MinigameLockOwnerName { get; private set; } = string.Empty;

    /// <summary>
    /// Evaluates this tick's lock and publishes it. Called from <c>PumpOnce</c> BEFORE the
    /// minigame hide-filter, against the UNFILTERED roster — see the call site's own comment.
    /// </summary>
    private void PumpMinigameLock(in PlayerState local, IReadOnlyList<RemotePeer> peers)
    {
        if (!_settings.LockPeersDuringMinigame)
        {
            // Turned off mid-session (or never on): make sure nothing is left asserted, then
            // stop touching the block entirely. The bridge's own idempotence makes the
            // steady-state cost of this branch zero.
            if (_minigameLockOwner != 0)
            {
                ReleaseMinigameLock("minigame_lock_peers was turned off");
            }

            return;
        }

        _minigameLockPeers.Clear();
        foreach (RemotePeer peer in peers)
        {
            // Away snapshots retain the last module and minigame bit. A turret game-over
            // must release the waiting player as soon as that absence is announced.
            _minigameLockPeers.Add(new MinigameLockPeer(
                peer.PeerId, peer.Name, peer.State.IsInMinigame && !peer.State.IsAway, peer.State.Module));
        }

        MinigameLockRule rule = new(Enabled: true, TimeoutMs: _settings.MinigameLockTimeoutMs);
        MinigameLockDecision decision = rule.Decide(
            local.IsInMinigame,
            _minigameLockPeers,
            _minigameLockOwner,
            _minigameLockStartedAtMs,
            SessionClock.NowMs,
            _settings.ExtraMinigameModules);

        if (!decision.Locked)
        {
            if (_minigameLockOwner != 0)
            {
                ReleaseMinigameLock(decision.Reason);
            }

            return;
        }

        if (_minigameLockOwner != decision.OwnerId)
        {
            _minigameLockOwner = decision.OwnerId;
            _minigameLockStartedAtMs = SessionClock.NowMs;
            MinigameLockOwnerName = decision.OwnerName;
            _log($"minigame lock: {decision.Reason}");
        }
        else if (!string.Equals(MinigameLockOwnerName, decision.OwnerName, StringComparison.Ordinal))
        {
            // A peer that renamed itself mid-lock (a rejoin under the same id). Cheap to
            // honour and it keeps the notice honest.
            MinigameLockOwnerName = decision.OwnerName;
        }

        _bridge.TryWriteMinigameLock(true, MinigameLockOwnerName);
    }

    private void ReleaseMinigameLock(string reason)
    {
        _minigameLockOwner = 0;
        _minigameLockStartedAtMs = 0;
        MinigameLockOwnerName = string.Empty;
        _bridge.TryWriteMinigameLock(false, string.Empty);

        if (reason.Length > 0)
        {
            _log($"minigame lock: {reason}");
        }
    }
}
