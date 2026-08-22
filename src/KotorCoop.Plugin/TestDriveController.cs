namespace KotorCoop.Plugin;

/// <summary>
/// Drives the local player from the shared block — talk to an NPC, walk to a point — so
/// co-op behaviour that depends on a player DOING something can be exercised without
/// injected keyboard and mouse input.
///
/// That matters more here than it sounds: KOTOR tracks its own cursor from relative
/// DirectInput deltas rather than the OS pointer, and injected input is a focus lottery
/// (traps 22 and 25), so "click the NPC" is exactly the step automation keeps failing at.
/// These hooks run the engine's own calls instead — <c>StartDialogueWith</c> sends the
/// same player-to-server dialogue request a click produces, so the whole conversation
/// path, including the exclusivity gate, is exercised for real rather than simulated.
///
/// Test-only surface, like the pickup and phantom-hit hooks beside it: agent-written,
/// serial-edged, and inert until something writes a serial.
/// </summary>
internal sealed unsafe class TestDriveController(SharedBlockView block)
{
    private uint _talkSerial;
    private uint _walkSerial;
    private uint _attackSerial;
    private uint _barkSerial;

    // ROUND 58 DIAGNOSTIC: once a real attack order is armed, watch both bodies for a bounded
    // window instead of trusting "the round timer was set once" as proof anything actually
    // swings. See shared-state-audit.md round 56/57 — armed rounds that never produced a hit.
    private uint _probeAttacker;
    private uint _probeTarget;
    private double _probeUntil = -1;
    private double _probeLastLogAt;
    private const double ProbeDurationSeconds = 20.0;
    private const double ProbeLogIntervalSeconds = 1.0;

    public void Invalidate()
    {
        // Serials are NOT reset: a module change must not replay the last request.
        _talkSerial = block.TestTalkSerial;
        _walkSerial = block.TestWalkSerial;
        _attackSerial = block.TestAttackSerial;
        _barkSerial = block.TestBarkSerial;
    }

    public void OnFrame()
    {
        HandleTalk();
        HandleWalk();
        HandleAttack();
        HandleBark();
        ProbeAttack();
    }

    /// <summary>
    /// ROUND 58 DIAGNOSTIC: runs every frame for <see cref="ProbeDurationSeconds"/> after
    /// <see cref="HandleAttack"/> arms a real order, logging once a second so an "armed but
    /// silent" round leaves a curve instead of one snapshot. Deliberately independent of
    /// whether the order came from this controller's own serial or whatever else calls
    /// <see cref="Odyssey.AttackTarget"/> — see the arm site in <see cref="HandleAttack"/>.
    /// </summary>
    private void ProbeAttack()
    {
        if (_probeUntil < 0)
        {
            return;
        }

        double now = Environment.TickCount64 / 1000.0;
        if (now >= _probeUntil)
        {
            PluginHost.Log($"attack probe: window closed for 0x{_probeAttacker:X} -> 0x{_probeTarget:X}");
            _probeUntil = -1;
            return;
        }

        if (now - _probeLastLogAt < ProbeLogIntervalSeconds)
        {
            return;
        }

        _probeLastLogAt = now;

        nint attacker = Odyssey.ResolveServerObject(_probeAttacker);
        nint target = Odyssey.ResolveServerObject(_probeTarget);

        Odyssey.GetAttackDiagnostics(attacker, out int aCommandable, out int aMovePermitted,
            out int aHead, out int aRoundActive, out int aRoundDelay);
        Odyssey.GetAttackDiagnostics(target, out int tCommandable, out int tMovePermitted,
            out int tHead, out int tRoundActive, out int tRoundDelay);

        short targetHp = target == 0 ? (short)-1 : Odyssey.GetCurrentHitPoints(target);
        bool targetDead = target == 0 || Odyssey.IsCreatureDead(target);

        float dist = -1f;
        if (attacker != 0 && target != 0)
        {
            Vec3 a = Odyssey.GetPosition(attacker);
            Vec3 t = Odyssey.GetPosition(target);
            dist = MathF.Sqrt(((a.X - t.X) * (a.X - t.X)) + ((a.Y - t.Y) * (a.Y - t.Y)));
        }

        PluginHost.Log(
            $"attack probe: 0x{_probeAttacker:X}(resolved={attacker != 0}) commandable={aCommandable} "
            + $"movePermitted=0x{aMovePermitted:X} head=0x{aHead:X} round(active={aRoundActive},delay={aRoundDelay}) "
            + $"-> 0x{_probeTarget:X}(resolved={target != 0}) commandable={tCommandable} "
            + $"movePermitted=0x{tMovePermitted:X} head=0x{tHead:X} round(active={tRoundActive},delay={tRoundDelay}) "
            + $"hp={targetHp} dead={targetDead} dist={dist:F2} "
            + $"localCombatExcluded=0x{block.LocalCombatExcludedId:X}");
    }

