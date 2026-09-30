using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// Both ends of the companion-damage channel, on the game's own thread — the companion sibling
/// of <see cref="PeerDamageController"/>. See that class's own header comment first (the
/// "engine, not a health poke" reasoning applies here unchanged) and
/// docs/research/companion-state-sync.md §13 for this channel's own design.
///
/// Client end (a mirroring instance that has RELEASED a companion via
/// <c>companion_local_combat</c>): publishes <see cref="PhantomHitHook"/>'s companion watch —
/// cumulative damage this instance's own engine has actually applied to that companion — for
/// the agent to forward to the host.
///
/// Host end (the instance that owns the party): reads the total every peer has dealt to each of
/// ITS OWN companions, keyed by NPC roster slot rather than object id (a companion's identity
/// survives a module transition, unlike an enemy's — <see cref="PartyPresenceController"/>
/// already resolves companions by roster slot for the identical reason), and turns the increase
/// into real damage through the engine's own instant <c>EffectDamage</c>. At 0 hp this produces
/// the REAL temp-death <c>TempDeathUpdate</c> expects — the same win the enemy channel gets: no
/// separate "it went down" message to trust.
/// </summary>
internal sealed unsafe class CompanionDamageController(SharedBlockView block)
{
    /// <summary>Same cadence as <see cref="PeerDamageController"/> — damage arrives at network
    /// rate, so there is nothing to gain from testing it every frame.</summary>
    private const int IntervalFrames = 10;

    // The APP_MANAGER -> server -> internal walk, same chain PartyPresenceController and
    // PeerDamageController both already use.
    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppServerOffset = 0x08;
    private const int InternalOffset = 0x04;

    /// <summary>CServerExoAppInternal + this = the embedded CSWPartyTable.</summary>
    private const int PartyTableOffset = 0x1B770;

    /// <summary>CSWPartyTable + this = int[9] spawned object id per roster slot.</summary>
    private const int PartyObjectIds = 0x0C;

    /// <summary>Reused as-is: it is already keyed by an arbitrary uint id, and a roster slot
    /// (plus one) is just as valid a key as an enemy object id — see that class's own doc
    /// comment for why the arithmetic itself needs no change to serve a second channel. Twice
    /// the row capacity since 2026-09-18: a heal row (<see cref="CompanionDamageSlots"/>) is a
    /// second key per roster slot.</summary>
    private readonly DamageOwedLedger _owed = new(SharedBlock.CompanionDamageOwedCapacity * 2);

    private uint _generation = 1;

    // ---- heals a released companion received here (client side, 2026-09-18) ---------------
    //
    // Sampled, not hooked: see CompanionHealSampleRule. One sample per presence slot (which
    // body, its hit points, the damage watch's serial at the time), one running TOTAL per
    // roster slot — kept across a reclaim for the identical reason PhantomHitHook's own
    // companion total is (its _companionCumulative doc comment), and zeroed only by
    // Invalidate, alongside the generation bump the host's ledger reads as a restart.

    private readonly CompanionHealSampleRule.Sample[] _healSample =
        new CompanionHealSampleRule.Sample[SharedBlock.CompanionDamageSlotCapacity];

    private readonly uint[] _healCumulative = new uint[SharedBlock.PartyRosterSlots + 1];
    private readonly uint[] _healSerial = new uint[SharedBlock.PartyRosterSlots + 1];

    private int _countdown;
    private int _logged;
    private int _unresolvedLogged;
    private int _healsLogged;
    private int _healsRefused;

    public void Invalidate()
    {
        _countdown = 0;
        _generation++;
        _owed.Clear();
        Array.Clear(_healSample);
        Array.Clear(_healCumulative);
        Array.Clear(_healSerial);
        block.CompanionDamageCount = 0;
        block.CompanionDamageOwedCount = 0;
    }

    public void OnFrame()
    {
        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        PublishTaken();
        ApplyOwed();
    }

    /// <summary>Hands the hook's per-slot watch to the agent. Cumulative totals, so a pass the
    /// agent misses costs nothing.</summary>
    private void PublishTaken()
    {
        int published = 0;
        for (int i = 0; i < SharedBlock.CompanionDamageSlotCapacity; i++)
        {
            PhantomHitHook.ReadCompanionSlot(
                i, out uint rosterSlotPlusOne, out uint cumulative, out uint serial, out uint objectId);

            SampleHeal(i, rosterSlotPlusOne, objectId, serial);

            if (rosterSlotPlusOne == 0 || cumulative == 0
                || !block.CanMirrorPartyOccupant((int)rosterSlotPlusOne - 1))
            {
                continue;
            }

            block.SetCompanionDamageTaken(published++, rosterSlotPlusOne, cumulative, serial);
        }

        // Heal rows after every damage row, under the flagged key (CompanionDamageSlots): with
        // at most two companions ever released at once, four rows hold both kinds for both.
        for (int i = 0; i < SharedBlock.CompanionDamageSlotCapacity && published < SharedBlock.CompanionDamageSlotCapacity; i++)
        {
            PhantomHitHook.ReadCompanionSlot(i, out uint rosterSlotPlusOne, out _, out _);
            if (rosterSlotPlusOne == 0 || _healCumulative[rosterSlotPlusOne] == 0
                || !block.CanMirrorPartyOccupant((int)rosterSlotPlusOne - 1))
            {
                continue;
            }

            block.SetCompanionDamageTaken(
                published++,
                CompanionDamageSlots.HealSlot(rosterSlotPlusOne),
                _healCumulative[rosterSlotPlusOne],
                _healSerial[rosterSlotPlusOne]);
        }

        // Blank the tail before moving the count down, so the agent never reads a row a
        // shorter list has left behind — same discipline PeerDamageController.PublishDealt uses.
        for (int i = published; i < SharedBlock.CompanionDamageSlotCapacity; i++)
        {
            block.SetCompanionDamageTaken(i, 0, 0, 0);
        }

        block.CompanionDamageGeneration = _generation;
        block.CompanionDamageCount = (uint)published;
    }

