using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The pure decision behind the client-first-door case
/// (docs/research/transition-smoothness.md), extended for lead-swap: should THIS
/// instance arm the plugin's refusal of a CLIENT-initiated real door transition right now, and
/// if so, why?
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
    /// (<see cref="AgentSettings.ClientDoorRefusalEnabled"/>) — the master switch: OFF means no
    /// refusal ever arms, for either <see cref="FollowLeadMode"/>.</param>
    /// <param name="hostRecentlyTransitioning">The host has announced (or queued movies for)
    /// a transition of its own recently — see <c>CoopAgent</c>'s tracked
    /// <c>_lastHostTransitionSignalAtMs</c>. Disarms the refusal unconditionally: a client
    /// crossing the SAME door the host just led through (the accidental-success case
    /// docs/research/cinematics.md §2.4 names) is not a premature crossing and must not be
    /// cancelled, under either lead mode.</param>
    /// <param name="mode">coop.ini <c>follow_lead</c> (<see cref="AgentSettings.FollowLeadMode"/>).
    /// Defaults to <see cref="FollowLeadMode.Host"/> so every pre-existing call site (and every
    /// pre-existing test) that does not pass it keeps EXACTLY today's behaviour.</param>
    /// <param name="hostBusy">Only consulted under <see cref="FollowLeadMode.Any"/>: the host is
    /// currently mid its own transition, in dialogue, or in real combat, per
    /// <see cref="LeadFollowRule.HostMaySafelyFollow"/> read from the HOST's own published
    /// <c>PlayerState</c> flags (a client reads these off the host's <c>RemotePeer</c>, the same
    /// way it already reads everything else about the host).</param>
    public static bool ShouldArm(
        bool isClient,
        bool followHostModule,
        bool refusalEnabled,
        bool hostRecentlyTransitioning,
        FollowLeadMode mode = FollowLeadMode.Host,
        bool hostBusy = false) =>
        Decide(isClient, followHostModule, refusalEnabled, hostRecentlyTransitioning, mode, hostBusy)
            != ClientDoorRefusalReason.None;

    /// <summary>
    /// The full decision, including WHY — the plugin shows a different on-screen line for
    /// "the host must lead the way" (<see cref="FollowLeadMode.Host"/>) than for "the host
    /// cannot follow right now" (<see cref="FollowLeadMode.Any"/>, host busy), and it cannot
    /// tell them apart from the boolean alone.
    /// </summary>
    public static ClientDoorRefusalReason Decide(
        bool isClient,
        bool followHostModule,
        bool refusalEnabled,
        bool hostRecentlyTransitioning,
        FollowLeadMode mode = FollowLeadMode.Host,
        bool hostBusy = false)
    {
        if (!isClient || !followHostModule || !refusalEnabled || hostRecentlyTransitioning)
        {
            return ClientDoorRefusalReason.None;
        }

        if (mode == FollowLeadMode.Host)
        {
            return ClientDoorRefusalReason.HostMustLead;
        }

        // follow_lead=any: the client MAY lead - only refuse when the host would not be able
        // to safely follow right now, so the crossing does not desync the world out from
        // under it. Otherwise let it through: it becomes an announce the host follows.
        return hostBusy ? ClientDoorRefusalReason.HostBusy : ClientDoorRefusalReason.None;
    }
}
