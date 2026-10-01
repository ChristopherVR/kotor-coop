using System.Diagnostics;

namespace KotorCoop.Plugin;

// The per-frame drive: OnFrame (spawn/despawn gating, the main per-slot loop) and every
// helper it calls each pass (animation mirroring, equipment, naming, targetable-faction,
// hit-breakdown/test-hit diagnostics, appearance, and the direct-placement smoother). See
// PhantomController.cs for the per-slot state this shares with lifecycle/transition-flush.
internal sealed unsafe partial class PhantomController
{
    /// <summary>
    /// The engine's main loop runs at roughly 317 Hz, far above the render rate, so the
    /// phantom is stepped on a wall clock instead of once per callback.
    /// </summary>
    private const double StepIntervalSeconds = 1.0 / 60.0;

    /// <summary>
    /// How far a remote player must move before the phantom's move action is re-issued.
    /// Squared to avoid a square root on the per-frame path. 0.6 units: tight enough that
    /// a stopped peer's phantom stands almost exactly on the true spot, loose enough that
    /// interpolation jitter does not thrash the pathfinder — churn is additionally capped
    /// by <see cref="RepathCooldownSteps"/>.
    /// </summary>
    private const float GoalResendDistanceSquared = 0.6f * 0.6f;

    /// <summary>
    /// Minimum steps (at ~60 Hz) between re-issued move actions per phantom. Re-queueing
    /// every tick resets the pathfinder continuously and the creature never gets anywhere;
    /// five steps ≈ 80 ms keeps the goal fresh without the churn.
    /// </summary>
    private const int RepathCooldownSteps = 5;

    /// <summary>
    /// Beyond this the phantom is not lagging, it is lost (peer teleported, or the pathfinder
    /// dead-ended). Snap instead of walking a 20-second catch-up.
    ///
    /// ⚠ Do NOT lower this to "bound the lag". It was tried at 3.5 units and play-tested as
    /// the character FLYING AROUND: during ordinary running the phantom is legitimately a few
    /// units behind, so a low threshold fires a teleport every couple of seconds. A snap is
    /// for recovery, not for tracking.
    /// </summary>
    private const float SnapDistanceSquared = 15f * 15f;

    /// <summary>The walk animation; anything else while moving is treated as a run.
    /// KOTOR's default movement IS the run, so run is the common case.</summary>
    private const uint AnimWalkValue = 0x2712;

    // Movement animations the drive loop owns (they come from the walk/run action, not from
    // mirroring the peer's field), and the death poses the down-state path owns. A phantom
    // standing still mirrors any OTHER animation the peer is playing — attack swings, weapon
    // flourishes, gestures, the "x" ready pose — so those show up across instances.
    private const uint AnimIdleValue = 0x2710;
    private const uint AnimRunValue = 0x2714;
    private const uint AnimDeath1 = 0x2716;
    private const uint AnimDeath2 = 0x2718;
    private const uint AnimDeath3 = 0x27AC;

    private static bool IsPlayableAnimation(uint anim) =>
        anim is not (AnimIdleValue or AnimWalkValue or AnimRunValue or AnimDeath1 or AnimDeath2 or AnimDeath3);

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>How many applied one-shots are logged before the channel goes quiet. Not
    /// five: five is spent within a minute of a real fight, and it is the LATER ones — an
    /// overlay flourish, an unusual id — that a verification pass needs to see land.</summary>
    private const int OneShotLogLimit = 40;

    private int _oneShotsLogged;

    private double _lastStep;
    private int _spawnCooldownFrames;
    private int _equipCooldownFrames;
    private int _stepsLogged;
    private int _equipsLogged;

    public void OnFrame()
    {
        block.FrameCounter = (uint)FrameHook.FrameCount;

        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            // Idle (a disconnect): despawn any phantoms so a player who left does not leave
            // a frozen body behind. One per frame keeps it cheap.
            //
            // ⚠ NOT GATED ON THE CACHED COUNT, and that gate is half of why a body could
            // survive a disconnect. `_phantomCount` is DERIVED, and this branch never
            // recomputed it — so it sat at whatever the live session left. Read one way it
            // let this branch run against nothing forever; read the other (a slot cleared by
            // the active "peer left" path on the frame the command fell) it stopped the
            // branch running at all while a body was still standing. FlushOneBody is an
            // eight-slot scan that terminates by itself; it needs no gate.
            if (FlushOneBody())
            {
                PublishPhantomCount();
            }

            return;
        }

        // The agent's pointer is only the handshake; the engine's own lookup is the
        // authority. The agent-written pointer goes stale across a module transition and
        // dereferencing it then would read freed memory.
        if (block.PlayerCreature == 0)
        {
            return;
        }

        nint player = Odyssey.GetPlayerCreaturePointer();
        if (player == 0)
        {
            return;
        }

        if (AgentLivenessController.RemoteWritesFrozen)
        {
            // The agent has stopped pumping this block (see AgentLiveness); everything
            // below reads its remote slots, which are now a stale snapshot nobody is
            // updating. Leave whatever phantoms already exist exactly where they stand
            // rather than keep spawning, destroying or "driving" bodies off a picture that
            // has stopped changing — AgentLivenessController's on-screen warning is what
            // tells the player why. Recovers on its own the moment the heartbeat resumes.
            return;
        }

        // ⚠ RemoteCount is the OCCUPIED RANGE, not a population count. The agent keeps a
        // peer in the slot it joined in so nothing downstream is re-attributed when someone
        // leaves, which means slot 1 can be live while slot 0 is empty. Everything below
        // therefore walks all the slots and decides per slot.
        int wanted = (int)Math.Min(block.RemoteCount, (uint)SharedBlock.SlotCapacity);

        // Spawning (and despawning) is rate-limited to at most one creature every 30 frames.
        // A spawn is by far the most expensive thing here, and doing several in one frame is
        // how you turn a working mod into a stutter - which is exactly what a third player
        // joining would otherwise ask for.
        if (_spawnCooldownFrames > 0)
        {
            _spawnCooldownFrames--;
        }

        double now = _clock.Elapsed.TotalSeconds;
        bool step = (now - _lastStep) >= StepIntervalSeconds;
        if (step)
        {
            _lastStep = now;
        }

        // Reused across every slot below (CA2014: a stackalloc inside the loop risks a stack
        // overflow if the compiler cannot prove it runs only once per call).
        Span<uint> attackerIds = stackalloc uint[PhantomAttackerTable.Capacity];
        Span<uint> attackerCumulative = stackalloc uint[PhantomAttackerTable.Capacity];

        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            RemoteView remote = i < wanted ? block.GetRemote(i) : default;