    /// <summary>
    /// One hit-point sample of a released companion, folded through
    /// <see cref="CompanionHealSampleRule"/> into the per-roster-slot heal total. Runs at the
    /// same 10-frame cadence as the publish, so a medpac's rise and the next hit are almost
    /// always in different windows; when they are not, that window is a baseline and the
    /// medpac is not credited — the safe side, and a rare one (a hit and a heal inside the
    /// same sixth of a second).
    /// </summary>
    private void SampleHeal(int slot, uint rosterSlotPlusOne, uint objectId, uint damageSerial)
    {
        CompanionHealSampleRule.Sample current = default;

        if (objectId != 0 && rosterSlotPlusOne is > 0 and <= SharedBlock.PartyRosterSlots)
        {
            nint creature = Odyssey.ResolveServerObject(objectId);
            if (creature != 0)
            {
                current = new CompanionHealSampleRule.Sample(objectId, Odyssey.GetCurrentHitPoints(creature), damageSerial);
            }
        }

        CompanionHealSampleRule.Sample previous = _healSample[slot];
        CompanionHealSampleRule.Verdict verdict = CompanionHealSampleRule.Observe(previous, current, out int heal);
        _healSample[slot] = current;

        switch (verdict)
        {
            case CompanionHealSampleRule.Verdict.Healed:
                uint total = _healCumulative[rosterSlotPlusOne];
                _healCumulative[rosterSlotPlusOne] = (uint)heal > uint.MaxValue - total ? uint.MaxValue : total + (uint)heal;
                _healSerial[rosterSlotPlusOne]++;
                if (_healsLogged < 20)
                {
                    _healsLogged++;
                    PluginHost.Log(
                        $"companion heal: released companion 0x{objectId:X} (roster slot {rosterSlotPlusOne - 1}) "
                        + $"healed {heal} here -> {current.HitPoints} hp, cumulative {_healCumulative[rosterSlotPlusOne]}");
                }

                break;

            case CompanionHealSampleRule.Verdict.Refused:
                _healsRefused++;
                if (_healsRefused <= 5)
                {
                    PluginHost.Log(
                        $"companion heal: REFUSED an implausible rise on 0x{objectId:X} "
                        + $"({previous.HitPoints} -> {current.HitPoints} hp in one window) — not a heal");
                }

                break;
        }
    }

