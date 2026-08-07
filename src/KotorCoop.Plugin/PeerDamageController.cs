namespace KotorCoop.Plugin;

/// <summary>
/// Both ends of the damage ledger, on the game's own thread.
///
/// Peer end: keeps the damage hook told whose hits count as ours, and publishes the running
/// per-enemy total it accumulates so the agent can forward it to whoever owns the fight.
///
/// Owner end: reads the total every peer says it has dealt to each of our enemies, and
/// turns the increase into REAL DAMAGE through the engine's own instant EffectDamage.
///
/// ⚠ WHY THE ENGINE AND NOT A HEALTH POKE, and why re-running the pipeline is not
/// double-counting. The number on the wire is the effect's raw amount read at the entry to
/// <c>OnApplyDamage</c> — BEFORE the target's resistance, immunity and reduction virtuals
/// run (verified: they execute at 0x004DFC35/0x004DFC4F/0x004DFC73 and overwrite the total
/// the engine then passes to DoDamage). So it has had no resistance applied, and applying
/// it here runs the pipeline exactly once, not twice.
///
/// That works because the target is the SAME CREATURE on both instances — same blueprint,
/// same equipment, same id, kept in step by the health mirror — so "this side's pipeline"
/// is the pipeline the dealer's copy already ran. It is emphatically NOT the phantom case,
/// where the two bodies are different creatures (a bare spawn versus a real character) and
/// health has to be replicated absolutely.
///
/// And the difficulty slider does not break the symmetry: <c>CSWSCreature::DoDamage</c>
/// applies difficultyopt.2da only to party members and the PC (0x004F3876 branches straight
/// past it for anything else), and the remaining non-party multiplier at [0x007A1B30] is 1
/// in the shipped image with no code that writes it.
///
/// The reward for going through the engine is the kill: an enemy whose accumulated damage
/// exceeds its health simply dies here, with the corpse, the loot, the XP and the OnDeath
/// script the engine runs for any other death — no separate "it is dead" message to trust,
/// and no body left standing at zero hit points.
/// </summary>
internal sealed unsafe class PeerDamageController(SharedBlockView block)
{
    /// <summary>Frames between passes. Damage arrives at network rate; there is nothing to
    /// gain from testing it hundreds of times a second, and every pass that finds work does
    /// an engine call.</summary>
    private const int IntervalFrames = 10;

    // The APP_MANAGER -> server -> internal walk, same chain PartyPresenceController uses.
    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppServerOffset = 0x08;
    private const int InternalOffset = 0x04;

    /// <summary>CServerExoAppInternal + this = the embedded CSWPartyTable.</summary>
    private const int PartyTableOffset = 0x1B770;

    /// <summary>CSWPartyTable + this = int[9] spawned object id per roster slot.</summary>
    private const int PartyObjectIds = 0x0C;

    private const int PartyNpcSlots = 9;

    private readonly DamageOwedLedger _owed = new(SharedBlock.DamageLedgerCapacity);

    /// <summary>Scratch for the attacker set: the PC plus the party roster.</summary>
    private readonly uint[] _attackers = new uint[PartyNpcSlots + 1];

    private int _countdown;
    private int _logged;

    /// <summary>
    /// A module change reassigns every object id, so both halves start again — and they must
    /// start again TOGETHER, which is what the agent's epoch is for. This clears the local
    /// half; the epoch check in <see cref="ApplyOwed"/> covers the case where the agent got
    /// there first.
    /// </summary>
    public void Invalidate()
    {
        _countdown = 0;
        _owed.Clear();
        block.DamageOwedCount = 0;
        block.DamageDealtCount = 0;
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

        RefreshAttackers();
        PublishDealt();
        ApplyOwed();
    }

