namespace KotorCoop.Plugin;

// hurt/down: the two dev-only verbs that make the downed/revive/game-over pipeline
// (DeathController, DeathPump, Odyssey.Downed.cs) live-testable without waiting for a real
// fight to drop a party member below 1 hp. See docs/research/death-handling.md's 2026-09-02
// section for the full design and the live-verification recipe.
internal sealed unsafe partial class CheatController
{
    /// <summary>Enough damage to certainly cross zero from any hit-point total <c>down</c>
    /// might find a party member at — the same value <see cref="Odyssey.KillCreature"/> uses,
    /// clamped to 10000 by the VM handler either way.</summary>
    private const int DownLethalDamage = 9999;

    /// <summary>
    /// Frames <see cref="ApplyPendingMinOneHpRestore"/> waits before re-arming the pin.
    /// Live-measured 2026-09-02 (death-handling.md §6): the death decision has visibly run by
    /// the NEXT frame after <see cref="Odyssey.ApplyDamageEffect"/> returns — on a host the
    /// engine's death auto-swap had already moved <c>GetPlayerCreature</c> to the companion
    /// 47 ms after the hit — so one frame is enough and two is margin. Note hp does NOT
    /// "settle" to anything observable: OnApplyDeath's -11 clamp only RAISES a value above it
    /// (death-handling.md §1), so a -9987 body reads -9987 until the auto-revive stands it up.
    /// </summary>
    private const int MinOneHpRestoreDelayFrames = 2;

    /// <summary>
    /// Real damage through the exact pipeline a live hit uses — <see cref="Odyssey.ApplyDamageEffect"/>,
    /// the same VM-dispatched <c>EffectDamage</c>/<c>ApplyEffectToObject</c> call
    /// <see cref="Slay"/>'s <c>KillHere</c> branch and <see cref="Odyssey.KillCreature"/> both
    /// use, which routes through <c>CSWSCreature::DoDamage</c>/<c>CSWSObject::DoDamage</c>:
    /// difficulty scaling, temp-HP (shield) absorption and BOTH of the engine's own 1-hp
    /// floors (a <c>min1hp</c> pin and the difficulty <c>MinHP1</c> rule) apply exactly as
    /// they would to a real swing. This is deliberate — <c>hurt</c> is meant to prove the
    /// pipeline reacts to a genuine hit, not to bypass anything <see cref="Down"/> bypasses.
    /// </summary>
    private void Hurt(nint player, uint playerId)
    {
        int amount = block.CheatAmount;
        if (amount <= 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "hurt needs a positive damage amount");
            return;
        }

        if (!TryResolvePartyOrSelf(block.CheatTarget, player, playerId, out nint creature, out uint creatureId, out string who, out string refusal))
        {
            Answer(SharedBlock.CheatStatusNoTarget, refusal);
            return;
        }

        short before = Odyssey.GetCurrentHitPoints(creature);

        if (!Odyssey.ApplyDamageEffect(creatureId, amount, out int failure))
        {
            Answer(SharedBlock.CheatStatusEngineRefused, $"hurt refused for {who} (failure {failure})");
            return;
        }

        short after = Odyssey.GetCurrentHitPoints(creature);
        string downedNote = Odyssey.IsDowned(creature) ? " (downed)" : string.Empty;

