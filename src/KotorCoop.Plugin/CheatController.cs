using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Runs the staging cheats the agent publishes on the block's cheat channel, on the game's
/// own thread, through controllers and engine calls that already exist.
///
/// Why this is not "the retail cheats again": KOTOR's own console already ships
/// <c>giveitem</c>, <c>givecredits</c>, <c>addexp</c>, <c>addlevel</c>, <c>revealmap</c> and
/// <c>heal</c>, every one of which touches only the typist's own single-player state and is
/// therefore already correct in co-op (see docs/research/console-cheats.md for the table).
/// What retail has no idea about is AUTHORITY: an enemy belongs to one instance, a phantom
/// belongs to a peer, a companion belongs to the host. So the verbs here are the ones whose
/// answer depends on who typed them, plus the two that stage a co-op scenario nothing else
/// can (put me next to my partner; put a shared item on the floor).
///
/// Every path either does the thing and says so, or refuses and says exactly what it saw.
/// A guard that silently does nothing is a bug: this channel is meant to be driven by an
/// unattended verification run, which can only trust an answer that names its own reason.
/// </summary>
internal sealed unsafe class CheatController(SharedBlockView block)
{
    /// <summary>Hit points to stand a player up with when the request names none — the same
    /// number <see cref="DeathController"/> uses for a peer rescue, for the same reason: enough
    /// to act, not enough to hide the fight that put them down.</summary>
    private const int DefaultStandUpHitPoints = 10;

    private uint _appliedSerial;

    /// <summary>
    /// A top-up owed to a creature revived on an EARLIER tick. The Resurrection effect can
    /// run deferred, and hit points written while the target still reads dead are dropped by
    /// the engine's own "heal cannot touch the dead" rule — the trap DeathController already
    /// paid for once.
    /// </summary>
    private int _pendingHitPoints;
    private uint _pendingHitPointTarget;

    /// <summary>
    /// A module change does NOT drop a pending request: its serial is still ahead, and the
    /// next stable frame either runs it against the new world or refuses it with a reason.
    /// The deferred top-up is dropped, because its object id names a stranger now.
    /// </summary>
    public void Invalidate()
    {
        _pendingHitPoints = 0;
        _pendingHitPointTarget = 0;
    }

    public void OnFrame()
    {
        ApplyPendingHitPoints();

        uint serial = block.CheatSerial;
        if (serial == _appliedSerial)
        {
            return;
        }

        _appliedSerial = serial;

        try
        {
            Execute(block.CheatKind);
        }
        catch (Exception ex)
        {
            // A staging cheat must never take the game with it. Report the throw as the
            // answer rather than swallowing it: an unattended run reading "applied" after an
            // exception would be worse than no channel at all.
            Answer(SharedBlock.CheatStatusEngineRefused, $"cheat threw: {ex.GetType().Name}");
        }
        finally
        {
            block.CheatApplied = serial;
        }
    }

