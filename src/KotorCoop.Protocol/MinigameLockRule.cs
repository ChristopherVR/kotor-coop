namespace KotorCoop.Protocol;

/// <summary>What a minigame peer-lock pass decided this tick. <see cref="OwnerId"/> is 0 and
/// <see cref="OwnerName"/> empty whenever <see cref="Locked"/> is false.</summary>
/// <param name="Locked">Hold this instance's own player still and show the notice.</param>
/// <param name="OwnerId">The peer whose minigame the lock is being held for.</param>
/// <param name="OwnerName">That peer's published name, for the notice.</param>
/// <param name="Reason">Why the lock started or ended, for one log line on the edge.</param>
public readonly record struct MinigameLockDecision(
    bool Locked,
    uint OwnerId,
    string OwnerName,
    string Reason);

/// <summary>
/// The decision half of "one peer races, everybody else waits" (docs/research/minigames.md
/// §24) — the product rule the swoop race needs and the turret sequence wants: a KOTOR
/// minigame swaps the racer's whole area for a track, so for as long as it lasts there is no
/// shared world for the other players to be standing in. Rather than let them wander a world
/// the racer cannot see, and rather than drag them onto the track (which is single-seat), they
/// are control-locked with a line saying who is racing.
///
/// Pure, like <see cref="MinigameRule"/> next to it, and for the same reason: the three ways
/// this ends — the owner finishes, the owner disconnects, or the owner never finishes and the
/// timeout fires — are one line each to test here and invisible-until-live anywhere else. A
/// lock that fails to RELEASE is the worst outcome this feature has (a player frozen for the
/// rest of the session), so every release path gets its own test.
///
/// Ownership is FIRST-COME and sticky: the first peer observed racing owns the lock until they
/// stop, so two peers entering minigames a tick apart do not make the notice flicker between two
/// names. Ties among simultaneous starters break on the lowest peer id, which is stable across
/// ticks (unlike roster order).
///
/// ⚠ A LOCK NEEDS BOTH THE FLAG AND A TRACK MODULE, and the second half is not belt-and-braces
/// — it is what keeps Pazaak out. <c>pazaak_detect</c> (ON by default) deliberately sets the SAME
/// <c>PlayerState.FlagInMinigame</c> bit for a running card hand, because for phantom-parking
/// purposes a player staring at a Pazaak table is exactly as absent as one on a swoop bike. For
/// LOCKING purposes they are not remotely the same: a Pazaak hand is an ordinary, long, optional
/// side activity that happens in a normal module, and freezing everybody else through one would be
/// a plain regression. The flag alone cannot tell the two apart (it is one bit, and widening it
/// would be a wire change); the peer's published MODULE can, and already crosses the network.
/// See <see cref="MinigameModuleRule"/> for the catalogue and the <c>minigame_modules</c> escape
/// hatch for a mod with its own track.
/// </summary>
/// <param name="Enabled">coop.ini <c>minigame_lock_peers</c> — default ON.</param>
/// <param name="TimeoutMs">coop.ini <c>minigame_lock_timeout_s</c> x 1000. A lock older than
/// this releases regardless of what the owner's flag still says: a peer whose agent died
/// mid-race leaves its last published state behind forever, and "frozen forever" must not be a
/// state this feature can reach. Zero or negative disables the timeout.</param>
public readonly record struct MinigameLockRule(bool Enabled, int TimeoutMs)
{
    /// <summary>
    /// Decides this tick's lock from the whole peer roster — deliberately the UNFILTERED
    /// roster, not <c>CoopAgent</c>'s own <c>visible</c> list: <c>minigame_hide_peer</c>
    /// (default ON) removes a peer from that list in the very tick their minigame flag turns
    /// true, which is exactly the tick this rule needs to see them.
    /// </summary>
    /// <param name="localInMinigame">True when THIS player is the one racing. The racer is
    /// never locked by their own race, and a peer who also happens to be in a minigame does
    /// not lock them either — two players in separate minigames simply both play.</param>
    /// <param name="peers">Every peer this instance currently knows: id, published name,
    /// minigame flag and module.</param>
    /// <param name="extraModules">coop.ini <c>minigame_modules</c> — extra track module names
    /// on top of <see cref="MinigameModuleRule.BuiltIn"/>.</param>
    /// <param name="currentOwnerId">The owner from the previous tick, or 0. Passing this back
    /// in is what makes ownership sticky.</param>
    /// <param name="lockStartedAtMs">When the current lock began (only meaningful while
    /// <paramref name="currentOwnerId"/> is non-zero).</param>
    /// <param name="nowMs">The session clock.</param>
    public MinigameLockDecision Decide(
        bool localInMinigame,
        IReadOnlyList<MinigameLockPeer> peers,
        uint currentOwnerId,
        long lockStartedAtMs,
        long nowMs,
        IReadOnlyList<string>? extraModules = null)
    {
        if (!Enabled)
        {
            return default;
        }

        // The racer is never locked. Checked before anything else so a two-player session in
        // which BOTH ends are in a minigame (the turret gunner and a swoop racer, or simply a
        // stale flag on one side) can never deadlock into locking each other.
        if (localInMinigame)
        {
            return currentOwnerId == 0
                ? default
                : new MinigameLockDecision(false, 0, string.Empty,
                    "this player is in a minigame too — control stays theirs");
        }

        // Is the peer who already owns the lock still in one?
        if (currentOwnerId != 0)
        {
            foreach (MinigameLockPeer peer in peers)
            {
                if (peer.PeerId != currentOwnerId)
                {
                    continue;
                }

                if (!IsRacing(peer, extraModules))
                {
                    return new MinigameLockDecision(false, 0, string.Empty,
                        $"{Describe(peer.Name)} finished — control is yours again");
                }

                if (TimeoutMs > 0 && nowMs - lockStartedAtMs >= TimeoutMs)
                {
                    return new MinigameLockDecision(false, 0, string.Empty,
                        $"{Describe(peer.Name)}'s minigame ran past {TimeoutMs / 1000}s — "
                        + "releasing control rather than waiting forever");
                }

                return new MinigameLockDecision(true, peer.PeerId, peer.Name, string.Empty);
            }

            // The owner is gone from the roster entirely — a disconnect, mid-race.
            return new MinigameLockDecision(false, 0, string.Empty,
                "the racing peer left the session — control is yours again");
        }

        // No owner yet: adopt the lowest-id peer currently in a minigame.
        MinigameLockPeer? candidate = null;
        foreach (MinigameLockPeer peer in peers)
        {
            if (IsRacing(peer, extraModules) && (candidate is null || peer.PeerId < candidate.Value.PeerId))
            {
                candidate = peer;
            }
        }

        return candidate is { } owner
            ? new MinigameLockDecision(true, owner.PeerId, owner.Name,
                $"{Describe(owner.Name)} started a minigame — holding control until it ends")
            : default;
    }

    /// <summary>Is this peer in a minigame that other players must WAIT for — as opposed to one
    /// they can simply ignore? See the class remarks for why the module test carries as much
    /// weight as the flag.</summary>
    private static bool IsRacing(MinigameLockPeer peer, IReadOnlyList<string>? extraModules) =>
        !peer.Away
        && (peer.InMinigame || MinigameModuleRule.IsSoloEnvironmentModule(peer.Module))
        && MinigameModuleRule.IsMinigameModule(peer.Module, extraModules);

    private static string Describe(string name) => name.Length == 0 ? "A peer" : name;
}

/// <summary>One row of <see cref="MinigameLockRule.Decide"/>'s input: the four things about a
/// peer the decision actually reads, so a test does not have to build a whole
/// <c>RemotePeer</c>. <paramref name="Module"/> is the peer's own published module name — the
/// half of the decision that separates a swoop track from a Pazaak table.</summary>
public readonly record struct MinigameLockPeer(uint PeerId, string Name, bool InMinigame, string Module, bool Away = false);