            // A module/script can retire a body after it was spawned. Validate before
            // the inactive-peer destroy branch as well as before the drive reads.
            if (_phantoms[i] != 0 && !IsCurrentBody(_phantoms[i], _phantomIds[i]))
            {
                ForgetStaleSlot(i);
                continue;
            }

            if (!remote.Active)
            {
                // The peer that owned this slot is gone. Destroy the body rather than leaving
                // it standing: with stable slots an abandoned body is inherited by the next
                // joiner, still wearing the departed player's name and gear.
                if (_phantoms[i] != 0 && _spawnCooldownFrames == 0)
                {
                    DestroySlot(i, "peer left");
                    PublishPhantomCount();
                    _spawnCooldownFrames = 30;
                }

                // The PEER is gone, not just its body: forget the identity kept across module
                // changes, so the next joiner in this slot is never dressed as the last one.
                _appearanceCache[i] = 0;

                continue;
            }

            if (_phantoms[i] == 0)
            {
                if (_spawnCooldownFrames == 0 && !_spawnDisabled)
                {
                    TrySpawnOne(i, player);
                    _spawnCooldownFrames = 30;
                }

                continue;
            }

            if (!step)
            {
                continue;
            }

            nint phantom = _phantoms[i];

            // A body spawned before the client half of the world existed can be permanently
            // invisible with every readable field correct (PhantomSpawnReadinessRule). The
            // wait budget only expires when the world is unusually slow, so this is rare —
            // but when it does happen, replacing the body is the only repair there is.
            if (PhantomSpawnReadinessRule.ShouldReplaceProvisional(
                    _provisional[i],
                    Odyssey.HasRenderedBody(player),
                    _provisionalReplacements[i],
                    PhantomSpawnReadinessRule.MaxProvisionalReplacements)
                && _spawnCooldownFrames == 0)
            {
                _provisionalReplacements[i]++;
                PluginHost.Log(
                    $"phantom slot {i}: the client world has come up since this body was "
                    + "spawned provisionally - replacing it so the peer is drawn");
                DestroySlot(i, "provisional body replaced");
                PublishPhantomCount();
                _spawnCooldownFrames = 30;
                continue;
            }

            ApplyAppearance(i, phantom, remote.Appearance);
            if (!IsCurrentBody(phantom, _phantomIds[i]))
            {
                ForgetStaleSlot(i);
                continue;
            }
            ApplyEquipment(i);
            if (!IsCurrentBody(phantom, _phantomIds[i]))
            {
                ForgetStaleSlot(i);
                continue;
            }
            ApplyName(i, phantom);
            if (!IsCurrentBody(phantom, _phantomIds[i]))
            {
                ForgetStaleSlot(i);
                continue;
            }

            // The peer left combat: release every local creature still locked onto their
            // phantom. The phantom is min-1-hp, so it never reads dead and an attacker
            // would otherwise hold the lock forever (the aggro-sink failure of 7.4).
            bool peerInCombat = remote.InCombat;
            // Reconcile while fighting, and release once on exit. Do not keep
            // forcing idle outside combat: that would cancel a voluntary flourish.
            if (peerInCombat || _peerInCombat[i])
            {
                Odyssey.MirrorWeaponCombatVisual(phantom, peerInCombat);
            }

            if (_peerInCombat[i] && !peerInCombat)
            {
                Odyssey.PacifyPhantom(phantom);
            }

            _peerInCombat[i] = peerInCombat;

            // Targetability. A phantom in faction 0 (PLAYER) is evaluated by every hostile
            // exactly as the real player is, so the peer is genuinely part of the fight
            // here; back in Neutral, `GetReputation > 10` releases every attacker at the
            // next combat-round boundary. That release lever is what the original
            // aggro-sink design lacked entirely — see docs/research/combat-participation.md.
            // menu_guard_sync: drop any hit that still lands on this slot BEFORE the faction
            // flip below has a chance to run (it is edge-only, so a hit landing the very frame
            // the peer's menu opens would otherwise still reach them) — see PhantomHitHook's
            // own gate, the same early-drop shape the party-mine window uses.
            bool menuGuarded = _menuGuardSync && remote.MenuGuarded;
            PhantomHitHook.SetMenuGuarded(i, menuGuarded);

            ApplyTargetable(i, phantom, alive: !remote.Downed, stealthed: remote.Stealthed, menuGuarded: menuGuarded);
            ApplyStealthVisual(i, phantom, remote.Stealthed);
            ApplyVitals(i, phantom, remote);

            // Forward what this body has taken to the peer whose character it stands for.
            PhantomHitHook.ReadSlot(i, out uint watchedId, out uint cumulative, out uint attacker, out uint hitSerial);

            int attackerCount = PhantomHitHook.ReadAttackers(i, attackerIds, attackerCumulative);

            block.SetPhantomHit(
                i, watchedId, cumulative, attacker, hitSerial,
                attackerIds[..attackerCount], attackerCumulative[..attackerCount]);

            ApplyTestHit(i);
            LogHitBreakdown();

            // A player at zero health lies where they fell, and their phantom does the
            // same — otherwise a downed teammate looks alive and well in your game while
            // their screen shows a death. The pose comes back off the moment the peer's
            // health does, so a revive (or a respawned host) stands the phantom up.
            bool down = remote.Downed;
            if (down != _down[i])
            {
                _down[i] = down;

                if (down)
                {
                    // Stop mid-stride first: a queued move action executing after the
                    // pose would stand the body straight back up. And release anything
                    // attacking the body — it cannot die, so nothing lets go on its own.
                    Odyssey.ClearActions(phantom);
                    Odyssey.PacifyPhantom(phantom);
                }

                Odyssey.SetDownState(phantom, down);
                PluginHost.Log(down
                    ? $"phantom 0x{_phantomIds[i]:X}: down (player at 0 hp)"
                    : $"phantom 0x{_phantomIds[i]:X}: back up");
            }

            if (down)
            {
                continue;
            }

            // Mirror the peer's played animation every frame (change-detected). It overlays
            // on the twin, so it does not fight the movement action below.
            MirrorAnimation(i, phantom, remote.Animation);

            // One-shots (weapon draw, attack swings, flourish, injecting a stim) arrive as an
            // EDGE rather than a level, because they finish between polls and some never touch
            // an object field at all. Fire each exactly once, through the engine's own
            // client-side call — a field write cannot do it (the engine's update handler drops
            // repeats and skips the whole combat set outright).
            uint oneShotSerial = block.GetRemoteOneShotSerial(i);
            if (oneShotSerial != _lastOneShotSerial[i])
            {
                _lastOneShotSerial[i] = oneShotSerial;

                ushort animation = (ushort)block.GetRemoteOneShotAnim(i);
                if (animation != 0 && Odyssey.PlayOneShotAnimation(phantom, animation)
                    && _oneShotsLogged < OneShotLogLimit)
                {
                    _oneShotsLogged++;
                    PluginHost.Log($"animation: played one-shot 0x{animation:X} on phantom 0x{_phantomIds[i]:X}");
                }
            }

