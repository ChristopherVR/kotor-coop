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

    /// <summary>
    /// A module change invalidates nothing about a player's own health, so the applied
    /// total is deliberately NOT reset here — resetting it would replay the session's
    /// whole damage history on the next tick. It is only reset when the agent restarts the
    /// count (a fresh session publishes zero).
    /// </summary>
    public void Invalidate()
    {
        _countdown = 0;
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
        }
        else if (_logged < 20)
        {
            _logged++;
            PluginHost.Log($"combat: incoming damage refused (failure {failure})");
        }
    }
}
