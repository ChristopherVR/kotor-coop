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
///
/// Roadmap item 4, OFF by default: <c>companion_local_combat=1</c> lets ONE companion — the one
/// standing close enough to both this instance's own player and the enemy <c>local_combat</c> is
/// already fighting locally — be handed to its own engine AI instead of mirrored, via
/// <see cref="CompanionLocalFightRule"/>. See docs/research/party-reconciliation.md, dated entry
/// 2026-08-21, for why the gate is this tight.
///
/// ROUND 83: released no longer means "handed to a party AI script that solo mode has silenced
/// and hoped it acts anyway" — round 82 live-confirmed it never does. Instead the released
/// companion's attack is driven directly through <see cref="Odyssey.AttackTarget"/> (see
/// <see cref="DriveAttackIfDue"/>), the same engine call the <c>attack</c> cheat already uses,
/// which a comprehensive static scan found is never gated on solo mode at all — so this makes
/// no write to the party-wide flag and carries none of that write's risk to a second, still
/// mirrored companion. Live-verified 2026-08-24 (docs/research/party-reconciliation.md's
/// "two-companion `companion_local_combat` release... Flipped ON by default" entry): two
/// companions released in the same window, both drove attacks on the same target, real
/// credited kills, no cross-slot contamination.
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

    // ---- roadmap item 11: companion equipment (docs/research/companion-state-sync.md).
    // Live-verified 2026-08-28 (§12): both weapon slots and the body slot confirmed working,
    // mid-session propagation under 3s. Default ON, same escape-hatch discipline every other
    // engine-write feature in this file follows. Dresses a mirrored companion in the gear the
    // HOST publishes (see OffsetCompanionEquipSlots' own comment), through
    // Odyssey.EquipItemFromTemplateDirect (§7's fix — PhantomController.ApplyEquipment still
    // uses the older EquipItemFromTemplate for a peer's phantom, unchanged).

    private readonly bool _companionGearSyncEnabled = PluginConfig.ReadBool("companion_gear_sync", true);

    private readonly uint[,] _appliedEquip = new uint[SharedBlock.PartyPresenceCapacity, SharedBlock.EquipCount];

    private int _equipCooldownFrames;

    private int _equipsLogged;

    private readonly uint[] _trackedId = new uint[SharedBlock.PartyPresenceCapacity];
    private readonly nint[] _creature = new nint[SharedBlock.PartyPresenceCapacity];
    private readonly uint[] _appliedOneShot = new uint[SharedBlock.PartyPresenceCapacity];
    private readonly bool[] _oneShotPrimed = new bool[SharedBlock.PartyPresenceCapacity];

    // ---- roadmap item 4: companion local combat (CompanionLocalFightRule). OFF by default —
    // see docs/research/party-reconciliation.md, dated entry 2026-08-21, for the full design
    // question and the reasoning behind the triple-proximity gate below.

    private readonly bool _companionLocalCombatEnabled = PluginConfig.ReadBool("companion_local_combat", false);

    /// <summary>How close a companion's host-driven position must be to BOTH the locally-fighting
    /// enemy and this instance's own player before it may be released to its own engine AI —
    /// "the players are actually fighting side by side," not merely both individually near
    /// something hostile. coop.ini <c>companion_local_combat_range</c>.</summary>
    private static readonly float CompanionLocalCombatRangeSquared =
        Squared(Math.Clamp(PluginConfig.ReadFloat("companion_local_combat_range", 10f), 1f, 100f));

    private static float Squared(float value) => value * value;

    /// <summary>Per slot: is this companion currently released to its own engine AI. Edge state
    /// for <see cref="CompanionLocalFightRule"/> — <see cref="Forget"/> clears it, the same as
    /// every other per-slot cache here.</summary>
    private readonly bool[] _released = new bool[SharedBlock.PartyPresenceCapacity];

    /// <summary>CSWPartyTable + this = the solo-mode byte. Read-only here — this controller
    /// never writes it (see the "No write to solo mode" note on the release gate below); the
    /// field is entirely owned by <c>KotorGameBridge.TryWritePartyMembers</c>/
    /// <c>ReleasePartyFreeze</c>, one layer up. Same offset <see cref="PartyFollowController"/>
    /// already reads for its own diagnostic.</summary>
    private const int PartyTableSoloMode = 0x190;

    /// <summary>
    /// ⚠ NEVER LIVE-CONFIRMED: whether a companion's own engine AI can complete an attack
    /// round AT ALL while solo mode holds the party table's own "companions stand still"
    /// flag (<see cref="PartyTableSoloMode"/>) — see docs/research/party-reconciliation.md,
    /// dated entry 2026-08-23 ("Does solo mode block a released companion's own combat AI,
    /// not just its follow AI"). <c>KotorGameBridge.TryWritePartyMembers</c> asserts solo mode
    /// EVERY agent pump for the WHOLE co-op session, release state or not — this diagnostic
    /// does not change that (an untested party-wide write is exactly the class of engine
    /// change this project's own rule holds back); it only makes the open question visible: if
    /// a released companion's action-queue head NEVER leaves -1 (empty) while solo mode reads
    /// nonzero and a hostile is in range, that is live, first-hand evidence the swing is being
    /// blocked here — not by the release/reclaim gate, and not by the party-presence staleness
    /// bug already on record. Throttled per slot so a long fight costs a handful of lines, not
    /// one per frame; capped in total so a session left running does not grow the log forever.
    /// </summary>
    private const double ReleaseDiagIntervalSeconds = 3.0;

    private const int ReleaseDiagMaxLines = 60;

    private readonly double[] _lastReleaseDiagAt = new double[SharedBlock.PartyPresenceCapacity];

    private int _releaseDiagLogged;

    /// <summary>
    /// ROUND 83 (static pass, no rig access): a comprehensive scan of every instruction in
    /// swkotor.exe's .text that reads <see cref="PartyTableSoloMode"/> off the party table
    /// pointer (every direct call to <c>CServerExoApp::GetPartyTable</c>, 226 sites, followed
    /// out to a ret or 250 instructions either way) found exactly six read sites: two GUI
    /// button-state updates (<c>CSWGuiMainInterface::Update</c>,
    /// <c>CSWGuiInGameSoloModeQuery::SetQueryText</c>), one client-side follow-toggle read
    /// (<c>CClientExoAppInternal::TogglePartyFollow</c>), one stealth-menu read
    /// (<c>CSWCCreature::PartyMenuStealth</c>), one temp-death update
    /// (<c>CServerExoAppInternal::TempDeathUpdate</c>), and the script-facing
    /// <c>GetSoloMode()</c> nwscript command itself (<c>ExecuteCommandGetSoloMode</c>). NONE of
    /// them sit anywhere on the path from <c>ExecuteCommandAttack</c> through
    /// <c>CSWSCreature::AddAttackActions</c>/<c>AddAttackAction</c> to <c>RunActions</c> — the
    /// exact chain <see cref="Odyssey.AttackTarget"/> already drives, and RunActions' own action
    /// dispatch never reads party-table state at all (party-reconciliation.md, 2026-07-31: "Trap
    /// 5" — RunActions never even reads commandable). Read together with round 82's live result
    /// (a released companion's action-queue head never leaves empty while solo mode holds), the
    /// conclusion this project's own earlier writing never separated is: solo mode's "companions
    /// stand still" behaviour is the party AI SCRIPT choosing not to act on what
    /// <c>GetSoloMode()</c> tells it — an engine-level kill switch nothing here can see, not an
    /// engine-level GATE on the action queue that a directly-issued action would have to fight.
    /// Directly queuing an attack for the released companion, exactly the way <c>attack</c>/
    /// <c>TestDriveController</c> already do for any other creature, therefore never touches
    /// solo mode at all — no write, no read, no risk to the OTHER, still-mirrored companion's
    /// own solo-suppressed AI. See docs/research/companion-combat-credit-plan.md's dated
    /// 2026-08-23 entry for the full scan methodology and every candidate ruled out.
    ///
    /// Two conditions gate re-issuing, both in <see cref="CompanionLocalFightRule.ShouldDriveAttack"/>:
    /// the queue must currently be empty (nothing to contest) and at least
    /// <see cref="AttackDriveIntervalSeconds"/> must have passed since the last attempt (a
    /// throttle, not a cadence the engine's own combat-round timing requires — untested live).
    /// NEEDS-LIVE: this whole mechanism is a disassembly-backed hypothesis, never fired against
    /// a real fight. <see cref="_companionLocalCombatEnabled"/> keeps it off by default.
    /// </summary>
    private const double AttackDriveIntervalSeconds = 1.5;

    private const int AttackDriveMaxLines = 60;

    private readonly double[] _lastAttackDriveAt = new double[SharedBlock.PartyPresenceCapacity];

    private int _attackDriveLogged;

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

        // Resolved ONCE per pass, not per companion: local_combat runs at most one fight at a
        // time (its own design), so every companion this pass shares the same answer to "is
        // there a fight to join, and where is it." block.LocalCombatExcludedId is the exact
        // value EnemyPresenceController publishes right where it arms StartLocalFight — see
        // that field's own doc comment.
        bool localFightActive = false;
        Vec3 localEnemyPosition = default;
        uint localEnemyObjectId = 0;
        uint locallyFightingOwnerId = block.LocalCombatExcludedId;
        if (_companionLocalCombatEnabled && locallyFightingOwnerId != 0)
        {
            uint mappedEnemyId = MaterialisedEnemies.ToLocal(locallyFightingOwnerId);
            localEnemyObjectId = mappedEnemyId != 0 ? mappedEnemyId : locallyFightingOwnerId;
            nint enemyCreature = Odyssey.ResolveServerObject(localEnemyObjectId);
            if (enemyCreature != 0)
            {
                localEnemyPosition = Odyssey.GetPosition(enemyCreature);
                localFightActive = true;
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (DriveOne(i, block.GetPartyPresence(i), now, player, localFightActive, localEnemyPosition, localEnemyObjectId))
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

    private bool DriveOne(
        int slot, PartyPresenceView member, double now, nint player,
        bool localFightActive, Vec3 localEnemyPosition, uint localEnemyObjectId)
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

        // Gear first, unconditionally — the companion has to already be wearing the right
        // equipment BEFORE a local-combat release hands it to its own engine AI below, since
        // once released this controller stops touching the body entirely (see
        // CompanionLocalFightAction.StayReleased's own remarks).
        if (_companionGearSyncEnabled)
        {
            ApplyCompanionEquipment(slot, objectId);
        }

        // ---- roadmap item 4: companion local combat release gate. See
        // docs/research/party-reconciliation.md, dated entry 2026-08-21, for the full design.
        // Decided BEFORE the claim below, same reasoning as EnemyPresenceController's own
        // ROUND 46 note: a companion about to be handed its own engine AI must not be claimed
        // or have its queue touched this same frame, or the hand-off disarms the very AI it is
        // being released to.
        bool nearEnemy = localFightActive
            && PlanarDistanceSquared(member.Position, localEnemyPosition) <= CompanionLocalCombatRangeSquared;
        bool nearPlayer = player != 0
            && PlanarDistanceSquared(member.Position, Odyssey.GetPosition(player)) <= CompanionLocalCombatRangeSquared;

        CompanionLocalFightAction fightAction = CompanionLocalFightRule.Decide(
            _companionLocalCombatEnabled, _rosterFrozen, localFightActive, nearEnemy, nearPlayer, _released[slot]);

        switch (fightAction)
        {
            case CompanionLocalFightAction.ReleaseAndLog:
                _released[slot] = true;
                // Put it back in the player faction, off the min-1-hp pin — the opposite of the
                // blanket local_combat hold just below, because THIS companion can now genuinely
                // fight back.
                ApplyCompanionFaction(slot, creature, objectId, neutral: false);
                PluginHost.Log(
                    $"party drive: companion 0x{objectId:X} released to fight locally — "
                    + "within range of both you and the enemy you are fighting "
                    + $"({ReleaseDiagSnapshot(creature, now, slot)})");
                return false;

            case CompanionLocalFightAction.StayReleased:
                // Hands off entirely, the same handoff EnemyPresenceController's own excluded
                // enemy gets from DriveOne: no claim, no position/health write, nothing except
                // the direct attack drive below. The companion's own engine AI still owns
                // movement/facing/animation from here — this only ever queues ONE attack action
                // through the same engine call the `attack` cheat already uses, and only when
                // that companion's own queue is empty. See AttackDriveIntervalSeconds' doc.
                LogReleaseDiagIfDue(objectId, creature, now, slot);
                if (localFightActive && localEnemyObjectId != 0)
                {
                    DriveAttackIfDue(objectId, creature, localEnemyObjectId, now, slot);
                }

                return false;

            case CompanionLocalFightAction.ReclaimAndLog:
                _released[slot] = false;
                PluginHost.Log(
                    $"party drive: companion 0x{objectId:X} reclaimed — resuming the host mirror");
                break;

            case CompanionLocalFightAction.Mirror:
                break;
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
        //
        // The one companion CompanionLocalFightRule has released (above) never reaches this
        // line at all this pass — it returned before the switch fell through — so the blanket
        // hold below only ever applies to a companion that genuinely cannot fight back yet.
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

    /// <summary>
    /// One line of ground truth for the open solo-mode question (see
    /// <see cref="PartyTableSoloMode"/>'s own doc comment): the released companion's
    /// action-queue head type (-1 = empty), its <c>CreMoveState</c>, and whether solo mode
    /// currently reads nonzero. Three reads, no engine call — safe to call from the frame
    /// hook, same discipline as <see cref="Odyssey.GetHeadActionType"/> itself.
    /// </summary>
    private static string ReleaseDiagSnapshot(nint creature, double now, int slot)
    {
        int headType = Odyssey.GetHeadActionType(creature);
        int moveState = *(int*)(creature + Odyssey.CreMoveState);
        int soloMode = ReadSoloMode();
        return $"head=0x{headType:X} moveState={moveState} soloMode={soloMode} t={now:F1}s slot={slot}";
    }

    /// <summary>Throttled per slot (<see cref="ReleaseDiagIntervalSeconds"/>) and capped in
    /// total (<see cref="ReleaseDiagMaxLines"/>) — see <see cref="PartyTableSoloMode"/>'s own
    /// doc comment for what this is trying to settle.</summary>
    private void LogReleaseDiagIfDue(uint objectId, nint creature, double now, int slot)
    {
        if (_releaseDiagLogged >= ReleaseDiagMaxLines
            || now - _lastReleaseDiagAt[slot] < ReleaseDiagIntervalSeconds)
        {
            return;
        }

        _lastReleaseDiagAt[slot] = now;
        _releaseDiagLogged++;
        PluginHost.Log(
            $"party drive: companion 0x{objectId:X} still released — {ReleaseDiagSnapshot(creature, now, slot)}");
    }

    /// <summary>
    /// ROUND 83: drives the released companion's attack directly through
    /// <see cref="Odyssey.AttackTarget"/> — the engine's own <c>ExecuteCommandAttack</c>, the
    /// same call the <c>attack</c> console command and <c>TestDriveController</c> already use —
    /// instead of waiting for the party AI script to queue one, which round 82's live evidence
    /// says never happens while solo mode holds. See <see cref="AttackDriveIntervalSeconds"/>'s
    /// doc comment for the disassembly this rests on: nothing on this call's path reads solo
    /// mode, so this makes no engine write of its own and carries none of the party-wide risk a
    /// scoped solo-mode release would.
    ///
    /// <see cref="CompanionLocalFightRule.ShouldDriveAttack"/> gates the call: only when the
    /// companion's own action-queue head is empty (nothing to contest) and the per-slot
    /// throttle has elapsed. Live-verified 2026-08-24 against a real two-companion fight
    /// (docs/research/party-reconciliation.md's "two-companion `companion_local_combat`
    /// release... Flipped ON by default" entry) — both companions' release lines fired in the
    /// same tick and both drove their attack on the same target 4ms later, with real credited
    /// damage and a real kill following. The log line below is deliberately loud (not folded
    /// into the quieter release-diag trickle) so a live pass can tell at a glance whether the
    /// call is even being attempted, separately from whether it lands. Capped in total for the
    /// same reason <see cref="LogReleaseDiagIfDue"/> is.
    /// </summary>
    private void DriveAttackIfDue(uint objectId, nint creature, uint enemyObjectId, double now, int slot)
    {
        int headType = Odyssey.GetHeadActionType(creature);
        if (!CompanionLocalFightRule.ShouldDriveAttack(headType, now, _lastAttackDriveAt[slot], AttackDriveIntervalSeconds))
        {
            return;
        }

        _lastAttackDriveAt[slot] = now;

        bool ok = Odyssey.AttackTarget(objectId, enemyObjectId, out int failure);
        if (_attackDriveLogged < AttackDriveMaxLines)
        {
            _attackDriveLogged++;
            PluginHost.Log(ok
                ? $"party drive: drove companion 0x{objectId:X}'s attack on 0x{enemyObjectId:X} directly (head was 0x{headType:X})"
                : $"party drive: could not drive companion 0x{objectId:X}'s attack on 0x{enemyObjectId:X} (failure {failure}, head was 0x{headType:X})");
        }
    }

    /// <summary>CSWPartyTable+0x190, read fresh every call — the same pointer chain
    /// <see cref="ResolveRosterObjectId"/> already walks, plus the solo-mode offset
    /// <see cref="PartyTableSoloMode"/>. Read-only: see that field's own doc comment for why
    /// this controller never writes it.</summary>
    private static int ReadSoloMode()
    {
        nint appManager = *(nint*)AppManagerPtr;
        if (appManager == 0)
        {
            return -1;
        }

        nint server = *(nint*)(appManager + AppServerOffset);
        if (server == 0)
        {
            return -1;
        }

        nint serverInternal = *(nint*)(server + InternalOffset);
        if (serverInternal == 0)
        {
            return -1;
        }

        return *(byte*)(serverInternal + PartyTableOffset + PartyTableSoloMode);
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

    /// <summary>Same cache-and-revalidate as the enemy drive: id and VTABLE must still match
    /// every frame, or the pointer is stale reused memory. Identity is the vtable, never the
    /// <c>+0x08</c> type field — see <see cref="PartyCreatureIdentity"/> for why an exact-5
    /// comparison there used to freeze a live companion.</summary>
    private nint Resolve(int slot, uint objectId)
    {
        nint cached = _creature[slot];
        if (cached != 0 && _trackedId[slot] == objectId
            && PartyCreatureIdentity.IsLiveMatch(*(nint*)cached, *(uint*)(cached + 0x04), objectId))
        {
            return cached;
        }

        Forget(slot);

        nint creature = Odyssey.ResolveServerObject(objectId);
        if (creature == 0
            || !PartyCreatureIdentity.IsLiveMatch(*(nint*)creature, *(uint*)(creature + 0x04), objectId))
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
        _released[slot] = false;
        _smoother.Reset(slot);

        // A different companion (or the same one, freshly re-resolved) may now occupy this
        // presence slot; forget what gear this controller last believed it applied so a real
        // change is never mistaken for one already handled.
        for (int j = 0; j < SharedBlock.EquipCount; j++)
        {
            _appliedEquip[slot, j] = 0;
        }
    }

    /// <summary>Maps equip index in the shared block to the engine's inventory slot — same
    /// order <c>PhantomController.EquipInventorySlots</c> already uses.</summary>
    private static ReadOnlySpan<int> EquipInventorySlots =>
    [
        Odyssey.InventorySlotBody,
        Odyssey.InventorySlotRightWeapon,
        Odyssey.InventorySlotLeftWeapon,
    ];

    /// <summary>
    /// Dresses a mirrored companion in the gear the host published, for each slot whose
    /// desired resref changed since the last apply. One engine mutation per 30 frames — an
    /// equip is as heavy as a spawn, the same cooldown
    /// <see cref="PhantomController.ApplyEquipment"/> already uses.
    ///
    /// An empty desired resref is left alone (companion_gear_sync off on the host, or the slot
    /// could not be read there); a resref this install cannot resolve is remembered as applied
    /// so a missing mod does not retry forever.
    /// </summary>
    private void ApplyCompanionEquipment(int slot, uint objectId)
    {
        if (_equipCooldownFrames > 0)
        {
            _equipCooldownFrames--;
            return;
        }

        for (int j = 0; j < SharedBlock.EquipCount; j++)
        {
            byte* resref = block.GetCompanionEquipResref(slot, j);

            int length = 0;
            uint hash = 2166136261;
            while (length < 16 && resref[length] != 0)
            {
                hash = (hash ^ resref[length]) * 16777619;
                length++;
            }

            if (!EquipApplyDecision.ShouldApply(length, hash, _appliedEquip[slot, j]))
            {
                continue;
            }

            _appliedEquip[slot, j] = hash;
            _equipCooldownFrames = 30;

            // NOT Odyssey.EquipItemFromTemplate (the phantom channel's own, proven-live call):
            // that method equips through ActionEquipItem's VM dispatch, which does not equip
            // synchronously — it queues the equip onto the creature's OWN CSWSCombatRound
            // pending list (round+0x9B0, CSWSCreature::AddEquipItemActions ->
            // CSWSCombatRound::AddEquipAction, VERIFIED by disassembly), and nothing in that
            // call chain ever arms the round's own processing gate the way
            // Odyssey.Combat.Attack.cs's QueueAttackAction has to for an attack. A phantom is
            // never claimed (PhantomController never calls Odyssey.ClaimCreature), so nothing
            // here is racing it; a MIRRORED companion is re-claimed every DriveOne pass, a few
            // frames after the equip is queued, with no engine tick ever having drained it.
            // EquipItemFromTemplateDirect equips through the engine's own synchronous
            // CSWSCreature::EquipItem (0x004FEB90) instead — no queue, nothing left pending.
            // See docs/research/companion-state-sync.md and Odyssey.Equipment.cs's own doc
            // comment on EquipItemFromTemplateDirect. NEEDS-LIVE.
            bool ok = Odyssey.EquipItemFromTemplateDirect(
                objectId, resref, length, EquipInventorySlots[j], out int failure);

            if (_equipsLogged < 12)
            {
                _equipsLogged++;
                string name = System.Text.Encoding.ASCII.GetString(resref, length);
                PluginHost.Log(ok
                    ? $"party drive: companion 0x{objectId:X} equips {name} (slot {EquipInventorySlots[j]})"
                    : $"party drive: {name} refused for companion 0x{objectId:X} (failure {failure})");
            }

            return;
        }
    }

    /// <summary>Planar (X/Y) distance squared — the same test <c>EnemyPresenceController
    /// .IsEngaged</c> uses for the identical reason: the Z of a driven body is the OWNER's, and a
    /// ramp between two players standing in the same fight is a metre of height, not a different
    /// floor.</summary>
    private static float PlanarDistanceSquared(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
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