            if (_repathCooldown[i] > 0)
            {
                _repathCooldown[i]--;
            }

            // Walkmesh guard, source side (docs/research/walkmesh-guard.md §3): a joiner's
            // phantom crashed on danm14aa shortly after arriving there because the HOST's own
            // agent republished (0,0,0) while the host's module was still loading — a raw
            // SetPosition write does not itself fault, but the body then sat off-mesh until
            // some later engine tick's own pathing check did. Unlike TrySpawnOne's own gate
            // (PhantomController.Lifecycle.IsPlausiblePosition), nothing here screened
            // remote.Position on every subsequent DRIVEN frame. The cheap sanity check only
            // (no engine call: this runs at up to ~60 Hz per phantom, and the walkability
            // call belongs at the low-frequency choke points below — SnapTo/MoveToLocation —
            // that actually risk TestDirectLine). Holds the body at its last good position
            // instead of driving it toward the poison value; health/appearance/hit-forwarding
            // above still ran this frame.
            if (!WalkmeshGuardRule.IsRemotePositionDriveable(remote.Position))
            {
                continue;
            }

            Vec3 at = Odyssey.GetPosition(phantom);
            float pdx = remote.Position.X - at.X;
            float pdy = remote.Position.Y - at.Y;
            float behindSquared = (pdx * pdx) + (pdy * pdy);

            // Hopelessly behind means the peer teleported or the path dead-ended:
            // snap rather than walk a long catch-up nobody asked to watch.
            //
            // ⚠ Skipped entirely in direct mode. Leaving it active while also placing the body
            // every frame is what made the first direct-drive attempt look like the character
            // flying around: SnapTo clears the action queue and sets position, so the two
            // writers fought each other several times a second.
            if (!_directMovement && behindSquared > SnapDistanceSquared)
            {
                Odyssey.SnapTo(phantom, remote.Position);
                _lastGoal[i] = remote.Position;
                _repathCooldown[i] = RepathCooldownSteps;
                continue;
            }

            if (_directMovement)
            {
                DriveDirect(i, phantom, remote, now, player);
                continue;
            }

            // The engine's own pathfinding walk: smooth, and inherently a little behind,
            // because a walk cannot close a gap against a peer moving at the same speed.
            // Kept as the fallback (coop.ini movement=walk) because it is known-good.
            Vec3 last = _lastGoal[i];
            float gdx = remote.Position.X - last.X;
            float gdy = remote.Position.Y - last.Y;
            bool goalMoved = ((gdx * gdx) + (gdy * gdy)) >= GoalResendDistanceSquared;

            if (!goalMoved)
            {
                // Arrived: mirror the peer's facing, which the engine otherwise leaves
                // pointing along the last path segment.
                if (behindSquared < 0.35f * 0.35f)
                {
                    Odyssey.SetFacing(phantom, remote.Facing);
                }

                continue;
            }

            if (_repathCooldown[i] > 0)
            {
                continue;
            }

            _lastGoal[i] = remote.Position;
            _repathCooldown[i] = RepathCooldownSteps;

            // Match the peer's gait: KOTOR's default movement is the run, so anything that is
            // not explicitly the walk animation runs.
            bool run = remote.Animation != AnimWalkValue;

