using System.Text;

namespace KotorCoop.Plugin;

// Party-affecting verbs: MoveNear (diagnostic snap), Leader (local control switch), Recruit
// (dev-only NPC-roster force-add) and RemoveParty (dev-only RemovePartyMember trigger, see
// docs/research/star-forge-finale.md §11).
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// TEMPORARY DIAGNOSTIC (docs/research/shared-state-audit.md §2, round 36). Snaps the
    /// target creature ~1.3m from END_M01AA's `sithsoldier 0xE` (29.74, 144.46), inside the
    /// ~2.6m attack range measured that round — so a genuinely non-census creature (a party
    /// member, never touched by EnemyPresenceController) has a valid, in-range hostile to test
    /// +0x260 firing against. A no-target result from a non-census creature otherwise proves
    /// nothing either way. Remove once settled.
    ///
    /// ⚠ ROOT CAUSE FIXED (live 2026-09-02, tar_m10aa): <c>Odyssey.ResolveServerObject</c> is
    /// the GENERIC untyped getter (see its own doc comment) — it hands back whatever object the
    /// engine's array holds for that id, creature or not, and object ids are per-module: an id
    /// that named a companion in one module names something else entirely (or nothing) after a
    /// transition. This verb used to trust that blind and snap it, and printed the SAME
    /// hardcoded "near 0xE" regardless of module or outcome, so a snap of the wrong object in a
    /// different module read back exactly like success. It now refuses — no snap, no
    /// misleading reply — unless the resolved object is both a live creature (vtable, never the
    /// flag-bearing +0x08 type field — see the object-type-field-trap lesson) AND already
    /// somewhere real (<see cref="Vec3.IsOrigin"/> catches the (0,0,0) tell a wrongly-typed or
    /// never-placed object reads back as).
    /// </summary>
    private void MoveNear()
    {
        uint objectId = block.CheatTarget;
        if (objectId == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "no id given");
            return;
        }

        nint creature = Odyssey.ResolveServerObject(objectId);
        uint candidateId = creature == 0 ? 0 : *(uint*)(creature + 0x04);
        nint candidateVtable = creature == 0 ? 0 : *(nint*)creature;
        if (creature == 0 || !PartyCreatureIdentity.IsLiveMatch(candidateVtable, candidateId, objectId))
        {
            Answer(SharedBlock.CheatStatusNoTarget, $"0x{objectId:X} does not resolve to a live creature here");
            return;
        }

        // Read the position straight back (ObjPosition, +0x090 - see Odyssey.cs) BEFORE
        // touching it: a real creature is never exactly at the world origin, so (0,0,0) here
        // means the id resolved to something never actually placed - refuse rather than snap it
        // and produce a second, harder-to-read (0,0,0).
        Vec3 before = *(Vec3*)(creature + 0x090);
        if (Vec3.IsOrigin(before))
        {
            Answer(SharedBlock.CheatStatusNoTarget, $"0x{objectId:X} has no real position yet (reads 0,0,0) - refusing");
            return;
        }

        Vec3 destination = new() { X = 29.0f, Y = 143.5f, Z = 0.0f };
        Odyssey.SnapTo(creature, destination);

        // Read the position straight back again rather than trust the write blind -
        // creatures.ps1's raw scan is known-unreliable for fresh state.
        Vec3 readBack = *(Vec3*)(creature + 0x090);
        PluginHost.Log(
            $"movenear: snapped 0x{objectId:X} from ({before.X:F1}, {before.Y:F1}, {before.Z:F1}) to "
            + $"({destination.X:F1}, {destination.Y:F1}, {destination.Z:F1}), "
            + $"read back ({readBack.X:F1}, {readBack.Y:F1}, {readBack.Z:F1})");
        Answer(
            SharedBlock.CheatStatusApplied,
            $"0x{objectId:X} snapped to ({readBack.X:F1}, {readBack.Y:F1}, {readBack.Z:F1})");
    }

    /// <summary>
    /// DEV-ONLY TEST PRIMITIVE (docs/research/control-return.md "Round 70"): switches the LOCAL
    /// player's controlled character through <see cref="Odyssey.SwitchControlToPartySlot"/>/
    /// <see cref="Odyssey.SwitchControlToPc"/> — the ENGINE'S OWN <c>ChangeCharacter</c> funnel
    /// (0x005F6B60), the same call a real Tab press and portrait click make, NOT a raw field
    /// poke. Because it goes through the real engine call, it moves the camera too (the same
    /// <c>SetCameraFollow</c> a Tab press triggers) — a caller no longer needs to follow up
    /// with <see cref="Odyssey.SetCameraFollowServerObject"/>/
    /// <see cref="Odyssey.RestoreCameraToControlledCreature"/> separately, though doing so is
    /// harmless (idempotent while already on target). Built because that live pass needed a way
    /// to put a client onto a party-roster companion BEFORE connecting — the one manual step
    /// <c>control-return-live.ps1</c> could never automate (injected keystrokes never reach
    /// this game). <see cref="SharedBlock.OffsetCheatAmount"/> is the party-roster slot (0-8),
    /// or -1 for "your own PC". Local only: this never crosses the network. Requires the target
    /// to already be a live CSWParty member (in the active party, not merely recruited) — the
    /// engine's own switch has no other way to name one.
    /// </summary>
    private void Leader()
    {
        int slot = block.CheatAmount;

        if (slot < 0)
        {
            if (!Odyssey.SwitchControlToPc())
            {
                Answer(SharedBlock.CheatStatusNoWorld, "leader: no client world to switch in yet");
                return;
            }

            PluginHost.Log("leader: switched control to your own PC");
            Answer(SharedBlock.CheatStatusApplied, "control switched to your own PC");
            return;
        }

        if (!Odyssey.SwitchControlToPartySlot(slot, out uint rosterId))
        {
            Answer(
                SharedBlock.CheatStatusNoTarget,
                $"leader: no party-roster companion in slot {slot} (empty, or no client world yet)");
            return;
        }

        PluginHost.Log($"leader: switched control to party slot {slot} (0x{rosterId:X})");
        Answer(SharedBlock.CheatStatusApplied, $"control switched to party slot {slot} (0x{rosterId:X})");
    }

    /// <summary>
    /// DEV-ONLY TEST PRIMITIVE (see <see cref="SharedBlock.CheatKindRecruit"/>): forces NPC
    /// roster slot <see cref="SharedBlockView.CheatAmount"/> into the local active party,
    /// running the same poke-available / spawn / add sequence
    /// <c>PartyRosterController.Reconcile</c> already runs for a peer's roster (party-
    /// reconciliation.md's "roster sync" section), on demand for a slot named directly rather
    /// than one read off a peer's mask.
    /// </summary>
    private void Recruit(nint player)
    {
        int slot = block.CheatAmount;
        if (slot < 0 || slot >= SharedBlock.PartyRosterSlots)
        {
            Answer(SharedBlock.CheatStatusRefused, $"recruit: slot must be 0-{SharedBlock.PartyRosterSlots - 1} (got {slot})");
            return;
        }

        if (player == 0)
        {
            Answer(SharedBlock.CheatStatusNoWorld, "recruit: no player creature to spawn near");
            return;
        }

        uint objectId = Odyssey.GetPartyRosterObjectId(slot);
        bool hadLiveBody = objectId is not (0 or Odyssey.ObjectInvalid) && Odyssey.ResolveServerObject(objectId) != 0;

        if (!hadLiveBody)
        {
            if (!Odyssey.TrySetSlotAvailable(slot))
            {
                Answer(SharedBlock.CheatStatusRefused, $"recruit: could not mark NPC slot {slot} available (no party table?)");
                return;
            }

            objectId = Odyssey.SpawnAvailableNpc(slot, Odyssey.GetPosition(player), out int spawnFailure);
            if (objectId is 0 or Odyssey.ObjectInvalid)
            {
                // A bare availability poke does not carry the blueprint association
                // AddAvailableNPCByTemplate also records — Odyssey.TrySetSlotAvailable's own
                // NEEDS-LIVE note, now confirmed live: SpawnAvailableNPC has nothing to spawn
                // FROM. Fall back to a tag lookup against a body already placed in the module
                // (the module's own static creature list, before any recruitment ever ran),
                // if the caller named one.
                byte* text = block.CheatText;
                int length = 0;
                while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
                {
                    length++;
                }

                if (length == 0)
                {
                    Answer(
                        SharedBlock.CheatStatusRefused,
                        $"recruit: spawn failed for NPC slot {slot} (failure {spawnFailure}; "
                        + "no blueprint on a raw availability poke) - retry as 'recruit "
                        + $"{slot} <tag>' to fall back to an already-placed body");
                    return;
                }

                string tag = Encoding.ASCII.GetString(text, length);
                nint found = CreatureFinder.FindByTag(new ReadOnlySpan<byte>(text, length));
                if (found == 0)
                {
                    Answer(
                        SharedBlock.CheatStatusRefused,
                        $"recruit: spawn failed for NPC slot {slot} (failure {spawnFailure}), "
                        + $"and no creature tagged '{tag}' is here either");
                    return;
                }

                objectId = *(uint*)(found + 0x04); // CSWSObject + 4 = the object's id
                PluginHost.Log($"recruit: NPC slot {slot} has no blueprint to spawn from "
                    + $"(failure {spawnFailure}); using already-placed '{tag}' (0x{objectId:X}) instead");
            }
            else
            {
                PluginHost.Log($"recruit: spawned NPC slot {slot} as 0x{objectId:X} (dev-only test primitive)");
            }
        }

        if (Odyssey.AddPartyMemberBySlot(slot, objectId, out int addFailure))
        {
            PluginHost.Log($"recruit: added NPC slot {slot} (object 0x{objectId:X}) to the party (dev-only test primitive)");
            Answer(SharedBlock.CheatStatusApplied, $"NPC slot {slot} recruited and added to the party (0x{objectId:X})");
        }
        else
        {
            Answer(SharedBlock.CheatStatusRefused, $"recruit: AddPartyMember refused NPC slot {slot} (failure {addFailure})");
        }
    }

    /// <summary>
    /// DEV-ONLY LIVE-TEST PRIMITIVE (2026-08-28, docs/research/star-forge-finale.md §11): calls
    /// the engine's own <c>RemovePartyMember(int nNPC)</c> — the SAME VM routine (id 575) every
    /// script in the game reaches through <c>CSWVirtualMachineCommands::
    /// ExecuteCommandRemovePartyMember 0x00541C00</c> — through
    /// <see cref="Odyssey.RemovePartyMemberBySlot"/>'s existing parameter-stack dispatch (push
    /// the argument onto the VM's own stack, dispatch through the routine table by verified
    /// handler address, pop the return — the same pattern <c>startdialog</c> established for
    /// <c>ActionStartConversation</c>). Built so <c>RemovePartyMemberHook</c>'s own detour can be
    /// exercised WITHOUT reaching <c>k_sta_bastila.dlg</c>'s entry 70 (§9.3/§9.4's own one-shot-
    /// precondition maze). See <see cref="SharedBlock.CheatKindRemoveParty"/>'s own doc comment.
    ///
    /// A REAL engine call, not a simulation of one: this genuinely removes the named companion
    /// from the active party (they stay recruited — see <see cref="Odyssey.RemovePartyMemberBySlot"/>'s
    /// own doc comment), the same as if a script had done it. NOT a staging primitive for normal
    /// roster changes — use <c>recruit</c>/<c>leader</c> for that.
    /// </summary>
    private void RemoveParty()
    {
        int slot = block.CheatAmount;

        if (slot is < 0 or >= SharedBlock.PartyRosterSlots)
        {
            Answer(
                SharedBlock.CheatStatusRefused,
                $"removeparty: slot must be 0-{SharedBlock.PartyRosterSlots - 1} (got {slot})");
            return;
        }

        if (!Odyssey.RemovePartyMemberBySlot(slot, out int failure))
        {
            Answer(SharedBlock.CheatStatusEngineRefused, $"removeparty {slot} refused (failure {failure})");
            return;
        }

        PluginHost.Log($"removeparty: RemovePartyMember({slot}) dispatched through the VM routine table");
        Answer(SharedBlock.CheatStatusApplied, $"party slot {slot} removed");
    }

    /// <summary>
    /// DEV-ONLY LIVE-TEST PRIMITIVE (see <see cref="SharedBlock.CheatKindFollowKick"/>,
    /// docs/research/party-reconciliation.md Round 96): dumps NPC roster slot
    /// <see cref="SharedBlockView.CheatAmount"/>'s follow-record fields (phase, attempts,
    /// target point, current path node) to the plugin log, then forces
    /// <c>record+4 = 10</c> (<c>TeleportForward</c>) UNCONDITIONALLY through
    /// <see cref="Odyssey.TryForceTeleportForward"/> — regardless of the companion's current
    /// phase, unlike the automatic <c>party_follow_phase2_kick</c> pass (phase 2 only). A raw
    /// diagnostic poke for answering Round 96's own open question live, not a staging primitive
    /// for normal roster changes.
    /// </summary>
    private void FollowKick()
    {
        int slot = block.CheatAmount;
        if (slot is < 0 or >= SharedBlock.PartyRosterSlots)
        {
            Answer(
                SharedBlock.CheatStatusRefused,
                $"followkick: slot must be 0-{SharedBlock.PartyRosterSlots - 1} (got {slot})");
            return;
        }

        uint objectId = Odyssey.GetPartyRosterObjectId(slot);
        if (objectId is 0 or Odyssey.ObjectInvalid)
        {
            Answer(SharedBlock.CheatStatusNoTarget, $"followkick: no NPC in roster slot {slot}");
            return;
        }

        nint creature = Odyssey.ResolveServerObject(objectId);
        if (creature == 0)
        {
            Answer(
                SharedBlock.CheatStatusNoTarget,
                $"followkick: roster slot {slot} (0x{objectId:X}) does not resolve here");
            return;
        }

        if (Odyssey.TryGetFollowDiagnostics(creature, objectId, out Odyssey.FollowDiag diag) && diag.Valid)
        {
            PluginHost.Log(
                $"followkick: 0x{objectId:X} BEFORE — phase={diag.PhaseIndex} attempts={diag.PhaseAttempts} "
                + $"target=({diag.TargetPoint.X:F2},{diag.TargetPoint.Y:F2},{diag.TargetPoint.Z:F2}) "
                + $"pathNode={diag.CurrentPathNode} destNode={diag.DestNode} "
                + $"writeCursor={diag.WriteCursor} creMoveState={diag.CreMoveState}");
        }
        else
        {
            PluginHost.Log($"followkick: 0x{objectId:X} — no follow record to dump before the poke (index<=0?)");
        }

        if (!Odyssey.TryForceTeleportForward(objectId, out int phaseBefore, out int phaseAfter))
        {
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"followkick: roster slot {slot} (0x{objectId:X}) has no follow-party index to poke");
            return;
        }

        PluginHost.Log($"followkick: 0x{objectId:X} AFTER — phase {phaseBefore} -> {phaseAfter} (forced)");
        Answer(
            SharedBlock.CheatStatusApplied,
            $"0x{objectId:X} phase {phaseBefore} -> {phaseAfter} (forced TeleportForward)");
    }

    /// <summary>
    /// DEV-ONLY LIVE-TEST PRIMITIVE (see <see cref="SharedBlock.CheatKindFollowStall"/>,
    /// docs/research/party-reconciliation.md Round 105): dumps NPC roster slot
    /// <see cref="SharedBlockView.CheatAmount"/>'s follow-record WEDGE SIGNATURE — the exact
    /// fields <see cref="FollowStallRule.IsWedgedShape"/> decides on, including the two squared
    /// distances <c>PathWaitDest</c>'s own strict accept test compares — and then applies one of
    /// <see cref="Odyssey.TryRecoverFollowStall"/>'s four candidate recoveries UNCONDITIONALLY:
    /// no wedge check, no 2-second stall timer, no anti-spin cooldown, unlike the automatic
    /// <c>party_follow_stall_recovery</c> pass this same code backs.
    ///
    /// The method comes from <see cref="SharedBlockView.CheatTarget"/>, falling back to
    /// <c>party_follow_stall_recovery_method</c> when it is 0. That parameter is the whole point
    /// of the verb: the automatic pass reads its method once at plugin construction, so isolating
    /// four candidates through it alone would cost four full rig restarts; through this verb they
    /// are four console round-trips in one session.
    /// </summary>
    private void FollowStall()
    {
        int slot = block.CheatAmount;
        if (slot is < 0 or >= SharedBlock.PartyRosterSlots)
        {
            Answer(
                SharedBlock.CheatStatusRefused,
                $"followstall: slot must be 0-{SharedBlock.PartyRosterSlots - 1} (got {slot})");
            return;
        }

        int method = (int)block.CheatTarget;
        if (method == 0)
        {
            method = PluginConfig.ReadInt(
                "party_follow_stall_recovery_method", FollowStallRule.DefaultMethod);
        }

        if (!FollowStallRule.IsKnownMethod(method))
        {
            Answer(
                SharedBlock.CheatStatusRefused,
                "followstall: method must be 1 (phasereset), 2 (followleader), 3 (positionparty), "
                + $"4 (priortie), 5 (destnode) or 6 (priorpush) — got {method}");
            return;
        }

        uint objectId = Odyssey.GetPartyRosterObjectId(slot);
        if (objectId is 0 or Odyssey.ObjectInvalid)
        {
            Answer(SharedBlock.CheatStatusNoTarget, $"followstall: no NPC in roster slot {slot}");
            return;
        }

        // allowCountRestore: true — this is a one-shot verb, not a per-frame pass, so the engine
        // field write party_together_fix's clamp makes necessary (round 101) cannot possibly
        // become the repeated write docs/research/party-reconciliation.md's own per-frame rule
        // forbids.
        if (Odyssey.TryGetFollowStallState(objectId, allowCountRestore: true, out Odyssey.FollowStallState before)
            && before.Valid)
        {
            PluginHost.Log(
                $"followstall: 0x{objectId:X} BEFORE — phase={before.Phase} attempts={before.Attempts} "
                + $"pathNode={before.PathNode} destNode={before.DestNode} writeCursor={before.WriteCursor} "
                + $"distSqToTarget={before.DistSqToTarget:F4} distSqToPrior={before.DistSqToPrior:F4} "
                + $"cannotAccept={FollowStallRule.CannotAccept(before.DistSqToTarget, before.DistSqToPrior)} "
                + $"wedged={FollowStallRule.IsWedgedShape(before.Phase, before.Attempts, before.PathNode, before.DestNode, before.DistSqToTarget, before.DistSqToPrior)} "
                + $"countRestored={before.CountWasRestored}");
        }
        else
        {
            PluginHost.Log(
                $"followstall: 0x{objectId:X} — no follow record to dump before the poke "
                + "(GetIndex refused even after restoring CSWParty's own member count)");
        }

        nint leader = Odyssey.GetPlayerCreaturePointer();
        Vec3 leaderPos = leader == 0 ? default : Odyssey.GetPosition(leader);

        if (!Odyssey.TryRecoverFollowStall(
                objectId, method, leaderPos, allowCountRestore: true, out bool wrote, out string detail))
        {
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"followstall: roster slot {slot} (0x{objectId:X}) — {detail}");
            return;
        }

        PluginHost.Log($"followstall: 0x{objectId:X} AFTER — {detail} (wrote={wrote})");
        Answer(SharedBlock.CheatStatusApplied, $"0x{objectId:X} {detail}");
    }
}
