namespace KotorCoop.Plugin;

// The `attack` live-test primitive (docs/research/combat-participation.md §15): orders the
// LOCAL PLAYER's own creature to attack a target through the engine's own ActionAttack action
// queue (Odyssey.AttackTarget - the SAME verified ExecuteCommandAttack route this plugin
// already drives for a mirrored companion/enemy under local_combat, round 11/71), so:
//   (1) the sticky-aggro test ("the joiner out-damages the host; the enemy should stay on the
//       joiner") can finally run on a rig that cannot click, and
//   (2) a live `cast` can be exercised - effect-sync.md §14.8/§14.9 found a queued Force power
//       cast is only promoted to a real FP-spending cast by the CASTER'S OWN active combat
//       round, which this verb is what arms.
// Never bypasses PhantomHitHook's damage-forwarding path: this only queues the same engine
// action a real click produces, nothing about damage application is touched here.
internal sealed unsafe partial class CheatController
{
    /// <summary>Matches TestDriveController.PlayerAttackMeleeRange (round 71,
    /// docs/research/combat-participation.md §15): a queued ActionAttack for a
    /// PLAYER-CONTROLLED creature never closes the gap on its own (that only happens for an
    /// AI-driven attacker's own per-tick processing) - a real click's accompanying move-to-click
    /// path is what normally does it. Without staging the range here first, the order arms
    /// (head action becomes 0x3E Combat) but the target's hp never moves, exactly the round-68
    /// failure this constant's sibling was built to close.</summary>
    private const float AttackMeleeRange = 1.5f;

    private void Attack(nint player, uint playerId)
    {
        uint targetId = block.CheatTarget;

        // CheatTarget == 0 is "stop" (ConsoleCommand.Attack.cs's `attack stop`) - the
        // counterpart to the order below. Clearing queued actions leaves an active round
        // running; use the engine's real CancelCombat route and prove its target was cleared.
        if (targetId == 0)
        {
            bool cleared = Odyssey.StopLocalPlayerCombat(playerId, out string stopDiagnostic);
            Answer(
                cleared ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
                cleared
                    ? $"attack: stopped 0x{playerId:X}'s combat ({stopDiagnostic})"
                    : $"attack: could not stop 0x{playerId:X}'s combat ({stopDiagnostic})");
            return;
        }

        if (targetId == playerId)
        {
            Answer(SharedBlock.CheatStatusRefused, "attack: cannot target yourself");
            return;
        }

        nint targetCreature = Odyssey.ResolveServerObject(targetId);
        if (targetCreature == 0 || *(nint*)targetCreature != CreatureFinder.CreatureVtable)
        {
            Answer(SharedBlock.CheatStatusNoTarget, $"attack: 0x{targetId:X} is not a live creature here");
            return;
        }

        int headBefore = Odyssey.GetHeadActionType(player);

        bool unstaged = block.CheatAmount == 1;
        if (block.CheatAmount is not (0 or 1))
        {
            Answer(SharedBlock.CheatStatusRefused, "attack: unknown range mode");
            return;
        }
        // A ranged fixture must keep its real distance from a downed teammate. The
        // native client attack is still responsible for dice, movement and damage.
        if (!unstaged) CloseLocalPlayerToMeleeRange(player, targetCreature, targetId);

        // combat-participation.md §16: the client-driven route (the exact function a real
        // click-to-attack calls) tried FIRST; the raw VM ActionAttackObject route
        // (Odyssey.AttackTarget) only when the client path is unavailable (no client app, the
        // player is not the client's own controlled creature, or the verified bytes no longer
        // match). Landing exactly one hit and then going silent (2026-09-25 live result) is
        // what motivated adding the client route at all - see the doc section for why.
        bool viaClient = Odyssey.AttackTargetViaClient(playerId, targetId, out string clientDiagnostic);
        int headAfter = Odyssey.GetHeadActionType(player);

        if (viaClient)
        {
            PluginHost.Log(
                $"attack: 0x{playerId:X} -> 0x{targetId:X} via client route - {clientDiagnostic} "
                + $"(head action 0x{headBefore:X} -> 0x{headAfter:X})");
            Answer(
                SharedBlock.CheatStatusApplied,
                $"attack 0x{targetId:X} sent via client route (head action 0x{headBefore:X} -> 0x{headAfter:X})");
            return;
        }

        if (unstaged)
        {
            Answer(SharedBlock.CheatStatusEngineRefused, $"attack ranged: client route unavailable ({clientDiagnostic})");
            return;
        }

        PluginHost.Log($"attack: client route unavailable ({clientDiagnostic}), falling back to the VM route");

        bool ok = Odyssey.AttackTarget(playerId, targetId, out int failure);
        headAfter = Odyssey.GetHeadActionType(player);

        if (!ok)
        {
            string reason = failure switch
            {
                1 => "no VM available",
                2 => "no routine table / attack handler bytes do not match the verified build",
                3 => "a script currently owns the VM stack - try again next frame",
                4 => "VM stack push refused",
                5 => "the engine refused the attack order",
                _ => $"engine call refused (failure {failure})",
            };

            PluginHost.Log(
                $"attack: 0x{playerId:X} -> 0x{targetId:X} refused - {reason} "
                + $"(head action 0x{headBefore:X} -> 0x{headAfter:X})");
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"attack: 0x{targetId:X} refused - {reason} ({clientDiagnostic}; "
                + $"head action 0x{headBefore:X} -> 0x{headAfter:X})");
            return;
        }

        PluginHost.Log(
            $"attack: 0x{playerId:X} ordered to attack 0x{targetId:X} via VM route "
            + $"(head action 0x{headBefore:X} -> 0x{headAfter:X})");
        Answer(
            SharedBlock.CheatStatusApplied,
            $"queued attack on 0x{targetId:X} via VM route (head action 0x{headBefore:X} -> 0x{headAfter:X})");
    }

    /// <summary>
    /// Snaps the local player to melee range of the target BEFORE the attack order is armed —
    /// mirrors TestDriveController.CloseToMeleeRangeIfNeeded (round 71), because
    /// <see cref="Odyssey.SnapTo"/> clears the action queue as part of committing the position
    /// (ClearAllActionsFn), which would wipe an attack order queued before it. A no-op when
    /// already within <see cref="AttackMeleeRange"/>, so a live test standing next to its
    /// target already is left exactly where it stood.
    /// </summary>
    private void CloseLocalPlayerToMeleeRange(nint player, nint targetCreature, uint targetId)
    {
        Vec3 playerPos = Odyssey.GetPosition(player);
        Vec3 targetPos = Odyssey.GetPosition(targetCreature);

        float dx = playerPos.X - targetPos.X;
        float dy = playerPos.Y - targetPos.Y;
        float planarDistance = MathF.Sqrt((dx * dx) + (dy * dy));
        if (planarDistance <= AttackMeleeRange)
        {
            return;
        }

        // Approach from wherever the player already is (degenerate only when the two bodies
        // are exactly coincident, which the range check above already excludes).
        float scale = planarDistance > 0.001f ? (AttackMeleeRange / planarDistance) : 0f;
        Vec3 snapPos = new()
        {
            X = targetPos.X + (dx * scale),
            Y = targetPos.Y + (dy * scale),
            Z = playerPos.Z,
        };

        Odyssey.SnapTo(player, snapPos);

        PluginHost.Log(
            $"attack: closed range to 0x{targetId:X} ({planarDistance:F2}m -> "
            + $"{AttackMeleeRange:F2}m) before arming the attack");
    }
}
