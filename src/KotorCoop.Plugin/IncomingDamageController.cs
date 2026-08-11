namespace KotorCoop.Plugin;

/// <summary>
/// Applies damage this player took in someone else's game — hits that landed on their
/// phantom over there — to their real character here.
///
/// This is the half that makes enemies genuinely dangerous in co-op. Without it a client
/// could be surrounded and swung at on the host's screen and never lose a hit point.
///
/// The wire carries a CUMULATIVE total rather than individual hits, so this applies the
/// DIFFERENCE between the total and what it has already applied: a dropped packet costs a
/// tick of staleness instead of a lost hit, and a duplicate applies nothing. The applied
/// figure is published back so a re-attaching agent resumes rather than replaying.
///
/// Damage goes through the engine's own instant EffectDamage — the receiving side then
/// runs its full pipeline (resistances, difficulty scaling, floating numbers, and the
/// engine's own party temp-death at zero), which is exactly what a hand-written health
/// poke would fail to do.
///
/// ⚠ Going down is not always available, and that is not always a bug. KOTOR's own modules
/// pin the PC with min-1-hp for the length of a scene — the Endar Spire command deck sets
/// it from its area OnEnter and only clears it in the starboard section — and inside such a
/// scene damage floors at exactly 1 hit point with the downed flag never set, for local hits
/// and forwarded hits alike. Rather than leave that looking like lost damage, this measures
/// the flags and publishes WHICH floor is in the way.
/// See docs/research/min-one-hitpoint.md.
/// </summary>
internal sealed class IncomingDamageController(SharedBlockView block)
{
    /// <summary>Frames between passes. Damage arrives at network rate; there is nothing to
    /// gain from testing it hundreds of times a second.</summary>
    private const int IntervalFrames = 10;

    /// <summary>The same, under local_combat — see the window note at the call site.</summary>
    private const int LocalCombatIntervalFrames = 3;

    /// <summary>A single application is capped so that a desync, a restart with a stale
    /// total, or a garbage read can never one-shot a player. Anything larger arrives as
    /// several applications over successive passes, which is also more readable in play.</summary>
    private const int MaxPerApplication = 200;

    private readonly bool _enabled = PluginConfig.ReadBool("phantom_targetable", true);

    private int _countdown;

    /// <summary>Whether we are currently holding damage back for a conversation, so the
    /// hold is announced once rather than every pass.</summary>
    private bool _heldForDialogue;
    private int _logged;

    /// <summary>Running total absorbed under local_combat, and when it was last announced.
    /// A silent absorb is indistinguishable from a broken channel, so it is never silent.</summary>
    private uint _absorbed;

    private DateTime _lastAbsorbLog = DateTime.MinValue;

    private static readonly TimeSpan AbsorbLogInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How much absorbed damage counts as a claim worth checking. Big enough that one
    /// glancing blow in a corridor does not trip it, small enough that a real fight does
    /// within a few seconds. A player at full health in the Endar Spire has ~12 hit points,
    /// so this is several rounds of nothing happening.
    /// </summary>
    private const uint AbsorbProofThreshold = 40;

    private uint _absorbedSinceProof;
    private short _lastHealthSeen = short.MaxValue;
    private bool _localDamageSeen;
    private bool _watchdogTripped;

    private DownBlockReason _reason = DownBlockReason.None;
    private uint _reasonSerial;

    /// <summary>
    /// A module change invalidates nothing about a player's own health, so the applied
    /// total is deliberately NOT reset here — resetting it would replay the session's
    /// whole damage history on the next tick. It is only reset when the agent restarts the
    /// count (a fresh session publishes zero).
    ///
    /// The floor, on the other hand, is exactly what a module change alters: it is dropped
    /// so the new module re-announces its own rule rather than inheriting the old one's.
    /// </summary>
    public void Invalidate()
    {
        _countdown = 0;

        if (_reason != DownBlockReason.None)
        {
            _reason = DownBlockReason.None;
            block.DownBlockReason = (uint)DownBlockReason.None;
            block.DownBlockSerial = ++_reasonSerial;
        }
    }

