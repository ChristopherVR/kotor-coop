using System.Text;

namespace KotorCoop.Plugin;

// Player-affecting verbs: God, TopUp, StandUp (+ its deferred hit-point top-up), Slay, and
// Resolve (the object-id diagnostic), plus Slay's target-resolution helper.
internal sealed unsafe partial class CheatController
{
    /// <summary>Hit points to stand a player up with when the request names none — the same
    /// number <see cref="DeathController"/> uses for a peer rescue, for the same reason: enough
    /// to act, not enough to hide the fight that put them down.</summary>
    private const int DefaultStandUpHitPoints = 10;

    /// <summary>
    /// A top-up owed to a creature revived on an EARLIER tick. The Resurrection effect can
    /// run deferred, and hit points written while the target still reads dead are dropped by
    /// the engine's own "heal cannot touch the dead" rule — the trap DeathController already
    /// paid for once.
    /// </summary>
    private int _pendingHitPoints;
    private uint _pendingHitPointTarget;

    /// <summary>
    /// God mode as the engine's own min-1-hit-point pin, NOT as a health poke.
    ///
    /// That choice is the co-op correctness: your health is yours alone and is published to
    /// the peers from your creature, so a pin that floors damage at 1 keeps both screens
    /// agreeing on a number that really is your number. A god mode implemented by writing
    /// health back to full every frame would fight the forwarded-damage pipeline instead —
    /// a peer's hit would land, be published, and be erased, and the peer's mirror of you
    /// would flicker between two truths.
    ///
    /// ⚠ The pin is also KOTOR's own: the Endar Spire command deck and the Taris duel ring
    /// set it from area scripts (docs/research/min-one-hitpoint.md). Turning god mode OFF
    /// where a module had it on would hand the player a death the module forbade, so the
    /// answer says whose pin it is rather than pretending we own the flag.
    /// </summary>
    private void God(nint player)
    {
        int amount = block.CheatAmount;
        bool was = Odyssey.GetMinOneHitPoint(player);

        if (amount < 0)
        {
            Answer(SharedBlock.CheatStatusApplied, was ? "god is ON (1 hp floor)" : "god is OFF");
            return;
        }

        bool wanted = amount != 0;
        if (wanted == was)
        {
            Answer(SharedBlock.CheatStatusApplied, wanted ? "god was already ON" : "god was already OFF");
            return;
        }

        Odyssey.SetMinOneHitPoint(player, wanted);

        Answer(
            SharedBlock.CheatStatusApplied,
            wanted
                ? "god ON - damage floors at 1 hp"
                : "god OFF - a module may still pin you");
    }

    /// <summary>
    /// Sets the local player's hit points. Yours only, deliberately: healing a peer would
    /// mean writing a number into a creature whose owner republishes it several times a
    /// second, which is the exact race the enemy channel was rebuilt to avoid.
    /// </summary>
    private void TopUp(nint player)
    {
        // GetEffectiveMaxHitPoints, NOT GetMaxHitPoints: the plain stored-field read is always
        // zero for a player character (the engine never writes it for one - see
        // MaxHitPointsRule's own class doc comment), which is why this used to refuse with
        // "0 max hp" for every player, not just after combat. See docs/research/
        // playthrough-taris.md's dated follow-up.
        int max = Odyssey.GetEffectiveMaxHitPoints(player);
        if (max <= 0)
        {
            // Names the resolved object's own id/vtable/current-hp so a genuinely-zero max can
            // be told apart from GetPlayerCreaturePointer resolving the wrong object — see
            // docs/research/control-return.md "Round 70". Logged, not put in the 64-byte detail
            // field (SetCheatDetail truncates without complaint).
            uint id = *(uint*)(player + 0x04);
            nint vtable = *(nint*)player;
            short current = Odyssey.GetCurrentHitPoints(player);
            PluginHost.Log(
                $"topup: player resolved to 0x{player:X} (object 0x{id:X}, vtable 0x{vtable:X}, "
                + $"current hp {current}, max 0)");
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"0 max hp (obj 0x{id:X} vtable 0x{vtable:X} cur {current}) - see plugin log");
            return;
        }

