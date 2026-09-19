// Odyssey.Faction.cs - the targetability switch, faction reads, and faction
// membership repair for a plugin-driven world load.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- faction, the targetability switch ---------------------------------------
    //
    // Hostility in KOTOR 1 is faction-vs-faction with no personal reputation list, and
    // every targeting path re-reads it live: GetReputation -> stats+0x78 -> the faction
    // matrix (docs/research/combat-participation.md §2). So a phantom becomes a real
    // combat target by joining faction 0 (PLAYER) and stops being one by returning to 5
    // (Neutral) — one dword per state change, no engine paths patched.

    /// <summary>CFactionManager::GetFaction(int id) — the faction object for an id.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint> GetFactionFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint>)0x0052B3B0;

    public const int FactionPlayer = 0;

    /// <summary>Friendly_1, row/column 2 of repute.2da: 100 from the Player row (so
    /// <c>GetIsFriend</c> holds for a party member), 0 from Hostile_1/Hostile_2. Used only
    /// under coop.ini <c>partner_buffs</c> (docs/research/partner-buffs.md).</summary>
    public const int FactionFriendly1 = 2;

    /// <summary>
    /// Moves a creature into a faction through the engine's own membership call.
    ///
    /// This is what makes a peer's phantom a genuine combat target (faction 0) and what
    /// releases every attacker again (faction 5): retention test 6 in
    /// CreateNewAttackActions is a live `GetReputation > 10` check evaluated at every
    /// combat-round boundary, so the flip back is a real release lever — the one the
    /// original aggro-sink design (Handoff 7.4) never had.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool SetCreatureFaction(uint objectId, int factionId)
    {
        if (objectId is 0 or ObjectInvalid)
        {
            return false;
        }

        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        // The same accessor the world-up faction repair uses, rather than a second
        // hand-walked pointer chain to the same manager.
        nint factionManager = GetFactionManagerFn(server);
        if (factionManager == 0)
        {
            return false;
        }

        nint faction = GetFactionFn(factionManager, factionId);
        if (faction == 0)
        {
            return false;
        }

        FactionAddMemberFn(faction, objectId, 0);
        return true;
    }

    /// <summary>Reads a creature's faction id (dword), or -1.</summary>
    public static int GetCreatureFaction(nint creature)
    {
        if (creature == 0)
        {
            return -1;
        }

        nint stats = Deref(creature + CreStats);
        return stats == 0 ? -1 : (int)*(uint*)(stats + StatsFactionId);
    }

    /// <summary>
    /// Releases every local creature currently locked onto this phantom. Required
    /// whenever the owning peer leaves combat (or goes down): a min-1-hp body never
    /// reads dead, so nothing would ever let go on its own.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void PacifyPhantom(nint phantom)
    {
        if (phantom != 0)
        {
            PacifyCreatureFn(phantom);
        }
    }

    /// <summary>
    /// The same CSWSCreature::PacifyCreature (0x004FD960), named for the general case: release
    /// every local creature currently locked onto this body. Mirrored COMPANIONS need it for
    /// the same reason phantoms do — under local_combat they are pinned min-1-hp, so they
    /// never read dead and nothing lets go of them on its own.
    /// </summary>
    public static void ReleaseAttackersOn(nint creature) => PacifyPhantom(creature);

    /// <summary>
    /// Orders one creature to attack another through the engine's own ActionAttack — a real
    /// combat round, with swings, attack rolls, damage and the AI taking over from there.
    ///
    /// This exists because staging a fight on demand is otherwise unreliable: the Endar
    /// Spire's Sith are scripted placements that do not aggro (Handoff 7.0), so every
    /// combat-facing feature — the damage forward, swing animations, the phantom's
    /// targetability and its release levers — had no way to be exercised. Commanding the
    /// attack is the cheapest honest fight, and it is the route 7.0 itself recommends.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    /// <summary>
    /// CServerExoApp::ClearAllQueuedCombatActions(objectId) — drops a creature's queued
    /// combat actions, which is what actually ends a fight it was ordered into.
    ///
    /// Signature checked against the binary rather than taken from the symbol DB alone:
    /// one stack argument read at 0x004AEFA0 and `ret 4` at 0x004AEFBC, matching the DB's
    /// param size of 4. (See docs/research/engine-api.md for why that check is not optional.)
    ///
    /// Exists for the TEST BATTERY, which stages a real fight and then needs the world back:
    /// a creature left in combat cannot start a conversation, so the fight staged by one
    /// check was failing the dialogue check that ran after it.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool ClearQueuedCombatActions(uint objectId)
    {
        nint server = GetServer();
        if (server == 0 || objectId == 0)
        {
            return false;
        }

        ClearQueuedCombatActionsFn(server, objectId);
        return true;
    }

    private static readonly delegate* unmanaged[Thiscall]<nint, uint, void> ClearQueuedCombatActionsFn =
        (delegate* unmanaged[Thiscall]<nint, uint, void>)0x004AEFA0;

    public static bool AttackTarget(uint attackerObjectId, uint targetObjectId, out int failure)
    {
        failure = 0;

        // CONFIRMED LIVE (round 11): dispatches through the same ExecuteCommandAttack ->
        // AddAttackActions -> AddAttackAction chain as QueueAttackAction, and needs the same
        // round-timer arm (SetRoundTimerActiveFn/SetRoundDelayFn) since neither of
        // AddAttackActions's two preamble helpers (0x004D3770/0x004D37E0) ever calls them —
        // without it, a creature that never had a round started the AI's own way hits the same
        // silent-discard gates QueueAttackAction hits. Applying it here took a genuinely fresh
        // sithsoldier from inert to a real fight, landing actual damage (12 hp -> 8 hp -> 1 hp
        // across two ordered attacks, confirmed via `where`/`creatures.ps1`, not inferred). See
        // docs/research/shared-state-audit.md (round 10/11).
        nint attackerServer = GetServer();
        if (attackerServer != 0 && attackerObjectId != 0)
        {
            nint attackerCreature = GetGameObjectFn(attackerServer, attackerObjectId);
            if (attackerCreature != 0)
            {
                nint attackerRound = *(nint*)(attackerCreature + CreCombatRound);
                if (attackerRound != 0)
                {
                    int roundTimerBefore = *(int*)(attackerRound + 0x958);
                    int roundDelayBefore = *(int*)(attackerRound + 0x960);
                    SetRoundTimerActiveFn(attackerRound, 1, (int)attackerObjectId);
                    SetRoundDelayFn(attackerRound, 0x5DC, 0);
                    PluginHost.Log(
                        $"attacktarget diag: 0x{attackerObjectId:X} round+0x958 {roundTimerBefore:X}->{(*(int*)(attackerRound + 0x958)):X}, "
                        + $"round+0x960 {roundDelayBefore:X}->{(*(int*)(attackerRound + 0x960)):X}");
                }
                else
                {
                    PluginHost.Log($"attacktarget diag: 0x{attackerObjectId:X} round pointer at +0x{CreCombatRound:X} is null");
                }
            }
        }

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return false;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0 || *(nint*)(table + (RoutineActionAttack * 4)) != ExpectedAttack)
        {
            failure = 2;
            return false;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 3;
            return false;
        }

        int stackPointer = *(int*)(internalVm + StackPointerOffset);

        // OBJECT_SELF is the ATTACKER: an action always applies to the caller.
        int savedHasCaller = *(int*)(implementer + ImplHasCallerObject);
        uint savedCallerId = *(uint*)(implementer + ImplCallerObjectId);

        *(int*)(implementer + ImplHasCallerObject) = 1;
        *(uint*)(implementer + ImplCallerObjectId) = attackerObjectId;

        try
        {
            // ActionAttack(object oAttackee, int bPassive = FALSE), last-declared first.
            if (StackPushInteger(vm, 0) == 0 || StackPushObject(vm, targetObjectId) == 0)
            {
                failure = 4;
                return false;
            }

            var dispatch = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedAttack;
            if (dispatch(implementer, RoutineActionAttack, 2) != 0)
            {
                failure = 5;
                return false;
            }

            if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
            {
                failure = 6;
            }

            return true;
        }
        finally
        {
            *(int*)(implementer + ImplHasCallerObject) = savedHasCaller;
            *(uint*)(implementer + ImplCallerObjectId) = savedCallerId;
        }
    }

    /// <summary>
    /// Everything the "attack armed but never swings" investigation
    /// (docs/research/shared-state-audit.md, round 56-59) needs to watch on ONE creature,
    /// read together so the caller does not need <see cref="CreCombatRound"/>'s own private
    /// offset. Read-only, MAIN THREAD ONLY, safe to call every frame — no engine calls, just
    /// field reads.
    /// </summary>
    public static void GetAttackDiagnostics(
        nint creature, out int commandable, out int movePermitted, out int headAction,
        out int roundActive, out int roundDelay)
    {
        commandable = 0;
        movePermitted = 0;
        headAction = -1;
        roundActive = 0;
        roundDelay = 0;

        if (creature == 0)
        {
            return;
        }

        commandable = *(int*)(creature + ObjCommandable);
        movePermitted = *(int*)(creature + CreMovePermitted);
        headAction = GetHeadActionType(creature);

        nint round = *(nint*)(creature + CreCombatRound);
        if (round != 0)
        {
            roundActive = *(int*)(round + 0x958);
            roundDelay = *(int*)(round + 0x960);
        }
    }

    /// <summary>
    /// Makes a creature walk over and pick an item up, via the engine's own
    /// ActionPickUpItem. This is the same action a mouse click on a ground item produces,
    /// which is the point: it exercises the real pickup path rather than simulating one.
    ///
    /// Actions apply to the VM's caller object, so OBJECT_SELF is redirected to the
    /// creature for the dispatch and restored afterwards.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool PickUpItem(uint creatureObjectId, uint itemObjectId, out int failure)
    {
        failure = 0;

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return false;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0)
        {
            failure = 2;
            return false;
        }

        if (_routineActionPickUpItem < 0)
        {
            _routineActionPickUpItem = FindRoutine(table, ExpectedActionPickUpItem);
        }

        if (_routineActionPickUpItem < 0)
        {
            failure = 3;
            return false;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return false;
        }

        int stackPointer = *(int*)(internalVm + StackPointerOffset);
        int savedHasCaller = *(int*)(implementer + ImplHasCallerObject);
        uint savedCallerId = *(uint*)(implementer + ImplCallerObjectId);

        *(int*)(implementer + ImplHasCallerObject) = 1;
        *(uint*)(implementer + ImplCallerObjectId) = creatureObjectId;

        try
        {
            if (StackPushObject(vm, itemObjectId) == 0)
            {
                failure = 5;
                return false;
            }

            var dispatch = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedActionPickUpItem;
            if (dispatch(implementer, _routineActionPickUpItem, 1) != 0)
            {
                failure = 6;
                return false;
            }

            if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
            {
                failure = 9;
            }

            return true;
        }
        finally
        {
            *(int*)(implementer + ImplHasCallerObject) = savedHasCaller;
            *(uint*)(implementer + ImplCallerObjectId) = savedCallerId;
        }
    }

    /// <summary>
    /// Queues the engine's own <c>ActionFollowLeader()</c> on a party companion, so its real
    /// follow-AI (paths, formation slot, gait, animation) takes over from here — exactly what
    /// a recruiting conversation's own ending script fires in single-player.
    ///
    /// Built for <see cref="PartyFollowController"/>. Live-confirmed necessary —
    /// Trask recruited (via a real dialogue, or already recruited by a loaded save) sat with a
    /// genuinely EMPTY action queue (<c>+0xFC</c> head type <c>0xFFFFFFFF</c>) and
    /// <c>+0xA8C</c> (the follow state <c>AIActionPartyFollowLeader</c> itself sets to 3 while
    /// running) reading 1, not 3 — no follow order outstanding, nothing about to reissue one.
    /// The one-time in-party positioning a recruit script or a save's own load leaves behind is
    /// not a standing behaviour; nothing in retail re-arms it on an idle queue by itself
    /// (contrary to this project's earlier assumption that a companion's own heartbeat would).
    /// See docs/research/party-reconciliation.md and docs/research/shared-state-audit.md
    /// (round 68/71).
    ///
    /// Same VM-caller-redirect shape as <see cref="AttackTarget"/>/<see cref="PickUpItem"/>:
    /// OBJECT_SELF becomes the companion for the one dispatch, restored after. No stack push —
    /// <c>ActionFollowLeader</c> takes no parameters.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool QueuePartyFollowLeader(uint creatureObjectId, out int failure)
    {
        failure = 0;

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return false;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0)
        {
            failure = 2;
            return false;
        }

        if (_routineActionFollowLeader < 0)
        {
            _routineActionFollowLeader = FindRoutine(table, ExpectedActionFollowLeader);
        }

        if (_routineActionFollowLeader < 0)
        {
            failure = 3;
            return false;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return false;
        }

        int stackPointer = *(int*)(internalVm + StackPointerOffset);
        int savedHasCaller = *(int*)(implementer + ImplHasCallerObject);
        uint savedCallerId = *(uint*)(implementer + ImplCallerObjectId);

        *(int*)(implementer + ImplHasCallerObject) = 1;
        *(uint*)(implementer + ImplCallerObjectId) = creatureObjectId;

        try
        {
            var dispatch = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedActionFollowLeader;
            if (dispatch(implementer, _routineActionFollowLeader, 0) != 0)
            {
                failure = 5;
                return false;
            }

            if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
            {
                failure = 6;
            }

            return true;
        }
        finally
        {
            *(int*)(implementer + ImplHasCallerObject) = savedHasCaller;
            *(uint*)(implementer + ImplCallerObjectId) = savedCallerId;
        }
    }

    // ---- faction membership repair -----------------------------------------------
    //
    // A plugin-driven load brings the world up with the player creature in NO faction: the
    // engine's own CSWSCreature::PostProcess adds a PC to the player faction, but silently
    // falls through when the faction manager slot is not yet populated at the moment it
    // runs. Nothing retries, so GetFaction keeps returning null — and seven unguarded
    // dereferences downstream (six GetSingletonParty callers plus GetLeader) fault the
    // instant the server updates client game objects, about half a second after the world
    // appears. See docs/research/world-up-faction-crash.md.
    //
    // The repair is the engine's own membership call, on the engine's own objects, so every
    // derived value (faction id on the stats block, the member array, the client broadcast)
    // is rebuilt by the engine rather than poked by us.

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetCreatureFactionFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x00513FC0;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetFactionManagerFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AEFC0;

    /// <summary>CSWSFaction::AddMember(objectId, bSilent) — dedups, unlinks any stale
    /// membership, appends, broadcasts the update and writes the stats faction id.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int, void> FactionAddMemberFn =
        (delegate* unmanaged[Thiscall]<nint, uint, int, void>)0x005BFA70;

    /// <summary>CFactionManager + this = the player faction.</summary>
    private const int FactionManagerPlayerFaction = 0x18;

    /// <summary>CGameObject + this = the object id.</summary>
    private const int ObjectIdOffset = 0x04;

    /// <summary>
    /// Ensures the player creature belongs to a faction, repairing it through the engine's
    /// own AddMember if not. Returns a short status for the log: 0 = nothing to do (already
    /// in a faction), 1 = repaired, 2 = no player yet, 3 = no faction manager or no player
    /// faction (new information — the world loaded without factions).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static int EnsurePlayerFaction()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 2;
        }

        nint creature = GetPlayerCreatureFn(server);
        if (creature == 0)
        {
            return 2;
        }

        if (GetCreatureFactionFn(creature) != 0)
        {
            return 0;
        }

        nint manager = GetFactionManagerFn(server);
        if (manager == 0)
        {
            return 3;
        }

        nint playerFaction = *(nint*)(manager + FactionManagerPlayerFaction);
        if (playerFaction == 0)
        {
            return 3;
        }

        FactionAddMemberFn(playerFaction, *(uint*)(creature + ObjectIdOffset), 0);
        return 1;
    }

}
