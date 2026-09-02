using System.Text;

namespace KotorCoop.Plugin;

// Party-affecting verbs: MoveNear (diagnostic snap), Leader (local control switch), Recruit
// (dev-only NPC-roster force-add) and RemoveParty (dev-only RemovePartyMember trigger, see
// docs/research/star-forge-finale.md §11).
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// LIVE-TEST/DIAGNOSTIC PRIMITIVE (docs/research/shared-state-audit.md §2, round 36):
    /// snaps the target creature next to the LOCAL PLAYER by default, or to an explicit point
    /// when <c>movenear &lt;id&gt; &lt;x&gt; &lt;y&gt; [z]</c> named one — so a genuinely
    /// non-census creature (a party member, never touched by EnemyPresenceController) can be
    /// put somewhere useful to test against (e.g. in range of a hostile) without depending on
    /// what that module happens to contain. <see cref="MoveNearDestinationRule.Resolve"/> is
    /// the pure "which destination, and was it named explicitly" arithmetic; everything here is
    /// the engine read/write it cannot do.
    ///
    /// 2026-09-02: originally snapped to a hardcoded END_M01AA point (29, 143.5) regardless of
    /// module — measured live in tar_m10aa, <c>movenear 0x46</c> teleported a live Vulkar to
    /// (29.0, 143.5, 0.0), off the map. Module-agnostic now: the default is the player's own
    /// position (published to every peer every tick, so it always names somewhere real in
    /// whatever module is actually loaded).
    ///
    /// ⚠ ROOT CAUSE (separately) FIXED (live 2026-09-02, tar_m10aa): <c>Odyssey.ResolveServerObject</c>
    /// is the GENERIC untyped getter (see its own doc comment) — it hands back whatever object
    /// the engine's array holds for that id, creature or not, and object ids are per-module: an
    /// id that named a companion in one module names something else entirely (or nothing) after
    /// a transition. This verb used to trust that blind and snap it, so a snap of the wrong
    /// object in a different module read back exactly like success. It now refuses — no snap,
    /// no misleading reply — unless the resolved object is both a live creature (vtable, never
    /// the flag-bearing +0x08 type field — see the object-type-field-trap lesson) AND already
    /// somewhere real (<see cref="Vec3.IsOrigin"/> catches the (0,0,0) tell a wrongly-typed or
    /// never-placed object reads back as).
    /// </summary>
    private void MoveNear(nint player)
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

        Vec3 destination = MoveNearDestinationRule.Resolve(Odyssey.GetPosition(player), block.CheatPosition, out bool isExplicit);
        Odyssey.SnapTo(creature, destination);

        // Read the position straight back again rather than trust the write blind -
        // creatures.ps1's raw scan is known-unreliable for fresh state.
        Vec3 readBack = *(Vec3*)(creature + 0x090);
        string how = isExplicit ? "explicit" : "near the player";
        PluginHost.Log(
            $"movenear: snapped 0x{objectId:X} from ({before.X:F1}, {before.Y:F1}, {before.Z:F1}) to "
            + $"({destination.X:F1}, {destination.Y:F1}, {destination.Z:F1}) ({how}), "
            + $"read back ({readBack.X:F1}, {readBack.Y:F1}, {readBack.Z:F1})");
        Answer(
            SharedBlock.CheatStatusApplied,
            $"0x{objectId:X} snapped to ({readBack.X:F1}, {readBack.Y:F1}, {readBack.Z:F1}) ({how})");
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
    /// roster slot <see cref="SharedBlockView.CheatAmount"/> into the local active party the way
    /// the game's own recruit scripts do — <c>AddAvailableNPCByTemplate</c> first (the engine's
    /// <c>AddNPC</c>: flag, faction, inventory AND the <c>AVAILNPC&lt;slot&gt;</c> cache), then
    /// <c>AddPartyMember</c> on a body: the creature tagged <see cref="SharedBlockView.CheatText"/>
    /// if one stands in the module (<c>recruit 2 carth</c> on the Endar Spire bridge), otherwise
    /// one spawned from the cache just written.
    ///
    /// ⚠ ORDER MATTERS (docs/research/transition-crashes.md 2026-09-03, "the two-companion host
    /// warp crash"). Until then this verb raw-poked the availability flag, TRIED to spawn (which
    /// failed — there was no cache to spawn from) and fell back to the tagged body. The member
    /// that produced looked fine in the party (it was Carth's own placed body) but the engine
    /// could not rebuild it after a module move: <c>CreateParty</c> came back OBJECT_INVALID for
    /// the slot and <c>PositionParty</c> crashed on <c>SetPosition(NULL)</c>, 3/3 warps. Recruit
    /// through the engine BEFORE anything looks the slot up, and the same warp is clean.
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
            byte* text = block.CheatText;
            int length = 0;
            while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
            {
                length++;
            }

            string tag = length == 0 ? string.Empty : Encoding.ASCII.GetString(text, length);
            nint placed = length == 0 ? 0 : CreatureFinder.FindByTag(new ReadOnlySpan<byte>(text, length));
            if (length != 0 && placed == 0)
            {
                Answer(SharedBlock.CheatStatusRefused, $"recruit: no creature tagged '{tag}' is here to recruit as NPC slot {slot}");
                return;
            }

            if (Odyssey.IsSlotAvailable(slot))
            {
                PluginHost.Log($"recruit: NPC slot {slot} was already recruited here (dev-only test primitive)");
            }
            else if (Odyssey.AddAvailableNpcByTemplate(slot))
            {
                PluginHost.Log($"recruit: recruited NPC slot {slot} through the engine's own "
                    + "AddAvailableNPCByTemplate (AVAILNPC cache written) (dev-only test primitive)");
            }
            else
            {
                Answer(SharedBlock.CheatStatusRefused, $"recruit: the engine refused to recruit NPC slot {slot} (no template for that slot, or no party table)");
                return;
            }

            if (placed != 0)
            {
                objectId = *(uint*)(placed + 0x04); // CSWSObject + 4 = the object's id
                PluginHost.Log($"recruit: using the already-placed '{tag}' (0x{objectId:X}) as NPC slot {slot}'s body");
            }
            else
            {
                objectId = Odyssey.SpawnAvailableNpc(slot, Odyssey.GetPosition(player), out int spawnFailure);
                if (objectId is 0 or Odyssey.ObjectInvalid)
                {
                    Answer(
                        SharedBlock.CheatStatusRefused,
                        $"recruit: spawn failed for NPC slot {slot} (failure {spawnFailure}) - a slot that was "
                        + "recruited without its AVAILNPC cache (an older build's raw flag poke?) has nothing to "
                        + $"spawn from; retry as 'recruit {slot} <tag>' to use an already-placed body");
                    return;
                }

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