        int requested = block.CheatAmount;
        int target = requested > 0 ? Math.Min(requested, max) : max;
        short before = Odyssey.GetCurrentHitPoints(player);

        Odyssey.SetCurrentHitPoints(player, target);

        Answer(SharedBlock.CheatStatusApplied, $"hit points {before} -> {target} (max {max})");
    }

    /// <summary>
    /// Stands the local player back up, through the same Resurrection effect a peer rescue
    /// uses. This is additive rather than a retail duplicate: KOTOR's own <c>heal</c> restores
    /// the party's hit points but leaves a temp-dead body prone, and the co-op down state is
    /// exactly that body.
    /// </summary>
    private void StandUp(nint player, uint playerId)
    {
        if (!Odyssey.IsDowned(player))
        {
            Answer(SharedBlock.CheatStatusApplied, "you are already up");
            return;
        }

        // The Resurrection handler refuses a creature that is not raiseable; the engine's own
        // auto-revive sets this same flag raw before applying the effect.
        Odyssey.SetRaiseable(player);

        if (!Odyssey.ReviveCreature(playerId, out int failure))
        {
            Answer(SharedBlock.CheatStatusEngineRefused, $"revive refused (failure {failure})");
            return;
        }

        int requested = block.CheatAmount;
        _pendingHitPoints = requested > 0 ? requested : DefaultStandUpHitPoints;
        _pendingHitPointTarget = playerId;

        Answer(SharedBlock.CheatStatusApplied, $"standing up with {_pendingHitPoints} hp");
    }

    private void ApplyPendingHitPoints()
    {
        if (_pendingHitPoints <= 0)
        {
            return;
        }

        nint target = Odyssey.ResolveServerObject(_pendingHitPointTarget);
        int hitPoints = _pendingHitPoints;

        _pendingHitPoints = 0;
        _pendingHitPointTarget = 0;

        if (target == 0)
        {
            return;
        }

        // GetEffectiveMaxHitPoints, NOT GetMaxHitPoints, for the same reason TopUp uses it: the
        // target here is always a player character (StandUp's own playerId), whose plain
        // stored-field max reads zero forever.
        int max = Odyssey.GetEffectiveMaxHitPoints(target);
        if (max > 0 && hitPoints > max)
        {
            hitPoints = max;
        }

        Odyssey.SetCurrentHitPoints(target, hitPoints);
    }

    /// <summary>
    /// Kills a creature through whichever authority path this instance actually owns —
    /// <see cref="CheatAuthority.ForSlay"/> decides, this only measures the facts and carries
    /// out the verdict.
    ///
    /// On the enemies' owner that is the engine's own lethal damage effect, so corpse, loot
    /// and OnDeath all run. On a mirroring instance it is a large entry in the damage-dealt
    /// ledger: the owner spends it as real damage, its copy dies its own real death, and the
    /// death comes back down the ordinary channel to everyone. Poking the local copy instead
    /// would produce a corpse in one game and a living enemy in the other, and the next
    /// mirror pass would stand our corpse back up.
    /// </summary>
    private void Slay(uint playerId)
    {
        if (!TryResolveTarget(out uint targetId, out nint creature, out string what))
        {
            Answer(SharedBlock.CheatStatusNoTarget, what);
            return;
        }

        if (MaterialisedEnemies.IsRetainedOwnerCandidate(targetId, block))
        {
            if (!MaterialisedEnemies.TryGetCurrentRetainedLocal(targetId, block, out uint localRetainedId))
            {
                Answer(SharedBlock.CheatStatusRefused, "retained companion handoff is not current yet");
                return;
            }

            targetId = localRetainedId;
            creature = Odyssey.ResolveServerObject(localRetainedId);
            if (creature == 0
                || !PartyCreatureIdentity.IsLiveMatch(*(nint*)creature, *(uint*)(creature + 0x04), localRetainedId))
            {
                Answer(SharedBlock.CheatStatusNoTarget, "retained companion body is not live here");
                return;
            }
        }
        else if ((MaterialisedEnemies.IsRetainedLocal(targetId)
                || MaterialisedEnemies.IsQuarantinedRetainedLocal(targetId))
            && !MaterialisedEnemies.TryGetCurrentRetainedOwner(targetId, block, out _))
        {
            Answer(SharedBlock.CheatStatusRefused, "retained companion owner mapping is not current yet");
            return;
        }

        SlayVerdict verdict = CheatAuthority.ForSlay(
            targetResolved: creature != 0,
            targetIsLocalPlayer: targetId == playerId,
            targetIsPhantom: IsPhantom(targetId),
            targetIsCompanion: IsCompanion(targetId),
            targetIsPlot: creature != 0 && Odyssey.IsPlot(creature),
            mirrorsEnemies: block.DamageDealtEnabled);

        string explanation = CheatAuthority.Explain(verdict, targetId);

        switch (verdict)
        {
            case SlayVerdict.KillHere:
                // The pin has to come off first or the death effect just floors health at 1 —
                // the same order CombatController executes an owner's death in.
                Odyssey.SetMinOneHitPoint(creature, false);

                if (Odyssey.KillCreature(targetId, out int failure))
                {
                    Answer(SharedBlock.CheatStatusApplied, explanation);
                }
                else
                {
                    Answer(SharedBlock.CheatStatusEngineRefused, $"kill refused (failure {failure})");
                }

                break;

            case SlayVerdict.ForwardToOwner:
                // Keyed in the OWNER's id space: a materialised stand-in's local id means
                // nothing to the instance that owns the fight.
                uint ownerId = MaterialisedEnemies.ToOwner(targetId);
                uint ledgerId = ownerId != 0 ? ownerId : targetId;

                if (PhantomHitHook.Dealt.Add(ledgerId, CheatAuthority.ForwardedKillDamage))
                {
                    // Name the id space, and do not claim the kill landed. All this call can
                    // know is that the debt was BANKED here; whether the owner can resolve
                    // that id is the owner's business and it never answers back. Found live
                    // on 2026-08-15: the old wording ("sent the kill to them") read as done,
                    // and the enemy was still standing after two attempts because the host
                    // could not resolve the id at all.
                    string keyed = ownerId != 0
                        ? $"owner id 0x{ownerId:X}"
                        : $"our own id 0x{targetId:X} (no owner mapping - only lands if the "
                          + "owner uses the same id)";

                    Answer(
                        SharedBlock.CheatStatusForwarded,
                        $"queued a killing blow for the owner, keyed by {keyed}");
                }
                else
                {
                    Answer(SharedBlock.CheatStatusRefused, "the damage ledger is full; nothing sent");
                }

                break;

            default:
                Answer(SharedBlock.CheatStatusRefused, explanation);
                break;
        }
    }

    /// <summary>
    /// Reports what this engine knows about one object id — the whole chain, both lookups,
    /// one frame.
    ///
    /// ⚠ The full line goes to the PLUGIN LOG, and only a summary to the console answer. The
    /// answer channel is 96 characters (<c>SharedBlock.ConsoleReplyLength</c>) and this line
    /// is several times that; a diagnostic that is cut off exactly where it becomes
    /// interesting is how the previous round's evidence was lost.
    /// </summary>
    /// <summary>How many refused targets have written their full chain to the log. A cheat is
    /// typed by a person, so a handful is a session's worth.</summary>
    private int _resolveFailuresLogged;

    private void Resolve()
    {
        uint objectId = block.CheatTarget;

        if (objectId == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "no id given");
            return;
        }

        if (objectId == SharedBlock.CheatTargetParty)
        {
            string party = Odyssey.DescribeParty();
            PluginHost.Log(party);
            Answer(SharedBlock.CheatStatusApplied, party);
            return;
        }

        string detail = Odyssey.DescribeObjectResolution(objectId);
        PluginHost.Log($"resolve: {detail}");

        // The summary keeps the one comparison that decides everything: what the engine's own
        // lookup returned, and what the resolver made of it.
        nint resolved = Odyssey.ResolveServerObject(objectId);
        Answer(
            resolved != 0 ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusNoTarget,
            $"0x{objectId:X} resolves to 0x{resolved:X} here; chain in the plugin log");
    }

    /// <summary>
    /// Resolves the request's target: an explicit object id when the agent supplied one,
    /// otherwise the tag in the text field, swept for the same way dialogue and barks resolve
    /// a named creature.
    ///
    /// ⚠ AN EXPLICIT ID IS TRANSLATED INTO THIS ENGINE'S SPACE FIRST, on an instance that
    /// mirrors somebody else's enemies. The agent's bare-<c>slay</c> sweep reads the OWNER's
    /// published census there — that is the only id space a forwarded kill can be keyed by —
    /// so what arrives is an owner id, and owner ids resolve here only by GIT identity.
    /// Wherever the mirror had to materialise a stand-in they do not: resolving the owner's
    /// number directly finds nothing and the kill is refused as "no creature here answers to
    /// that", or finds a DIFFERENT creature that happens to hold that number and judges the
    /// phantom/companion/plot rules against the wrong body.
    ///
    /// <paramref name="targetId"/> comes back in the LOCAL space, which is what every rule
    /// below it is expressed in; the owner key for a forwarded debt is recovered from it with
    /// <c>MaterialisedEnemies.ToOwner</c> at the point of use.
    /// </summary>
    private bool TryResolveTarget(out uint targetId, out nint creature, out string what)
    {
        targetId = block.CheatTarget;
        creature = 0;

        if (targetId != 0)
        {
            // ToLocal is consulted first deliberately: on a mirroring instance the ids that
            // arrive are the owner's, so "this number is an owner id we hold a body for"
            // outranks "this number is also one of our own stand-in ids".
            (uint localId, _) = block.DamageDealtEnabled
                ? CheatAuthority.MirrorIds(
                    targetId, MaterialisedEnemies.ToLocal(targetId), MaterialisedEnemies.ToOwner(targetId))
                : (targetId, targetId);

            creature = Odyssey.ResolveServerObject(localId);

            what = creature != 0
                ? string.Empty
                : localId == targetId
                    ? $"no object 0x{targetId:X} in this module"
                    : $"the owner's 0x{targetId:X} is our 0x{localId:X}, which nothing here answers to";

            // ⚠ A REFUSAL PRINTS ITS OWN WORKING. "No object 0x35 in this module" was measured
            // live at the same minute that the `resolve` verb — the same function, the same
            // thread — handed back a live pointer for the same id. Two answers that cannot both
            // be right were seconds apart and neither carried enough to say which had moved. So
            // the refusal now records the whole chain AT THE INSTANT IT REFUSES: array both
            // ways, the engine's lookup, the in-process walk, and what came back. If the engine
            // resolves it here and this still refused, the fault is between the two and the
            // line proves it; if the engine returns nothing, the object really did go away and
            // the line dates it.
            if (creature == 0 && _resolveFailuresLogged < 5)
            {
                _resolveFailuresLogged++;
                PluginHost.Log($"cheat: refusing target - {Odyssey.DescribeObjectResolution(localId)}");
            }

            targetId = localId;
            return creature != 0;
        }

        byte* text = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            what = "no target given";
            return false;
        }

        string tag = Encoding.ASCII.GetString(text, length);
        creature = CreatureFinder.FindByTag(new ReadOnlySpan<byte>(text, length));
        if (creature == 0)
        {
            what = $"no creature tagged '{tag}' here";
            return false;
        }

        targetId = *(uint*)(creature + 0x04); // CSWSObject + 4 = the object's id
        what = string.Empty;
        return true;
    }
}