        Answer(SharedBlock.CheatStatusApplied, $"{who} hit for {amount}: {before} -> {after} hp{downedNote}");
    }

    /// <summary>A min1hp restore owed after a <see cref="Down"/> hit that bypassed it — see
    /// that method's own doc comment for why an immediate same-call restore is unsafe.
    /// 0 = nothing pending (object ids here are never 0 for a resolved creature).</summary>
    private uint _pendingMinOneHpRestoreTarget;

    /// <summary>Frames left before <see cref="ApplyPendingMinOneHpRestore"/> re-arms the pin.</summary>
    private int _pendingMinOneHpRestoreFramesLeft;

    /// <summary>
    /// Forces the SAME genuine temp-death state a real lethal hit produces, by bypassing the
    /// one thing that would stop it: a <c>min1hp</c> pin (a module's own, or <c>god</c>'s)
    /// converts a lethal Death effect into "set hp 1" (death-handling.md §1's OnApplyDeath
    /// writes), so the pin comes off first, exactly the order <see cref="Slay"/>'s
    /// <c>KillHere</c> branch already uses before a real kill — and goes back on afterward,
    /// because a party member (unlike a slain enemy) keeps playing, and a module's own safety
    /// net should still be there once they are revived.
    ///
    /// A REAL party member never truly dies from this: <c>CSWSCreature::GetDead</c>
    /// hard-returns 0 for a client-party member (death-handling.md §1), so the lethal damage
    /// this applies can only ever produce temp-death, which <see cref="DeathController"/>/
    /// <c>DeathPump</c>/the engine's own <c>TempDeathUpdate</c> auto-revive already
    /// know how to handle — that is the entire point of this verb existing.
    ///
    /// ⚠ THE RESTORE IS DEFERRED, NOT IMMEDIATE. death-handling.md §6.1 flagged an immediate
    /// same-call restore as the first suspect if a live round ever saw `down` land as "set hp
    /// 1" instead of a genuine downed state on a creature that DID have min1hp set; the
    /// 2026-09-02 live round (death-handling.md §6) measured the death decision landing
    /// within the frame AFTER <see cref="Odyssey.ApplyDamageEffect"/> returns, not inside it,
    /// so <see cref="ApplyPendingMinOneHpRestore"/> re-arms the pin
    /// <see cref="MinOneHpRestoreDelayFrames"/> frames later from OnFrame. The same round also
    /// found that touching the creature again from THIS call (an IsDowned/GetMinOneHitPoint
    /// read straight after the apply) hung the host — OnApplyDeath's choreography is still in
    /// flight — so nothing here reads the creature back after the hit.
    /// </summary>
    private void Down(nint player, uint playerId)
    {
        if (!TryResolvePartyOrSelf(block.CheatTarget, player, playerId, out nint creature, out uint creatureId, out string who, out string refusal))
        {
            Answer(SharedBlock.CheatStatusNoTarget, refusal);
            return;
        }

        if (Odyssey.IsDowned(creature))
        {
            Answer(SharedBlock.CheatStatusApplied, $"{who} is already down");
            return;
        }

        bool hadMinOneHp = Odyssey.GetMinOneHitPoint(creature);
        if (hadMinOneHp)
        {
            Odyssey.SetMinOneHitPoint(creature, false);
        }

        bool applied = Odyssey.ApplyDamageEffect(creatureId, DownLethalDamage, out int failure);

        // No creature read-back from here on — see the class doc comment's hang note.
        if (!applied)
        {
            // The hit never landed: nothing is racing a death decision that will never fire,
            // so restore right away rather than leave a module's (or god mode's) floor
            // quietly disarmed for even a tick.
            if (hadMinOneHp)
            {
                Odyssey.SetMinOneHitPoint(creature, true);
            }

            Answer(SharedBlock.CheatStatusEngineRefused, $"down refused for {who} (failure {failure})");
            return;
        }

        string floorNote;
        if (hadMinOneHp)
        {
            _pendingMinOneHpRestoreTarget = creatureId;
            _pendingMinOneHpRestoreFramesLeft = MinOneHpRestoreDelayFrames;
            floorNote = "min1hp floor bypassed, restored a frame later";
        }
        else
        {
            floorNote = "no min1hp floor was set";
        }

        Answer(SharedBlock.CheatStatusApplied, $"{who} downed ({floorNote})");
    }

    /// <summary>The min1hp restore <see cref="Down"/> deferred — see that method's own doc
    /// comment for why it is not done in the same call. Re-arms the pin
    /// <see cref="MinOneHpRestoreDelayFrames"/> frames after the hit, by id, so a module
    /// change in between simply drops it (<see cref="Invalidate"/> clears the target too).</summary>
    private void ApplyPendingMinOneHpRestore()
    {
        if (_pendingMinOneHpRestoreTarget == 0)
        {
            return;
        }

        if (--_pendingMinOneHpRestoreFramesLeft > 0)
        {
            return;
        }

        uint target = _pendingMinOneHpRestoreTarget;
        _pendingMinOneHpRestoreTarget = 0;

        nint creature = Odyssey.ResolveServerObject(target);
        if (creature == 0)
        {
            return; // the world changed under it; nothing left to restore the pin on
        }

        Odyssey.SetMinOneHitPoint(creature, true);
        PluginHost.Log($"cheat: min1hp floor restored on 0x{target:X} (hp {Odyssey.GetCurrentHitPoints(creature)})");
    }

    /// <summary>
    /// Resolves a <c>hurt</c>/<c>down</c> target: the local player
    /// (<see cref="SharedBlock.CheatTargetLocalPlayer"/>, the default when no slot was typed)
    /// or a live party-roster companion (slot 0-8) — the only two kinds of body the engine's
    /// temp-death machinery ever applies to. An id-addressed enemy has no "downed" state to
    /// reach (it just dies), so this deliberately never resolves one — unlike
    /// <see cref="TryResolveTarget"/>, which <see cref="Slay"/> uses.
    /// </summary>
    private static bool TryResolvePartyOrSelf(
        uint target, nint player, uint playerId,
        out nint creature, out uint creatureId, out string who, out string refusal)
    {
        if (target == SharedBlock.CheatTargetLocalPlayer)
        {
            creature = player;
            creatureId = playerId;
            who = "you";
            refusal = string.Empty;
            return true;
        }

        if (target > 8)
        {
            creature = 0;
            creatureId = 0;
            who = string.Empty;
            refusal = $"target must be yourself or a party-roster slot 0-8 (got {target})";
            return false;
        }

        int slot = (int)target;
        uint objectId = Odyssey.GetPartyRosterObjectId(slot);
        if (objectId is 0 or Odyssey.ObjectInvalid)
        {
            creature = 0;
            creatureId = 0;
            who = string.Empty;
            refusal = $"no party-roster companion in slot {slot}";
            return false;
        }

        nint resolved = Odyssey.ResolveServerObject(objectId);
        if (resolved == 0)
        {
            creature = 0;
            creatureId = 0;
            who = string.Empty;
            refusal = $"party-roster slot {slot} (0x{objectId:X}) does not resolve here";
            return false;
        }

        creature = resolved;
        creatureId = objectId;
        who = $"party slot {slot} (0x{objectId:X})";
        refusal = string.Empty;
        return true;
    }
}