    /// <summary>
    /// Tells the hook whose damage is ours. Cheap and re-done every pass rather than cached:
    /// companions join, leave and are replaced, and a stale id here would credit us with a
    /// stranger's hits (or, worse, miss our own).
    /// </summary>
    private void RefreshAttackers()
    {
        int count = 0;

        // An empty set makes the hook's dealt path a single comparison and stop, which is
        // how the fight's OWNER opts out: its own damage is already authoritative, nobody
        // will ever read its ledger, and this sits inside every damage event in the game.
        if (!block.DamageDealtEnabled)
        {
            PhantomHitHook.WatchAttackers([]);
            return;
        }

        uint player = Odyssey.GetPlayerObjectId();
        if (player != 0)
        {
            _attackers[count++] = player;
        }

        nint serverInternal = ServerInternal();
        if (serverInternal != 0)
        {
            for (int slot = 0; slot < PartyNpcSlots; slot++)
            {
                uint id = *(uint*)(serverInternal + PartyTableOffset + PartyObjectIds + (slot * 4));
                if (id is 0 or Odyssey.ObjectInvalid || id == player)
                {
                    continue;
                }

                // A roster slot holds a stale id for a companion that is not spawned. Only
                // an id that still resolves to a live object is worth crediting — a stale
                // one could collide with a creature that now holds that number.
                if (Odyssey.ResolveServerObject(id) != 0)
                {
                    _attackers[count++] = id;
                }
            }
        }

        PhantomHitHook.WatchAttackers(_attackers.AsSpan(0, count));
    }

    private static nint ServerInternal()
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

        return *(nint*)(server + InternalOffset);
    }

    /// <summary>Hands the hook's ledger to the agent. Cumulative totals, so a pass the agent
    /// misses costs nothing.</summary>
    private void PublishDealt()
    {
        DamageDealtTable dealt = PhantomHitHook.Dealt;

        int published = 0;
        for (int i = 0; i < dealt.Capacity; i++)
        {
            uint id = dealt.IdAt(i);
            if (id == 0)
            {
                continue;
            }

            block.SetDamageDealt(published++, id, dealt.TotalAt(i));
        }

        // Blank the tail before moving the count down, so the agent never reads a row that
        // a shorter list has left behind.
        for (int i = published; i < dealt.Capacity; i++)
        {
            block.SetDamageDealt(i, 0, 0);
        }

        block.DamageDealtGeneration = dealt.Generation;
        block.DamageDealtDropped = dealt.Dropped;
        block.DamageDealtCount = (uint)published;
    }

    /// <summary>Turns what the peers have dealt into damage our own engine delivers.</summary>
    private void ApplyOwed()
    {
        uint count = Math.Min(block.DamageOwedCount, (uint)SharedBlock.DamageLedgerCapacity);
        uint epoch = block.DamageOwedEpoch;

        if (_owed.AdoptEpoch(epoch) && _logged < 20)
        {
            _logged++;
            PluginHost.Log($"combat: peer-damage ledger reset for epoch {epoch:X8}");
        }

        if (count == 0)
        {
            return;
        }

        // Not while a conversation owns the screen. The dialogue system wipes every
        // hostile's action queue each tick in the talking instance, so a death landing now
        // would run its scripts into a scene that is mid-flight; the enemy channel freezes
        // for the same reason and the debt is still owed on the next pass.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            block.GetDamageOwed(i, out uint objectId, out uint owed);
            if (objectId is 0 or Odyssey.ObjectInvalid)
            {
                continue;
            }

            int amount = _owed.Pending(objectId, owed);
            if (amount <= 0)
            {
                continue;
            }

            // Resolve first: an id we cannot find is a creature that is not here (shed on a
            // transition, or already destroyed after its death). Leaving the debt unapplied
            // is right — it lands when the creature does, and never on a stranger.
            if (Odyssey.ResolveServerObject(objectId) == 0)
            {
                continue;
            }

            if (Odyssey.ApplyDamageEffect(objectId, amount, out int failure))
            {
                _owed.Commit(objectId, amount);
                block.DamageOwedApplied += 1;

                if (_logged < 20)
                {
                    _logged++;
                    PluginHost.Log(
                        $"combat: applied {amount} of a peer's damage to 0x{objectId:X} "
                        + $"(owed {owed})");
                }
            }
            else if (_logged < 20)
            {
                _logged++;
                PluginHost.Log($"combat: peer damage refused for 0x{objectId:X} (failure {failure})");
            }
        }
    }
}