    public void OnFrame()
    {
        if (!_enabled || block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        bool localCombat = EnemyPresenceController.LocalCombat && block.DamageDealtEnabled;

        // ⚠ THE WINDOW IS THE ATTRIBUTION ERROR. The wire carries ONE cumulative total for
        // this player summed across every attacker, plus the id of the LAST attacker only, so
        // each pass can only bill its whole delta to one id. A short window is the only lever
        // that narrows that without a protocol change: three frames is ~50 ms at 60 fps,
        // comfortably inside the ~150 ms combat pump, so a delta rarely mixes an attributable
        // attacker with a mine.
        _countdown = localCombat ? LocalCombatIntervalFrames : IntervalFrames;

        uint owed = block.IncomingDamage;
        uint applied = block.IncomingDamageApplied;

        // A fresh session (or a peer that restarted its count) publishes a total lower than
        // ours: adopt it rather than treating the difference as damage. ⚠ Above the absorb, so
        // a restart can never be booked as absorbed damage that never existed.
        if (owed < applied)
        {
            block.IncomingDamageApplied = owed;
            return;
        }

        if (owed == applied)
        {
            return;
        }

        // ⚠ DAMAGE AUTHORITY, AND IT MOVES WITH EnemyPresenceController.LocalCombat.
        //
        // With local combat on, this instance's own mirrored enemies swing at this player
        // here, and the owner ALSO forwards what its copies did to this player's phantom over
        // there — the same swings, counted twice. So exactly one instance may apply damage to
        // a given player.
        //
        // ⚠ BUT THE ABSORB MUST BE ATTRIBUTABLE, NOT BLANKET. The first version stood down for
        // the whole channel, and several damage classes produce a forwarded total with NO
        // local analogue at all: enemies shed by the 32-slot census cap or by distance,
        // creatures the resolver skipped, phantom-id collisions, mines and traps and scripted
        // EffectDamage, a fight across the module where no driven body is anywhere near this
        // player, and the conversation hold. Every one of those was silently deleted.
        //
        // The channel already carries the attacker, in the OWNER's id space, which is the same
        // space the enemy census uses. EnemyPresenceController answers the only question that
        // matters: is that attacker a body I am driving, right now, close enough to this
        // player to be hitting them here. Yes -> absorb; anything else -> fall through and
        // APPLY.
        //
        // ⚠ ROLE GATE. `local_combat` is read from this install's own coop.ini and never
        // crosses the wire. DamageDealtEnabled is set only by an instance that is actually
        // mirroring — the same test PortraitController uses for "am I the client here".
        // ⚠ THE INVULNERABILITY WATCHDOG, AND IT IS WHY THIS FEATURE CAN BE ON AT ALL.
        //
        // Everything above assumes the premise nobody has ever observed: that a mirrored enemy
        // whose POSITION IS OVERWRITTEN FROM THE OWNER EVERY PASS can still complete a combat
        // round against the local PC. If it cannot, every test above still passes — the body
        // is driven, it is in range, it is the last attacker — and this player takes no damage
        // from anyone, for the whole session, with only a log line to say so.
        //
        // So the absorb is required to SHOW ITS WORK. Absorbing is a claim that our own copies
        // are dealing this damage locally; if that were true, the player's hit points would be
        // falling. They are sampled here, and if a real amount has been absorbed across a
        // window without the health moving DOWN even once, the claim is false and the channel
        // reverts to applying. Self-correcting, loud, and it fails towards a player who can be
        // hurt rather than one who cannot.
        nint watched = Odyssey.GetPlayerCreaturePointer();
        short healthNow = watched == 0 ? _lastHealthSeen : Odyssey.GetCurrentHitPoints(watched);
        if (healthNow < _lastHealthSeen)
        {
            _localDamageSeen = true;   // something hurt us locally; the claim is good
        }

        _lastHealthSeen = healthNow;

        if (_absorbedSinceProof >= AbsorbProofThreshold && !_localDamageSeen)
        {
            if (!_watchdogTripped)
            {
                _watchdogTripped = true;
                PluginHost.Log(
                    $"combat: ⚠ local_combat WATCHDOG TRIPPED — absorbed {_absorbedSinceProof} damage "
                    + "on the claim that our own mirrored enemies were dealing it here, and this "
                    + "player's hit points never fell once. They are not. Applying forwarded damage "
                    + "again so this player is not invulnerable; combat state on this instance will "
                    + "stay wrong until the local copies can actually land a hit.");
            }
        }
        else if (_localDamageSeen)
        {
            // Proof arrived: reset the window and re-arm rather than latching either way.
            _absorbedSinceProof = 0;
            _localDamageSeen = false;
            _watchdogTripped = false;
        }

        if (localCombat && !_watchdogTripped
            && EnemyPresenceController.IsLocallyEngaged(block.IncomingDamageAttacker))
        {
            _absorbedSinceProof += owed - applied;
            _absorbed += owed - applied;
            block.IncomingDamageApplied = owed;

            // ⚠ ON A HEARTBEAT, NOT A COUNTER, and it names the two things that decide whether
            // this is working. If the local enemies do not in fact engage, this branch is
            // total invulnerability — and "absorbing while the engine says we are not in
            // combat" is exactly that smell. The engine's in-combat field is DERIVED from
            // being fought rather than settable (a written flag is reverted in 0-2 ms, see
            // CombatStateController), so it is honest evidence rather than our own belief
            // echoed back.
            if (DateTime.UtcNow - _lastAbsorbLog >= AbsorbLogInterval)
            {
                _lastAbsorbLog = DateTime.UtcNow;
                PluginHost.Log(
                    $"combat: absorbed {_absorbed} forwarded damage in total, attributed to mirrored "
                    + $"enemies we are driving (last attacker 0x{block.IncomingDamageAttacker:X}); "
                    + $"this engine says in-combat={Odyssey.IsPlayerInCombat} — if that is false "
                    + "while this line keeps printing, our copies are NOT dealing the damage and "
                    + "this player is invulnerable");
            }

            return;
        }

        // (The restart-adoption and no-change tests now run ABOVE the absorb, so a restart
        // cannot be booked as absorbed damage that never happened.)

        // Not while a conversation owns the screen: the dialogue system is mid-scene and
        // the enemy channel freezes for the same reason. The debt is not lost — it is
        // still owed on the next pass.
        //
        // ⚠ AND IT SAYS SO, because a silent hold here is indistinguishable from the
        // channel being broken. Measured: after a module transition the client's
        // conversation-active flag (gui+0xB4) was still set with no conversation on screen,
        // and this returned every pass forever — forwarded damage read `owed 20 applied 0`
        // and the player was quietly immune. Twenty minutes went into a bug that one log
        // line would have named. The stale flag itself is a separate defect; this only
        // makes the hold visible.
        if (Odyssey.IsConversationActive)
        {
            if (!_heldForDialogue)
            {
                _heldForDialogue = true;
                PluginHost.Log(
                    $"combat: holding {owed - applied} forwarded damage — a conversation is on screen");
            }

            return;
        }

        if (_heldForDialogue)
        {
            _heldForDialogue = false;
            PluginHost.Log("combat: conversation over; applying the forwarded damage held for it");
        }

        uint playerId = Odyssey.GetPlayerObjectId();
        nint player = playerId == 0 ? 0 : Odyssey.ResolveServerObject(playerId);
        if (player == 0)
        {
            return;
        }

        // A downed player takes no further damage: the engine's temp-death owns the body,
        // and piling on would fight the revive path.
        if (Odyssey.IsDowned(player))
        {
            block.IncomingDamageApplied = owed;
            return;
        }

        uint outstanding = owed - applied;
        int amount = (int)Math.Min(outstanding, MaxPerApplication);

        // Sampled before the effect purely so the "cannot be downed" log can quote what the
        // blow actually did; the verdict itself comes off the flags, not this number.
        short before = Odyssey.GetCurrentHitPoints(player);

        if (Odyssey.ApplyDamageEffect(playerId, amount, out int failure))
        {
            block.IncomingDamageApplied = applied + (uint)amount;

            if (_logged < 20)
            {
                _logged++;
                PluginHost.Log(
                    $"combat: took {amount} damage from a hit on our phantom "
                    + $"(attacker 0x{block.IncomingDamageAttacker:X})");
            }

            ReportFloor(player, before, amount);
        }
        else if (_logged < 20)
        {
            _logged++;
            PluginHost.Log($"combat: incoming damage refused (failure {failure})");
        }
    }

    /// <summary>
    /// Publishes which engine floor, if any, will stop this player from going down.
    ///
    /// It is read straight off the flags rather than inferred from the hit-point movement,
    /// because the flags are the ground truth and the arithmetic is not: the pipeline scales
    /// the figure by difficulty and eats part of it with temporary hit points, so "46 damage
    /// and they are on 1" is consistent with both a floor and an ordinary survivable hit.
    /// The measured before/after only decides how loud the log line is.
    ///
    /// Published as a level and logged on the EDGE — the Endar Spire pins the PC for a whole
    /// deck, and one line per scene is the useful amount.
    /// </summary>
    private void ReportFloor(nint player, short before, int amount)
    {
        DownBlockReason reason = DownBlock.Classify(
            Odyssey.IsPlot(player),
            Odyssey.IsDifficultyMinOneHitPointOn(),
            Odyssey.GetMinOneHitPoint(player));

        if (reason == _reason)
        {
            return;
        }

        _reason = reason;
        block.DownBlockReason = (uint)reason;
        block.DownBlockSerial = ++_reasonSerial;

        if (reason == DownBlockReason.None)
        {
            PluginHost.Log("combat: forwarded damage can down this player again");
            return;
        }

        // "The blow covered every hit point they had and they are still up" is the case
        // worth spelling out; before any resistance this is a lower bound, so it under-
        // reports rather than crying wolf.
        short after = Odyssey.GetCurrentHitPoints(player);
        string evidence = before > 0 && amount >= before && after > 0
            ? $" (took {amount} on {before} hp and is on {after})"
            : string.Empty;

        PluginHost.Log($"combat: this player cannot be downed{evidence} — " + DownBlock.Describe(reason));
    }
}
