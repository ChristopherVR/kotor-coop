namespace KotorCoop.Plugin;

/// <summary>
/// The load-detection primitive roadmap item 14 phase 2 needed (docs/research/session-save.md's
/// "Phase 2" section): a save-identity DIGEST change alone cannot tell a genuine "Load Game to a
/// different save" apart from an ordinary module transition, because KOTOR's own engine autosaves
/// through every transition too (live-measured, shared-state-audit.md round 53 — the exact
/// false-positive that shipped <c>SessionSaveHostRefusal</c> default OFF in phase 1).
///
/// PluginHost already tracks two independent facts every frame, for reasons that predate this
/// file:
/// <list type="bullet">
/// <item><c>Odyssey.TryGetWorldEpoch</c> — combines the server's object-array pointer (which
/// <c>UnloadModule</c> replaces on every module load, load OR transition alike) with a hash of
/// the module name. A plain SAVE never calls <c>UnloadModule</c> — nothing about writing the
/// current world to disk tears down the object array — so the epoch does NOT change on a save.
/// INFERRED from <c>TryGetWorldEpoch</c>'s own doc comment (sourced from the disassembly around
/// <c>CServerExoApp::GetObjectArray</c>/<c>UnloadModule</c>), not measured live against a real
/// in-game save.</item>
/// <item><c>Odyssey.IsAreaTransitionPending</c> (trans_pending) — a byte the engine itself sets
/// roughly half a second BEFORE an ordinary door/trigger transition stores the current area,
/// tracked by PluginHost as <c>_transitionPending</c>. An ordinary transition is always preceded
/// by this flag going true; a Load Game action (main menu OR the in-game pause menu) goes
/// straight from <c>CSWGuiSaveLoad</c>'s LOAD branch into <c>ExecuteLoadModule</c> with no such
/// precursor — trans_pending belongs to <c>CSWSArea</c>'s own scripted-transition bookkeeping,
/// unrelated to the save/load UI. INFERRED, not measured live: see this codebase's own
/// convention of shipping a detector with its assumptions named rather than silently trusted.</item>
/// </list>
///
/// Combined: a world teardown (the epoch about to become unreadable) that trans_pending never
/// announced is the discontinuity a Load Game action produces and an ordinary transition does
/// not. This is the whole rule — deliberately a one-line pure function, so the assumption above
/// is exactly what a test pins, not buried inside PluginHost's unsafe engine-pointer code.
/// </summary>
internal static class WorldLoadRule
{
    /// <summary>
    /// True when a world teardown should be treated as an unannounced load (bump
    /// <c>SharedBlockView.PublishWorldLoadObserved</c>), false when it was an ordinary,
    /// already-announced module transition (do nothing new — the existing epoch-invalidation
    /// path already handles it).
    /// </summary>
    /// <param name="transitionWasPending">Whatever <c>Odyssey.IsAreaTransitionPending()</c> had
    /// most recently reported (PluginHost's own <c>_transitionPending</c>) at the instant the
    /// world became unreadable — i.e. read BEFORE it is reset for the next epoch.</param>
    public static bool IsUnannouncedTeardown(bool transitionWasPending) => !transitionWasPending;
}
