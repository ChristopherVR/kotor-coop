namespace KotorCoop.Plugin;

/// <summary>
/// The decision half of minigame gating (swoop races, forced turret sequences), separated
/// so the three independent policy questions below are each one line to test rather than
/// folded into the agent's pump loop, where a wrong verdict is invisible until it is live.
///
/// A player inside <c>CSWCModule::GetMiniGame</c> (docs/research/minigames.md) is not
/// meaningfully standing in the shared world: their engine has swapped the whole area for a
/// track or a gunnery view, so a peer's copy of them is stale the instant the sequence
/// starts, and any "damage" or "presence" this instance would publish while inside one is
/// not the fight anybody else is having.
///
/// Kept toggle-carrying rather than a static method set because both toggles come from
/// coop.ini once per session and every call site needs both — <c>Decide</c> methods that
/// took the toggle as a parameter every time read fine here but drift wherever a caller
/// forgets one, which a config object at the call site cannot do.
/// </summary>
internal readonly record struct MinigameRule(bool HidePeersEnabled, bool SpectateEnabled)
{
    /// <summary>
    /// Should THIS peer's phantom be parked (hidden, not driven) the same way a peer in a
    /// different module already is? Only the peer's OWN flag matters here — whether the
    /// LOCAL player also happens to be in a minigame says nothing about whether the peer's
    /// body belongs in this instance's world right now.
    /// </summary>
    public bool ShouldHidePeer(bool peerInMinigame) => HidePeersEnabled && peerInMinigame;

    /// <summary>
    /// Should this instance stop applying damage forwarded from peers (and any incoming
    /// phantom write that would land on the local player)? True exactly while the LOCAL
    /// player is in a minigame — a turret gunner cannot be hit by a monster in a fight three
    /// players away, and there is no engine body standing in the world to apply it to.
    /// </summary>
    public bool ShouldSuppressIncomingDamage(bool localInMinigame) => localInMinigame;

    /// <summary>
    /// Should this instance stop publishing its own enemy/party presence census? True
    /// exactly while the LOCAL player is in a minigame: whatever the engine reports as
    /// nearby creatures during a swoop/turret sequence is not the shared-world fight, and
    /// publishing it would send every peer's mirror chasing a fight that is not happening.
    /// </summary>
    public bool ShouldSuppressPresencePublish(bool localInMinigame) => localInMinigame;
}
