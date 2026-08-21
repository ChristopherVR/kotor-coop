using System.Diagnostics;

namespace KotorCoop.Plugin;

/// <summary>
/// Drives this instance's party companions to wherever the host's engine has them.
///
/// The host owns the companions — its own AI runs them and the agent streams their state.
/// This instance holds solo mode (the agent's job) so the local party AI issues no follow
/// orders, and this controller places each companion body every frame with the seamless
/// recipe, replacing the agent's old external position writes. Those writes never touched
/// the client twin, so the rendered companion trailed the engine's 512 ms ease and stepped
/// once per network tick — the same defect the phantom had before
/// docs/research/realtime-creature-movement.md, solved the same way.
///
/// Companions are keyed by NPC roster slot (identical across two instances of the same
/// save); each is resolved against THIS instance's party table, because runtime object
/// ids are not a reliable cross-instance key for party members.
///
/// Kill switch: <c>party_drive=0</c> in coop.ini (the agent then keeps only solo mode and
/// nothing moves the local copies — visibly worse, but safe).
/// </summary>
internal sealed unsafe class PartyPresenceController(SharedBlockView block)
{
    // The APP_MANAGER -> server -> internal walk, same chain the agent's EngineGlobals
    // uses (no ASLR; the plugin runs inside the image these are absolute in).
    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppServerOffset = 0x08;
    private const int InternalOffset = 0x04;

    /// <summary>CServerExoAppInternal + this = the embedded CSWPartyTable.</summary>
    private const int PartyTableOffset = 0x1B770;

    /// <summary>CSWPartyTable + this = int[9] spawned object id per roster slot.</summary>
    private const int PartyObjectIds = 0x0C;

    private const int PartyNpcSlots = 9;

    private readonly bool _enabled = PluginConfig.ReadBool("party_drive", true);

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly uint[] _trackedId = new uint[SharedBlock.PartyPresenceCapacity];
    private readonly nint[] _creature = new nint[SharedBlock.PartyPresenceCapacity];
    private readonly uint[] _appliedOneShot = new uint[SharedBlock.PartyPresenceCapacity];
    private readonly bool[] _oneShotPrimed = new bool[SharedBlock.PartyPresenceCapacity];

    /// <summary>Per slot: is the body currently held Neutral. Tri-state via
    /// <see cref="_factionKnown"/> so the first pass always writes.</summary>
    private readonly bool[] _heldNeutral = new bool[SharedBlock.PartyPresenceCapacity];
    private readonly bool[] _factionKnown = new bool[SharedBlock.PartyPresenceCapacity];

    private int _repairsLogged;

    /// <summary>Edge state for <see cref="PartyFreezeRule"/> — was the roster frozen last pass.</summary>
    private bool _rosterFrozen;

    // mirror_smooth_ms, not the phantom's smooth_ms: companions arrive on the agent's
    // combat pump (~150 ms apart), not the 30 Hz tick — see EnemyPresenceController.
    private readonly MotionSmoother _smoother = new(
        SharedBlock.PartyPresenceCapacity,
        Math.Clamp(PluginConfig.ReadFloat("mirror_smooth_ms", 150f), 0f, 600f) / 1000f);

    private int _drivenLogged = -1;

    public void Invalidate()
    {
        for (int i = 0; i < SharedBlock.PartyPresenceCapacity; i++)
        {
            Forget(i);
        }

        block.PartyPresenceCount = 0;
        _drivenLogged = -1;
        _rosterFrozen = false;
    }

    /// <summary>
    /// The session ended: give every companion this instance was mirroring back to the local
    /// game before forgetting it.
    ///
    /// ⚠ THE CLASS COMMENT ON <see cref="ApplyCompanionFaction"/> USED TO CLAIM THIS HAPPENED
    /// BY ITSELF — "switching local_combat off, or this instance ceasing to mirror, puts them
    /// back in the player faction on the next pass". The second half was never true: ceasing
    /// to mirror means <see cref="OnFrame"/> returns at its command gate, so the restoring
    /// pass never runs. A client that disconnected mid-fight left Trask in the NEUTRAL faction
    /// and pinned at min-1-hp — no longer a target, no longer killable, no longer fighting
    /// for the player — and both of those are SAVED state, so it baked into the next save.
    ///
    /// The bodies are re-resolved by id rather than trusted from the cache: teardown can land
    /// on the same frame as a module change, and a cached creature pointer is the one thing in
    /// this file that is never safe to use unverified.
    /// </summary>
    public void ReleaseSession()
    {
        int restored = ReleaseAllHeld();

        if (restored > 0)
        {
            PluginHost.Log($"party drive: released {restored} companion(s) back to your own game");
        }

        Invalidate();
    }