    /// <summary>
    /// Makes a named creature bark a known line through the engine's own BarkString, WITHOUT
    /// the replay guard — so the capture hook sees it exactly as it sees one the game
    /// produced, and the whole channel (capture, block, wire, replay) runs end to end on
    /// demand.
    ///
    /// Nothing else could reach this path. Barks come from scripts and AI actions the
    /// starting area only fires on triggers automation cannot reach, and clicking a droid
    /// opens a conversation rather than barking. So the channel was only ever exercised by a
    /// human playing the game, and it shipped broken twice — most recently with the capture
    /// stub reading the wrong stack slots, which put a heap pointer where the speaker's
    /// object id belonged and made every replay fail with "that creature does not exist
    /// here" on the far side.
    /// </summary>
    private void HandleBark()
    {
        uint serial = block.TestBarkSerial;
        if (serial == _barkSerial)
        {
            return;
        }

        _barkSerial = serial;

        byte* tag = block.TestBarkTag;
        int length = 0;
        while (length < SharedBlock.TestBarkTagLength && tag[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            return;
        }

        nint creature = CreatureFinder.FindByTag(new ReadOnlySpan<byte>(tag, length));
        if (creature == 0)
        {
            PluginHost.Log($"test: no creature tagged "
                + $"'{System.Text.Encoding.ASCII.GetString(tag, length)}' to bark");
            return;
        }

        uint objectId = *(uint*)(creature + 0x04); // CSWSObject + 4 = the object's id
        if (objectId is 0 or Odyssey.ObjectInvalid)
        {
            return;
        }

        // A line distinctive enough that finding it on the far side proves it travelled,
        // rather than merely that something arrived.
        ReadOnlySpan<byte> line = "coop test bark"u8;

        bool spoke = Odyssey.PlayBark(objectId, line, default, 1, suppressCapture: false);

        PluginHost.Log(spoke
            ? $"test: 0x{objectId:X} barked a test line"
            : $"test: could not bark from 0x{objectId:X}");
    }

    /// <summary>The engine's "current local player" sentinel id, used throughout Odyssey.cs as
    /// a bare literal — named here once because <see cref="HandleAttack"/> has to recognise it
    /// specifically (see the round-71 note below).</summary>
    private const uint LocalPlayerSentinelId = 0x7FFFFFFF;

    /// <summary>How close the player-as-attacker form snaps to its target before arming the
    /// order, in metres. Comfortably inside the ~2.6m attack range round 23 measured, so the
    /// swing lands on the first round rather than racing a marginal distance check.</summary>
    private const float PlayerAttackMeleeRange = 1.5f;

    /// <summary>
    /// Stages a real fight: one creature is ordered to attack another and the AI carries on
    /// from there. This is what makes combat testable at all — the starting area's enemies
    /// never aggro on their own, so without it the damage forward, the swing animations and
    /// the phantom's targetability have no way to be exercised outside a human playthrough.
    ///
    /// ROUND 71 (2026-08-23): the player-as-attacker form (<c>attack &lt;target&gt; 0x7FFFFFFF</c>)
    /// was reported never queuing a real order across five targets in two modules (round 68,
    /// shared-state-audit.md). Live re-investigation found <see cref="Odyssey.AttackTarget"/>
    /// itself unchanged and correct — round 58's own four confirmed kills, and this round's
    /// fresh one (a `tar_m10aa` Vulkar taken 22 hp -&gt; 13 -&gt; 4 -&gt; dead, `resolve` losing the
    /// object exactly as a real death does), both used the SAME code path. The difference is
    /// distance: an NPC ordered to attack paths to its target through its own AI, but the local
    /// player is drive-controlled and has no such step — <c>ActionAttack</c>'s queued order
    /// sits at the head of the action queue (confirmed live: <c>head=0x1</c>, steady, for the
    /// whole 20s probe window) while nothing ever closes the gap, exactly the same "queued move
    /// never executes for a player-controlled creature" limitation <see cref="HandleWalk"/>'s
    /// own doc comment already names for <c>ActionMoveToLocation</c>. Round 68's targets were
    /// never confirmed to be within melee range before the order; round 58's were. So the fix
    /// is not to <see cref="Odyssey.AttackTarget"/> — it is to stop assuming the caller already
    /// staged the range, and stage it here instead, the same way a human playtester would walk
    /// up first: when the attacker is the local player and it is not already adjacent to the
    /// target, snap it to melee range through <see cref="Odyssey.SnapTo"/> — the same real
    /// engine call the `tp` cheat itself uses — before arming the order.
    /// </summary>
    private void HandleAttack()
    {
        uint serial = block.TestAttackSerial;
        if (serial == _attackSerial)
        {
            return;
        }

        _attackSerial = serial;

        uint attacker = block.TestAttacker;
        uint target = block.TestAttackTarget;
        if (attacker == 0)
        {
            return;
        }

        // An attacker with NO TARGET means "stop fighting" — the counterpart to the order
        // above, and the thing the battery needs to put the world back. A creature still in
        // combat cannot start a conversation, so the fight staged by the animation check was
        // failing the dialogue check that ran after it, intermittently and for a reason that
        // had nothing to do with dialogue. (attacker set, target zero) was previously an
        // ignored no-op, so nothing else means anything by it.
        if (target == 0)
        {
            bool cleared = Odyssey.ClearQueuedCombatActions(attacker);
            PluginHost.Log(cleared
                ? $"test: 0x{attacker:X} ordered to break off combat"
                : $"test: could not break off combat for 0x{attacker:X}");
            return;
        }

        if (attacker == LocalPlayerSentinelId)
        {
            CloseToMeleeRangeIfNeeded(attacker, target);
        }

        bool ok = Odyssey.AttackTarget(attacker, target, out int failure);

        PluginHost.Log(ok
            ? $"test: 0x{attacker:X} ordered to attack 0x{target:X}"
            : $"test: attack order refused (0x{attacker:X} -> 0x{target:X}, failure {failure})");

        if (ok)
        {
            _probeAttacker = attacker;
            _probeTarget = target;
            double now = Environment.TickCount64 / 1000.0;
            _probeUntil = now + ProbeDurationSeconds;
            _probeLastLogAt = now - ProbeLogIntervalSeconds; // log immediately, not 1s from now
        }
    }

    /// <summary>
    /// ROUND 71: the drive-controlled local player never walks itself to a queued attack's
    /// target (see <see cref="HandleAttack"/>'s own remarks), so this closes the distance the
    /// same way a real click-to-attack would have found the player already standing there.
    /// A no-op when either body fails to resolve or is already inside
    /// <see cref="PlayerAttackMeleeRange"/> — <see cref="Odyssey.AttackTarget"/> is left to
    /// report its own failure for a target it genuinely cannot find.
    /// </summary>
    private void CloseToMeleeRangeIfNeeded(uint attacker, uint target)
    {
        nint attackerCreature = Odyssey.ResolveServerObject(attacker);
        nint targetCreature = Odyssey.ResolveServerObject(target);
        if (attackerCreature == 0 || targetCreature == 0)
        {
            return;
        }

        Vec3 attackerPos = Odyssey.GetPosition(attackerCreature);
        Vec3 targetPos = Odyssey.GetPosition(targetCreature);

        float dx = attackerPos.X - targetPos.X;
        float dy = attackerPos.Y - targetPos.Y;
        float planarDistance = MathF.Sqrt((dx * dx) + (dy * dy));
        if (planarDistance <= PlayerAttackMeleeRange)
        {
            return;
        }

        // Approach from wherever the attacker already is (degenerate only when the two bodies
        // are exactly coincident, which the range check above already excluded), so a staging
        // script that positioned the player deliberately keeps that side of the target.
        float scale = planarDistance > 0.001f ? (PlayerAttackMeleeRange / planarDistance) : 0f;
        Vec3 snapPos = new()
        {
            X = targetPos.X + (dx * scale),
            Y = targetPos.Y + (dy * scale),
            Z = targetPos.Z,
        };

        Odyssey.SnapTo(attackerCreature, snapPos);

        PluginHost.Log(
            $"test: closed range for 0x{attacker:X} -> 0x{target:X} "
            + $"({planarDistance:F2}m -> {PlayerAttackMeleeRange:F2}m) before arming the attack");
    }

    private void HandleTalk()
    {
        uint serial = block.TestTalkSerial;
        if (serial == _talkSerial)
        {
            return;
        }

        _talkSerial = serial;

        byte* tag = block.TestTalkTag;
        int length = 0;
        while (length < SharedBlock.TestTalkTagLength && tag[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            return;
        }

        // The tag "close" ends whatever conversation is running instead of starting one.
        // A verification pass has to be able to put the game back the way it found it —
        // without this, one run's conversation silently changes the next run's results
        // (measured: a leftover conversation deferred damage and blocked an attack order,
        // and three checks failed for reasons that had nothing to do with them).
        if (length == 5
            && tag[0] == (byte)'c' && tag[1] == (byte)'l' && tag[2] == (byte)'o'
            && tag[3] == (byte)'s' && tag[4] == (byte)'e')
        {
            Odyssey.CloseDialogue();
            PluginHost.Log("test: closed any running conversation");
            return;
        }

        // Exactly what a click does — so a refusal here is a real refusal, including the
        // one the dialogue gate issues while a peer holds the conversation token.
        bool started = Odyssey.StartDialogueWith(new ReadOnlySpan<byte>(tag, length));

        PluginHost.Log(started
            ? $"test: opened a conversation with '{System.Text.Encoding.ASCII.GetString(tag, length)}'"
            : $"test: could not open a conversation with '{System.Text.Encoding.ASCII.GetString(tag, length)}'"
                + " (not here, already talking, or refused)");
    }

    /// <summary>
    /// Queues the engine's own move action on the LOCAL PLAYER.
    ///
    /// ⚠ THIS DOES NOT MOVE THE PLAYER CHARACTER, and the reason is worth writing down
    /// because it cost a live round. Measured 2026-08-15: two requests, both acknowledged with
    /// a serial, and the PC never left (20.2, 23.0).
    ///
    /// <see cref="Odyssey.MoveToLocation"/> queues NWScript <c>ActionMoveToLocation</c>, which
    /// is the AI action queue — and that is what it was built and verified for, on PHANTOMS,
    /// which have no player driving them. The player character does not move that way in this
    /// build. The proof is negative and complete: every one of the 17 call sites of
    /// <c>CSWCMessage::SendPlayerToServerMessage 0x00677410</c> was enumerated and the message
    /// type read off each, and on the player-input channel (6) the only types this game ever
    /// sends are 2 Attack, 3 ChangeDoorState, 7 UseSkill, 8 Dialog, 9 UseItem, 0xB UseObject,
    /// 0xC UnlockObject and 0x12 CastSpell. Type **1**, <c>WalkToWaypoint</c>, whose server
    /// handler <c>CSWSMessage::HandlePlayerToServerInputWalkToWaypoint 0x005235B0</c> is a
    /// complete implementation, is **never sent by anything** — dead code inherited from the
    /// networked lineage this engine came from. There is simply no "walk the player here"
    /// message to borrow, which is exactly why `usedoor` could go through a message and this
    /// cannot. The PC is moved by drive control from local input instead.
    ///
    /// INFERRED, not proven: whether the queued action is cancelled by the drive controller or
    /// never run at all for a player-controlled creature. The logging below is what will
    /// settle it — before this pass, three of the four ways out of here said nothing at all,
    /// which is why "the walk did nothing" could not be told from "the walk was never tried".
    ///
    /// To move the party for a staging run, use <c>tp</c> (which moves the PC) and then
    /// <c>usedoor</c>, which gathers the companions through the engine's own PositionParty.
    /// </summary>
    private void HandleWalk()
    {
        uint serial = block.TestWalkSerial;
        if (serial == _walkSerial)
        {
            return;
        }

        _walkSerial = serial;

        uint playerId = Odyssey.GetPlayerObjectId();
        if (playerId == 0)
        {
            PluginHost.Log("test: walk ignored - no player object id (not in a module yet)");
            return;
        }

        nint player = Odyssey.ResolveServerObject(playerId);
        if (player == 0)
        {
            PluginHost.Log($"test: walk ignored - player id 0x{playerId:X} does not resolve here");
            return;
        }

        Vec3 here = Odyssey.GetPosition(player);
        Vec3 destination = new() { X = block.TestWalkX, Y = block.TestWalkY, Z = here.Z };

        bool ok = Odyssey.MoveToLocation(playerId, destination, run: true, out int failure);

        PluginHost.Log(ok
            ? $"test: queued a move for 0x{playerId:X} from ({here.X:F2}, {here.Y:F2}) to "
              + $"({destination.X:F2}, {destination.Y:F2}) - NOTE the PC is drive-controlled, "
              + "so an action-queue move may not move it at all; use tp then usedoor"
            : $"test: walk refused (failure {failure})");
    }
}