    private void Execute(uint kind)
    {
        nint player = Odyssey.GetPlayerCreaturePointer();
        uint playerId = Odyssey.GetPlayerObjectId();

        if (player == 0 || playerId == 0)
        {
            Answer(SharedBlock.CheatStatusNoWorld, "no player loaded (not in a module yet)");
            return;
        }

        switch (kind)
        {
            case SharedBlock.CheatKindGod:
                God(player);
                break;

            case SharedBlock.CheatKindTopUp:
                TopUp(player);
                break;

            case SharedBlock.CheatKindStandUp:
                StandUp(player, playerId);
                break;

            case SharedBlock.CheatKindSlay:
                Slay(playerId);
                break;

            case SharedBlock.CheatKindTeleport:
                Teleport(player);
                break;

            case SharedBlock.CheatKindDropItem:
                DropItem(player);
                break;

            case SharedBlock.CheatKindResolve:
                Resolve();
                break;

            default:
                Answer(SharedBlock.CheatStatusRefused, $"unknown cheat kind {kind}");
                break;
        }
    }

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
        ushort max = Odyssey.GetMaxHitPoints(player);
        if (max == 0)
        {
            Answer(SharedBlock.CheatStatusEngineRefused, "the player reports 0 maximum hit points");
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

        ushort max = Odyssey.GetMaxHitPoints(target);
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
    private void Resolve()
    {
        uint objectId = block.CheatTarget;

        if (objectId == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "no id given");
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

    /// <summary>True when the id is one of the bodies this instance renders for a peer.</summary>
    private bool IsPhantom(uint objectId)
    {
        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            if (block.GetPhantomId(i) == objectId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the id is a party companion, read from the engine's own roster rather than
    /// inferred from faction — a mirrored companion on a client is frozen and neutral, so
    /// faction would not tell them apart from scenery.
    /// </summary>
    private static bool IsCompanion(uint objectId)
    {
        if (!Odyssey.TryReadPartyRoster(out _, out uint partyMask))
        {
            return false;
        }

        for (int slot = 0; slot < 32; slot++)
        {
            if ((partyMask & (1u << slot)) == 0)
            {
                continue;
            }

            if (Odyssey.GetPartyRosterObjectId(slot) == objectId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Puts the local player at a point in the current area.
    ///
    /// Self only, and that is what makes it safe: your position is yours, published to the
    /// peers every tick, so their phantom of you follows without anything being told about
    /// the teleport. Moving somebody ELSE's character would mean writing a position their own
    /// instance overwrites on its next tick.
    ///
    /// ⚠ NEEDS-LIVE: nothing here consults the walkmesh. The staged form the agent produces
    /// (<c>goto &lt;player&gt;</c>) lands on a point a peer is standing on, which is on the
    /// mesh by construction; hand-typed coordinates are the player's own risk and the answer
    /// says so.
    /// </summary>
    private void Teleport(nint player)
    {
        Vec3 destination = block.CheatPosition;
        Vec3 here = Odyssey.GetPosition(player);

        if (destination.X == 0f && destination.Y == 0f && destination.Z == 0f)
        {
            Answer(SharedBlock.CheatStatusRefused, "refusing to teleport to the area origin");
            return;
        }

        Odyssey.SnapTo(player, destination);

        Vec3 landed = Odyssey.GetPosition(player);
        Answer(
            SharedBlock.CheatStatusApplied,
            $"moved from {here.X:F1},{here.Y:F1} to {landed.X:F1},{landed.Y:F1}");
    }

    /// <summary>
    /// Spawns a blueprint on the floor at the player's feet.
    ///
    /// On the floor rather than in the inventory ON PURPOSE. A ground item is already shared
    /// world state — the item channel publishes it, the peer's instance materialises a real
    /// clickable object, and it vanishes for both when either picks it up — so this is a
    /// "give the party an item" that obeys the replication rule the mod already has. The
    /// retail <c>giveitem</c> puts the item straight in the typist's pack, where no peer will
    /// ever see it; both are useful and they are different things.
    /// </summary>
    private void DropItem(nint player)
    {
        byte* text = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "no blueprint given");
            return;
        }

        string resref = Encoding.ASCII.GetString(text, length);
        Vec3 position = Odyssey.GetPosition(player);

        uint itemId = Odyssey.SpawnItemOnFloor(text, length, position, out int failure);
        if (itemId is 0 or Odyssey.ObjectInvalid)
        {
            // The overwhelmingly likely cause, and worth naming: the blueprint is not in
            // this install. That is also the mod-mismatch symptom, so say which resref.
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"'{resref}' did not spawn (failure {failure}; wrong resref?)");
            return;
        }

        Answer(SharedBlock.CheatStatusApplied, $"dropped '{resref}' as 0x{itemId:X}");
    }

    /// <summary>Writes the answer where both the agent and the log will find it. Status and
    /// detail together, always — a status with no sentence is the silent guard this channel
    /// exists to avoid.</summary>
    private void Answer(int status, string detail)
    {
        block.SetCheatDetail(detail);
        block.CheatStatus = status;
        PluginHost.Log($"cheat: {detail} (status {status})");
    }
}