    /// <summary>
    /// Puts every companion this instance currently holds Neutral and min-1-hp pinned back in
    /// the player faction, off the pin. The cache is left alone — unlike <see cref="Invalidate"/>,
    /// a caller may still want <see cref="_trackedId"/>/<see cref="_creature"/> afterwards (the
    /// roster-freeze guard in <see cref="OnFrame"/> re-resolves the same bodies the moment the
    /// freeze lifts, through the ordinary <see cref="DriveOne"/> path).
    ///
    /// Bodies are re-resolved by id rather than trusted from the cache: this can be called on
    /// the same frame as a module change, and a cached creature pointer is the one thing in this
    /// file that is never safe to use unverified.
    /// </summary>
    private int ReleaseAllHeld()
    {
        int restored = 0;

        for (int i = 0; i < SharedBlock.PartyPresenceCapacity; i++)
        {
            if (!_factionKnown[i] || !_heldNeutral[i] || _trackedId[i] == 0)
            {
                continue;
            }

            uint objectId = _trackedId[i];
            nint creature = Odyssey.ResolveServerObject(objectId);
            if (creature == 0 || *(uint*)(creature + 0x04) != objectId)
            {
                // The world went before the release did; there is no body left to restore.
                PluginHost.Log($"party drive: companion 0x{objectId:X} is gone — "
                    + "cannot put it back in the player faction");
                continue;
            }

            ApplyCompanionFaction(i, creature, objectId, neutral: false);
            restored++;
        }

        return restored;
    }

