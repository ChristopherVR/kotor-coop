namespace KotorCoop.Plugin;

/// <summary>How <see cref="Odyssey.SetJournalState"/> should reach a target quest state,
/// given what the engine's own journal array currently shows for that tag.</summary>
internal enum JournalApplyAction
{
    /// <summary>The entry is already at the target — nothing to do (still shows the
    /// idempotence popup-suppression <c>alreadyCurrent</c> path in <see
    /// cref="Odyssey.SetJournalState"/>).</summary>
    Skip,

    /// <summary>A plain <c>CSWSJournal::SetState</c> call with <c>bOverrideHigher=0</c> —
    /// either a fresh tag (no existing entry) or a genuine advance on one that exists. This
    /// is the path ordinary quest progression has exercised for the entire life of the base
    /// game; it is not in question.</summary>
    Add,

    /// <summary>A genuine decrease. Live pass 2026-08-22 (docs/research/quest-sync.md)
    /// exercised <c>SetState</c>'s own <c>bOverrideHigher=1</c> path twice, independently, on
    /// two different tags: both calls reported success and logged the lower number, and a raw
    /// probe of the journal array's own <c>+0x20</c> state field showed neither one actually
    /// moved. Disassembly of <c>SetState</c> (0x005C5A40) shows the entry-array state write
    /// itself (<c>cmp</c>/<c>je</c>/<c>mov</c> at 0x5C5E27/0x5C5E29/0x5C5E30) is NOT
    /// conditioned on <c>bOverrideHigher</c> at all — that argument only feeds two
    /// notification/flag decisions elsewhere in the function (the "changed" flags bit at
    /// 0x5C5ADD/0x5C5ADF and the entry's own <c>+0x2C</c> bit 0x4 at 0x5C60CC-0x5C60E3), and
    /// specifically suppresses the JournalUpdated send at 0x5C629D-0x5C62C1 when the bit is
    /// set — matching the doc's existing note that 0x5C62BC's send is "gated only on
    /// bOverrideHigher == 0". The write is instead reached only via a branch at 0x5C5D3C
    /// inside a per-player loop (0x5C5C94-0x5C5D45) whose own gating condition (a per-player
    /// message/cache comparison at 0x5C5D24-0x5C5D3A) was not fully decompiled this pass — so
    /// which exact instruction refuses a decrease remains open (NEEDS-LIVE / a future
    /// decompiler-assisted pass), while the observed effect (decreases silently don't take,
    /// advances do) is confirmed live, twice.
    ///
    /// Given that, this composes two primitives that ARE each already live-verified instead
    /// of trying to force the disputed direct path: <c>CSWSJournal::Destroy</c> (removal,
    /// confirmed live 2026-08-22 to genuinely shrink the array) followed by a plain add at the
    /// target state — which is exactly the <see cref="Add"/> path above, proven correct by
    /// ordinary gameplay.
    /// </summary>
    RemoveThenAdd,
}

/// <summary>
/// Decides how <see cref="Odyssey.SetJournalState"/> reaches a target quest state. Separated
/// out so the decision is unit-testable without the engine's own array walk — the same split
/// <see cref="SpawnDrainRule"/> and <see cref="ControlReturnRule"/> use.
/// </summary>
internal static class JournalApplyRule
{
    /// <summary>
    /// <paramref name="hasExisting"/>/<paramref name="existingState"/> come from the caller's
    /// own walk of the live journal array (a tag not found has no existing state to compare
    /// against). <paramref name="targetState"/> is the session's authoritative value.
    /// </summary>
    public static JournalApplyAction Decide(bool hasExisting, int existingState, int targetState)
    {
        if (!hasExisting)
        {
            return JournalApplyAction.Add;
        }

        if (existingState == targetState)
        {
            return JournalApplyAction.Skip;
        }

        return existingState > targetState
            ? JournalApplyAction.RemoveThenAdd
            : JournalApplyAction.Add;
    }
}
