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

    /// <summary>Set by <see cref="MoviePlay"/> when it defers the actual engine call to
    /// <see cref="PendingMoviePlay"/> (docs/research/cinematics.md §10.9): the serial this
    /// deferred call belongs to, so <see cref="OnFrame"/>'s own <c>finally</c> does not mark it
    /// <see cref="SharedBlockView.CheatApplied"/> before the call has actually run — that would
    /// tell a poller "done" a frame before the movie even started.</summary>
    private uint _deferredMovieSerial;

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

        // PendingMoviePlay itself is cancelled centrally (PluginHost.InvalidateAll); this just
        // stops OnFrame's finally from waiting on a serial that will never complete now.
        _deferredMovieSerial = 0;

        // A walk armed in the world we just left names a door that no longer exists — and if
        // the module changed BECAUSE the walk worked, the watch has nothing left to report.
        _walkFramesLeft = 0;
        _walkWatchFrames = 0;
        _walkDoorId = 0;
    }

    public void OnFrame()
    {
        ApplyPendingHitPoints();
        RetryPendingDoorWalk();
        WatchDoorWalk();

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
            if (_deferredMovieSerial != serial)
            {
                block.CheatApplied = serial;
            }
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

            case SharedBlock.CheatKindStageMovie:
                StageMovie();
                break;

            case SharedBlock.CheatKindSetItem:
                SetItem();
                break;

            case SharedBlock.CheatKindResolve:
                Resolve();
                break;

            case SharedBlock.CheatKindUseDoor:
                UseDoor(player, playerId);
                break;

            case SharedBlock.CheatKindOpenDoor:
                OpenDoor(player, playerId);
                break;

            case SharedBlock.CheatKindGiveLoot:
                GiveLoot();
                break;

            case SharedBlock.CheatKindFireScript:
                FireScript();
                break;

            case SharedBlock.CheatKindMoveNear:
                MoveNear();
                break;

            case SharedBlock.CheatKindApplyEffect:
                ApplyEffect(player);
                break;

            case SharedBlock.CheatKindMoviePlay:
                MoviePlay();
                break;

            case SharedBlock.CheatKindDestroyObject:
                DestroyObject(playerId);
                break;

            case SharedBlock.CheatKindLeader:
                Leader();
                break;

            case SharedBlock.CheatKindUsePlaceable:
                UsePlaceable(player);
                break;

            case SharedBlock.CheatKindClosePlaceable:
                ClosePlaceable(player);
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
            // ROUND 70 DIAGNOSTIC (2026-08-22, docs/research/control-return.md "Round 70"):
            // this used to just say "0 maximum hit points" with nothing to chase it by. The
            // resolved object's own id/vtable/current-hp are cheap to add and are exactly what
            // distinguishes "GetPlayerCreaturePointer resolved the wrong object" from "it
            // resolved the right one but read a genuinely-zero field". Logged, not put in the
            // 64-byte detail field (SetCheatDetail truncates without complaint — see this
            // method's own note below on player, and CheatController.cs:2303).
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
    /// STRATEGY 2 DIAGNOSTIC (2026-08-21). <c>CheatAmount</c> 0 peeks the creature's +0x260
    /// event script slot without firing it (a safe read, confirms the field looks like a real
    /// resref before ever touching it live); nonzero fires it through
    /// <see cref="Odyssey.FireCreatureScriptSlot0x260"/>. See docs/research/shared-state-audit.md
    /// §2, "Strategy 2," for what this is and why.
    /// </summary>
    private void FireScript()
    {
        uint objectId = block.CheatTarget;
        if (objectId == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "no id given");
            return;
        }

        nint creature = Odyssey.ResolveServerObject(objectId);
        if (creature == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, $"0x{objectId:X} does not resolve here");
            return;
        }

        // ROUND 34: CheatAmount now doubles as a slot selector - 0x270 fires the OnNotice
        // candidate found this round, anything else nonzero still means "fire +0x260".
        bool useSlot270 = block.CheatAmount == 0x270;
        int slotOffset = useSlot270 ? 0x270 : 0x260;
        string slot = useSlot270
            ? Odyssey.PeekCreatureScriptSlot0x270(creature)
            : Odyssey.PeekCreatureScriptSlot0x260(creature);

        if (block.CheatAmount == 0)
        {
            PluginHost.Log($"firescript: 0x{objectId:X} +0x{slotOffset:X} = '{slot}' (peek only, not fired)");
            Answer(SharedBlock.CheatStatusApplied, $"0x{objectId:X} +0x{slotOffset:X} = '{slot}'");
            return;
        }

        // ROUND 39: neither slot auto-arms the diagnostic watch any more (production code -
        // EnemyPresenceController.StartLocalFight - now calls FireCreatureScriptSlot0x260 for
        // every real fight, and it must not spam 6000 frames of log per fight). The manual
        // diagnostic path still wants the watch, so arm it explicitly here for both slots.
        if (useSlot270)
        {
            Odyssey.FireCreatureScriptSlot0x270(creature);
            Odyssey.ArmFireScriptWatch(creature);
        }
        else
        {
            Odyssey.FireCreatureScriptSlot0x260(creature);
            Odyssey.ArmFireScriptWatch(creature);
        }

        PluginHost.Log($"firescript: fired 0x{objectId:X} +0x{slotOffset:X} = '{slot}'");
        Answer(SharedBlock.CheatStatusApplied, $"fired 0x{objectId:X} +0x{slotOffset:X} = '{slot}'");
    }

    /// <summary>
    /// ROUND 36 TEMPORARY DIAGNOSTIC (2026-08-21). Snaps the target creature ~1.3m from
    /// END_M01AA's `sithsoldier 0xE` (29.74, 144.46), inside the ~2.6m attack range round 23
    /// measured — so a genuinely non-census creature (a party member, never touched by
    /// EnemyPresenceController) has a valid, in-range hostile to test +0x260 firing against.
    /// A no-target result from a non-census creature otherwise proves nothing either way. See
    /// docs/research/shared-state-audit.md §2, round 36. Remove once settled.
    /// </summary>
    private void MoveNear()
    {
        uint objectId = block.CheatTarget;
        if (objectId == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "no id given");
            return;
        }

        nint creature = Odyssey.ResolveServerObject(objectId);
        if (creature == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, $"0x{objectId:X} does not resolve here");
            return;
        }

        Vec3 destination = new() { X = 29.0f, Y = 143.5f, Z = 0.0f };
        Odyssey.SnapTo(creature, destination);

        // ROUND 36: read the position straight back (ObjPosition, +0x090 - see Odyssey.cs)
        // rather than trust the write blind - creatures.ps1's raw scan is known-unreliable
        // for fresh state.
        Vec3 readBack = *(Vec3*)(creature + 0x090);
        PluginHost.Log(
            $"movenear: snapped 0x{objectId:X} to ({destination.X:F1}, {destination.Y:F1}, {destination.Z:F1}), "
            + $"read back ({readBack.X:F1}, {readBack.Y:F1}, {readBack.Z:F1})");
        Answer(SharedBlock.CheatStatusApplied, $"0x{objectId:X} snapped near 0xE");
    }

    /// <summary>
    /// STAGING VERB (2026-08-22), STAGING ONLY / LOCAL APPLY: runs one of the peer-effect
    /// channel's non-damage constructors through <see cref="OdysseyEffects.ApplyEffect"/> — the
    /// EXACT apply-side path a peer's forwarded effect calls when it lands for real
    /// (<see cref="PeerEffectController.ApplyIncoming"/>). Aimed at a peer's phantom (the
    /// default, when <see cref="SharedBlock.OffsetCheatTarget"/> is 0) this IS the live test
    /// docs/research/effect-sync.md's list has never run: <see cref="PhantomEffectHook"/>
    /// captures the effect landing there just like it would for a real enemy attack, and the
    /// ordinary channel forwards it exactly as if it had come from combat. Aimed at an explicit
    /// id it is a constructor smoke test only — including the local player's own id
    /// (<c>0x7FFFFFFF</c>), which the `applyeffect ... me` console form resolves to, so the
    /// applying half of a constructor can be checked without touching the network at all.
    /// </summary>
    private void ApplyEffect(nint player)
    {
        uint kind = (uint)block.CheatAmount;
        if (kind is PeerEffectKindValues.None || kind > PeerEffectKindValues.Whirlwind)
        {
            Answer(
                SharedBlock.CheatStatusRefused,
                $"unknown effect kind {kind} (1..9 - stun, paralyze, knockdown, slow, "
                + "frightened, sleep, choke, forcepush, whirlwind)");
            return;
        }

        uint targetId = block.CheatTarget;
        nint target;
        string what;

        if (targetId != 0)
        {
            target = Odyssey.ResolveServerObject(targetId);
            what = $"0x{targetId:X}";

            if (target == 0)
            {
                Answer(SharedBlock.CheatStatusNoTarget, $"no object 0x{targetId:X} in this module");
                return;
            }
        }
        else
        {
            target = FindNearestPhantom(player, out targetId, out int phantomsHeld, out int phantomsResolved);
            what = "the nearest phantom";

            if (target == 0)
            {
                Answer(
                    SharedBlock.CheatStatusNoTarget,
                    $"no phantom resolves here ({phantomsHeld} slot(s) held, {phantomsResolved} resolved) "
                    + "- name an object id instead, or 'me' to test locally");
                return;
            }
        }

        uint durationMs = block.CheatItemCharges;
        uint magnitude = block.CheatItemMaxCharges;

        bool applied = OdysseyEffects.ApplyEffect(targetId, kind, durationMs, magnitude, out int failure);

        if (!applied)
        {
            string reason = failure == -1
                ? "no apply-side constructor for this kind yet (see effect-sync.md §4)"
                : $"engine call refused (failure {failure})";

            PluginHost.Log($"applyeffect: kind {kind} on {what} (0x{targetId:X}) refused - {reason}");
            Answer(SharedBlock.CheatStatusEngineRefused, $"kind {kind} on {what} (0x{targetId:X}) refused - {reason}");
            return;
        }

        PluginHost.Log(
            $"applyeffect: kind {kind} applied to {what} (0x{targetId:X}), duration {durationMs}ms, "
            + $"magnitude {magnitude}");
        Answer(
            SharedBlock.CheatStatusApplied,
            $"applied kind {kind} to {what} (0x{targetId:X}), {durationMs}ms, mag {magnitude}");
    }

    /// <summary>
    /// STAGING VERB (2026-08-22): destroys a creature outright through
    /// <see cref="Odyssey.DestroyObjectById"/> — the SAME engine call
    /// <see cref="LivenessReconcileController"/>'s own Destroy action already wraps (see
    /// docs/research/creature-liveness.md §5). Built to unstick that channel's live passes:
    /// twice (END_M01AA and tat_m20aa, 2026-08-22) the Destroy dispatch could not be exercised
    /// live because <c>slay</c> only ever calls <see cref="Odyssey.KillCreature"/>, and no other
    /// verb could put a creature into the "Gone" state.
    ///
    /// Follows <see cref="ApplyEffect"/>'s wiring and <see cref="Slay"/>'s authority rules
    /// (person/companion/plot, same order, same reasons — see <see cref="CheatAuthority.ForDestroy"/>)
    /// but is HOST-ONLY where <see cref="Slay"/> forwards: a mirroring instance is refused
    /// outright (<see cref="DestroyVerdict.RefuseNotOwner"/>), not handed off, because there is
    /// no destroy-ledger to spend a forwarded destroy through.
    /// </summary>
    private void DestroyObject(uint playerId)
    {
        uint targetId = block.CheatTarget;
        if (targetId == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "destroyobject needs an object id");
            return;
        }

        nint target = Odyssey.ResolveServerObject(targetId);

        DestroyVerdict verdict = CheatAuthority.ForDestroy(
            targetResolved: target != 0,
            targetIsLocalPlayer: targetId == playerId,
            targetIsPhantom: IsPhantom(targetId),
            targetIsCompanion: IsCompanion(targetId),
            targetIsPlot: target != 0 && Odyssey.IsPlot(target),
            mirrorsEnemies: block.DamageDealtEnabled);

        string explanation = CheatAuthority.Explain(verdict, targetId);

        if (verdict == DestroyVerdict.DestroyHere)
        {
            if (Odyssey.DestroyObjectById(targetId, out int failure))
            {
                PluginHost.Log($"destroyobject: destroyed 0x{targetId:X}");
                Answer(SharedBlock.CheatStatusApplied, explanation);
            }
            else
            {
                Answer(SharedBlock.CheatStatusEngineRefused, $"0x{targetId:X}: destroy refused (failure {failure})");
            }

            return;
        }

        Answer(
            verdict == DestroyVerdict.RefuseMissing ? SharedBlock.CheatStatusNoTarget : SharedBlock.CheatStatusRefused,
            explanation);
    }

    /// <summary>
    /// DEV-ONLY TEST PRIMITIVE (2026-08-22, rewired Round 70 2026-08-22): switches the LOCAL
    /// player's controlled character through <see cref="Odyssey.SwitchControlToPartySlot"/>/
    /// <see cref="Odyssey.SwitchControlToPc"/> — the ENGINE'S OWN <c>ChangeCharacter</c> funnel
    /// (0x005F6B60), the same call a real Tab press and portrait click make
    /// (docs/research/control-return.md "Round 70"), NOT a raw field poke as of this round.
    /// Because it goes through the real engine call, it moves the camera too (the same
    /// <c>SetCameraFollow</c> a Tab press triggers) — a caller no longer needs to follow up
    /// with <see cref="Odyssey.SetCameraFollowServerObject"/>/
    /// <see cref="Odyssey.RestoreCameraToControlledCreature"/> separately, though doing so is
    /// harmless (idempotent while already on target). Built because docs/research/control-return.md's
    /// live pass needed a way to put a client onto a party-roster companion BEFORE connecting —
    /// the one manual step <c>control-return-live.ps1</c> could never automate (injected
    /// keystrokes never reach this game). <see cref="SharedBlock.OffsetCheatAmount"/> is the
    /// party-roster slot (0-8), or -1 for "your own PC". Local only: this never crosses the
    /// network. Requires the target to already be a live CSWParty member (in the active party,
    /// not merely recruited) — the engine's own switch has no other way to name one.
    /// </summary>
    private void Leader()
    {
        int slot = block.CheatAmount;

        if (slot < 0)
        {
            if (!Odyssey.SwitchControlToPc())
            {
                Answer(SharedBlock.CheatStatusNoWorld, "leader: no client world to switch in yet");
                return;
            }

            PluginHost.Log("leader: switched control to your own PC");
            Answer(SharedBlock.CheatStatusApplied, "control switched to your own PC");
            return;
        }

        if (!Odyssey.SwitchControlToPartySlot(slot, out uint rosterId))
        {
            Answer(
                SharedBlock.CheatStatusNoTarget,
                $"leader: no party-roster companion in slot {slot} (empty, or no client world yet)");
            return;
        }

        PluginHost.Log($"leader: switched control to party slot {slot} (0x{rosterId:X})");
        Answer(SharedBlock.CheatStatusApplied, $"control switched to party slot {slot} (0x{rosterId:X})");
    }

    /// <summary>
    /// TEST HOOK (2026-08-22, fixed 2026-08-22 per §10.9): plays
    /// <see cref="SharedBlock.OffsetCheatText"/> (a bare resref) through
    /// <see cref="OdysseyMovies.PlayMovieNow"/> — the SAME engine entry
    /// (<c>CClientExoApp::PlayMovie 0x005EDB30</c>) a mid-module script's own <c>PlayMovie</c>
    /// reaches, and the one <see cref="MoviePlayHook"/> watches. Exists because
    /// docs/research/cinematics.md §10.7 found no isolated way to fire that hook on the host
    /// without waiting for a story trigger: <c>moviewarp</c> (<see cref="StageMovie"/>) only
    /// ever writes the engine's TRANSITION queue, a different path entirely that
    /// <see cref="MoviePlayHook"/> does not watch (§10.1's call-graph argument).
    ///
    /// Gated by the SAME <see cref="MovieStaging.DecidePlayNow"/> check
    /// <see cref="MidModuleMovieController"/>'s own <c>PlayNow</c> uses before calling the
    /// identical function, so a refusal here reads exactly like a refusal there.
    ///
    /// ⚠⚠ §10.8 found calling <see cref="OdysseyMovies.PlayMovieNow"/> RIGHT HERE — inside
    /// <see cref="CheatController.OnFrame"/>, which runs on the <see cref="FrameHook"/> callback,
    /// itself nested inside the hooked <c>GDI32!SwapBuffers</c> call — hangs the engine
    /// indefinitely (two different real movies, both killed after 60+ seconds / 4+ minutes with
    /// no completion). §10.9 root-caused it: neither retail caller of <c>PlayMovie</c> ever runs
    /// from inside <c>SwapBuffers</c> (one is mid-<c>MainLoop</c> script execution, the other a
    /// GUI callback off the message pump), so this verb now only DECIDES and ARMS here — the
    /// actual engine call is deferred to <see cref="PendingMoviePlay"/>, which runs from
    /// <see cref="MainLoopHook"/>'s callback, immediately BEFORE <c>MainLoop</c> for the next
    /// frame: outside render, outside <c>SwapBuffers</c>, the same territory the retail call
    /// sites already stand in. That call still BLOCKS THE MAIN THREAD for the length of the film
    /// — that stall, and the OTHER instance's <c>movie-relay:</c> log lines once
    /// <c>movie_relay=1</c>, are the actual test — it just does so one frame later and from a
    /// different call site than before.
    /// </summary>
    private void MoviePlay()
    {
        string resref = ReadCheatText();
        if (!MovieStaging.IsMovieName(resref))
        {
            Answer(
                SharedBlock.CheatStatusRefused,
                resref.Length == 0
                    ? "movieplay needs a resref, e.g. movieplay 01c"
                    : $"'{resref}' is not a movie name (letters, digits, _; max {MovieStaging.MaxNameLength})");
            return;
        }

        int queued = Odyssey.QueuedTransitionMovieCount();
        MovieStagingVerdict verdict = MovieStaging.DecidePlayNow(
            Odyssey.HasClient, Odyssey.IsAreaTransitionPending(), Odyssey.IsMoviePlaying(), queued);

        if (verdict != MovieStagingVerdict.Stage)
        {
            string reason = MovieStaging.Explain(verdict, queued);
            PluginHost.Log($"movieplay: refused '{resref}' - {reason}");
            Answer(SharedBlock.CheatStatusRefused, $"'{resref}' refused - {reason}");
            return;
        }

        // Heap, not stackalloc: this buffer has to survive into NEXT frame's MainLoopHook
        // callback, well past this method's own return - a stackalloc'd span does not.
        byte[] buffer = new byte[MovieStaging.MaxNameLength];
        int length = Math.Min(resref.Length, MovieStaging.MaxNameLength);
        for (int i = 0; i < length; i++)
        {
            buffer[i] = (byte)resref[i];
        }

        uint serial = block.CheatSerial;
        _deferredMovieSerial = serial;

        PluginHost.Log(
            $"movieplay: '{resref}' staged - deferred to the pre-MainLoop callback "
            + "(docs/research/cinematics.md §10.9), plays next frame");

        PendingMoviePlay.Arm(() =>
        {
            // Re-checked, not reused: one frame separates the decision above from the engine
            // call here, and MovieStaging.DecidePlayNow is cheap - the same "distrust a stale
            // decision" posture §10.7 already documents for this exact check.
            int recheckQueued = Odyssey.QueuedTransitionMovieCount();
            MovieStagingVerdict recheck = MovieStaging.DecidePlayNow(
                Odyssey.HasClient, Odyssey.IsAreaTransitionPending(), Odyssey.IsMoviePlaying(), recheckQueued);

            if (recheck != MovieStagingVerdict.Stage)
            {
                string reason = MovieStaging.Explain(recheck, recheckQueued);
                PluginHost.Log($"movieplay: '{resref}' no longer safe by the time it was due - {reason}");
                Answer(SharedBlock.CheatStatusRefused, $"'{resref}' refused - {reason}");
                _deferredMovieSerial = 0;
                block.CheatApplied = serial;
                return;
            }

            PluginHost.Log($"movieplay: playing '{resref}' now - the main thread stalls for its length");

            // BLOCKS. Everything the plugin does resumes on the far side of this call, exactly
            // as it does for a real mid-module PlayMovie (docs/research/cinematics.md §1.3) -
            // just from MainLoopHook's callback rather than FrameHook's (§10.9).
            fixed (byte* p = buffer)
            {
                bool started = OdysseyMovies.PlayMovieNow(p, length);

                Answer(
                    started ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
                    started
                        ? $"played '{resref}' (returned from the block)"
                        : $"'{resref}' did not start (no client, or name too long)");
            }

            _deferredMovieSerial = 0;
            block.CheatApplied = serial;
        });
    }

    /// <summary>
    /// The closest phantom this instance renders for a peer — the default target for
    /// <see cref="ApplyEffect"/>, because landing the effect ON a phantom is the whole point:
    /// <see cref="PhantomEffectHook"/> only watches ids in the phantom table
    /// (<c>PhantomHitHook.IsWatchedPhantom</c>), so an effect applied to anything else is never
    /// captured or forwarded at all.
    /// </summary>
    private nint FindNearestPhantom(nint player, out uint objectId, out int held, out int resolved)
    {
        Vec3 here = Odyssey.GetPosition(player);

        nint best = 0;
        uint bestId = 0;
        float bestDistance = float.MaxValue;
        held = 0;
        resolved = 0;

        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            uint phantomId = block.GetPhantomId(i);
            if (phantomId == 0)
            {
                continue;
            }

            held++;

            nint candidate = Odyssey.ResolveServerObject(phantomId);
            if (candidate == 0)
            {
                continue;
            }

            resolved++;

            float distance = Distance(here, Odyssey.GetPosition(candidate));
            if (distance >= bestDistance)
            {
                continue;
            }

            best = candidate;
            bestId = phantomId;
            bestDistance = distance;
        }

        objectId = bestId;
        return best;
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

        // ⚠ The guard above cannot catch a wrong resref, so this one has to. CreateItemOnFloor
        // 0x00547F60 does not test LoadFromTemplate's result (0x0054806B), so a blueprint that
        // is not in this install still returns a perfectly good object id — and the old answer
        // "did not spawn (failure N; wrong resref?)" could therefore never be the one a typo
        // produced. What a typo produces is a nameless, empty body reported as a success.
        if (Odyssey.TryReadItemIdentityFields(itemId, out Odyssey.ItemIdentityFields spawned)
            && spawned.LooksUnloaded)
        {
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"'{resref}' is not a blueprint here - 0x{itemId:X} spawned EMPTY");
            return;
        }

        Answer(SharedBlock.CheatStatusApplied, $"dropped '{resref}' as 0x{itemId:X}");
    }

    /// <summary>
    /// Gives an item to either of two targets, decided by <see cref="SharedBlock.CheatTarget"/>:
    /// a materialised enemy's local stand-in, one item of its real corpse's actual loot (the
    /// OWNER's enemy object id, translated locally — see
    /// <see cref="SharedBlock.CheatKindGiveLoot"/> for the full field layout); or, when the
    /// target is the <see cref="SharedBlock.CheatTargetLocalPlayer"/> sentinel, THIS instance's
    /// own player directly — the receiving half of the dialogue item-grant channel
    /// (docs/research/dialogue-consequences.md §3.4), which needs no translation at all.
    /// </summary>
    private void GiveLoot()
    {
        uint ownerEnemyId = block.CheatTarget;
        uint localId;

        if (ownerEnemyId == SharedBlock.CheatTargetLocalPlayer)
        {
            // The dialogue item-grant channel (docs/research/dialogue-consequences.md §3.4):
            // give straight to THIS instance's own player. No owner-id translation is involved
            // — "the local player" already names the same creature on every instance — so
            // MaterialisedEnemies.ToLocal must never be consulted for this sentinel.
            localId = Odyssey.GetPlayerObjectId();
            if (localId == 0)
            {
                Answer(SharedBlock.CheatStatusNoTarget, "giveloot: no local player to give to");
                return;
            }
        }
        else if (ownerEnemyId is 0 or Odyssey.ObjectInvalid)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "giveloot needs the owner's enemy object id");
            return;
        }
        else
        {
            localId = MaterialisedEnemies.ToLocal(ownerEnemyId);
            if (localId == 0)
            {
                // Not an error: the overwhelmingly common case is a peer holding the REAL
                // creature (same GIT id), which already rolled its own equivalent loot
                // independently when it died — applying this here too would double it. Only a
                // materialised stand-in has none of its own, and only one maps through this
                // table.
                Answer(
                    SharedBlock.CheatStatusRefused,
                    $"0x{ownerEnemyId:X} is not a materialised stand-in here - nothing to give");
                return;
            }
        }

        byte* text = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "no item resref given");
            return;
        }

        string resref = Encoding.ASCII.GetString(text, length);
        int stack = block.CheatAmount;

        uint itemId = Odyssey.GiveItemToInventory(localId, text, length, Math.Max(1, stack), out int failure);
        if (itemId is 0 or Odyssey.ObjectInvalid)
        {
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"'{resref}' did not spawn onto 0x{localId:X} (failure {failure}; wrong resref?)");
            return;
        }

        uint charges = block.CheatItemCharges;
        uint maxCharges = block.CheatItemMaxCharges;
        uint upgrades = block.CheatItemUpgrades;
        uint wireFlags = block.CheatItemFlags;

        bool applied = Odyssey.ApplyItemIdentity(
            itemId,
            upgrades == SharedBlock.CheatFieldUnchanged ? 0 : upgrades,
            (uint)Math.Max(1, stack),
            charges == SharedBlock.CheatFieldUnchanged ? 0 : charges,
            maxCharges == SharedBlock.CheatFieldUnchanged ? 0 : maxCharges,
            (wireFlags == SharedBlock.CheatFieldUnchanged ? 0 : wireFlags) | SharedBlock.ItemIdentityKnown,
            out Odyssey.ItemIdentityFields after);

        string forWhom = ownerEnemyId == SharedBlock.CheatTargetLocalPlayer
            ? "the local player (dialogue grant)"
            : $"owner 0x{ownerEnemyId:X}";

        PluginHost.Log(applied
            ? $"materialise: gave loot '{resref}' (0x{itemId:X}) to stand-in 0x{localId:X} for "
              + $"{forWhom} — stack {after.StackSize}, "
              + $"charges {after.Charges}/{after.MaxCharges}, upgrades 0x{after.Upgrades:X}"
            : $"materialise: '{resref}' spawned as 0x{itemId:X} on 0x{localId:X} but identity "
              + $"apply failed — {Odyssey.LastItemResolveNote}");

        Answer(SharedBlock.CheatStatusApplied, $"gave '{resref}' to 0x{localId:X}");
    }

    /// <summary>
    /// Stages a cinematic onto a module transition. The instrument that makes the highest-value
    /// unknown in docs/research/cinematics.md testable without a human playing the escape-pod
    /// conversation: it puts movie names into the engine's own <c>move_to_module_movies</c>
    /// queue, which <c>CServerExoAppInternal::StartNewModule</c> drains on the next module load.
    ///
    /// It has four modes, and only two of them touch the engine:
    ///
    /// <list type="bullet">
    /// <item><b>warp</b> — ARM the names; <see cref="TravelController"/> writes them into the
    ///   queue on the frame it fires the warp, after every plugin-spawned body has been
    ///   flushed. Nothing is written here, so a warp that never happens leaves no trace on the
    ///   engine.</item>
    /// <item><b>queue</b> — write them NOW and leave them, so the next transition of ANY kind
    ///   carries them. This is the honest reproduction of the risk case: a transition the
    ///   plugin did not initiate and therefore did not flush ahead of.</item>
    /// <item><b>clear</b> — empty the queue and disarm. The way out.</item>
    /// <item><b>show</b> — read the queue back and change nothing.</item>
    /// </list>
    ///
    /// Every refusal names what it saw (<see cref="MovieStaging.Explain"/>). This is the
    /// verb where a silent guard would be worst: "nothing happened" and "the movie played and
    /// the plugin was unreachable for six seconds" leave almost the same evidence.
    /// </summary>
    private void StageMovie()
    {
        int mode = block.CheatAmount;
        int queued = Odyssey.QueuedTransitionMovieCount();

        if (mode == MovieStaging.ModeShow)
        {
            Answer(
                SharedBlock.CheatStatusApplied,
                $"queued: {DescribeQueue(queued)}; armed: {TravelController.StagedMovies.Describe()}");
            return;
        }

        if (mode == MovieStaging.ModeClear)
        {
            bool forgot = TravelController.StagedMovies.Forget("cleared by hand", out string dropped);
            bool cleared = Odyssey.ClearQueuedTransitionMovies();

            if (forgot)
            {
                PluginHost.Log($"cinematic: {dropped}");
            }

            Answer(
                cleared ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
                cleared
                    ? $"transition movie queue emptied (held {queued}); armed: nothing"
                    : "no client application here, so there is no queue to empty");
            return;
        }

        if (!MovieStaging.TrySplit(ReadCheatText(), out string[] names, out string error))
        {
            Answer(SharedBlock.CheatStatusRefused, error);
            return;
        }

        MovieStagingVerdict verdict = MovieStaging.Decide(
            hasClient: Odyssey.HasClient,
            transitionPending: Odyssey.IsAreaTransitionPending(),
            moviePlaying: Odyssey.IsMoviePlaying(),
            alreadyQueued: queued,
            nameCount: names.Length);

        if (verdict != MovieStagingVerdict.Stage)
        {
            Answer(SharedBlock.CheatStatusRefused, MovieStaging.Explain(verdict, queued));
            return;
        }

        if (mode == MovieStaging.ModeQueueOnly)
        {
            int accepted = QueueMoviesNow(names);
            Answer(
                accepted == names.Length ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
                accepted == names.Length
                    ? $"the engine queued {accepted}: {string.Join(", ", names)} - the NEXT transition plays them"
                    : $"the engine took only {accepted} of {names.Length} (missing .bik?)");
            return;
        }

        string previous = TravelController.StagedMovies.Arm(names, Environment.TickCount64);

        PluginHost.Log(
            $"cinematic: armed {string.Join(", ", names)} for the next co-op warp "
            + $"(previously armed: {previous})");

        Answer(
            SharedBlock.CheatStatusApplied,
            $"armed {string.Join(", ", names)} for the next warp (was: {previous})");
    }

    /// <summary>
    /// Writes each name into the engine's queue and counts how many the ENGINE took. It
    /// refuses a name it has no <c>.bik</c> for (see <c>Odyssey.QueueTransitionMovie</c>), and
    /// that refusal is the difference between "the cinematic did not play because the design
    /// is wrong" and "because the file is not in this install" — worth one log line each.
    /// </summary>
    private static int QueueMoviesNow(string[] names)
    {
        int accepted = 0;
        byte* buffer = stackalloc byte[MovieStaging.MaxNameLength + 1];

        foreach (string name in names)
        {
            int length = name.Length <= MovieStaging.MaxNameLength ? name.Length : MovieStaging.MaxNameLength;
            for (int i = 0; i < length; i++)
            {
                buffer[i] = (byte)name[i];
            }

            bool ok = Odyssey.QueueTransitionMovie(buffer, length);
            if (ok)
            {
                accepted++;
            }

            PluginHost.Log(ok
                ? $"cinematic: the engine accepted '{name}' onto the transition movie queue"
                : $"cinematic: the engine REFUSED '{name}' - no such movie in this install");
        }

        return accepted;
    }

    /// <summary>
    /// How many frames a refused walk keeps being retried. `Odyssey.MoveToLocation` declines
    /// with failure 4 while the script VM is mid-call — transient, frame-shaped, and the same
    /// refusal that orphaned a phantom on teardown until destroys were retried. Two seconds of
    /// frames is far more than a VM call lasts and still bounded.
    /// </summary>
    private const int WalkRetryFrames = 120;

    /// <summary>
    /// How long to watch a queued walk before reporting where it actually ended up. Ten
    /// seconds at 60 fps: far longer than a four-metre walk, and the watch is cancelled the
    /// moment the transition it was staging happens.
    /// </summary>
    private const int WalkWatchFrames = 600;

    /// <summary>
    /// How far into the watch to check whether the walk has stalled and, if so, reissue it
    /// once. Three seconds at 60 fps: comfortably longer than the walk itself should take, so
    /// this fires only once the accepted walk has had every reasonable chance to have already
    /// crossed. Repeated live runs at coordinates that had worked before still measured the
    /// walk stopping 0.9-2.4 m short of the door despite <see cref="DoorUse.WalkThroughOvershoot"/>
    /// being enlarged for exactly this — the path-arrival tolerance (or a corner the
    /// pathfinder cuts near the doorway) can eat a queued walk before the crossing event
    /// fires, and nothing else re-issues it. This is the retry that does.
    /// </summary>
    private const int WalkStallCheckFrame = 180;

    private uint _walkDoorId;
    private Vec3 _walkTarget;
    private int _walkFramesLeft;
    private int _walkWatchFrames;
    private float _walkStartDistance;

    /// <summary>
    /// Queues the walk that carries the player through the door, and reports the engine's own
    /// refusal code rather than a boolean.
    /// </summary>
    private bool TryWalkThroughDoor(out int failure)
    {
        failure = 0;

        uint playerId = Odyssey.GetPlayerObjectId();
        if (playerId == 0)
        {
            failure = 1;
            return false;
        }

        return Odyssey.MoveToLocation(playerId, _walkTarget, run: true, out failure);
    }

    /// <summary>
    /// Retries a walk the VM was too busy to accept. Runs every frame, does nothing on almost
    /// all of them, and — this is the point — SAYS SO when it gives up. A staging verb that
    /// reports "walking" and then quietly stops trying is the exact silent-guard shape that has
    /// cost this project several rounds.
    /// </summary>
    private void RetryPendingDoorWalk()
    {
        if (_walkFramesLeft <= 0)
        {
            return;
        }

        _walkFramesLeft--;

        if (TryWalkThroughDoor(out int failure))
        {
            PluginHost.Log(
                $"door: the walk through 0x{_walkDoorId:X} was accepted after "
                + $"{WalkRetryFrames - _walkFramesLeft} retries");
            _walkFramesLeft = 0;
            return;
        }

        if (_walkFramesLeft == 0)
        {
            PluginHost.Log(
                $"door: gave up queueing the walk through 0x{_walkDoorId:X} after "
                + $"{WalkRetryFrames} frames (last failure {failure}) - nobody is walking anywhere");
            _walkWatchFrames = 0;
        }
    }

    /// <summary>
    /// Says where the walk actually ENDED, once, some seconds after it was queued.
    ///
    /// This exists because of a specific ambiguity that cost a live round: the verb reported
    /// "walking through door 0x4A" and the run produced no transition, and nothing anywhere
    /// could distinguish "it walked into a shut door and stopped at the face" from "it mis-aimed
    /// and walked somewhere else". The answer turned out to be the first, and it took a manual
    /// `where` and a coordinate subtraction to find out. A staging verb that stages something
    /// unobservable has staged nothing.
    ///
    /// It reports only on the frame the watch expires, and it is cancelled by
    /// <see cref="Invalidate"/> — so the success case, where the module changes and the epoch
    /// resets everything, prints nothing at all.
    /// </summary>
    private void WatchDoorWalk()
    {
        if (_walkWatchFrames <= 0)
        {
            return;
        }

        if (_walkWatchFrames == WalkWatchFrames - WalkStallCheckFrame)
        {
            RetryStalledDoorWalk();
        }

        if (--_walkWatchFrames > 0)
        {
            return;
        }

        nint door = Odyssey.ResolveDoor(_walkDoorId);
        nint player = Odyssey.GetPlayerCreaturePointer();

        if (door == 0 || player == 0)
        {
            PluginHost.Log(
                $"door: {WalkWatchFrames} frames after the walk, door 0x{_walkDoorId:X} or the "
                + "player no longer resolves here - something moved worlds");
            return;
        }

        Vec3 here = Odyssey.GetPosition(player);
        float now = Distance(here, Odyssey.GetPosition(door));

        PluginHost.Log(
            $"door: {WalkWatchFrames} frames after the walk the player is {now:F1}m from "
            + $"0x{_walkDoorId:X} (was {_walkStartDistance:F1}m), at "
            + $"{here.X:F1},{here.Y:F1}; aimed at {_walkTarget.X:F1},{_walkTarget.Y:F1}. "
            + "No transition happened - the door is still an obstacle, or the path stopped short.");
    }

    /// <summary>
    /// Fires once, partway through the watch: if the walk has not carried the player near the
    /// door by then, reissue it to the same target. A transition that already happened has
    /// already zeroed <see cref="_walkWatchFrames"/> via <see cref="Invalidate"/>, so this
    /// never fires after a success — only while something queued is still, apparently, going
    /// nowhere.
    /// </summary>
    private void RetryStalledDoorWalk()
    {
        nint door = Odyssey.ResolveDoor(_walkDoorId);
        nint player = Odyssey.GetPlayerCreaturePointer();

        if (door == 0 || player == 0)
        {
            // Already gone from this world one way or another; the frame-0 report (if the
            // watch is still running at all) says so.
            return;
        }

        float now = Distance(Odyssey.GetPosition(player), Odyssey.GetPosition(door));
        if (now <= 1.0f)
        {
            // Close enough that the walk is either about to cross or already did and the
            // transition just has not landed on this frame yet - do not fight it.
            return;
        }

        uint playerId = Odyssey.GetPlayerObjectId();
        if (playerId == 0)
        {
            return;
        }

        bool accepted = Odyssey.MoveToLocation(playerId, _walkTarget, run: true, out int failure);
        PluginHost.Log(accepted
            ? $"door: {WalkStallCheckFrame} frames in, still {now:F1}m from 0x{_walkDoorId:X} - "
              + "reissued the walk to the same point"
            : $"door: {WalkStallCheckFrame} frames in, still {now:F1}m from 0x{_walkDoorId:X}, "
              + $"and the reissue was refused (failure {failure})");
    }

    /// <summary>Door open state 0 = closed. 1 and 2 are the two swing directions, 3 is
    /// destroyed (see <c>Odyssey.GetDoorOpenState</c>).</summary>
    private const byte DoorClosedState = 0;

    /// <summary>Doors one module can hold. The Endar Spire's busiest area has a dozen; a
    /// hundred and twenty-eight is a ceiling, not an expectation, and the sweep stops there
    /// rather than writing past the buffer.</summary>
    private const int DoorSweepCapacity = 128;

    /// <summary>
    /// Makes the local player USE a door, so the ENGINE starts a module transition.
    ///
    /// This is the other half of the cinematic instrument, and the half that can actually
    /// answer the question. <c>moviewarp</c> stages a movie onto a transition the PLUGIN
    /// starts — and on that path <see cref="PluginHost"/> has already destroyed every spawned
    /// body before it raises the move-to-module flag, so the pre-store flush window is never
    /// needed and never tested. A door is the opposite: <c>CSWSDoor::EventHandler</c> raises
    /// <c>CSWSArea::SetTransitionPending(1, id)</c> and starts a half-second fade FIRST
    /// (0x0058C2A0), and only when that fade completes does it do the move-to-module calls
    /// (0x0058C185). Those intervening frames are the entire flush window, and a movie playing
    /// across them is the case docs/research/cinematics.md has never been able to stage.
    ///
    /// ⚠ **THE VERB WALKS THROUGH THE DOOR; IT DOES NOT USE IT**, and the name is a historical
    /// artefact kept because live runs are staged against it. Using a transition door provably
    /// cannot transition, and the binary is unambiguous about why. `CSWSDoor::EventHandler`
    /// switches on the event's type word through the jump table at 0x0058C594/0x0058C5C8, and
    /// only **event 0x1E** reaches the transition arm at 0x0058C0BE (GetIsLinked, then
    /// `linked_to_module` length, then the `trans_pending` test that forks into phase 1 or
    /// phase 2). Using an object posts **0x1A** — both `AIActionUseObject` (0x0057ECC0) and
    /// `AIActionOpenDoor` (0x0057D661) build that type — which lands at 0x0058BD62, the arm
    /// that runs the door's OnUsed script and nothing else.
    ///
    /// Event 0x1E is built in exactly five functions, and every one of them is MOVEMENT:
    /// `CSWSCreature::HandleSubAreaStateChanges` (0x00516020, three sites),
    /// `WalkUpdateLocation` (0x0051BB10), `DriveUpdateLocation` (0x0051CD20),
    /// `WalkUpdateLocation_QuickWalk` (0x0051D9C0) and `AIActionMoveToPoint` (0x0051F4F0).
    /// A KOTOR module transition is a creature CROSSING INTO the door's sub-area, noticed by
    /// its own movement update — which is also why `tp` onto the door does nothing: a
    /// `SetPosition` snap runs no movement update, so nothing ever notices the crossing.
    /// (`HandleSubAreaStateChanges` additionally builds the event only for party index 0, i.e.
    /// the leader — 0x00516263 — which agrees with the door's own `GetIsLeader` check.)
    ///
    /// So this queues `ActionMoveToLocation` to a point just PAST the door
    /// (<see cref="DoorUse.WalkThroughPoint"/>) and lets `AIActionMoveToPoint` post the event
    /// as the player crosses. Everything before it is guard: <see cref="DoorUse.Decide"/> gets
    /// facts measured on this frame and nothing else, and every refusal names what it saw.
    /// </summary>
    private void UseDoor(nint player, uint playerId)
    {
        uint requestedId = block.CheatTarget;
        string text = ReadCheatText();

        nint door;
        string what;
        int doorsSeen = 0;
        int linkedSeen = 0;

        if (requestedId != 0)
        {
            // ResolveDoor is vtable-checked, so a non-door id comes back as 0 — which would
            // read as "missing". Resolve generically first so the answer can tell a wrong id
            // from a right id naming the wrong kind of thing.
            door = Odyssey.ResolveDoor(requestedId);
            what = $"0x{requestedId:X}";

            if (door == 0 && Odyssey.ResolveServerObject(requestedId) != 0)
            {
                Answer(
                    SharedBlock.CheatStatusNoTarget,
                    DoorUse.Explain(DoorUseVerdict.RefuseNotADoor, what, 0f));
                return;
            }
        }
        else if (text.Length > 0)
        {
            byte* tag = block.CheatText;
            int length = 0;
            while (length < SharedBlock.CheatTextLength - 1 && tag[length] != 0)
            {
                length++;
            }

            door = Odyssey.FindDoorByTag(tag, length);
            what = $"'{text}'";
        }
        else
        {
            door = FindNearestTransitionDoor(player, out doorsSeen, out linkedSeen);
            what = "the nearest transition door";
        }

        float distance = door != 0 ? Distance(Odyssey.GetPosition(player), Odyssey.GetPosition(door)) : 0f;

        DoorUseVerdict verdict = DoorUse.Decide(
            hasPlayer: player != 0,
            found: door != 0,
            isDoor: door != 0,
            linked: door != 0 && Odyssey.DoorIsLinked(door),
            openState: door != 0 ? Odyssey.GetDoorOpenState(door) : 0,
            transitionPending: Odyssey.IsAreaTransitionPending(),
            moviePlaying: Odyssey.IsMoviePlaying(),
            distance: distance);

        if (verdict != DoorUseVerdict.Use)
        {
            string detail = DoorUse.Explain(verdict, what, distance);

            // A failed sweep has to say what it swept. "No transition door here" is the wrong
            // module; "no doors at all" is a broken sweep, and a reader with neither number
            // cannot tell them apart.
            if (verdict == DoorUseVerdict.RefuseMissing && requestedId == 0 && text.Length == 0)
            {
                detail = DoorUse.DescribeSweep(doorsSeen, linkedSeen);
            }

            // The link flags are the one refusal worth the extra byte: 0 means the module data
            // never linked this door, which is a fact about the module and not about us.
            if (verdict == DoorUseVerdict.RefuseNotLinked)
            {
                PluginHost.Log(
                    $"door: refusing to use {what} - linked_to_flags reads "
                    + $"{Odyssey.GetDoorLinkFlags(door)} (1 or 2 would be a module transition)");
            }

            Answer(SharedBlock.CheatStatusRefused, detail);
            return;
        }

        uint doorId = *(uint*)(door + 0x04);

        byte* destination = stackalloc byte[SharedBlock.TravelNameLength];
        int destinationLength = Odyssey.ReadDoorLinkedModule(door, destination, SharedBlock.TravelNameLength);
        string module = destinationLength > 0
            ? Encoding.ASCII.GetString(destination, destinationLength)
            : "(unreadable)";

        byte* waypoint = stackalloc byte[SharedBlock.TravelNameLength];
        int waypointLength = Odyssey.ReadDoorLinkedWaypoint(door, waypoint, SharedBlock.TravelNameLength);
        string entry = waypointLength > 0
            ? Encoding.ASCII.GetString(waypoint, waypointLength)
            : "(none)";

        // Logged BEFORE anything is done, because what follows may take the main thread away:
        // if a movie is queued, the transition parks it inside WaitForMovie and nothing written
        // afterwards would survive the window to be written at all. The OPEN STATE is part of
        // the line for a reason paid for live - a walk that stopped short was indistinguishable
        // from a walk that mis-aimed, and that ambiguity cost a whole round.
        byte openState = Odyssey.GetDoorOpenState(door);
        PluginHost.Log(
            $"door: 0x{doorId:X} at {distance:F1}m links to module '{module}' at waypoint "
            + $"'{entry}'; open state {openState}"
            + (Odyssey.IsDoorLocked(door) ? ", LOCKED" : string.Empty));

        // A CLOSED DOOR IS A WALL. Measured live: the walk-through ran and stopped 1.8 m short
        // of the door face, because the closed door is a pathing obstacle and the path ended
        // at it. So open it first, through the engine's own OpenDoor - which picks the swing
        // side from where the opener stands, moves the linked twin, and runs the OnOpen script
        // - and commit the swing at once so the walkmesh is open on this frame.
        if (openState == DoorClosedState)
        {
            byte opened = Odyssey.OpenDoorFully(door, playerId);

            PluginHost.Log(opened != DoorClosedState
                ? $"door: opened 0x{doorId:X} (state {openState} -> {opened}) so the walk has "
                  + "somewhere to go"
                : $"door: 0x{doorId:X} REFUSED to open (still state {opened}) - the walk will "
                  + "stop at its face, as it did before");

            openState = opened;
        }

        bool together = GatherPartyIfScattered();

        // WALK THROUGH IT. Not "use" it — see WalkThroughDoor for why using a transition door
        // provably cannot transition.
        Vec3 through = DoorUse.WalkThroughPoint(
            Odyssey.GetPosition(player),
            Odyssey.GetPosition(door),
            Odyssey.GetOrientation(door),
            DoorUse.WalkThroughOvershoot);

        _walkDoorId = doorId;
        _walkTarget = through;
        _walkFramesLeft = WalkRetryFrames;
        _walkWatchFrames = WalkWatchFrames;
        _walkStartDistance = distance;

        if (!TryWalkThroughDoor(out int failure))
        {
            // Failure 4 is the script VM being mid-call, which is transient and frame-shaped —
            // the same refusal the teardown destroys hit. Keep the retry armed and say so
            // rather than reporting a walk that never started.
            Answer(
                failure == 4 ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
                failure == 4
                    ? $"door 0x{doorId:X}: VM busy, retrying the walk for {WalkRetryFrames} frames"
                    : $"the engine refused the walk to door 0x{doorId:X} (failure {failure})");

            if (failure != 4)
            {
                _walkFramesLeft = 0;
                _walkWatchFrames = 0;
            }

            return;
        }

        // Accepted first time: nothing left to retry.
        _walkFramesLeft = 0;

        Answer(
            SharedBlock.CheatStatusApplied,
            together
                ? $"walking through door 0x{doorId:X} ({distance:F1}m) -> '{module}'"
                : $"door 0x{doorId:X} -> '{module}' BUT the party is not together");
    }

    /// <summary>
    /// ROUND 57: opens an ORDINARY door — one <c>usedoor</c> refuses by design (see its own
    /// doc comment: a bare <c>usedoor</c> must be incapable of doing anything except starting
    /// a module transition). Built because docs/research/shared-state-audit.md round 56 left
    /// the non-transition case genuinely untested: no working <c>walk</c>/click primitive in
    /// the rig could reach one, and every door <c>usedoor</c>'s own sweep found in
    /// END_M01AA/AB that is not linked to a module is refused outright.
    ///
    /// Calls <see cref="Odyssey.OpenDoorFully"/> — the SAME engine door-use action
    /// <see cref="UseDoor"/> already calls before its walk — so the ordinary open-state mirror
    /// (<c>DoorController</c>) is what actually gets exercised, not a poke of the open-state
    /// byte. Per <see cref="UseDoor"/>'s own §1465-1470 finding, an engine "open" call posts
    /// event 0x1A (OnUsed), never 0x1E (the movement-crossing transition event) — so this is
    /// safe to point at a LINKED door too; it will only ever open it, never transition it.
    ///
    /// Deliberately does not walk the player through it or gather the party: an ordinary door
    /// has nowhere to transition to, so neither concern applies.
    /// </summary>
    private void OpenDoor(nint player, uint playerId)
    {
        uint requestedId = block.CheatTarget;
        string text = ReadCheatText();

        nint door;
        string what;
        int doorsSeen = 0;

        if (requestedId != 0)
        {
            door = Odyssey.ResolveDoor(requestedId);
            what = $"0x{requestedId:X}";

            if (door == 0 && Odyssey.ResolveServerObject(requestedId) != 0)
            {
                Answer(
                    SharedBlock.CheatStatusNoTarget,
                    DoorUse.Explain(DoorUseVerdict.RefuseNotADoor, what, 0f));
                return;
            }
        }
        else if (text.Length > 0)
        {
            byte* tag = block.CheatText;
            int length = 0;
            while (length < SharedBlock.CheatTextLength - 1 && tag[length] != 0)
            {
                length++;
            }

            door = Odyssey.FindDoorByTag(tag, length);
            what = $"'{text}'";
        }
        else
        {
            door = FindNearestAnyDoor(player, out doorsSeen);
            what = "the nearest door";
        }

        float distance = door != 0 ? Distance(Odyssey.GetPosition(player), Odyssey.GetPosition(door)) : 0f;

        // Same decision table usedoor uses, with `linked` forced true: that gate exists
        // specifically to keep a bare `usedoor` incapable of opening an ordinary door, and
        // opening an ordinary door is this verb's entire purpose.
        DoorUseVerdict verdict = DoorUse.Decide(
            hasPlayer: player != 0,
            found: door != 0,
            isDoor: door != 0,
            linked: true,
            openState: door != 0 ? Odyssey.GetDoorOpenState(door) : 0,
            transitionPending: Odyssey.IsAreaTransitionPending(),
            moviePlaying: Odyssey.IsMoviePlaying(),
            distance: distance);

        if (verdict != DoorUseVerdict.Use)
        {
            string detail = DoorUse.Explain(verdict, what, distance);

            if (verdict == DoorUseVerdict.RefuseMissing && requestedId == 0 && text.Length == 0)
            {
                detail = doorsSeen == 0
                    ? "the sweep found no doors at all in this module"
                    : $"the sweep found {doorsSeen} door(s) but none close enough to open";
            }

            Answer(SharedBlock.CheatStatusRefused, detail);
            return;
        }

        uint doorId = *(uint*)(door + 0x04);
        byte before = Odyssey.GetDoorOpenState(door);
        byte after = Odyssey.OpenDoorFully(door, playerId);

        PluginHost.Log(
            $"door: opened 0x{doorId:X} (state {before} -> {after}) at {distance:F1}m"
            + (Odyssey.IsDoorLocked(door) ? ", LOCKED" : string.Empty));

        Answer(
            after != before ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            after != before
                ? $"door 0x{doorId:X}: opened (state {before} -> {after})"
                : $"door 0x{doorId:X}: REFUSED to open (still state {after})");
    }

    /// <summary>
    /// CONTAINER-SYNC LIVE-TEST PRIMITIVE (2026-08-22): makes the local player USE a placeable
    /// through <c>Odyssey.UseObject</c> — the engine's own client-to-server "I clicked that"
    /// input pipe, so the real <c>AIActionUseObject</c>/<c>CSWSPlaceable::EventHandler</c>/
    /// <c>OpenInventory</c> path runs exactly as a real click would, which is what makes this a
    /// meaningful test of <see cref="ContainerUseHook"/> and <see cref="InteractionLockController"/>
    /// rather than a synthetic poke. Unlike <see cref="UseDoor"/>/<see cref="OpenDoor"/> there is
    /// no walk to queue and no walkmesh coupling — <c>Odyssey.UseObject</c> is a single fire-and-
    /// forget send, so this either reports the send succeeded or explains why it could not be
    /// attempted at all (range, resolution). See docs/research/container-sync.md.
    /// </summary>
    private void UsePlaceable(nint player)
    {
        uint requestedId = block.CheatTarget;
        string text = ReadCheatText();

        nint placeable;
        string what;
        int seen = 0;

        if (requestedId != 0)
        {
            placeable = Odyssey.ResolvePlaceable(requestedId);
            what = $"0x{requestedId:X}";

            if (placeable == 0)
            {
                // ROUND 68 (docs/research/container-sync.md §6): a corpse is not a
                // CSWSPlaceable — clicking it in a real game sends the SAME UseObject input
                // (Odyssey.UseObject is already object-type-agnostic; only THIS cheat handler
                // used to gate it to placeables). Explicit-id/tag targets fall through to a
                // raw object use so the live-test rig can exercise a corpse (or anything else
                // an id names) through the identical client-to-server pipe a placeable uses,
                // without adding a second cheat verb. The bare sweep (no id/tag) keeps
                // refusing non-placeables — it exists to find containers, not to guess what a
                // nearby id might be.
                nint rawObject = Odyssey.ResolveServerObject(requestedId);
                if (rawObject == 0)
                {
                    Answer(SharedBlock.CheatStatusNoTarget, $"{what} does not resolve to anything here");
                    return;
                }

                float rawDistance = Distance(Odyssey.GetPosition(player), Odyssey.GetPosition(rawObject));
                if (rawDistance > PlaceableUseRange)
                {
                    Answer(
                        SharedBlock.CheatStatusRefused,
                        $"object {what} is {rawDistance:F1}m away, too far to use");
                    return;
                }

                bool rawSent = Odyssey.UseObject(requestedId);
                PluginHost.Log(
                    $"object: use-object sent for {what} (not a placeable) at {rawDistance:F1}m "
                    + $"({(rawSent ? "ok" : "FAILED")})");

                Answer(
                    rawSent ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
                    rawSent
                        ? $"object {what}: use sent (not a placeable — a corpse or other creature "
                            + "goes through the same UseObject pipe)"
                        : $"object {what}: UseObject failed (no client or no message object)");
                return;
            }
        }
        else if (text.Length > 0)
        {
            byte* tag = block.CheatText;
            int length = 0;
            while (length < SharedBlock.CheatTextLength - 1 && tag[length] != 0)
            {
                length++;
            }

            placeable = OdysseyPlaceables.FindPlaceableByTag(tag, length);
            what = $"'{text}'";
        }
        else
        {
            // Logged FIRST, unconditionally: a bare `useplaceable` is also the live-test
            // script's way to discover ids, and it should see the whole sweep even when the
            // nearest-container search below comes up empty.
            OdysseyPlaceables.LogPlaceableSweep(player);
            placeable = OdysseyPlaceables.FindNearestContainer(player, out seen);
            what = "the nearest container";
        }

        if (placeable == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, seen == 0
                ? "the sweep found no placeables at all in this module"
                : $"the sweep found {seen} placeable(s) but none close enough / none with inventory");
            return;
        }

        uint placeableId = OdysseyPlaceables.GetPlaceableObjectId(placeable);
        float distance = Distance(Odyssey.GetPosition(player), Odyssey.GetPosition(placeable));

        if (distance > PlaceableUseRange)
        {
            Answer(
                SharedBlock.CheatStatusRefused,
                $"placeable 0x{placeableId:X} ({what}) is {distance:F1}m away, too far to use");
            return;
        }

        bool sent = Odyssey.UseObject(placeableId);
        PluginHost.Log(
            $"placeable: use-object sent for 0x{placeableId:X} at {distance:F1}m ({(sent ? "ok" : "FAILED")})");

        Answer(
            sent ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            sent
                ? $"placeable 0x{placeableId:X}: use sent (the container/interaction-lock "
                    + "machinery reacts to it like a real click)"
                : $"placeable 0x{placeableId:X}: UseObject failed (no client or no message object)");
    }

    /// <summary>
    /// LIVE-PASS ADDITION (2026-08-22): the close-side counterpart to <see cref="UsePlaceable"/>.
    /// <c>useplaceable</c> only replicates the network half of a real click, so the engine's own
    /// container GUI never actually shows on this instance — leaving no panel for an Escape
    /// keystroke, or a real player, to close, which is exactly what the container-sync
    /// live-verify pass needed and did not have. This calls the real
    /// <c>CSWSPlaceable::CloseInventory</c> directly
    /// (<see cref="OdysseyPlaceables.CloseInventoryReal"/> — VERIFIED disasm, both stack args 0
    /// is the safe player-agnostic path, see that method's own remarks), so the mirror on every
    /// OTHER instance reacts exactly as it would to a real close: the peer-held container drops
    /// out of <see cref="InteractionLockController"/>'s set, and <see cref="ContainerUseHook"/>
    /// stops refusing it. See docs/research/container-sync.md.
    /// </summary>
    private void ClosePlaceable(nint player)
    {
        uint requestedId = block.CheatTarget;
        string text = ReadCheatText();

        nint placeable;
        string what;
        int seen = 0;

        if (requestedId != 0)
        {
            placeable = Odyssey.ResolvePlaceable(requestedId);
            what = $"0x{requestedId:X}";

            if (placeable == 0 && Odyssey.ResolveServerObject(requestedId) != 0)
            {
                Answer(SharedBlock.CheatStatusNoTarget, $"{what} exists but is not a placeable");
                return;
            }
        }
        else if (text.Length > 0)
        {
            byte* tag = block.CheatText;
            int length = 0;
            while (length < SharedBlock.CheatTextLength - 1 && tag[length] != 0)
            {
                length++;
            }

            placeable = OdysseyPlaceables.FindPlaceableByTag(tag, length);
            what = $"'{text}'";
        }
        else
        {
            placeable = OdysseyPlaceables.FindNearestOpenContainer(player, out seen);
            what = "the nearest open container";
        }

        if (placeable == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, seen == 0
                ? "the sweep found no placeables at all in this module"
                : $"the sweep found {seen} placeable(s) but none of them are open");
            return;
        }

        uint placeableId = OdysseyPlaceables.GetPlaceableObjectId(placeable);

        if (!Odyssey.IsPlaceableOpen(placeable))
        {
            Answer(SharedBlock.CheatStatusRefused, $"placeable 0x{placeableId:X} ({what}) is not open");
            return;
        }

        OdysseyPlaceables.CloseInventoryReal(placeable, player);
        PluginHost.Log($"placeable: close-inventory called for 0x{placeableId:X}");

        Answer(
            SharedBlock.CheatStatusApplied,
            $"placeable 0x{placeableId:X}: closed (close animation played, +0x338 cleared)");
    }

    /// <summary>How close the player has to be for <c>useplaceable</c> to send the use — the
    /// engine resolves range/reach itself once the input reaches it, but a request aimed at a
    /// container across the map is a mistake worth catching here rather than silently sent.</summary>
    private const float PlaceableUseRange = 15f;

    /// <summary>Sweeps the module for doors and returns the nearest one, with no linked-to-
    /// module filter — the opposite of <see cref="FindNearestTransitionDoor"/>, and the whole
    /// reason <see cref="OpenDoor"/> exists.</summary>
    private static nint FindNearestAnyDoor(nint player, out int doorsSeen)
    {
        nint* doors = stackalloc nint[DoorSweepCapacity];
        doorsSeen = Odyssey.CollectDoors(doors, DoorSweepCapacity);

        Vec3 here = Odyssey.GetPosition(player);
        nint best = 0;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < doorsSeen; i++)
        {
            float distance = Distance(here, Odyssey.GetPosition(doors[i]));
            if (distance >= bestDistance)
            {
                continue;
            }

            best = doors[i];
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>
    /// Brings the party to the player when the engine says it is scattered, and reports
    /// whether that worked. Returns the state of the gate as it stands after the attempt.
    ///
    /// WHY THIS IS PART OF USING A DOOR. `CSWSDoor::EventHandler` tests
    /// `CSWParty::IsPartyTogether` at 0x0058C228 and, when it is false, runs the script
    /// `k_trg_transfail` INSTEAD of transitioning — a failure with no trace at all: the door
    /// is used, no fade starts, no module is stored. Measured live on 2026-08-15, exactly
    /// that: the PC teleported to the door, Trask stayed a hundred metres back at the module
    /// start, and the transition never happened.
    ///
    /// Gathering here rather than leaving it to the caller is what makes the verb
    /// self-sufficient, and it is also what a player does — you walk to a door WITH your
    /// party. It goes through the engine's own `PositionParty`, the very call a module
    /// transition makes, so the walkmesh snap and the formation are the engine's problem.
    ///
    /// ⚠ Host-side staging only, and deliberately unconditional about that: companions are
    /// host-authoritative, so on a mirroring instance this moves bodies whose owner will
    /// republish them. That is acceptable for a debug verb and would not be for a feature.
    /// </summary>
    private static bool GatherPartyIfScattered()
    {
        bool wasTogether = Odyssey.IsPartyTogether();
        bool gatherRan = false;
        int members = 0;
        bool skipFlagWasSet = false;

        if (!wasTogether)
        {
            PluginHost.Log(
                "door: the engine says the party is NOT together, and a door transition is gated "
                + "on that (0x0058C228) - gathering it first");

            gatherRan = Odyssey.GatherParty(out members, out skipFlagWasSet);
        }

        PartyGateOutcome outcome = DoorUse.ClassifyGate(
            wasTogether, gatherRan, togetherNow: !wasTogether && Odyssey.IsPartyTogether());

        switch (outcome)
        {
            case PartyGateOutcome.GatheredAndTogether:
                PluginHost.Log(
                    $"door: gathered {members} party member(s) to you"
                    + (skipFlagWasSet
                        ? " (the table's one-shot skip flag was set, so it was positioned twice)"
                        : string.Empty));
                break;

            case PartyGateOutcome.GatheredButStillApart:
                PluginHost.Log(
                    $"door: gathered {members} party member(s) and the engine STILL says the "
                    + "party is not together - this use will run k_trg_transfail and move nobody");
                break;

            case PartyGateOutcome.CouldNotGather:
                PluginHost.Log(
                    "door: could not gather the party (no player, no party object, or no table) "
                    + "- this use will run k_trg_transfail and move nobody");
                break;
        }

        return DoorUse.GateWillPass(outcome);
    }

    /// <summary>
    /// Sweeps the module for doors and returns the nearest one that is a module transition,
    /// with the counts so a failure can describe itself.
    ///
    /// Linked-only is the guard, not a convenience: a bare <c>usedoor</c> has to be incapable
    /// of doing anything except starting a transition, and an ordinary door two metres away
    /// would otherwise win the distance comparison and merely open — which looks exactly like
    /// the command working.
    /// </summary>
    private static nint FindNearestTransitionDoor(nint player, out int doorsSeen, out int linkedSeen)
    {
        nint* doors = stackalloc nint[DoorSweepCapacity];
        doorsSeen = Odyssey.CollectDoors(doors, DoorSweepCapacity);
        linkedSeen = 0;

        Vec3 here = Odyssey.GetPosition(player);
        nint best = 0;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < doorsSeen; i++)
        {
            if (!Odyssey.DoorIsLinked(doors[i]))
            {
                continue;
            }

            linkedSeen++;

            float distance = Distance(here, Odyssey.GetPosition(doors[i]));
            if (distance >= bestDistance)
            {
                continue;
            }

            best = doors[i];
            bestDistance = distance;
        }

        return best;
    }

    private static float Distance(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>The engine's queue in words, for an answer a reader can act on.</summary>
    private static string DescribeQueue(int queued)
    {
        if (queued <= 0)
        {
            return "nothing";
        }

        StringBuilder names = new();
        byte* buffer = stackalloc byte[MovieStaging.MaxNameLength + 1];

        for (int i = 0; i < queued; i++)
        {
            int length = Odyssey.ReadQueuedTransitionMovie(i, buffer, MovieStaging.MaxNameLength);
            if (length <= 0)
            {
                continue;
            }

            if (names.Length > 0)
            {
                names.Append(", ");
            }

            names.Append(Encoding.ASCII.GetString(buffer, length));
        }

        return names.Length > 0 ? names.ToString() : $"{queued} unreadable";
    }

    /// <summary>The request's text field as a string, cut at the first null.</summary>
    private string ReadCheatText()
    {
        byte* text = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
        {
            length++;
        }

        return length == 0 ? string.Empty : Encoding.ASCII.GetString(text, length);
    }

    /// <summary>
    /// Reads one live item's per-instance identity, and pokes whichever of the five fields
    /// the request named — through <see cref="Odyssey.ApplyItemIdentity"/>, the very function
    /// the ground-item channel calls on a peer's dropped item.
    ///
    /// ⚠ Two jobs on purpose, and the same code path for both. A request that names no field
    /// is a pure read; a request that names some reads first, overlays them, and writes the
    /// whole set back — so the "leave this alone" case is a value the writer was handed
    /// rather than a branch inside the writer. That matters because the writer is the thing
    /// under test: a `setitem` that took a different route to memory would prove nothing
    /// about what an arriving item gets.
    ///
    /// Why a staging cheat is the right shape for this. The apply path only ever runs on an
    /// item the far side has genuinely modified, and KOTOR gives a script no way to modify
    /// one: there is no drop UI, the workbench is a mouse-driven panel, and a blueprint
    /// spawned by `dropitem` is stock by definition. So an unattended run could produce a
    /// SENDER but never an interesting sender, which is precisely why the feature shipped
    /// with its whole apply half unmeasured. This makes the interesting sender.
    ///
    /// The answer always reports the fields READ BACK out of the item afterwards, never the
    /// values that were asked for.
    /// </summary>
    private void SetItem()
    {
        uint itemId = block.CheatTarget;
        if (itemId is 0 or Odyssey.ObjectInvalid)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "setitem needs an item object id");
            return;
        }

        if (!Odyssey.TryReadItemIdentityFields(itemId, out Odyssey.ItemIdentityFields before))
        {
            // The resolve note names what it saw — no such object, an id at neither of the
            // two layouts' offsets, or a vtable belonging to some other class entirely.
            Answer(SharedBlock.CheatStatusNoTarget, Odyssey.LastItemResolveNote);
            return;
        }

        int stack = block.CheatAmount;
        uint charges = block.CheatItemCharges;
        uint maxCharges = block.CheatItemMaxCharges;
        uint upgrades = block.CheatItemUpgrades;
        uint wireFlags = block.CheatItemFlags;

        bool changes = stack > 0
            || charges != SharedBlock.CheatFieldUnchanged
            || maxCharges != SharedBlock.CheatFieldUnchanged
            || upgrades != SharedBlock.CheatFieldUnchanged
            || wireFlags != SharedBlock.CheatFieldUnchanged;

        if (!changes)
        {
            Answer(SharedBlock.CheatStatusApplied, Describe(itemId, before));
            return;
        }

        // The engine's own bit numbers going in, the wire's flag numbers coming out of the
        // request: ApplyItemIdentity takes the WIRE shape (it is fed by the block's item
        // slot), so an unchanged flags field has to be re-encoded from the item's live bits
        // rather than passed through.
        uint flags = wireFlags != SharedBlock.CheatFieldUnchanged
            ? wireFlags | SharedBlock.ItemIdentityKnown
            : ToWireFlags(before.BitFlags);

        bool applied = Odyssey.ApplyItemIdentity(
            itemId,
            upgrades != SharedBlock.CheatFieldUnchanged ? upgrades : before.Upgrades,
            stack > 0 ? (uint)stack : before.StackSize,
            charges != SharedBlock.CheatFieldUnchanged ? charges : before.Charges,
            maxCharges != SharedBlock.CheatFieldUnchanged ? maxCharges : before.MaxCharges,
            flags,
            out Odyssey.ItemIdentityFields after);

        if (!applied)
        {
            // It resolved a moment ago, so this is the interesting failure: something freed
            // the item between the read and the write. Say that rather than repeating "no
            // target", which would read as a typo in the id.
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"0x{itemId:X} stopped resolving mid-poke: {Odyssey.LastItemResolveNote}");
            return;
        }

        // The full before/after goes to the LOG, where there is room for it, and the short
        // "what it holds now" goes in the block's 64-byte detail field. Splitting them is not
        // cosmetic: SetCheatDetail truncates without complaint, and the field it would have
        // cut off is the upgrade mask — the one number this whole feature exists to carry.
        PluginHost.Log(
            $"setitem 0x{itemId:X}: stack {before.StackSize}->{after.StackSize}, "
            + $"charges {before.Charges}/{before.MaxCharges}->{after.Charges}/{after.MaxCharges}, "
            + $"upgrades 0x{before.Upgrades:X}->0x{after.Upgrades:X}, "
            + $"bits 0x{before.BitFlags:X}->0x{after.BitFlags:X}, "
            + $"possessor 0x{after.Possessor:X}");

        Answer(SharedBlock.CheatStatusApplied, Describe(itemId, after));
    }

    /// <summary>
    /// One item's state in a sentence short enough to survive the block's 64-byte detail
    /// field, worst case included: a 0x7FFFFFFF id with every field at its maximum still
    /// fits. Hex without the 0x prefix for the two mask fields, because those four saved
    /// characters are the difference.
    /// </summary>
    private static string Describe(uint itemId, Odyssey.ItemIdentityFields fields) =>
        $"0x{itemId:X}: stk {fields.StackSize} chg {fields.Charges}/{fields.MaxCharges} "
        + $"upg {fields.Upgrades:X} bits {fields.BitFlags:X}";

    /// <summary>
    /// The engine's +0x288 bits as the wire's identity flags. The inverse of what
    /// <see cref="Odyssey.ApplyItemIdentity"/> does on the way in, and it deliberately covers
    /// only the three bits this channel carries — the other five real bits in that dword
    /// (dropable, pickpocketable, new-item, DELETING) are engine bookkeeping that
    /// docs/research/item-identity.md §5 explains we must not move.
    /// </summary>
    private static uint ToWireFlags(uint bits)
    {
        uint flags = SharedBlock.ItemIdentityKnown;

        if ((bits & (1u << 0)) != 0)
        {
            flags |= SharedBlock.ItemIdentityIdentified;
        }

        if ((bits & (1u << 5)) != 0)
        {
            flags |= SharedBlock.ItemIdentityStolen;
        }

        if ((bits & (1u << 6)) != 0)
        {
            flags |= SharedBlock.ItemIdentityNonEquippable;
        }

        return flags;
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
