namespace KotorCoop.Plugin;

/// <summary>Where the external agent stands, judged purely from its last heartbeat.</summary>
internal enum AgentLivenessState
{
    /// <summary>No agent has ever pumped this block (heartbeat still 0) — nothing to warn
    /// about, since nothing was ever driving remote peers in the first place.</summary>
    NeverLive,

    /// <summary>The heartbeat has advanced within <see cref="AgentLiveness.StaleAfterMs"/>.</summary>
    Live,

    /// <summary>The heartbeat has sat still past <see cref="AgentLiveness.StaleAfterMs"/> but
    /// not yet past <see cref="AgentLiveness.DeadAfterMs"/>: worth an on-screen warning and
    /// freezing remote-peer writes, not yet worth a relaunch.</summary>
    Stale,

    /// <summary>The heartbeat has sat still past <see cref="AgentLiveness.DeadAfterMs"/> —
    /// the point <see cref="AgentLauncher"/> starts considering a relaunch.</summary>
    Dead,
}

/// <summary>
/// Pure decisions about whether the external agent is still alive, from nothing but the
/// heartbeat value it last stamped (agent-clock milliseconds — Environment.TickCount64,
/// the OS uptime counter both the plugin and the agent read independently, so no clock
/// sync or "did the value change" latch is needed: staleness is just <c>now - lastBeat</c>)
/// and the current time. Kept dependency-free — no SharedBlockView, no engine pointers —
/// so it is unit-testable without a live block; see AgentLivenessTests and
/// KotorCoop.Tests.csproj's plugin-file include list.
/// </summary>
internal static class AgentLiveness
{
    /// <summary>How long the heartbeat may sit unchanged before the on-screen warning fires
    /// and remote-peer writes freeze.</summary>
    public const long StaleAfterMs = 5_000;

    /// <summary>
    /// How long it may sit unchanged before <see cref="AgentLauncher"/> will attempt a
    /// relaunch. Deliberately longer than <see cref="StaleAfterMs"/>: the on-screen warning
    /// is a small hint the player can act on immediately ("type reconnect"), while an
    /// automatic relaunch is a bigger hammer that should not fire on a hiccup the player has
    /// not even had time to read about.
    /// </summary>
    public const long DeadAfterMs = 15_000;

    /// <summary>Minimum gap between two relaunch attempts, so an agent that keeps failing to
    /// start does not spawn a new process every frame.</summary>
    public const long RelaunchCooldownMs = 60_000;

    /// <summary>
    /// Classifies the agent's liveness from its last heartbeat value and the current time,
    /// both in the same agent-clock milliseconds. <paramref name="lastBeat"/> of 0 means the
    /// block has never heard from an agent at all (the field's zero-initialised state, which
    /// a real Environment.TickCount64 stamp essentially never collides with).
    /// </summary>
    public static AgentLivenessState Evaluate(long lastBeat, long now)
    {
        if (lastBeat == 0)
        {
            return AgentLivenessState.NeverLive;
        }

        long age = now - lastBeat;
        if (age >= DeadAfterMs)
        {
            return AgentLivenessState.Dead;
        }

        return age >= StaleAfterMs ? AgentLivenessState.Stale : AgentLivenessState.Live;
    }

    /// <summary>
    /// Whether AgentLauncher should attempt a relaunch right now: the agent must be
    /// classified Dead, THIS launcher must be the one that started the process being
    /// watched (an agent the player started by hand is never taken over), and the last
    /// attempt — 0 meaning none yet — must be outside <see cref="RelaunchCooldownMs"/>.
    /// </summary>
    public static bool ShouldRelaunch(long lastBeat, long now, bool startedByLauncher, long lastAttemptAtMs) =>
        startedByLauncher
        && Evaluate(lastBeat, now) == AgentLivenessState.Dead
        && (lastAttemptAtMs == 0 || now - lastAttemptAtMs >= RelaunchCooldownMs);
}
