namespace KotorCoop.Plugin;

/// <summary>Where a party-roster add's object id came from, this pass.</summary>
internal enum PartyRosterBodyOrigin
{
    /// <summary><see cref="Odyssey.AddAvailableNpcByTemplate"/> (<c>CSWPartyTable::AddNPC_2</c> /
    /// <c>AddNPC</c>, <c>0x005645F0</c> / <c>0x00564300</c>) just recruited the slot: flag set,
    /// <c>AVAILNPC&lt;slot&gt;</c> cache written, NO body — the engine deletes the template
    /// creature it built, and the call returns a success flag rather than an id.</summary>
    RecruitedOffStage,

    /// <summary><see cref="Odyssey.SpawnAvailableNpc"/> (<c>CSWPartyTable::SpawnNPC</c>), or an
    /// object <see cref="Odyssey.ResolveServerObject"/> already resolved — either way, a body
    /// that has actually been placed in an area.</summary>
    LiveInWorld,
}

/// <summary>
/// The decision half of <c>PartyRosterController.Reconcile</c>'s "can this pass's object go
/// straight into <see cref="Odyssey.AddPartyMemberBySlot"/>" question, separated so it can be
/// unit-tested — the same split <c>ControlReturnRule</c>/<c>PartyFreezeRule</c> use; the engine
/// call itself does not exist in the test runner.
///
/// ⚠ THE BUG THIS CLOSES (docs/research/dialogue-consequences.md, live pass 2026-08-23,
/// <c>dialogue_recruit_sync</c>): the old code handed <see cref="Odyssey.AddAvailableNpcByTemplate"/>'s
/// return value to <see cref="Odyssey.AddPartyMemberBySlot"/> in the SAME pass, which refused
/// with failure 9 every time, while the very next pass spawned a real body and succeeded. The
/// 2026-08-23 write-up read that value as an off-stage object id that "never gained the in-world
/// flag bit"; the 2026-09-03 disassembly of <c>AddNPC_2</c>'s tail (transition-crashes.md)
/// corrected it — <c>AddNPC_2</c> DELETES its temporary creature after <c>AddNPC</c> and returns
/// <c>AddNPC</c>'s success flag (the live logs' "recruited ... as 0x1"), so the add was being
/// given the number 1 as an object id. Either way the rule is the same and stays: a recruit is
/// not an add. Only a body that has actually been placed in an area —
/// <see cref="Odyssey.SpawnAvailableNpc"/>'s (rebuilt from the cache the recruit just wrote), or
/// one already resolvable through <see cref="Odyssey.ResolveServerObject"/> — can be added, and
/// the "recruited but bodyless" branch does that on the next pass.
/// </summary>
internal static class PartyRosterRecruitRule
{
    /// <summary>Whether this pass's object id may be handed to
    /// <see cref="Odyssey.AddPartyMemberBySlot"/> in the same pass.</summary>
    public static bool CanAddImmediately(PartyRosterBodyOrigin origin) =>
        origin == PartyRosterBodyOrigin.LiveInWorld;
}