    public void OnFrame()
    {
        if (!_enabled || block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        // A local conversation owns the stage — companions are positioned by the dialogue
        // system itself while one runs.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        // ⚠ ROSTER FROZEN: A SCRIPT OWNS ONE COMPANION, NOT THIS CONTROLLER. Party table
        // +0xF0 != -1 is the engine's own "Add/Remove/CreateParty all refuse" flag — set for a
        // Leviathan brig beat, the Rakatan temple, any forced-solo sequence — and
        // PartyRosterController already backs off the roster itself for the same reason. This
        // controller drives EVERY companion body every pass, not just the one the script has
        // taken, so it has to back off too: claiming the scripted body re-asserts movement
        // flags underneath the script, and pinning its health mid-sequence is the exact class of
        // bug the min-1-hp pin exists to prevent, not cause. `PartyFreezeRule` is the edge
        // detector so the log carries exactly two lines per freeze, not one per frame.
        //
        // The release on the freezing edge reuses `ReleaseAllHeld` — the same "hand the
        // companion back" path a disconnect runs through `ReleaseSession` — because a companion
        // can walk into a freeze already held Neutral and min-1-hp pinned from a fight seconds
        // earlier, and the pin is what re-creates the aggro lock if it is left in place (see
        // `ApplyCompanionFaction`). The cache itself is left alone: the freeze lifting re-drives
        // the same bodies through the ordinary `DriveOne` path below on the very next pass, so
        // nothing needs to be forgotten and re-resolved.
        //
        // Solo mode (party table +0x190) is deliberately NOT touched here. It is entirely owned
        // by the C# agent (KotorGameBridge.TryWritePartyMembers/ReleasePartyFreeze, outside this
        // plugin) and re-asserted every agent pump regardless of what this pass does; this
        // controller has never written it and adding a write here — untested, unverifiable
        // without a live rig — is exactly the kind of engine write the project's own rule
        // forbids shipping unverified. More importantly there is no evidence it needs releasing:
        // +0xF0 (one companion under direct script control) and +0x190 (the OTHER companions'
        // follow-AI switched off) are different fields serving different purposes, and the
        // script driving the controlled companion does not run through the follow-AI path
        // +0x190 gates — turning solo mode off mid-sequence would only risk the engine's own
        // follow-AI firing on the party members the script is NOT driving, which is the bug this
        // whole channel exists to prevent. See docs/research/party-reconciliation.md, dated
        // entry 2026-08-21, for the full reasoning.
        PartyFreezeAction freezeAction = PartyFreezeRule.Decide(Odyssey.IsPartyRosterFrozen(), _rosterFrozen);
        switch (freezeAction)
        {
            case PartyFreezeAction.ReleaseAndLog:
                _rosterFrozen = true;
                int released = ReleaseAllHeld();
                PluginHost.Log(released > 0
                    ? $"party drive: roster frozen by the engine — released {released} "
                        + "companion(s) held neutral and stopped driving"
                    : "party drive: roster frozen by the engine — stopped driving companions");
                return;

            case PartyFreezeAction.StayOff:
                return;

            case PartyFreezeAction.ResumeAndLog:
                _rosterFrozen = false;
                PluginHost.Log("party drive: roster freeze lifted — resuming companion drive");
                break;

            case PartyFreezeAction.Drive:
                break;
        }

        uint count = Math.Min(block.PartyPresenceCount, (uint)SharedBlock.PartyPresenceCapacity);
        if (count == 0)
        {
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;
        // Fresh engine resolve — the block's copy is agent-written and stale through a
        // module transition (see the EncounterController travel-follow crash).
        nint player = Odyssey.GetPlayerCreaturePointer();
        int driven = 0;

        for (int i = 0; i < count; i++)
        {
            if (DriveOne(i, block.GetPartyPresence(i), now, player))
            {
                driven++;
            }
        }

        if (driven != _drivenLogged)
        {
            _drivenLogged = driven;
            PluginHost.Log($"party drive: mirroring {driven} of the host's companions");
        }
    }

    private bool DriveOne(int slot, PartyPresenceView member, double now, nint player)
    {
        if (member.RosterSlotPlusOne is 0 or > PartyNpcSlots)
        {
            Forget(slot);
            return false;
        }

        if (!float.IsFinite(member.Position.X) || !float.IsFinite(member.Position.Y)
            || !float.IsFinite(member.Position.Z)
            || (member.Position.X == 0f && member.Position.Y == 0f))
        {
            return false;
        }

        uint objectId = ResolveRosterObjectId((int)member.RosterSlotPlusOne - 1);
        if (objectId is 0 or Odyssey.ObjectInvalid)
        {
            return false;
        }

        nint creature = Resolve(slot, objectId);
        if (creature == 0 || creature == player)
        {
            return false;
        }

        // Same reasoning as the enemy drive: the local AI (and any leftover follow
        // action) must never contest the writes, so re-claim every pass.
        Odyssey.ClaimCreature(creature);

        // ⚠ A MIRRORED COMPANION MUST NOT BE A BATTLEFIELD ON A MIRRORING INSTANCE.
        //
        // They are faction PLAYER, driven from here, cannot fight back (solo mode freezes the
        // party AI and the queue is cleared every pass) and are not min-1-hp pinned. Turn
        // local_combat on and they become the nearest player-faction bodies to every mirrored
        // enemy: an immortal aggro sink that soaks the whole fight while the local PC is never
        // touched — and their damage goes nowhere, because there is no client->host channel
        // for companion health, so the host never sees any of it.
        //
        // Held Neutral they stop being targets; pinned min-1-hp they stop being killable here.
        // Applied in BOTH directions, so switching local_combat off puts them back in the
        // player faction on the next pass. CEASING TO MIRROR DOES NOT COME BACK THROUGH HERE —
        // this whole method is behind OnFrame's command gate and roster-freeze guard — which is
        // what ReleaseSession and the freeze guard's own ReleaseAllHeld call are for.
        ApplyCompanionFaction(
            slot, creature, objectId,
            neutral: EnemyPresenceController.LocalCombat && block.DamageDealtEnabled);

        // ⚠ THE HOST OWNS COMPANION HEALTH — INCLUDING WHEN IT IS ZERO.
        //
        // The old `Health > 0` guard is why a client could end up with a companion lying down
        // under a FULL health bar, permanently: a burst between two ~150 ms passes drops the
        // body into the engine's temp-death, and the next pass wrote the host's positive
        // figure straight onto a GetDeadTemp body. The bar reads 34/34, the companion never
        // stands up, and if it happens to all of them this instance reaches a party wipe the
        // host never saw — which the agent then arbitrates across the whole session.
        //
        // So write what the host says, whatever it says, then repair the state that implies.
        if (Odyssey.GetCurrentHitPoints(creature) != member.Health)
        {
            Odyssey.SetCurrentHitPoints(creature, member.Health);
        }

        // The repair, through the engine's own Resurrection effect rather than a hit-point
        // poke: that is the only path that clears the death animation and restores the AI
        // flags. Hit points are already correct from the write above, and deliberately not set
        // in the same call — a heal landing while the body still reads dead is dropped by the
        // engine's own rule.
        if (member.Health > 0 && Odyssey.IsDowned(creature))
        {
            Odyssey.SetRaiseable(creature);

            bool revived = Odyssey.ReviveCreature(objectId, out int failure);
            if (_repairsLogged < 10)
            {
                _repairsLogged++;
                PluginHost.Log(revived
                    ? $"party drive: stood companion 0x{objectId:X} back up — it was down here "
                        + $"while the host has it on {member.Health} hp"
                    : $"party drive: could not revive companion 0x{objectId:X} (failure {failure})");
            }
        }

        if (!_oneShotPrimed[slot])
        {
            _oneShotPrimed[slot] = true;
            _appliedOneShot[slot] = member.OneShotSerial;
        }
        else if (member.OneShotSerial != _appliedOneShot[slot])
        {
            _appliedOneShot[slot] = member.OneShotSerial;
            if (member.OneShotAnimation != 0)
            {
                Odyssey.PlayOneShotAnimation(creature, (ushort)member.OneShotAnimation);
            }
        }

        (Vec3 shown, float speed, bool moving) = _smoother.Step(slot, member.Position, now);
        Odyssey.DriveEnemy(creature, shown, member.Facing, speed, moving, member.Animation);
        return true;
    }

    /// <summary>This instance's object id for a roster slot, from its OWN party table.</summary>
    private static uint ResolveRosterObjectId(int rosterSlot)
    {
        nint appManager = *(nint*)AppManagerPtr;
        if (appManager == 0)
        {
            return 0;
        }

        nint server = *(nint*)(appManager + AppServerOffset);
        if (server == 0)
        {
            return 0;
        }

        nint serverInternal = *(nint*)(server + InternalOffset);
        if (serverInternal == 0)
        {
            return 0;
        }

        return *(uint*)(serverInternal + PartyTableOffset + PartyObjectIds + (rosterSlot * 4));
    }

    /// <summary>Same cache-and-revalidate as the enemy drive: id and object type must
    /// still match every frame, or the pointer is stale reused memory.</summary>
    private nint Resolve(int slot, uint objectId)
    {
        nint cached = _creature[slot];
        if (cached != 0 && _trackedId[slot] == objectId
            && *(uint*)(cached + 0x04) == objectId
            && *(int*)(cached + 0x08) == 5)
        {
            return cached;
        }

        Forget(slot);

        nint creature = Odyssey.ResolveServerObject(objectId);
        if (creature == 0 || *(int*)(creature + 0x08) != 5)
        {
            return 0;
        }

        _creature[slot] = creature;
        _trackedId[slot] = objectId;
        return creature;
    }

    private void Forget(int slot)
    {
        if (_creature[slot] != 0)
        {
            Odyssey.ForgetGait(_creature[slot]);
        }

        _creature[slot] = 0;
        _trackedId[slot] = 0;
        _appliedOneShot[slot] = 0;
        _oneShotPrimed[slot] = false;
        _factionKnown[slot] = false;
        _smoother.Reset(slot);
    }

    /// <summary>
    /// Puts a mirrored companion in or out of the PLAYER faction, on the EDGE only.
    ///
    /// The faction call walks member lists, so a party standing still in a fight must not pay
    /// that every frame. On the way to Neutral it also pins min-1-hp and releases anything
    /// already locked onto the body — the pin ALONE re-creates the aggro lock, because a body
    /// that cannot die is never released by the engine's own retention test. That is exactly
    /// the mistake the original aggro-sink phantom design made.
    ///
    /// ⚠ SAVE HAZARD, PARTLY RESOLVED. Faction membership and min-1-hp are both persisted. A
    /// client that saves while local_combat is on could bake a Neutral, unkillable companion
    /// into that save. A live toggle restores here, a disconnect restores through
    /// <see cref="ReleaseSession"/>, and a roster freeze (party table +0xF0) restores through
    /// the same <see cref="ReleaseAllHeld"/> those two share, from <see cref="OnFrame"/>'s
    /// freeze guard; a CRASH mid-session still bakes it, because nothing runs.
    /// </summary>
    private void ApplyCompanionFaction(int slot, nint creature, uint objectId, bool neutral)
    {
        if (_factionKnown[slot] && _heldNeutral[slot] == neutral)
        {
            return;
        }

        if (!Odyssey.SetCreatureFaction(objectId, neutral ? Odyssey.FactionNeutralId : Odyssey.FactionPlayer))
        {
            return; // try again next pass rather than latching a state we did not achieve
        }

        Odyssey.SetMinOneHitPoint(creature, neutral);

        if (neutral)
        {
            Odyssey.ReleaseAttackersOn(creature);
        }

        _heldNeutral[slot] = neutral;
        _factionKnown[slot] = true;

        PluginHost.Log(neutral
            ? $"party drive: companion 0x{objectId:X} held neutral and pinned — the host owns its health"
            : $"party drive: companion 0x{objectId:X} back in the player faction");
    }
}
