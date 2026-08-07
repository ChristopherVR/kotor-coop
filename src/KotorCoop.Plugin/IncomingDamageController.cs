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

    /// <summary>A single application is capped so that a desync, a restart with a stale
    /// total, or a garbage read can never one-shot a player. Anything larger arrives as
    /// several applications over successive passes, which is also more readable in play.</summary>
    private const int MaxPerApplication = 200;

    private readonly bool _enabled = PluginConfig.ReadBool("phantom_targetable", true);

    private int _countdown;
    private int _logged;

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

        _countdown = IntervalFrames;

        uint owed = block.IncomingDamage;
        uint applied = block.IncomingDamageApplied;

        // A fresh session (or a peer that restarted its count) publishes a total lower
        // than ours: adopt it rather than treating the difference as damage.
        if (owed < applied)
        {
            block.IncomingDamageApplied = owed;
            return;
        }

        if (owed == applied)
        {
            return;
        }

        // Not while a conversation owns the screen: the dialogue system is mid-scene and
        // the enemy channel freezes for the same reason. The debt is not lost — it is
        // still owed on the next pass.
        if (Odyssey.IsConversationActive)
        {
            return;
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
