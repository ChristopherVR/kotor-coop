namespace KotorCoop.Plugin;

/// <summary>
/// Pure decision logic behind <see cref="Odyssey.TryReleaseFollowPhaseOnUnmirror"/> (round 98,
/// docs/research/party-reconciliation.md) — whether a companion's native follow-FSM record
/// (<c>party+0x24+index*0x88</c>, phase at <c>+4</c>) needs a reset at the moment THIS instance
/// STOPS mirroring it (<c>PartyPresenceController.ReleaseSession</c>, the session's falling edge).
///
/// Round 98's own finding: while <see cref="PartyPresenceController"/>.DriveOne mirrors a
/// companion, it calls <see cref="Odyssey.ClaimCreature(nint)"/> every single pass, which —
/// docs/research/realtime-creature-movement.md §6.1's own VERIFIED disassembly of
/// <c>ClearAllActionsFn</c> (0x004CCD80) — empties the action queue and sets <c>CreMoveState</c>
/// (creature+0xA8C) = 1 (idle) on every call. The AI master's own per-tick pre-pass that decides
/// whether to even enter <c>WalkUpdateLocation_QuickWalk</c> (0x0051D9C0) — and from there
/// <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> (0x0051C360), the whole phase-dispatch FSM
/// docs/research/party-reconciliation.md's rounds 71-97 disassembled — requires the action-queue
/// head to read 1 (MoveToPoint) or 0x3D (PartyFollowLeader), OR <c>CreMoveState</c> to read 4, 5
/// or 6 (docs/re/engine-movement.md, VERIFIED). A mirrored companion satisfies NONE of that, every
/// single pass, by construction — so this record's <c>phase</c> field is never read or written by
/// anything for the whole mirrored session. <see cref="PartyFollowController"/> itself already
/// knows this and backs off entirely while mirroring is active (its own
/// <c>block.PartyPresenceCount != 0</c> guard) — this rule targets the OTHER side of that same
/// boundary: what should happen to the record the moment mirroring stops and
/// <see cref="PartyFollowController"/> starts reading it again.
///
/// <see cref="EngineWatchdogResetPhase"/> (-1) is not a value this codebase invented: it is the
/// SAME sentinel <c>WalkUpdateLocation_QuickWalk_FollowLeader</c>'s own 50-attempt watchdog writes
/// (0x0051C964, <c>mov dword ptr [esi+4], 0xffffffff</c>), and the SAME value the already-shipped,
/// default-ON <see cref="Odyssey.TryResetStuckFollowPhase"/> already recognizes and turns into a
/// clean phase-0 (<c>FindPath</c>) restart with <c>attempts</c> zeroed. Round 84
/// (party-reconciliation.md) live-confirmed this exact sentinel reliably kicks off a genuinely
/// working walk cycle when the engine itself writes it ("attempts started counting up cleanly ...
/// phase progressed 5 -&gt; 3 for the first time in eleven rounds") — writing it here, at the
/// mirror-release boundary, reuses that already-proven recovery path instead of inventing a new
/// one. The dispatch table's own inline phase=-1 handler (0x0051C800) re-snapshots
/// <c>Snapshot</c> (record+0x5C) from the creature's OWN live position before advancing to phase
/// 5, so the very next restart is anchored to wherever <see cref="PartyPresenceController"/>
/// actually left the body, not the stale <c>Target</c>/<c>Prior</c> pair a frozen mid-session
/// record would otherwise resume against.
/// </summary>
internal static class FollowPhaseReleaseRule
{
    /// <summary>The engine's own "start the FSM over" sentinel — see this class's own remarks.</summary>
    public const int EngineWatchdogResetPhase = -1;

    /// <summary>
    /// Whether <see cref="Odyssey.TryReleaseFollowPhaseOnUnmirror"/> needs to write anything for
    /// this companion. False when the record already reads <see cref="EngineWatchdogResetPhase"/>
    /// — nothing to reset, matching every other fix in this codebase's own "never write a value
    /// that already holds" discipline (see <c>FinalNodeGeometryRule</c>'s epsilon guard).
    /// </summary>
    public static bool ShouldRelease(int phase) => phase != EngineWatchdogResetPhase;
}
