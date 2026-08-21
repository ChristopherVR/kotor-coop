namespace KotorCoop.Plugin;

/// <summary>
/// What the enemy owner's engine currently reads for one creature. Matches
/// <c>SharedBlock.LivenessState*</c> (and <c>Protocol.CreatureLivenessState</c>) by VALUE —
/// the plugin takes no assembly dependency on Protocol (it is a NativeAOT win-x86 DLL with no
/// dependencies on purpose; see KotorCoop.Plugin.csproj's own comment), so the three states
/// are duplicated here the same way <c>SharedBlock.EnemyFlagDead</c> duplicates its wire
/// counterpart.
/// </summary>
internal enum LivenessHostState : uint
{
    /// <summary>The owner's engine still owns a live, non-corpse body under this id.</summary>
    Alive = 0,

    /// <summary>The owner's engine owns the body but its hit points read at or below zero —
    /// a corpse, still resolvable.</summary>
    Dead = 1,

    /// <summary>Nothing answers to this id in the owner's engine any more — no corpse to
    /// mirror.</summary>
    Gone = 2,
}

/// <summary>What a creature-liveness reconciliation pass should do for one entry.</summary>
internal enum LivenessAction
{
    /// <summary>The two instances already agree, or there is nothing safe to do about it yet
    /// (a fresh id this pass has not resolved, for instance).</summary>
    Nothing,

    /// <summary>The owner says this body is a corpse and it is still standing here: kill it
    /// for real, through the ordinary pipeline, so the corpse/loot/XP/OnDeath all run exactly
    /// as they would for any other death.</summary>
    Kill,

    /// <summary>The owner says nothing answers to this id any more and a body still exists
    /// here: destroy it outright — there is no corpse to leave behind.</summary>
    Destroy,

    /// <summary>The owner says this body is alive and this instance's own engine already has
    /// it dead or unresolvable. Count it; never resurrect it — a body this instance killed (or
    /// lost) locally is not something a reconciliation channel should undo, and the right fix
    /// for a genuine divergence is upstream, not a forced revival here.</summary>
    Anomaly,
}

/// <summary>
/// The reconcile decision table for one creature-liveness entry, separated from
/// <c>LivenessReconcileController</c> so it can be unit-tested without a running game — the
/// same split <c>ControlReturnRule</c> and <c>MirrorHealthRule</c> already use for their own
/// controllers. See docs/research/creature-liveness.md.
/// </summary>
internal static class LivenessRule
{
    /// <summary>
    /// Decides the action for one entry.
    ///
    /// <paramref name="hostState"/> is what the enemy owner's engine currently reads for this
    /// id. <paramref name="localResolved"/> is whether THIS instance's own engine still owns a
    /// body under the translated id — the caller has already resolved the id (through
    /// <c>MaterialisedEnemies.ToLocal</c> for a stand-in, or directly for a GIT-identical
    /// creature) before calling this; a body that fails to resolve reads identically to one
    /// that was genuinely destroyed, which is the correct conflation — this instance cannot
    /// tell them apart and does not need to. <paramref name="localDead"/> and
    /// <paramref name="localPlot"/> are only meaningful when <paramref name="localResolved"/>
    /// is true.
    ///
    /// Plot creatures are un-killable by the module's own rule (a cutscene actor's health is
    /// never anyone's to spend — <c>CombatController</c> already leaves them alone entirely),
    /// so a <see cref="LivenessHostState.Dead"/> verdict is a no-op for one; a
    /// <see cref="LivenessHostState.Gone"/> verdict still applies — the module itself removed
    /// the actor (a script's own <c>DestroyObject</c>), and un-killable is not the same claim
    /// as un-destroyable.
    /// </summary>
    public static LivenessAction Decide(
        LivenessHostState hostState, bool localResolved, bool localDead, bool localPlot)
    {
        switch (hostState)
        {
            case LivenessHostState.Alive:
                return localResolved && !localDead ? LivenessAction.Nothing : LivenessAction.Anomaly;

            case LivenessHostState.Dead:
                if (!localResolved || localDead || localPlot)
                {
                    return LivenessAction.Nothing;
                }

                return LivenessAction.Kill;

            case LivenessHostState.Gone:
                return localResolved ? LivenessAction.Destroy : LivenessAction.Nothing;

            default:
                return LivenessAction.Nothing;
        }
    }
}