            if (Odyssey.MoveToLocation(_phantomIds[i], remote.Position, run, out int failure))
            {
                if (_stepsLogged < 10)
                {
                    _stepsLogged++;
                    PluginHost.Log(
                        $"move#{_stepsLogged} phantom 0x{_phantomIds[i]:X} at "
                        + $"({at.X:F2},{at.Y:F2}) -> goal ({remote.Position.X:F2},{remote.Position.Y:F2})"
                        + (run ? " (run)" : " (walk)"));
                }
            }
            else if (_stepsLogged < 10)
            {
                _stepsLogged++;
                PluginHost.Log($"move refused for 0x{_phantomIds[i]:X} (failure {failure})");
            }
        }
    }

    /// <summary>
    /// Replays whatever animation the peer is doing (weapon draw, attack swing, flourish, the
    /// "x" ready pose) onto the phantom, once per change. Applied as a fire-and-forget on the
    /// phantom's client twin — the engine's own replication path — which overlays the pose
    /// without cancelling the walk/run action, so it works whether the phantom is moving or
    /// still. Gait, idle and the death poses are owned elsewhere and skipped; a one-shot
    /// self-terminates back to idle, so nothing has to reset it.
    ///
    /// ⚠ "Not a gait" is not the same as "safe to fire and forget". This used to queue any
    /// id that was not one of the six it knows by name, and a LOOPING or OVERLAY id sent
    /// down the fire-and-forget path parks in the twin's current-animation field with no
    /// expiry — the body wedges in that pose. <see cref="Odyssey.PlayOneShotAnimation"/>
    /// asks the engine's own predicate first and refuses anything else.
    /// </summary>
    private void MirrorAnimation(int slot, nint phantom, uint anim)
    {
        if (anim == _lastAnim[slot])
        {
            return;
        }

        _lastAnim[slot] = anim;

        if (IsPlayableAnimation(anim) && anim <= ushort.MaxValue)
        {
            Odyssey.PlayOneShotAnimation(phantom, (ushort)anim);
        }
    }

    /// <summary>Maps equip index in the shared block to the engine's inventory slot.</summary>
    private static ReadOnlySpan<int> EquipInventorySlots =>
    [
        Odyssey.InventorySlotBody,
        Odyssey.InventorySlotRightWeapon,
        Odyssey.InventorySlotLeftWeapon,
    ];

    /// <summary>
    /// Dresses a phantom in its player's real gear: for each visible slot whose desired
    /// resref changed, create the item on the phantom and equip it through the engine.
    /// One engine mutation per 30 frames — an equip is as heavy as a spawn.
    ///
    /// An empty desired resref is left alone (the phantom keeps what it wears). Whether a
    /// resref is remembered as applied after the attempt is <see cref="EquipRetryRule"/>'s
    /// call, not this method's: a resref this install truly cannot resolve (failure 7) is
    /// latched so a missing mod does not retry forever, but every OTHER failure is transient
    /// and must retry — see that rule's doc comment for the live bug this fixed (a phantom
    /// whose first equip attempt failed because the engine's routine table had not resolved
    /// yet, right at connect, never got dressed for the rest of the module).
    /// </summary>
    private void ApplyEquipment(int slot)
    {
        if (_equipCooldownFrames > 0)
        {
            _equipCooldownFrames--;
            return;
        }

        for (int j = 0; j < SharedBlock.EquipCount; j++)
        {
            byte* resref = block.GetEquipResref(slot, j);

            int length = 0;
            uint hash = 2166136261;
            while (length < 16 && resref[length] != 0)
            {
                hash = (hash ^ resref[length]) * 16777619;
                length++;
            }

            if (length == 0 || hash == _appliedEquip[slot, j])
            {
                continue;
            }

            _equipCooldownFrames = 30;

            // The synchronous engine path (CSWSCreature::EquipItem), the same one
            // PartyPresenceController uses for companions and CheatController for `equip`.
            // The VM-routine path this used to call (EquipItemFromTemplate) failed with
            // failure 3 - CreateItemOnObject/ActionEquipItem not found in the commands table -
            // on EVERY attempt across two live sessions on 2026-09-16 (12/12 retries once the
            // latch bug below was fixed), so a peer whose appearance row needs clothing to
            // build a body model (row 114, pfbalb01) was invisible on the other side for the
            // whole session. See EquipRetryRule.
            bool ok = Odyssey.EquipItemFromTemplateDirect(
                _phantomIds[slot], resref, length, EquipInventorySlots[j], out int failure);

            // See EquipRetryRule's doc comment: only a genuine success or the one PERMANENT
            // failure (blueprint missing on this install) may skip this resref forever. Every
            // other failure — the routine table not resolved yet at connect time being the one
            // measured live — must be retried on a later pass, so do NOT advance the cache.
            //
            // ⚠ EVERY failure code past this point (Odyssey.Equipment.cs's 20-26) fires AFTER
            // GiveItemToInventory has already created a fresh item on the phantom — so an
            // unbounded retry here would create and orphan one new item every 30 frames for
            // the life of the module on a PERSISTENT (not transient) failure. ShouldGiveUp
            // bounds it: a resref latches (as failed, not as succeeded) once it has failed this
            // many times in a row, exactly the way TrySpawnOne's own MaxSpawnFailures stops a
            // missing blueprint from retrying forever.
            bool latch = EquipRetryRule.ShouldLatch(ok, failure);
            if (!latch && EquipRetryRule.ShouldGiveUp(++_equipFailures[slot, j]))
            {
                latch = true;
                PluginHost.Log(
                    $"equip: GIVING UP on phantom 0x{_phantomIds[slot]:X} slot "
                    + $"{EquipInventorySlots[j]} after {_equipFailures[slot, j]} consecutive "
                    + $"failures (last failure {failure}) — leaving it as last equipped rather "
                    + "than leaking another item every attempt");
            }

            if (latch)
            {
                _appliedEquip[slot, j] = hash;
                _equipFailures[slot, j] = 0;
            }

            if (_equipsLogged < 12)
            {
                _equipsLogged++;
                string name = System.Text.Encoding.ASCII.GetString(resref, length);
                PluginHost.Log(ok
                    ? $"equip: phantom 0x{_phantomIds[slot]:X} wears {name} (slot {EquipInventorySlots[j]})"
                    : $"equip: {name} refused for phantom 0x{_phantomIds[slot]:X} (failure {failure})");
            }

            return;
        }
    }

    /// <summary>
    /// Re-asserts the phantom's name until it has reached the CLIENT TWIN, which is what
    /// the selection band renders. The name is set at spawn on the server locstring, but
    /// the twin does not exist yet then and the engine only carries a name to the twin on
    /// its first update — so this catches the moment the twin appears (and after a module
    /// transition recreates it). Once written, it latches and costs one pointer read a step.
    /// </summary>
    private void ApplyName(int slot, nint phantom)
    {
        if (_twinNamed[slot])
        {
            return;
        }

        byte* name = block.GetRemoteName(slot);
        int length = 0;
        while (length < SharedBlock.NameLength - 1 && name[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            return;
        }

        // SetCreatureFirstName writes the twin when it exists; treat the name as done only
        // once the twin is actually present, so a pre-twin call does not latch prematurely.
        Odyssey.SetCreatureFirstName(phantom, name, length);

        if (Odyssey.HasClientTwin(phantom))
        {
            _twinNamed[slot] = true;
        }
    }

    /// <summary>Whether a peer's phantom may be attacked by this instance's enemies at all.
    /// Off falls back to the old decoration behaviour (Neutral forever).</summary>
    private readonly bool _targetable = PluginConfig.ReadBool("phantom_targetable", true);

    /// <summary>coop.ini <c>stealth_sync</c> (default ON): a peer publishing
    /// <c>PlayerState.FlagStealth</c> has their stand-in held Neutral here for as long as the
    /// bit is set — the same faction flip + PacifyCreature <c>phantom_targetable</c> already
    /// uses, no new engine write. See <see cref="PhantomTargetRule"/> and effect-sync.md §12.4.</summary>
    private readonly bool _stealthSync = PluginConfig.ReadBool("stealth_sync", true);

    /// <summary>coop.ini <c>menu_guard_sync</c> (default ON): a peer publishing
    /// <c>PlayerState.FlagMenuGuard</c> has their stand-in held Neutral here for as long as the
    /// bit is set — the same faction flip + PacifyCreature lever <see cref="_stealthSync"/>
    /// uses. The bit itself is only ever set within <c>MenuGuardRule</c>'s bounded window (the
    /// agent's own timer, not this plugin), so this cannot become a standing shield. See
    /// <see cref="PhantomTargetRule"/> and docs/research/effect-sync.md §13.</summary>
    private readonly bool _menuGuardSync = PluginConfig.ReadBool("menu_guard_sync", true);

    /// <summary>coop.ini <c>partner_buffs</c>, default ON since 2026-09-25 (live: a host Cure healed the joiner 4 -> 12 hp): seat the targetable
    /// stand-in in Friendly_1 rather than Player so a friend-sphere cast (Force Valor family)
    /// reaches it. Plugin-only key — the agent never reads it, so there is no digest field and
    /// no second default to keep in step. See docs/research/partner-buffs.md.</summary>
    private readonly bool _partnerBuffs = PluginConfig.ReadBool("partner_buffs", true);

    /// <summary>coop.ini <c>stealth_partner_visual</c> (<c>translucent</c> default since
    /// 2026-09-25; <c>hidden</c>; <c>off</c>) — how a stealthed peer's stand-in renders on THIS
    /// screen. Replaces the old boolean <c>stealth_visual_sync</c> (default OFF, always meaning
    /// "hidden" when on); <see cref="StealthVisualMode.Resolve"/> handles the migration so an
    /// existing coop.ini that set the old key keeps behaving identically. See
    /// docs/research/effect-sync.md §12.9/§12.10 for why the two tiers differ: <c>Hidden</c>
    /// uses the SERVER field (<see cref="Odyssey.SetStealthMode"/>), which pushes the stand-in
    /// through the detection-relevance broadcast gate a real stealthed enemy uses (full hide);
    /// <c>Translucent</c> uses the CLIENT-only cosmetic (<see cref="Odyssey.SetStealthVisualState"/>)
    /// instead, which never touches that gate. Latched per slot: called on the EDGE only, never
    /// per frame — read once at construction, so the configured tier cannot change mid-session.</summary>
    private readonly StealthVisualMode.Kind _stealthPartnerVisual = StealthVisualMode.Resolve(
        PluginConfig.Read(StealthVisualMode.KeyName), PluginConfig.Read(StealthVisualMode.LegacyKeyName));

    /// <summary>Per slot: is the body currently in the PLAYER faction. Tri-state via the
    /// separate _factionKnown so the first pass always writes.</summary>
    private readonly bool[] _hostileToLocals = new bool[SharedBlock.SlotCapacity];
    private readonly bool[] _factionKnown = new bool[SharedBlock.SlotCapacity];

    /// <summary>Per slot: the stealth mode last written to the body by
    /// <see cref="ApplyStealthVisual"/>, tri-state via <c>_stealthVisualKnown</c> the same way
    /// the faction pair above is. Cleared with the slot.</summary>
    private readonly bool[] _stealthVisualApplied = new bool[SharedBlock.SlotCapacity];
    private readonly bool[] _stealthVisualKnown = new bool[SharedBlock.SlotCapacity];

    private int _stealthLogged;

    /// <summary>
    /// The engine's stealth VISUAL on the stand-in — <c>stealth_partner_visual</c> only (never
    /// when it resolves to <see cref="StealthVisualMode.Kind.Off"/>). On the edge, per body: the
    /// same "latch what you wrote, act on the change" discipline <see cref="ApplyTargetable"/>
    /// follows — both underlying setters are cheap and idempotent (each compares before
    /// writing) but a per-frame engine call on a possibly-torn-down body is exactly the pattern
    /// this project's per-frame-write rule forbids. A body that was never put into stealth is
    /// never taken out of it either — the first write happens only when the peer's bit is
    /// actually SET, so with the peer never stealthing this method makes no engine call at all.
    /// </summary>
    private void ApplyStealthVisual(int slot, nint phantom, bool stealthed)
    {
        if (_stealthPartnerVisual == StealthVisualMode.Kind.Off || phantom == 0)
        {
            return;
        }

        bool want = _stealthSync && stealthed;

        if (_stealthVisualKnown[slot] ? _stealthVisualApplied[slot] == want : !want)
        {
            return;
        }

        bool hidden = _stealthPartnerVisual == StealthVisualMode.Kind.Hidden;
        bool ok = hidden
            ? Odyssey.SetStealthMode(phantom, want)
            : Odyssey.SetStealthVisualState(_phantomIds[slot], want, out _);

        if (!ok)
        {
            return; // try again next pass rather than latching a state we did not achieve
        }

        _stealthVisualApplied[slot] = want;
        _stealthVisualKnown[slot] = true;

        if (_stealthLogged < 20)
        {
            _stealthLogged++;
            string kindLabel = hidden ? "hidden" : "translucent";
            PluginHost.Log(want
                ? $"phantom 0x{_phantomIds[slot]:X}: engine stealth mode ON ({kindLabel}, stealth_partner_visual)"
                : $"phantom 0x{_phantomIds[slot]:X}: engine stealth mode OFF ({kindLabel}, stealth_partner_visual)");
        }
    }

    /// <summary>
    /// Puts a phantom in or out of the PLAYER faction as the peer lives and dies.
    ///
    /// Only on the EDGE: AddMember walks faction member lists, and a peer standing still
    /// in a fight must not pay that every frame. On the way out it also pacifies, which
    /// releases attackers immediately rather than at the next round boundary — belt and
    /// braces against the one failure this design must never reproduce.
    /// </summary>
    private void ApplyTargetable(int slot, nint phantom, bool alive, bool stealthed, bool menuGuarded)
    {
        // ⚠ THE AUTHORITY SPLIT ONLY HOLDS IF EACH INSTANCE'S ENEMIES FIGHT ITS OWN PLAYER,
        // AND NOTHING IN THE ENGINE ENFORCES THAT.
        //
        // Under local_combat this instance's mirrored enemies swing for themselves and the
        // owner's forwarded total for this player is absorbed instead of applied. That trade
        // is only sound while the local copies are hitting the LOCAL PC. Target selection is
        // hostility + line of sight + distance; a peer's phantom is faction PLAYER while that
        // peer lives; and the mirrored enemies are driven to the OWNER's coordinates — which
        // puts them standing next to the owner's phantom, the nearest player-faction body they
        // can see. So the client's copy of a Sith soldier locks onto the HOST's phantom while
        // the host's copy hits the client's phantom, both sides absorb, and nobody loses a hit
        // point.
        //
        // The lock is sticky, which turns one bad acquisition into a whole battle: the engine
        // releases a target only on dead / unresolvable / a reputation flip, and a phantom is
        // min-1-hp so it never reads dead and stays in faction 0.
        //
        // So on a MIRRORING instance with local_combat on, peer phantoms are held Neutral.
        // Nothing is lost: this instance's damage to a phantom was never authoritative (the
        // owner's copy of that fight is the real one), and the body is still drawn, named,
        // equipped, posed and animated. What changes is that the only player-faction bodies a
        // local enemy can pick become the local PC and the mirrored companions.
        //
        // ⚠ GATED ON DamageDealtEnabled, NOT ON local_combat ALONE. On the OWNING instance the
        // peer's phantom MUST stay in the player faction: it is that peer's only body inside
        // the authoritative simulation, and hits on it are how they participate at all. Getting
        // this gate wrong makes a distant player un-fightable everywhere.
        bool holdNeutral = EnemyPresenceController.LocalCombat && block.DamageDealtEnabled;

        // A STEALTHED peer is held Neutral too (stealth_sync, PlayerState.FlagStealth — a
        // level, republished every tick, so the body rejoins the player faction on the same
        // edge logic the moment they drop out of stealth). Same lever as the authority split
        // above: no new engine write, and PacifyPhantom below releases anything already
        // locked on. See PhantomTargetRule / effect-sync.md §12.4.
        //
        // A MENU-GUARDED peer (menu_guard_sync, PlayerState.FlagMenuGuard) is held Neutral the
        // same way, for as long as MenuGuardRule's bounded window says so. `menuGuarded` here
        // already has `_menuGuardSync` folded in by the caller (it doubles as the flag
        // PhantomHitHook's damage gate uses), so `menuGuardSync` is passed as `true` below —
        // gating it a second time would be a no-op, not a bug, but the single fold-in is the
        // one place this project's "one gate, not two disagreeing copies" rule wants it.
        bool want = PhantomTargetRule.WantsPlayerFaction(
            _targetable, alive, holdNeutral, _stealthSync, stealthed, menuGuardSync: true, menuGuarded);
        bool stealthHold = PhantomTargetRule.IsStealthHold(_targetable, alive, holdNeutral, _stealthSync, stealthed);
        bool menuGuardHold = PhantomTargetRule.IsMenuGuardHold(
            _targetable, alive, holdNeutral, menuGuardSync: true, menuGuarded);

        if (_factionKnown[slot] && _hostileToLocals[slot] == want)
        {
            return;
        }

        uint objectId = _phantomIds[slot];
        if (objectId == 0)
        {
            return;
        }

        // partner_buffs (default ON since 2026-09-25): the targetable state uses Friendly_1 instead of
        // Player so the engine's GetIsFriend (reputation >= 90) holds for the local caster — see
        // PhantomTargetRule.FactionFor and docs/research/partner-buffs.md. The latch below stays
        // keyed on `want`: the key is read once at construction, so the pair never disagrees.
        PhantomTargetRule.Kind kind = PhantomTargetRule.FactionFor(want, _partnerBuffs);
        int factionId = kind switch
        {
            PhantomTargetRule.Kind.Friendly => Odyssey.FactionFriendly1,
            PhantomTargetRule.Kind.Player => Odyssey.FactionPlayer,
            _ => Odyssey.FactionNeutralId,
        };

        if (!Odyssey.SetCreatureFaction(objectId, factionId))
        {
            return; // try again next pass rather than latching a state we did not achieve
        }

        if (!want)
        {
            Odyssey.PacifyPhantom(phantom);
        }

        _hostileToLocals[slot] = want;
        _factionKnown[slot] = true;

        PluginHost.Log(want
            ? kind == PhantomTargetRule.Kind.Friendly
                ? $"phantom 0x{objectId:X}: joined the friendly faction (partner_buffs); local enemies can fight it and a friend-sphere cast reaches it"
                : $"phantom 0x{objectId:X}: joined the player faction; local enemies can fight it"
            : stealthHold
                ? $"phantom 0x{objectId:X}: back to neutral; attackers released (peer is in stealth)"
                : menuGuardHold
                    ? $"phantom 0x{objectId:X}: back to neutral; attackers released (peer has a menu open)"
                    : $"phantom 0x{objectId:X}: back to neutral; attackers released");
    }

    private uint _loggedAmountsSerial;
    private int _breakdownsLogged;

    /// <summary>Logs which per-type slots a hit actually populated, a few times. Damage is
    /// accounted per damage TYPE and the same amount can appear in more than one slot, so
    /// summing them blind over-counts — this is the measurement that settles what to sum.
    /// Runs on the frame hook, never inside the damage hook.</summary>
    private void LogHitBreakdown()
    {
        if (_breakdownsLogged >= 5 || PhantomHitHook.AmountsSerial == _loggedAmountsSerial)
        {
            return;
        }

        _loggedAmountsSerial = PhantomHitHook.AmountsSerial;
        _breakdownsLogged++;

        System.Text.StringBuilder line = new("hit breakdown:");
        for (int i = 0; i < 15; i++)
        {
            int amount = PhantomHitHook.ReadAmount(i);
            if (amount != 0)
            {
                line.Append($" [{i}]={amount}");
            }
        }

        PluginHost.Log(line.ToString());
    }

    private uint _testHitSerial;

    /// <summary>
    /// Test hook: lands a real engine damage effect on a phantom, so the combat-forward
    /// chain can be exercised without an armed encounter (Handoff 7.0 — staging a real
    /// fight on demand does not work reliably). It goes through the same VM EffectDamage
    /// the engine itself uses, so OnApplyDamage runs and the watch sees it exactly as it
    /// would see a sword.
    /// </summary>
    private void ApplyTestHit(int slot)
    {
        uint serial = block.TestHitSerial;
        if (serial == _testHitSerial || block.TestHitSlot != (uint)slot)
        {
            return;
        }

        _testHitSerial = serial;

        int amount = (int)Math.Clamp(block.TestHitAmount, 1u, 1000u);
        bool ok = Odyssey.ApplyDamageEffect(_phantomIds[slot], amount, out int failure);

        PluginHost.Log(ok
            ? $"test: applied {amount} damage to phantom 0x{_phantomIds[slot]:X}"
            : $"test: damage on phantom 0x{_phantomIds[slot]:X} refused (failure {failure})");
    }

    /// <summary>
    /// Mirrors the peer's current/max hit points onto their stand-in. Nothing in this
    /// plugin ever set them before, so a partner at 1/12 stood and walked on this screen as
    /// if untouched (reported 2026-09-17 from the site's own fight clip: the host at 1 hp,
    /// the joiner's copy of her walking normally). The point is not the numbers: the drive
    /// above writes the walk/run gait through <c>CSWSCreature::SetAnimation</c>, which
    /// substitutes the limping walk/run (0x2712->0x276D, 0x2714->0x276E) whenever
    /// <c>CSWSCreature::IsInjured</c> (0x4EFF30) says current/max &lt; 0.2 - the same rule
    /// that limps the peer on their own screen. With the fields mirrored, the engine's own
    /// substitution does the rest, no new animation plumbing.
    ///
    /// Max is written first (<c>ExecuteCommandSetMaxHitPoints</c>'s order: +0xE0, then the
    /// SetCurrentHitPoints call) and current is clamped to 1..max: a stand-in must never
    /// reach 0 through this path - the downed pose is <see cref="_down"/>'s job and the
    /// engine's death handling must not run on a body that stands for a living peer. A max
    /// of 0 means the agent is older than the packing (see <see cref="RemoteView.FlagsMask"/>)
    /// and the stand-in keeps its blueprint values. Latched per slot; the engine's own hit
    /// path may move +0xDC between steps and the next change re-asserts it.
    /// </summary>
    private void ApplyVitals(int slot, nint phantom, in RemoteView remote)
    {
        int max = remote.MaxHealth;
        if (max <= 0)
        {
            return;
        }

        int current = Math.Clamp(remote.Health, 1, max);
        if (_appliedHealth[slot] == current && _appliedMaxHealth[slot] == max)
        {
            return;
        }

        _appliedHealth[slot] = current;
        _appliedMaxHealth[slot] = max;
        *(ushort*)(phantom + Odyssey.ObjMaxHitPoints) = (ushort)Math.Min(max, ushort.MaxValue);
        Odyssey.SetCurrentHitPoints(phantom, current);
    }

    /// <summary>appearance.2da row, u16 inside CSWSCreatureAppearanceInfo at +0xA50.</summary>
    private const int CreAppearanceType = 0xA60;

    /// <summary>CSWSObject + this = portrait_id, the portraits.2da row (u16) — the same field
    /// <c>KotorGameBridge.ReadPartyPortraits</c> reads (⚠ NOT +0x20, which is dead for a
    /// creature in the world). Logged, never written: a phantom is not a party member, so
    /// nothing on this instance's HUD draws its portrait, and the blueprint's own 0 (an empty
    /// row in portraits.2da) is the correct value for a body that has no portrait.</summary>
    private const int ObjPortraitId = 0x30;

    /// <summary>
    /// Makes the phantom wear the remote player's appearance.2da row. Writing the row field is
    /// not enough on its own: the engine rebuilds a creature's model-type and per-body-part model
    /// strings from that row via UpdateAppearanceDependantInfo after every change, so a bare poke
    /// leaves the phantom holding body-part strings for its OLD (k_coop_phantom) appearance. A
    /// later weapon equip then rebuilds the appearance, reads one of those stale strings and
    /// crashes. Rebuild the derived state right after the write. See
    /// docs/research/phantom-equip-crash.md.
    ///
    /// ROUND 64 X2: <c>UpdateAppearanceDependantInfo</c> is the SERVER-side rebuild only. The
    /// CLIENT twin's own model/animbase — spawned for the blueprint's original appearance
    /// (<c>k_coop_phantom</c>) and never told to reload — was never rebuilt by anything in this
    /// codebase before this round. <see cref="Odyssey.RebuildClientAppearance"/> is the
    /// corresponding client-side call (docs/research/phantom-locomotion.md §11's X2
    /// experiment); called here, once, the same way the server-side rebuild already is.
    ///
    /// ⚠ WRONG-LOOK-AFTER-A-WARP (docs/research/name-plates.md, "The peer's LOOK has the same
    /// two halves as its name"). The old shape of this method decided "already applied" from
    /// the SERVER field alone, and applied nothing when it matched. That is the wrong question
    /// on the one path that matters: at SPAWN time the client twin does not exist yet, so the
    /// server field is written, <see cref="Odyssey.RebuildClientAppearance"/> finds no twin and
    /// silently does nothing, and the twin the engine builds a frame later is built from the
    /// BLUEPRINT's row. From then on the server field reads correct forever, so nothing ever
    /// re-applied it and the peer rendered as the blueprint (or, before the appearance channel
    /// existed at all, as whatever the fallback template was) for the rest of the module.
    ///
    /// It only bites after a RE-spawn — a module change — because that is when the peer's row is
    /// already known at spawn time; on a first connect the row usually arrives a tick later, so
    /// the drive loop's own call did the work with a twin present and the bug hid.
    ///
    /// So: the row is chosen by <see cref="PeerIdentityRule"/> (peer packet &gt; that peer's
    /// last published row &gt; leave it alone — never anything read off a local body), and the
    /// client half is re-asserted until <see cref="_twinDressed"/> latches, per body.
    /// </summary>
    private void ApplyAppearance(int slot, nint phantom, uint published)
    {
        if (PeerIdentityRule.IsUsableAppearance(published))
        {
            _appearanceCache[slot] = published;
        }

        uint chosen = PeerIdentityRule.ChooseAppearance(published, _appearanceCache[slot]);

        ushort* field = (ushort*)(phantom + CreAppearanceType);
        ushort onBody = 0;
        if (!CreatureFinder.TryReadSelf((nint)field, (byte*)&onBody, sizeof(ushort)))
        {
            return;
        }
        if (!PeerIdentityRule.ShouldApply(chosen, onBody, _twinDressed[slot]))
        {
            return;
        }

        if (onBody != (ushort)chosen)
        {
            *field = (ushort)chosen;
            Odyssey.RebuildAppearance(phantom);
        }

        // Cheap while the twin is missing (two reads and a return), fires its engine call
        // exactly once per body once it exists — the latch is the per-object discipline this
        // project's own per-frame-write rule asks for, not a cadence.
        if (!Odyssey.RebuildClientAppearance(phantom))
        {
            return;
        }

        _twinDressed[slot] = true;

        if (_identityLogged < MaxIdentityLines)
        {
            _identityLogged++;
            PluginHost.Log(
                $"identity: phantom slot {slot} (0x{_phantomIds[slot]:X}) now renders appearance "
                + $"{chosen} (server row was {onBody}, peer published {published})");
        }
    }

    /// <summary>
    /// Places a phantom exactly where its peer is, every frame, instead of walking it there.
    ///
    /// This is the seamless path. It writes BOTH halves — the server object and the client
    /// twin's desired position with a zero ease budget — because the renderer reads the twin,
    /// and the twin otherwise eases toward the server position over a fixed 512 ms, which is a
    /// permanent ~2-unit visual error at running speed no matter how well the server object
    /// tracks. See docs/research/realtime-creature-movement.md.
    ///
    /// No move action is ever queued in this mode and the snap is disabled: both are writers
    /// that would fight this one.
    /// </summary>
    private void DriveDirect(int slot, nint phantom, RemoteView remote, double now, nint player)
    {
        // How far the peer actually moved since the last update — the honest signal for both
        // "are they walking" and "how fast", independent of any estimate.
        float mdx = remote.Position.X - _lastGoal[slot].X;
        float mdy = remote.Position.Y - _lastGoal[slot].Y;
        float moved = MathF.Sqrt((mdx * mdx) + (mdy * mdy));

        // Speed is measured from that ACTUAL movement and smoothed, rather than sampled over
        // a fixed window. The window version read 0.00 in practice — most windows closed on a
        // frame where the block had not changed — so the gait never reached the run threshold
        // and a sprinting peer walked.
        //
        // ⚠ The clock advances ONLY when a displacement is consumed. This runs per frame
        // (hundreds of Hz) while the peer's position changes per network tick (~30 Hz), so
        // most frames see no movement. Advancing the clock every frame divided each tick's
        // full displacement by a single frame's dt — measured live as speeds of 5–8 u/s for
        // a peer running at 3.96, so the legs churned at double speed, then half, and the
        // body read as sliding across the floor. Sampling update-to-update yields the peer's
        // true speed.
        if (moved > MovementEpsilon)
        {
            if (_speedFromTime[slot] > 0)
            {
                float dt = (float)(now - _speedFromTime[slot]);
                if (dt > 0.001f)
                {
                    float instant = MathF.Min(moved / dt, 12f);
                    _peerSpeed[slot] = _peerSpeed[slot] <= 0f
                        ? instant
                        : (_peerSpeed[slot] * 0.7f) + (instant * 0.3f);
                }
            }

            _speedFromTime[slot] = now;
        }

        // Keep the body out of the local player's camera. Exact placement means two players
        // standing on the same spot occupy the same space — and since both sides of a test
        // load the same save, that is the FIRST thing that happens: the camera ends up inside
        // the phantom and the screen fills with its clothing texture. The engine's own
        // creature collision does not apply to a body we place directly, so the separation
        // has to be explicit. Only nudges when they would genuinely overlap, so it never
        // fights normal movement.

        Vec3 target = remote.Position;
        Vec3 mine = Odyssey.GetPosition(player);
        float sx = target.X - mine.X;
        float sy = target.Y - mine.Y;
        float apartSquared = (sx * sx) + (sy * sy);

        if (apartSquared < MinSeparation * MinSeparation)
        {
            float apart = MathF.Sqrt(apartSquared);
            if (apart > 0.01f)
            {
                target.X = mine.X + (sx / apart * MinSeparation);
                target.Y = mine.Y + (sy / apart * MinSeparation);
            }
            else
            {
                // Exactly co-located (a fresh load from the same save): pick a direction.
                target.X = mine.X + MinSeparation;
            }
        }

        // SMOOTH toward the target instead of snapping onto it.
        //
        // This is what "floating / teleporting" was. Placing the body exactly on each
        // received position means the only motion it ever has is a jump: it sits still
        // between updates and then appears somewhere new, with nothing in between. Network
        // jitter makes it worse, because the jumps are unevenly spaced.
        //
        // Chasing the target with a short time constant turns those discrete arrivals into
        // continuous motion, at the cost of a fraction of the gap. A genuinely large jump —
        // a teleport, a door, a peer who was off-screen — still snaps, because gliding
        // across a room would look far stranger than a cut.
        Vec3 shown = _shownPosition[slot];
        float jdx = target.X - shown.X;
        float jdy = target.Y - shown.Y;
        float gapSquared = (jdx * jdx) + (jdy * jdy);

        // How fast the body is actually shown covering ground this step, for the gait below.
        float chaseSpeed = 0f;

        if (!_smoothing[slot] || gapSquared > SnapGapSquared)
        {
            _smoothing[slot] = true;
            shown = target;
        }
        else
        {
            // Frame-rate independent: the same fraction of the gap closes per unit of time
            // whatever the step rate happens to be.
            float dt = (float)Math.Clamp(now - _shownAtTime[slot], 0.0, 0.25);
            float alpha = _smoothingTauSeconds <= 0f ? 1f : 1f - MathF.Exp(-dt / _smoothingTauSeconds);

            float stepX = jdx * alpha;
            float stepY = jdy * alpha;

            // Bound the chase to a speed a body could plausibly have. The exponential term
            // alone closes a gap at gap/tau — after any stall (a burst of packet loss, a
            // heavy pass hogging the pump thread, an interpolation-buffer dry spell) that is
            // tens of units per second under the snap threshold, and the phantom streaks
            // across the screen with its legs at a normal cycle: the "flying" look. Capped,
            // the same recovery is a sprint; a deficit the cap cannot close grows until it
            // crosses SnapGapSquared and cuts, which is what a real teleport deserves.
            // 12 u/s is the plausibility ceiling the speed estimator already uses, so
            // ordinary tracking (force speed included) is never capped, only recovery.
            float stepLength = MathF.Sqrt((stepX * stepX) + (stepY * stepY));
            float maxStep = MaxChaseUnitsPerSecond * dt;
            if (stepLength > maxStep && stepLength > 0f)
            {
                float scale = maxStep / stepLength;
                stepX *= scale;
                stepY *= scale;
                stepLength = maxStep;
            }

            shown.X += stepX;
            shown.Y += stepY;
            shown.Z = target.Z;

            if (dt > 0.001f)
            {
                chaseSpeed = stepLength / dt;
            }
        }

        _shownPosition[slot] = shown;
        _shownAtTime[slot] = now;

        // "Is the peer moving" is decided with HYSTERESIS, not by thresholding the speed
        // estimate. Measured live, a bare threshold alternated run/idle several times a
        // second, and because the engine only pushes an animation to the render side on
        // change, the twin mostly ended up holding idle — the body glided with no run cycle,
        // which is what "looks like the character is flying" was. Any movement at all starts
        // the gait; it stops only after the peer has genuinely been still for a moment.
        if (moved > MovementEpsilon)
        {
            _lastMovedAt[slot] = now;
        }

        bool moving = _lastMovedAt[slot] > 0 && (now - _lastMovedAt[slot]) < StillnessSeconds;

        // The gait follows the speed the body is SHOWN moving at, not only the peer's
        // measured speed. In steady tracking the two agree (the chase settles at exactly
        // the stream's speed), but during a capped catch-up the body genuinely covers
        // ground faster than the peer does — feeding the peer's number there kept the legs
        // at a walk while the floor streamed past, which reads as sliding. Max, not
        // replacement: the chase speed decays as the gap closes, and the peer's smoothed
        // estimate is the steadier signal while simply following.
        float gaitSpeed = MathF.Max(_peerSpeed[slot], chaseSpeed);

        Odyssey.DrivePhantom(phantom, shown, remote.Facing, gaitSpeed, moving);
        _lastGoal[slot] = remote.Position;
    }

    /// <summary>Movement below this between updates is jitter, not walking.</summary>
    private const float MovementEpsilon = 0.02f;

    /// <summary>How long the peer must be still before the gait drops back to idle. Longer
    /// than any plausible gap between updates, so a late packet cannot look like a stop.</summary>
    private const double StillnessSeconds = 0.35;

    private readonly double[] _lastMovedAt = new double[SharedBlock.SlotCapacity];

    /// <summary>
    /// How quickly the drawn position chases the received one. 60 ms closes most of a gap
    /// within a couple of frames, so the body reads as moving rather than stepping, without
    /// adding meaningful lag on top of the network.
    /// </summary>
    private readonly float _smoothingTauSeconds =
        Math.Clamp(PluginConfig.ReadFloat("smooth_ms", 60f), 0f, 400f) / 1000f;

    /// <summary>Past this the peer did not walk, they moved — cut instead of gliding.</summary>
    private const float SnapGapSquared = 8f * 8f;

    /// <summary>The fastest the shown position may chase the received one, in units per
    /// second. Bounds the recovery glide after a stall; see the comment in DriveDirect.
    /// Mirrors <see cref="MotionSmoother"/>'s cap — keep the two in agreement.</summary>
    private const float MaxChaseUnitsPerSecond = 12f;

    private readonly Vec3[] _shownPosition = new Vec3[SharedBlock.SlotCapacity];
    private readonly double[] _shownAtTime = new double[SharedBlock.SlotCapacity];
    private readonly bool[] _smoothing = new bool[SharedBlock.SlotCapacity];

    /// <summary>How close a phantom may get to the local player before it is pushed out.
    /// Roughly a body's width — enough that the camera never enters it.</summary>
    private const float MinSeparation = 1.2f;
}
