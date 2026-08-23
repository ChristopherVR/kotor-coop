namespace KotorCoop.Plugin;

/// <summary>Where a party-roster add's object id came from, this pass.</summary>
internal enum PartyRosterBodyOrigin
{
    /// <summary><see cref="Odyssey.AddAvailableNpcByTemplate"/> (<c>CSWPartyTable::AddNPC_2</c> /
    /// <c>AddNPC</c>, <c>0x005645F0</c> / <c>0x00564300</c>) — an off-stage character-sheet
    /// object, constructed and registered as a real engine object but never placed in an
    /// area.</summary>
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
/// <c>dialogue_recruit_sync</c>). <c>CSWPartyTable::AddNPC_2</c>/<c>AddNPC</c>
/// (<c>0x005645F0</c>/<c>0x00564300</c>, <see cref="Odyssey.AddAvailableNpcByTemplate"/>)
/// constructs and registers a companion object in <c>CGameObjectArray</c> — VERIFIED by
/// disassembly, its constructor call is the identical <c>CSWSCreature::Constructor(0x7F000000,
/// 0)</c> `docs/re/spawn.md`'s own `CreateObject` trace uses for step 2/2b — but it never calls
/// <c>CSWSCreature::AddToArea</c> (`0x004FA100`) the way `CreateObject`'s own trace does at its
/// step 9. So the object's type byte at <c>+0x08</c> never gains the in-world flag bit a live
/// creature carries: this project's own `docs/research/enemy-materialisation.md` finding is
/// that a live census creature reads <c>0x40005</c> at that offset, where a freshly constructed
/// one reads plain <c>5</c> (the type `CSWSObject::Constructor` writes, VERIFIED against
/// `CreateObject`'s own step 2a). <c>CSWPartyTable::AddMember</c>'s own live-body check — VERIFIED
/// by disassembling `0x00565625` — does not call the generic, flag-blind
/// <c>CServerExoApp::GetGameObject</c> walker; it calls the TYPED getter
/// <c>GetCreatureByGameObjectID</c> (`0x004AE770` -&gt; `0x004B1790`) at `0x0056566A`, which
/// re-tests that exact flag bit — the same object-type trap this project's docs warn every other
/// resolver about (`docs/research/enemy-presence-sync.md`, `enemy-materialisation.md`). So
/// <see cref="Odyssey.AddPartyMemberBySlot"/> called on an object fresh off
/// <see cref="Odyssey.AddAvailableNpcByTemplate"/> refuses with failure 9 FOREVER, not just the
/// pass it was built on — this is a structural mismatch, not a timing race, confirmed against
/// the live log (the object was still resolvable through the generic
/// <see cref="Odyssey.ResolveServerObject"/> afterward, proving it is a real, registered object;
/// it simply never became "live" by the typed check <c>AddMember</c> itself insists on). Only
/// <see cref="Odyssey.SpawnAvailableNpc"/> (<c>CSWPartyTable::SpawnNPC</c>/<c>GetNPCObject</c>) —
/// or an object already resolvable through <see cref="Odyssey.ResolveServerObject"/> — has
/// actually been placed in an area and can pass that check.
///
/// The old code attempted the add anyway in the SAME pass <see cref="Odyssey.AddAvailableNpcByTemplate"/>
/// ran, which always failed and left the off-stage object behind, unreferenced, while the VERY
/// NEXT pass took the OTHER branch (<see cref="Odyssey.SpawnAvailableNpc"/>) and built a SECOND,
/// different object that did succeed — two engine objects built for one recruit. The fix is not
/// to clean up after that second build: the first object is not a rendered, in-world duplicate —
/// it never left the off-stage character-sheet cache <see cref="Odyssey.AddAvailableNpcByTemplate"/>
/// itself populates (the `"AVAILNPC%d"` resource — see <see cref="Odyssey.TrySetSlotAvailable"/>'s
/// own doc comment), the SAME cache <see cref="Odyssey.SpawnAvailableNpc"/> reads from on the very
/// next pass to build the real body. It is also exactly what NWScript's own
/// <c>AddAvailableNPCByTemplate</c> — declared <c>void</c>, no script ever receives this id
/// either — implies was always meant to happen: recruit off-stage now, spawn a body later. The
/// fix is to never attempt the doomed add in the first place, and let the existing "recruited but
/// bodyless" branch (already correct, already covers dismiss/rejoin) do the spawn+add on a later
/// pass, exactly as it already does for every OTHER "no live body yet" case.
/// </summary>
internal static class PartyRosterRecruitRule
{
    /// <summary>Whether this pass's object id may be handed to
    /// <see cref="Odyssey.AddPartyMemberBySlot"/> in the same pass.</summary>
    public static bool CanAddImmediately(PartyRosterBodyOrigin origin) =>
        origin == PartyRosterBodyOrigin.LiveInWorld;
}
