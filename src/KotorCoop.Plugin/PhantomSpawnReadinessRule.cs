namespace KotorCoop.Plugin;

/// <summary>What <c>PhantomController.TrySpawnOne</c> should do about the client world this
/// frame.</summary>
internal enum PhantomSpawnReadiness
{
    /// <summary>The client world is built; spawn normally.</summary>
    Spawn,

    /// <summary>The client world is not built yet. Do nothing this pass and try again.</summary>
    Defer,

    /// <summary>The client world has not come up inside the wait budget. Spawn anyway — a
    /// possibly-unrendered peer beats no peer at all — but mark the body PROVISIONAL so it is
    /// replaced once the world does come up.</summary>
    SpawnProvisionally,
}

/// <summary>
/// Whether a phantom may be spawned yet, and when a body spawned too early has to be replaced
/// — a pure decision, so the live bug below is regression-tested without a game.
///
/// ⚠ THE BUG THIS CLOSES — "the joined player cannot see the host at all", live 2026-09-16/17.
///
/// The client's own frame pass is gated on <c>Odyssey.TryGetWorldEpoch</c>, which asks the
/// SERVER whether a module is up (<c>CServerExoAppInternal+0x100B4</c> clear, mode word 2).
/// The CLIENT half of the world — the twins, their animbases, the scene the renderer walks —
/// is built AFTERWARDS, and nothing was watching for it. So on the one flow that reloads the
/// world mid-session (<c>auto_adopt_world</c>: the joiner loads the host's staged autosave to
/// adopt their world) the sequence measured live was:
///
/// <code>
/// 22:45:18.749  menu: adopting a world - login left on TEMP:pifo
/// 22:45:19.455  spawned phantom in slot 0: id 0x12D ... (renders no twin yet)
/// 22:45:19.458  session released: destroyed phantom 0x12D
/// 22:45:20.442  spawned phantom in slot 0: id 0x12E ... (renders no twin yet)
/// 22:45:20.453  equip: phantom 0x12E wears g_a_clothes01 (slot 1)
/// 22:45:20.471  gait-name: ... player-&gt; name='' len=-1 model='' weaponClass=255 animBase=0x0
/// </code>
///
/// That last line is the LOCAL PLAYER, one frame after the spawn: no client twin, no animbase,
/// nothing on screen yet. The phantom created in that window was still invisible three and a
/// half hours later, and — this is the part that made it hard to find — every field anyone
/// would think to check read back CORRECT on a live memory probe: server appearance row 114,
/// twin desired row 114, <c>model='pfbbl01'</c> (identical to the host's own body), a non-null
/// <c>Gob*</c>, and a twin position that tracked the host's walks to two decimal places. It was
/// simply never attached to the client area that was still being built underneath it. Forcing a
/// re-spawn into the settled world (disconnect/reconnect, 02:23:43) made the same peer appear
/// instantly with no other change.
///
/// So the gate is not "is the server world up" (already true, too early) but "does the LOCAL
/// PLAYER have a body on screen" — <see cref="Odyssey.HasRenderedBody"/>. The budget exists
/// because a gate that can never open would mean NO peers at all, which is strictly worse than
/// a peer who might not render: after it expires the body is spawned anyway and flagged
/// provisional, and <see cref="ShouldReplaceProvisional"/> swaps it for a fresh one the moment
/// the client world does come up.
/// </summary>
internal static class PhantomSpawnReadinessRule
{
    /// <summary>How long the client world gets to finish building before a phantom is spawned
    /// into it regardless. Ten seconds is far longer than any load measured on the rig (the
    /// adoption reload's client half was up well inside two) and short enough that a player
    /// staring at an empty room is not left waiting.</summary>
    public const int ClientWorldWaitMs = 10_000;

    /// <summary>At most one forced replacement per provisional body: the replacement is a
    /// destroy plus a spawn, and a loop of those on a rig where the gate never opens would be
    /// worse than the invisible peer it is trying to fix.</summary>
    public const int MaxProvisionalReplacements = 1;

    /// <param name="localBodyRendering"><see cref="Odyssey.HasRenderedBody"/> for the LOCAL
    /// player — the proxy for "the client half of this world is built".</param>
    /// <param name="nowMs">Monotonic milliseconds.</param>
    /// <param name="waitingSinceMs">When this controller first refused to spawn because the
    /// client world was not up, or 0 if it has not refused yet.</param>
    /// <param name="budgetMs"><see cref="ClientWorldWaitMs"/>, injectable for the tests.</param>
    public static PhantomSpawnReadiness Decide(
        bool localBodyRendering, long nowMs, long waitingSinceMs, int budgetMs)
    {
        if (localBodyRendering)
        {
            return PhantomSpawnReadiness.Spawn;
        }

        // First refusal: the caller has not started the clock yet, so it cannot have expired.
        if (waitingSinceMs == 0)
        {
            return PhantomSpawnReadiness.Defer;
        }

        return nowMs - waitingSinceMs >= budgetMs
            ? PhantomSpawnReadiness.SpawnProvisionally
            : PhantomSpawnReadiness.Defer;
    }

    /// <summary>
    /// Whether a body spawned under <see cref="PhantomSpawnReadiness.SpawnProvisionally"/> must
    /// now be destroyed so the next sweep spawns a replacement into the world that has since
    /// come up. False for an ordinary body, false while the world is still not up (destroying
    /// it then would only produce another provisional one), and false once the replacement
    /// budget is spent.
    /// </summary>
    public static bool ShouldReplaceProvisional(
        bool provisional, bool localBodyRendering, int replacementsDone, int maxReplacements) =>
        provisional && localBodyRendering && replacementsDone < maxReplacements;
}
