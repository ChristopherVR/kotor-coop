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

    /// <summary>THREAT_RETARGET's own per-creature threat bookkeeping — see that class's own
    /// doc comment for what it does and does not track. Only ever touched when
    /// <see cref="Odyssey.ThreatRetargetEnabled"/> is on; harmless (and empty) otherwise.</summary>
    private readonly ThreatTable _threat = new();

    /// <summary>Rule-defined tunables for the retarget decision. Not coop.ini keys of their
    /// own — see docs/research/peer-damage-ledger.md §9 for why the defaults were picked.</summary>
    private static readonly ThreatRetargetOptions ThreatOptions = ThreatRetargetOptions.Default;

    private int _threatLogged;

    /// <summary>Scratch for the attacker set: the PC plus the party roster.</summary>
    private readonly uint[] _attackers = new uint[PartyNpcSlots + 1];

    private int _countdown;
    private int _logged;

    /// <summary>Separate budget from <see cref="_logged"/>: an unresolvable id is the quiet
    /// failure, so it must not be crowded out of the log by successful applies.</summary>
    private int _unresolvedLogged;

    /// <summary>How many full engine chains have been written beside a refusal. Expensive to
    /// read and long to print, so a few is plenty — the one-line verdict carries the rest.</summary>
    private int _chainsLogged;

    /// <summary>
    /// Ids that resolved here at least once since the epoch reset, and the ids already
    /// complained about — both fixed-size, because this runs on the game thread and a
    /// dictionary that grows during a fight is a stutter nobody can explain later.
    /// </summary>
    private readonly uint[] _wasResolvable = new uint[64];

    private readonly uint[] _complainedAbout = new uint[16];

    /// <summary>
    /// True the FIRST time an id is refused. Ten lines about one id told us nothing three
    /// rounds running, while a second unresolvable id would have been the whole story.
    /// </summary>
    private bool NoteUnresolved(uint objectId)
    {
        for (int i = 0; i < _complainedAbout.Length; i++)
        {
            if (_complainedAbout[i] == objectId)
            {
                return false;
            }

            if (_complainedAbout[i] == 0)
            {
                _complainedAbout[i] = objectId;
                _unresolvedLogged++;
                return true;
            }
        }

        return false;
    }

    private void RememberResolvable(uint objectId)
    {
        for (int i = 0; i < _wasResolvable.Length; i++)
        {
            if (_wasResolvable[i] == objectId)
            {
                return;
            }

            if (_wasResolvable[i] == 0)
            {
                _wasResolvable[i] = objectId;
                return;
            }
        }
    }

    private bool WasResolvableHere(uint objectId)
    {
        foreach (uint id in _wasResolvable)
        {
            if (id == objectId)
            {
                return true;
            }
        }

        return false;
    }

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
        ForgetResolutionHistory();
        block.DamageOwedCount = 0;
        block.DamageDealtCount = 0;

        // Object ids are per module load; a threat row keyed by a creature or attacker id from
        // the old module would name a stranger in the new one.
        _threat.Clear();
        block.ThreatDebugCount = 0;
    }

    /// <summary>Drops what is known about which ids resolve here. Object ids are per module
    /// load, so this must happen wherever the ledger itself restarts.</summary>
    private void ForgetResolutionHistory()
    {
        Array.Clear(_wasResolvable);
        Array.Clear(_complainedAbout);
        _unresolvedLogged = 0;
        _chainsLogged = 0;
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
        //
        // Also empty while THIS instance is spectating a peer's story-forced solo segment
        // (docs/research/star-forge-finale.md) — belt and braces alongside
        // SoloSegmentSpectateController's own input lock: even if a WASD press somehow lands a
        // swing on the owner's enemy mirror, it must never be credited into that fight's
        // ledger. See SoloSegmentSpectateController.IsSpectating's own doc comment.
        if (!block.DamageDealtEnabled || SoloSegmentSpectateController.IsSpectating)
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

        if (_owed.AdoptEpoch(epoch))
        {
            // Both histories are keyed by object id, and a new epoch reassigns every id — so
            // "it resolved here earlier" would be a claim about a different creature.
            ForgetResolutionHistory();

            if (_logged < 20)
            {
                _logged++;
                PluginHost.Log($"combat: peer-damage ledger reset for epoch {epoch:X8}");
            }
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

        // ⚠ ATTRIBUTION (coop.ini damage_creator_attribution, docs/research/peer-damage-ledger.md
        // §8). This ledger is keyed by ENEMY, merged across every contributing peer's rise
        // before it ever reaches this instance (§3.1 "bank each peer's rise") — so there is no
        // per-row peer identity left to pick from once more than one peer is connected. Correct
        // (and unambiguous) for the two-player topology this codebase tests against: with
        // exactly one remote peer, ITS phantom is the only body that could have dealt this. A
        // 3+-peer session simply falls back to no attribution (0) rather than guess wrong.
        // Computed once per pass, not per row: it does not vary within a pass.
        uint creatorId = Odyssey.DamageCreatorAttributionEnabled ? block.SoleRemotePhantomId() : 0;

        // THREAT_RETARGET (coop.ini threat_retarget, docs/research/peer-damage-ledger.md §9).
        // Independent of damage_creator_attribution above — this only needs the peer's phantom
        // id to bank ThreatTable rows, not to pass through EffectDamage's creator slot, so it
        // is resolved on its own rather than reusing creatorId (which stays 0 whenever
        // damage_creator_attribution itself is off). Same SoleRemotePhantomId() limit as §8.6:
        // unambiguous only with exactly one remote peer connected.
        uint threatAttackerId = Odyssey.ThreatRetargetEnabled ? block.SoleRemotePhantomId() : 0;
        double threatNowSeconds = Odyssey.ThreatRetargetEnabled ? Environment.TickCount64 / 1000.0 : 0;
        int threatDebugRows = 0;

        for (int i = 0; i < count; i++)
        {
            block.GetDamageOwed(i, out uint objectId, out uint owed);
            if (objectId is 0 or Odyssey.ObjectInvalid)
            {
                continue;
            }

            // ROUND 50: the decisive-control bypass — see
            // EnemyPresenceController.LocalCombatBypassId's own doc comment. A peer's reported
            // damage against this id is never applied; the debt simply sits unspent, same as
            // an id this instance cannot resolve at all.
            if (EnemyPresenceController.LocalCombatBypassId != 0
                && objectId == EnemyPresenceController.LocalCombatBypassId)
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
            //
            // ⚠ But SAY SO. This skip used to be silent, and silence made a whole failure
            // class invisible: a peer forwards damage keyed by an id this instance has never
            // heard of, the debt sits unapplied forever, and both ends look healthy. Found
            // live on 2026-08-15 — a client's `slay` reported "sent the kill to them" and the
            // host log had not one word about it. Bounded by its own counter so a genuine
            // transition (where every id is briefly unresolvable) costs a few lines, not a
            // flood.
            nint creature = Odyssey.ResolveServerObject(objectId);
            if (creature == 0)
            {
                // ⚠ ONE LINE PER ID, and it says whether the id was EVER ours. "Does not
                // resolve" is three different findings wearing one sentence: an id from
                // another instance's space (never here), a creature that has since been
                // destroyed (was here, gone now), and a transition window (here again in a
                // moment). Only the middle one is a race, and only the first one is the bug
                // the census gate exists to stop — so the message names which it is, and the
                // full engine chain goes beside it the first few times.
                if (NoteUnresolved(objectId))
                {
                    PluginHost.Log(
                        $"combat: a peer owes {amount} damage to 0x{objectId:X}, which does not "
                        + (WasResolvableHere(objectId)
                            ? "resolve here ANY MORE - it did earlier this epoch, so the body was "
                                + "destroyed; holding the debt in case it comes back"
                            : "resolve here and never has this epoch - the peer keyed it in an id "
                                + "space this instance does not share; the debt can never land"));

                    if (_chainsLogged < 3)
                    {
                        _chainsLogged++;
                        PluginHost.Log($"combat: {Odyssey.DescribeObjectResolution(objectId)}");
                    }
                }

                continue;
            }

            // Remembered BEFORE the apply, so "it was here once" is true even if the damage
            // itself is refused: the two failures are different and must not be conflated.
            RememberResolvable(objectId);

            if (Odyssey.ApplyDamageEffect(objectId, amount, creatorId, out int failure))
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

                if (Odyssey.ThreatRetargetEnabled && threatAttackerId != 0)
                {
                    threatDebugRows = EvaluateThreat(
                        objectId, creature, threatAttackerId, amount, threatNowSeconds, threatDebugRows);
                }
            }
            else if (_logged < 20)
            {
                _logged++;
                PluginHost.Log($"combat: peer damage refused for 0x{objectId:X} (failure {failure})");
            }
        }

        // Sticky: a pass with no owed damage leaves the last populated table in place rather
        // than zeroing it, or `tools/blockpoke.ps1 threat` (an external, between-passes read)
        // would only ever see `count 0` — live 2026-09-03 03:05 it did, through a whole fight.
        if (Odyssey.ThreatRetargetEnabled && threatDebugRows > 0)
        {
            block.ThreatDebugCount = (uint)threatDebugRows;
            block.ThreatDebugGeneration += 1;
        }
    }

    /// <summary>
    /// THREAT_RETARGET (coop.ini <c>threat_retarget</c>, docs/research/peer-damage-ledger.md
    /// §9): banks this pass's applied peer damage as threat against <paramref name="creatureId"/>,
    /// and — if its current attack target clearly no longer deserves it — orders the retarget
    /// directly through <see cref="Odyssey.AttackTarget"/> rather than relying on the engine's
    /// own passive <c>+0x158</c>/<c>+0x160</c> write, which §8.10 measured live reaches no
    /// native retargeting logic for an already-engaged creature.
    ///
    /// Publishes one diagnostic row for <c>blockpoke.ps1 threat</c> whenever a top attacker is
    /// known, regardless of the decision — up to <see cref="SharedBlock.ThreatDebugCapacity"/>
    /// rows per pass. Returns the row count after this creature's own row (unchanged if this
    /// creature was skipped or the table was already full).
    /// </summary>
    private int EvaluateThreat(
        uint creatureId, nint creature, uint attackerId, int amount, double nowSeconds, int debugRows)
    {
        // Never a plot/story actor — the same exemption CombatController's own census-driven
        // mirror already grants a cutscene actor (EnemyView.Plot). Checked before anything is
        // even recorded, not just before the retarget itself, so a plot creature's row never
        // exists for blockpoke to read either.
        if (Odyssey.IsPlot(creature))
        {
            return debugRows;
        }

        _threat.AddThreat(creatureId, attackerId, amount, nowSeconds);

        // Never retarget a creature that is not hostile/in combat: 0 (no round pointer, or the
        // round has no live target) is exactly what ThreatRetargetRule.Decide reads as
        // NotInCombat below — this call always runs (never gated on it) so the diagnostic row
        // still shows an idle creature's tracked threat.
        uint currentTargetId = Odyssey.GetCurrentAttackTargetId(creature);

        if (!_threat.TryGetTopAttacker(creatureId, nowSeconds, out uint topAttackerId, out double topThreat))
        {
            return debugRows;
        }

        double currentTargetThreat = _threat.GetThreat(creatureId, currentTargetId, nowSeconds);

        if (debugRows < SharedBlock.ThreatDebugCapacity)
        {
            block.SetThreatDebugRow(
                debugRows, creatureId, currentTargetId, topAttackerId, topThreat, currentTargetThreat);
            debugRows++;
        }

        double secondsSinceLastRetarget = _threat.SecondsSinceLastRetarget(creatureId, nowSeconds);
        ThreatRetargetDecision decision = ThreatRetargetRule.Decide(
            currentTargetId, topAttackerId, topThreat, currentTargetThreat, secondsSinceLastRetarget,
            ThreatOptions);

        if (decision != ThreatRetargetDecision.Retarget)
        {
            // A refusal that had enough threat to matter is worth one line: the live question
            // "why did 48 points on 0x26 never retarget it?" (2026-09-03 03:05) is only
            // answerable from the decision itself (AlreadyTarget: the creature was already on
            // the peer). Below-floor refusals are the ordinary case and stay silent.
            if (topThreat >= ThreatOptions.MinThreatToRetarget && _threatLogged < 20)
            {
                _threatLogged++;
                PluginHost.Log(
                    $"threat: 0x{creatureId:X} not retargeted ({decision}) - current 0x{currentTargetId:X} "
                    + $"threat {currentTargetThreat:F0}, top 0x{topAttackerId:X} threat {topThreat:F0}, "
                    + $"{(double.IsPositiveInfinity(secondsSinceLastRetarget) ? "never retargeted" : $"{secondsSinceLastRetarget:F1}s since last")}");
            }

            return debugRows;
        }

        // Never onto a dead or unresolved target: the top attacker's own phantom body has to
        // actually be here (and not downed) to be worth swinging at.
        nint topAttackerBody = Odyssey.ResolveServerObject(topAttackerId);
        if (topAttackerBody == 0 || Odyssey.IsDowned(topAttackerBody))
        {
            return debugRows;
        }

        // Armed before the call, win or lose: a refusal that repeats every pass (a structural
        // failure, not a transient one) must not hammer AttackTarget at the owed-loop's own
        // cadence for the whole cooldown window — the next real attempt is exactly as informed
        // once the cooldown clears again.
        _threat.MarkRetargeted(creatureId, nowSeconds);

        if (Odyssey.AttackTarget(creatureId, topAttackerId, out int failure))
        {
            if (_threatLogged < 20)
            {
                _threatLogged++;
                PluginHost.Log(
                    $"threat: 0x{creatureId:X} retargeted 0x{currentTargetId:X} -> 0x{topAttackerId:X} "
                    + $"(threat {topThreat:F0} vs {currentTargetThreat:F0})");
            }
        }
        else if (_threatLogged < 20)
        {
            _threatLogged++;
            PluginHost.Log(
                $"threat: 0x{creatureId:X} retarget refused 0x{currentTargetId:X} -> 0x{topAttackerId:X} "
                + $"(failure {failure})");
        }

        return debugRows;
    }
}
