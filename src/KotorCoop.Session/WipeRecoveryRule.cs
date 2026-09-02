using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>Where the wipe-recovery watch is: nothing seen, or a session wipe was seen and the
/// host has not yet been seen back in a world since.</summary>
public readonly record struct WipeRecoveryState(bool Wiped)
{
    public static WipeRecoveryState Initial => new(false);
}

/// <summary>
/// The rejoin trigger a session wipe needs and neither save/load classifier can produce.
///
/// After a unanimous wipe (<c>DeathPump</c>: every participant's whole party down) both engines
/// run their game-over: the world tears down ~13 s later and each player is parked at the
/// game-over menu with the session still connected. The host then loads a save. On the client
/// that load is invisible to <see cref="SaveOrLoadClassifier"/>: its load-serial edge only ever
/// comes from a world TEARDOWN (<c>WorldLoadRule</c>), and that edge fired at the game-over
/// teardown, long outside the 5 s correlation window of the load itself; and a reload of the
/// session save leaves the save-identity digest unchanged anyway. Live 2026-09-02
/// (docs/research/death-handling.md §6.4): host back in END_M01AA, client parked at the
/// game-over menu for good, no "host loaded a save" on either side.
///
/// So this watches the SESSION instead of the save files: remember that the wipe happened
/// (both this party and the host's reported <see cref="PlayerState.FlagPartyWiped"/> at once),
/// and the next time the host is reported standing in a world — module known, not away, not
/// down — follow them, exactly the way <see cref="RejoinTrigger.HostReload"/> does after an
/// in-world load. Pure so the edges are what a test pins.
///
/// Deliberately NOT cleared when the local player comes back first: a client who loads their
/// own save from the game-over menu and then sees the host return is still yanked into the
/// host's world — the host is authoritative, and that is what <see cref="RejoinTrigger.HostReload"/>
/// already does to a client standing in a world of their own.
/// </summary>
public static class WipeRecoveryRule
{
    /// <param name="local">This instance's own state, or null while the game cannot be read
    /// (the world is torn down; the player is at the game-over menu).</param>
    /// <param name="host">The host's state as reported over the wire, or null when the host is
    /// not in the peer list this tick.</param>
    /// <returns>Whether to start a rejoin THIS call, and the new state.</returns>
    public static (bool ShouldTrigger, WipeRecoveryState Next) Observe(
        WipeRecoveryState current, PlayerState? local, PlayerState? host)
    {
        if (host is not { } hostState)
        {
            return (false, current);
        }

        if (!current.Wiped)
        {
            // The latch: both parties gone at the same time. The local flag is readable for the
            // ~13 s between the release and the teardown, which is plenty for a 30 Hz tick.
            bool wiped = local is { IsPartyWiped: true } && hostState.IsPartyWiped;
            return (false, new WipeRecoveryState(wiped));
        }

        // A parked host keeps publishing its last known (wiped, away) state; the first report
        // that is none of those things is the host standing in a loaded world again.
        bool hostBack = hostState.Module.Length > 0
            && !hostState.IsAway
            && !hostState.IsPartyWiped
            && !hostState.IsDowned;

        return hostBack ? (true, WipeRecoveryState.Initial) : (false, current);
    }
}