    /// <summary>Turns what the peers have dealt to our own companions into damage our own
    /// engine delivers.</summary>
    private void ApplyOwed()
    {
        uint count = Math.Min(block.CompanionDamageOwedCount, (uint)SharedBlock.CompanionDamageOwedCapacity);
        if (count == 0)
        {
            return;
        }

        // Not while a conversation owns the screen — the dialogue system wipes every hostile's
        // (and every action queue's) state each tick in the talking instance, so a temp-death
        // landing now would run into a scene that is mid-flight. Same guard
        // PeerDamageController.ApplyOwed uses, for the identical reason; the debt is still owed
        // on the next pass.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        // ⚠ ATTRIBUTION (coop.ini damage_creator_attribution, docs/research/peer-damage-ledger.md
        // §8). Same reasoning as PeerDamageController.ApplyOwed's own remarks: this table is
        // merged across every contributing peer's rise per roster slot
        // (CompanionDamageLedger.Merge drops the reporting peer id at that point, by design —
        // see that class's own doc comment), so the only case this can name a real attacker is
        // exactly one remote peer connected. Otherwise it falls back to no attribution (0),
        // today's behaviour.
        uint creatorId = Odyssey.DamageCreatorAttributionEnabled ? block.SoleRemotePhantomId() : 0;

        for (int i = 0; i < count; i++)
        {
            block.GetCompanionDamageOwed(i, out uint slotField, out uint owed);

            // Damage and heal rows share the table under different keys (CompanionDamageSlots);
            // the applied ledger is keyed by the raw field, so the two never net against each
            // other — a heal is spent as a heal, a hit as a hit, each exactly once.
            bool isHeal = CompanionDamageSlots.IsHeal(slotField);
            uint rosterSlotPlusOne = CompanionDamageSlots.RosterSlotPlusOne(slotField);
            if (rosterSlotPlusOne == 0 || rosterSlotPlusOne > SharedBlock.PartyRosterSlots
                || !PartyOccupantHook.Captured.LegacySideChannelAllowed((int)rosterSlotPlusOne - 1))
            {
                continue;
            }

            int amount = _owed.Pending(slotField, owed);
            if (amount <= 0)
            {
                continue;
            }

            if (isHeal)
            {
                ApplyHeal(slotField, rosterSlotPlusOne, amount, owed);
                continue;
            }

            // Resolve first: a roster slot with no live body is a companion not currently
            // spawned (benched, or the world is mid-transition and nothing is resolvable yet).
            // Leaving the debt unapplied is right — it lands the moment the body does.
            uint objectId = ResolveRosterObjectId((int)rosterSlotPlusOne - 1);
            nint creature = objectId is 0 or Odyssey.ObjectInvalid ? 0 : Odyssey.ResolveServerObject(objectId);

            if (creature == 0)
            {
                if (_unresolvedLogged < 10)
                {
                    _unresolvedLogged++;
                    PluginHost.Log(
                        $"companion damage: a peer owes {amount} to roster slot {rosterSlotPlusOne - 1}, "
                        + "which has no live body here right now — holding the debt");
                }

                continue;
            }

            if (Odyssey.ApplyDamageEffect(objectId, amount, creatorId, out int failure))
            {
                _owed.Commit(slotField, amount);
                block.CompanionDamageApplied += 1;

                if (_logged < 20)
                {
                    _logged++;
                    PluginHost.Log(
                        $"companion damage: applied {amount} of a peer's damage to companion "
                        + $"0x{objectId:X} (roster slot {rosterSlotPlusOne - 1}, owed {owed})");
                }
            }
            else if (_logged < 20)
            {
                _logged++;
                PluginHost.Log(
                    $"companion damage: refused for companion 0x{objectId:X} "
                    + $"(roster slot {rosterSlotPlusOne - 1}, failure {failure})");
            }
        }
    }

    /// <summary>
    /// Spends a peer's heal on our own companion through the engine's own instant
    /// <c>EffectHeal</c> (<see cref="OdysseyEffects.ApplyEffect"/>, the same constructor the
    /// peer-effect channel already uses for a forwarded Heal on a stand-in). A companion with
    /// no live body holds the debt like a hit does; a companion at or below zero has the debt
    /// COMMITTED without applying — a medpac on a downed body is wasted in retail too, and
    /// holding it would land a stale heal the moment the engine's own recovery stood it up.
    /// NEEDS-LIVE: the sample-and-forward loop has not been watched on the rig.
    /// </summary>
    private void ApplyHeal(uint slotField, uint rosterSlotPlusOne, int amount, uint owed)
    {
        uint objectId = ResolveRosterObjectId((int)rosterSlotPlusOne - 1);
        nint creature = objectId is 0 or Odyssey.ObjectInvalid ? 0 : Odyssey.ResolveServerObject(objectId);
        if (creature == 0)
        {
            if (_unresolvedLogged < 10)
            {
                _unresolvedLogged++;
                PluginHost.Log(
                    $"companion heal: a peer owes {amount} healing to roster slot {rosterSlotPlusOne - 1}, "
                    + "which has no live body here right now — holding it");
            }

            return;
        }

        if (Odyssey.GetCurrentHitPoints(creature) <= 0)
        {
            _owed.Commit(slotField, amount);
            if (_logged < 20)
            {
                _logged++;
                PluginHost.Log(
                    $"companion heal: dropped {amount} owed to companion 0x{objectId:X} "
                    + $"(roster slot {rosterSlotPlusOne - 1}) — it is down here, a heal would be wasted");
            }

            return;
        }

        if (OdysseyEffects.ApplyEffect(objectId, PeerEffectKindValues.Heal, 0, (uint)amount, out int failure))
        {
            _owed.Commit(slotField, amount);
            block.CompanionDamageApplied += 1;
            if (_logged < 20)
            {
                _logged++;
                PluginHost.Log(
                    $"companion heal: applied {amount} of a peer's healing to companion 0x{objectId:X} "
                    + $"(roster slot {rosterSlotPlusOne - 1}, owed {owed})");
            }
        }
        else if (_logged < 20)
        {
            _logged++;
            PluginHost.Log(
                $"companion heal: refused for companion 0x{objectId:X} "
                + $"(roster slot {rosterSlotPlusOne - 1}, failure {failure})");
        }
    }

    /// <summary>This instance's object id for a roster slot, from its OWN party table — the
    /// identical walk <c>PartyPresenceController.ResolveRosterObjectId</c> uses (duplicated
    /// rather than shared, the same "small partials by concern" tradeoff
    /// <see cref="PeerDamageController"/> already makes for its own copy of this walk).
    /// </summary>
    private static uint ResolveRosterObjectId(int rosterSlot)
    {
        if (rosterSlot is < 0 or >= SharedBlock.PartyRosterSlots)
        {
            return 0;
        }

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
}
