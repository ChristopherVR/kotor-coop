namespace KotorCoop.Session;

/// <summary>
/// The pure decision behind task 3's client-first-door case
/// (docs/research/transition-smoothness.md): should THIS instance arm the plugin's refusal of
/// a CLIENT-initiated real door transition right now?
///
/// The plugin has no notion of "host" or "client" — that is a session-level concept — so the
/// agent computes this every tick and re-sends it as a LEVEL (<c>IGameBridge.TryArmClientDoorRefusal</c>),
/// the same shape as every other "trust what this says right now" agent-&gt;plugin flag
/// (<c>OffsetSuppressGameOver</c>, <c>OffsetSuppressSwap</c>).
/// </summary>
public static class ClientDoorRefusalRule
{
    /// <param name="isClient">This instance does not own enemies/encounters — the same
    /// <c>!session.IsEnemyAuthority</c> test <c>PumpTravel</c> already uses to mean "only a
    /// client follows anyone".</param>
    /// <param name="followHostModule">The follow feature itself is on
    /// (<see cref="AgentSettings.FollowHostModule"/>) — refusing a door with follow disabled
    /// would strand a player who explicitly asked to play apart.</param>
    /// <param name="refusalEnabled">coop.ini <c>client_door_refusal</c>
    /// (<see cref="AgentSettings.ClientDoorRefusalEnabled"/>).</param>
    /// <param name="hostRecentlyTransitioning">The host has announced (or queued movies for)
    /// a transition of its own recently — see <c>CoopAgent</c>'s tracked
    /// <c>_lastHostTransitionSignalAtMs</c>. Disarms the refusal: a client crossing the SAME
    /// door the host just led through (the accidental-success case
    /// docs/research/cinematics.md §2.4 names) is not a premature crossing and must not be
    /// cancelled.</param>
    public static bool ShouldArm(
        bool isClient, bool followHostModule, bool refusalEnabled, bool hostRecentlyTransitioning) =>
        isClient && followHostModule && refusalEnabled && !hostRecentlyTransitioning;
}
