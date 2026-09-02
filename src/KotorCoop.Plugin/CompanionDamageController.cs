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
    /// comment for why the arithmetic itself needs no change to serve a second channel.</summary>
    private readonly DamageOwedLedger _owed = new(SharedBlock.CompanionDamageOwedCapacity);

    private uint _generation = 1;

    private int _countdown;
    private int _logged;
    private int _unresolvedLogged;

    public void Invalidate()
    {
        _countdown = 0;
        _generation++;
        _owed.Clear();
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
            PhantomHitHook.ReadCompanionSlot(i, out uint rosterSlotPlusOne, out uint cumulative, out uint serial);
            if (rosterSlotPlusOne == 0 || cumulative == 0)
            {
                continue;
            }

            block.SetCompanionDamageTaken(published++, rosterSlotPlusOne, cumulative, serial);
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
            block.GetCompanionDamageOwed(i, out uint rosterSlotPlusOne, out uint owed);
            if (rosterSlotPlusOne == 0 || rosterSlotPlusOne > SharedBlock.PartyRosterSlots)
            {
                continue;
            }

            int amount = _owed.Pending(rosterSlotPlusOne, owed);
            if (amount <= 0)
            {
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
                _owed.Commit(rosterSlotPlusOne, amount);
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
