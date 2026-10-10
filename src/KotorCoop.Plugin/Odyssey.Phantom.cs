// Odyssey.Phantom.cs - driving a phantom (an un-controlled peer body) in real time:
// DrivePhantom, DriveEnemy, DriveBody.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>
    /// Rig-only gait diagnostics. Disabled by default because the name/class probes and their
    /// synchronous log writes run on the game's frame thread; enabling this does not alter the
    /// gait decision or any animation/position write.
    /// </summary>
    private static readonly bool GaitDiagnosticsEnabled =
        PluginConfig.ReadBool("phantom_gait_diagnostics", false);

    // ---- driving a phantom in real time -------------------------------------------
    //
    // The renderer does NOT read the server creature. It reads the client twin, and the twin
    // does not take the server's position either — it EASES toward it over a fixed 512 ms
    // budget (CSWCCreature::ResetDesiredPositionTimer writes 0x200 into twin+0x1E8, and that
    // is the only value anything ever writes there). So a peer running at speed renders a
    // permanent ~2 units behind where they actually are, no matter how perfectly the server
    // object tracks. That is the larger half of the reported lag; the pathfinding walk was
    // the smaller half.
    //
    // Driving both halves ourselves — the server position, and the twin's desired position
    // with a zero budget so it snaps instead of easing — removes both. Writing the twin's
    // desired position directly also sidesteps the engine's "visible to player" gate, so an
    // off-screen phantom keeps tracking instead of freezing until someone looks at it.
    // See docs/research/realtime-creature-movement.md.

    /// <summary>CSWCCreature + this = the desired position the twin eases toward.</summary>
    private const int TwinDesiredPosition = 0x1DC;

    /// <summary>CSWCCreature + this = milliseconds it is allowed to take. 0 = snap.</summary>
    private const int TwinDesiredPositionBudget = 0x1E8;

    /// <summary>CSWSObject + this = the current animation the gait is read from.</summary>
    private const int ObjAnimationField = 0xD4;

    /// <summary>
    /// Places a phantom exactly where its peer is, this frame, with the gait that matches how
    /// fast they are actually moving.
    ///
    /// <paramref name="speed"/> is measured from the position stream rather than taken from
    /// the peer's animation field, because it is the thing that decides walk-vs-run and the
    /// animation field lags it.
    ///
    /// MAIN THREAD ONLY, and specifically from the SwapBuffers hook: that runs after both
    /// main loops, so these writes are the last word before the next frame consumes them.
    /// </summary>
    /// <param name="moving">Whether the peer is moving AT ALL, decided by the caller with
    /// hysteresis. It is passed in rather than derived from <paramref name="speed"/> because
    /// a threshold on an instantaneous estimate flickers, and a flickering gait is worse than
    /// a wrong one: the engine only pushes an animation to the render side ON CHANGE, so a
    /// value that alternates run/idle several times a second mostly arrives as idle — which
    /// is exactly the body gliding along with no run cycle.</param>
    public static void DrivePhantom(nint creature, Vec3 position, float facing, float speed, bool moving, uint driveMask = 0)
    {
        (bool wasRunning, float belowIdleSeconds) = AdvanceGaitHysteresis(creature, speed);
        int want = GaitRule.SelectAnimation(speed, moving, wasRunning, belowIdleSeconds);
        DriveBody(creature, position, facing, speed, moving, want, GaitRule.IsRun(speed, wasRunning), driveMask);
    }

    /// <summary>
    /// Same drive as <see cref="DrivePhantom"/> for a creature the engine spawned itself —
    /// an enemy mirrored from the owning peer. The one difference is the standing
    /// animation: a phantom at rest is idle, but a mirrored enemy at rest should hold
    /// whatever its owner's engine has it holding (combat-ready stance, casting loop), so
    /// the owner's looping animation id is applied when it is plausible and idle when not.
    /// While moving, the gait still comes from the measured stream speed — the owner's
    /// field lags it, and a wrong gait reads worse than a wrong stance.
    /// </summary>
    /// <param name="bisectMask">0 (default) is today's unconditional behaviour,
    /// unaffected — <see cref="PartyPresenceController"/>'s call never passes anything else.
    /// See the bit table on <see cref="ClaimCreature(nint, bool, uint)"/>.</param>
    public static void DriveEnemy(nint creature, Vec3 position, float facing, float speed, bool moving, uint ownerAnimation, uint bisectMask = 0)
    {
        (bool wasRunning, float belowIdleSeconds) = AdvanceGaitHysteresis(creature, speed);

        // NEEDS-LIVE: engine looping ids sit at 0x2710 (10000) and up; anything outside
        // that band here is a read gone wrong or a one-shot that does not belong in a
        // looping field, and idle is the safe substitute.
        int want = moving
            ? GaitRule.SelectAnimation(speed, moving: true, wasRunning, belowIdleSeconds)
            : (ownerAnimation is >= 0x2710 and < 0x2800 ? (int)ownerAnimation : AnimIdle);

        DriveBody(creature, position, facing, speed, moving, want, GaitRule.IsRun(speed, wasRunning), bisectMask);
    }

    /// <summary>CSWCObject::SetOrientation vtable slot (0x0063C110, VERIFIED
    /// docs/re/rendering.md §9.2/§7.3 — the same low-level twin setter the engine itself
    /// calls at construction time to write the twin's rendered orientation directly). See
    /// the call site below for why the twin needs this in addition to
    /// <see cref="SetOrientationFn"/>.</summary>
    private const int TwinOrientationSetSlot = 0x88;

    private static void DriveBody(nint creature, Vec3 position, float facing, float speed, bool moving, int want, bool run, uint bisectMask = 0)
    {
        // Computed unconditionally (two cheap trig calls): the twin-orientation write
        // further down needs it even on a pass where bit 5 leaves the SERVER write alone.
        Vec3 direction = new() { X = MathF.Cos(facing), Y = MathF.Sin(facing), Z = 0f };
        if ((bisectMask & BisectBitDriveFacing) == 0)
        {
            SetOrientationFn(creature, &direction);
        }

        if ((bisectMask & BisectBitDriveRunMoveState) == 0)
        {
            *(int*)(creature + CreRunFlag) = run ? 1 : 0;
            *(int*)(creature + CreMoveState) = moving ? -1 : 1;
        }

        if ((bisectMask & BisectBitDriveAnimation) == 0 && *(int*)(creature + ObjAnimationField) != want)
        {
            var setAnimation =
                (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)(*(nint*)creature + VtableSetAnimation));
            setAnimation(creature, want);
        }

        // arg3 = 1 SUPPRESSES the position dirty bit, deliberately. When the bit is raised,
        // the AI master's sync pass calls the twin's position handler, which (a) takes its
        // own interpolation step against ours and (b) re-derives the animation playback rate
        // via ComputeSpeedFactor — a function that returns 0.0 for any creature without a
        // waypoint list, which a phantom never has. Rate 0.0 is a run cycle that never
        // advances: the legs freeze mid-pose while the body slides — the "flying" look.
        // We drive the twin ourselves below, so the engine's push is pure interference.
        if ((bisectMask & BisectBitDrivePosition) == 0)
        {
            SetPositionFn(creature, &position, 1, 0, 1);
        }

        // The half that actually renders. Re-read the twin every frame — it is created and
        // destroyed by the engine and a cached pointer would eventually be stale.
        nint twin = (bisectMask & BisectBitDriveTwin) == 0 ? *(nint*)(creature + CreClientTwin) : 0;
        if (twin != 0)
        {
            *(Vec3*)(twin + TwinDesiredPosition) = position;
            *(int*)(twin + TwinDesiredPositionBudget) = 0;

            if (*(nint*)twin == ClientCreatureVtable)
            {
                // ⚠ FACING RENDERS THROUGH A SEPARATE, STILL-EASED PATH — position does not.
                // The server-side SetOrientationFn write above (0x004CD170) always raises
                // dirty bit 2 (CSWSObject + 0x1FC), and CServerAIMaster::UpdateState's
                // per-frame sync consumes it through the twin's own dirty-bit handler,
                // ClientServerSyncHandler_Orientation (vtable +0x130, 0x00611EC0), which
                // calls RotateCreatureToOrientation (0x00611850) — a RATE-LIMITED TURN, not
                // a snap (docs/research/realtime-creature-movement.md §4.4, long flagged
                // NEEDS-LIVE and never actually addressed). Position gets a genuine snap
                // because ITS dirty-bit handler (+0x12C, just above) takes an instant branch
                // when TwinDesiredPositionBudget reads 0; orientation's handler has no such
                // budget to zero — §4.4 confirms it always eases. So a mirrored NPC/companion
                // that snaps to a new heading (a follow-AI recovery's positionparty
                // reformation, any sharp turn) renders on a peer with its body still turning
                // toward the real facing for a visible stretch, even though the position it
                // stands at is already exact — "pointing" the wrong way while correctly
                // placed. Confirmed live 2026-09-10: docs/research/network-topology.md's
                // 2026-09-10 follow-up.
                //
                // The fix bypasses +0x130 instead of trying to budget it: call the twin's
                // own low-level orientation setter, CSWCObject::SetOrientation (vtable +0x88,
                // 0x0063C110, VERIFIED docs/re/rendering.md §9.2/§7.3 — the exact call the
                // engine itself uses to write a fresh twin's orientation at construction
                // time), directly, every frame. This runs from the SwapBuffers hook, which
                // is the last word before the next frame's AIUpdate — so whatever partial
                // turn +0x130 already took this frame gets overwritten with the true target
                // right here, and next frame's +0x130 step starts from (and lands on) that
                // same target, i.e. a zero-length "turn" with nothing left to visibly ease.
                if ((bisectMask & BisectBitDriveFacing) == 0)
                {
                    var setTwinOrientation =
                        (delegate* unmanaged[Thiscall]<nint, Vec3*, void>)(*(nint*)(*(nint*)twin + TwinOrientationSetSlot));
                    setTwinOrientation(twin, &direction);
                }

                // ⚠ The gait must be set on the TWIN as well, and specifically through the
                // server→client animation sync handler (twin vtable +0x134) — the entry the
                // engine's own dirty-push uses. It dispatches looping animations (run/walk/
                // idle are looping) to the looping setter that writes twin+0x48, which
                // AnimationUpdate re-asserts from every frame. The plain setter (vtable
                // +0x9C) plays the animation without owning +0x48 and the next frame
                // reverted it — measured live as the twin never leaving idle.
                //
                // ⚠⚠ Assert only when OUR DESIRED GAIT CHANGES — never on "the twin shows
                // something else". A one-shot (weapon flourish, stim, the X key) briefly
                // takes over twin+0x48 while it plays, and the mismatch form re-asserted the
                // run cycle within a frame, visibly cancelling every one-shot on the phantom
                // — reported from play as "the X animation no longer shows". The engine
                // restores the looping gait by itself when a one-shot ends, so leaving the
                // field alone between gait changes costs nothing.
                // twin+0x58 (TwinLoopingAnimation) is written unconditionally every step to
                // hold our intended gait id. Without this, the engine's own per-frame
                // reassertion (AnimationUpdate -> GetLoopingAnimation -> SetAnimation) reads
                // whatever stale idle/pause id twin+0x58 last legitimately held and replays it —
                // and that replay succeeds where a direct walk/run write does not, because
                // idle/pause rows are Looping=1 in animations.2da while walkSS/runSS are not, so
                // CSWCObject::SetAnimation's AnimationLooping gate lets the stale idle through.
                // Keeping +0x58 synced turns that same reassertion into
                // SetAnimation(walkSS/runSS, ...), which AnimationLooping rejects, so it becomes
                // a no-op instead of a guaranteed revert to idle. Full derivation and offsets:
                // docs/research/phantom-locomotion.md §12.
                //
                // The budget passed to the +0x12C sync call below also matters: static
                // disassembly (§9.1) shows the running branch only reaches the real "play by
                // name" step when budget < 0.5 (verified live-read constant at 0x0073E9AC).
                // 0f is the correct "still moving, not a new blend" budget, matching
                // TwinDesiredPositionBudget's own use, and is deliberately not the same as
                // ObjAnimationBudget (unrelated to what TwinAnimationSyncSlot passes below).
                if ((bisectMask & DriveSkipTwinGait) != 0)
                {
                    // A dialogue stunt is playing on this body: the stunt sets its own animation
                    // ids and plays at its authored rate (Odyssey.Stunts.cs).
                    *(float*)(twin + TwinAnimationSpeedFactor) = 1.0f;
                    return;
                }

                *(ushort*)(twin + TwinLoopingAnimation) = (ushort)want;

                // TwinPositionSyncSlot (ClientServerSyncHandler_Position, +0x12C) is invoked
                // only on a gait CHANGE, not every drive step: its call chain has no
                // "already playing this id, skip" guard anywhere, so firing it every step
                // (60 Hz) restarted the walk/run cycle from frame 0 continuously — a
                // structural stutter, not a rare glitch. Gating on the edge is safe because
                // the twin+0x58 write above already keeps AnimationUpdate's per-frame
                // reassertion a no-op independent of this call firing, and position/rate are
                // both written unconditionally elsewhere; +0x12C's only remaining job is the
                // one-time "start playing this cycle" trigger a genuine gait transition needs.
                // See docs/research/phantom-locomotion.md §13.
                bool gaitChanged = GaitChanged(creature, want);

                if (gaitChanged)
                {
                    var syncPosition =
                        (delegate* unmanaged[Thiscall]<nint, Vec3*, uint, float, void>)(*(nint*)(*(nint*)twin + TwinPositionSyncSlot));

                    // Capped diagnostic: confirms whether this call's own write to twin+0x48
                    // sticks even briefly, or is reverted before the next sample can see it —
                    // see docs/research/phantom-locomotion.md §9.3. Only reachable on a gait
                    // edge, so the cap fills slowly.
                    bool diag = moving && _syncPositionDiagLogged < 8;
                    ushort before = diag ? *(ushort*)(twin + TwinCurrentAnimation) : (ushort)0;

                    syncPosition(twin, &position, (uint)want, TwinPositionSyncBudget);
                    _syncPositionCallCount++;

                    if (diag)
                    {
                        _syncPositionDiagLogged++;
                        ushort after = *(ushort*)(twin + TwinCurrentAnimation);
                        PluginHost.Log(
                            $"sync12c: want 0x{want:X} run={run} before=0x{before:X} "
                            + $"after=0x{after:X} budget={TwinPositionSyncBudget:F2}");
                    }
                }

                if (gaitChanged)
                {
                    // TwinAnimationSyncSlot (+0x134) is proven dead for Walk/Run: AnimationLooping
                    // structurally rejects 0x2712/0x2714 for every creature (walk/run are not a
                    // looping animations.2da category), so CSWCObject::SetAnimation's own gate
                    // always bails before touching twin+0x48/+0x58 for these two ids — confirmed
                    // live (skipping it changed nothing, 96% idle-while-moving unchanged from
                    // baseline). It's skipped only for Walk/Run; idle and DriveEnemy's
                    // owner-animation branch (real combat/overlay ids) still need this entry —
                    // see PlayOneShotAnimation/MirrorAnimation, which route one-shots through it.
                    // See docs/research/phantom-locomotion.md §10.
                    if (want != GaitRule.AnimWalk && want != GaitRule.AnimRun)
                    {
                        var syncAnimation =
                            (delegate* unmanaged[Thiscall]<nint, uint, float, void>)(*(nint*)(*(nint*)twin + TwinAnimationSyncSlot));
                        syncAnimation(twin, (uint)want, *(float*)(creature + ObjAnimationBudget));
                    }

                    // ⚠ DIAGNOSTIC (docs/research/phantom-locomotion.md): +0x134 is confirmed
                    // address-correct (live vtable read matches docs exactly) and confirmed
                    // NOT to stick — 132/133 samples at
                    // 40ms cadence during a sustained real run showed server 0xD4 correctly
                    // cycling walk/run, rate nonzero, moveState -1, but twin+0x48 never left
                    // idle. A direct call to this function's own disassembled fallthrough
                    // apply slot (+0xA0) did not fix it either (tested live, reverted) — so
                    // whatever gates the apply is upstream of both. The engine's OWN
                    // classifiers (already proven correct elsewhere in this file for
                    // one-shots, see PlayOneShotAnimation) settle whether this creature's
                    // animbase recognises 0x2710/0x2712/0x2714 as looping/fire-forget/overlay
                    // AT ALL: live-measured answer is looping=0, fireForget=0, overlay=0 for
                    // every one of them — the animbase does not classify these ids as
                    // anything playable, which is consistent with them being SERVER-side
                    // abstract state markers (AnimationWalking/AnimationStationary) rather
                    // than per-model animations.2da rows, and explains why the client-side
                    // sync handler's internal "is this a real animation" gate never opens for
                    // a directly-driven phantom. The real fix needs the per-model row a
                    // normally AI/waypoint-driven creature would resolve through — not found
                    // this pass; kept as a live, capped diagnostic for whoever picks it up.
                    if (GaitDiagnosticsEnabled && _gaitClassLogged < 30)
                    {
                        _gaitClassLogged++;
                        int loop = AnimationLoopingFn(twin, (ushort)want);
                        int fireForget = AnimationFireAndForgetFn(twin, (ushort)want);
                        int overlay = AnimationOverlayFn(twin, (ushort)want);

                        // AnimationLooping/FireAndForget/Overlay (above) do NOT gate this call —
                        // disassembly of ClientServerSyncHandler_Animation (0x0060C100) and the
                        // plain setter it falls through to (CSWCObject::SetAnimation,
                        // 0x0063E930) shows the real gate is CSWCAnimBase::AnimationExists
                        // (animbase vtable +0x50, 0x0069D910), which refuses everything while
                        // the animbase's own Gob* (+0xB8) is null. Probe both directly. Also
                        // probe AnimationWalking/Running — the predicates
                        // ClientServerSyncHandler_Position (the +0x12C call, above) actually
                        // branches on — to see whether that call is even reaching its
                        // animbase-vtable+0x54 "play" step for this creature.
                        nint animBaseC = *(nint*)(twin + TwinAnimBase);
                        nint gob = animBaseC == 0 ? 0 : *(nint*)(animBaseC + AnimBaseGob);
                        int exists = animBaseC == 0 ? -1 : AnimationExistsFn(animBaseC, (ushort)want);
                        int walking = AnimationWalkingFn(twin, (ushort)want);
                        int running = AnimationRunningFn(twin, (ushort)want);
                        byte weaponClassC = animBaseC == 0 ? (byte)0xFF : *(byte*)(animBaseC + 0xC5);
                        int runLooksAwful = animBaseC == 0 ? -1 : *(int*)(animBaseC + 0xCC);
                        PluginHost.Log(
                            $"gait-class: id 0x{want:X} on twin 0x{twin:X} -> looping={loop} "
                            + $"fireForget={fireForget} overlay={overlay} animBase=0x{animBaseC:X} "
                            + $"gob=0x{gob:X} animationExists={exists} walking={walking} "
                            + $"running={running} weaponClass={weaponClassC} runLooksAwful={runLooksAwful}");
                    }

                    // Ground-truths the actual resolved animation NAME rather than inferring it
                    // from the classifier booleans logged above. Capped at 20 PER CREATURE (its
                    // own cap, GaitNameLogged, independent of _gaitClassLogged), since that
                    // counter is process-lifetime and a long session would otherwise exhaust it
                    // before this diagnostic got a turn. See docs/research/phantom-locomotion.md §11.
                    nint animBase = *(nint*)(twin + TwinAnimBase);
                    int gaitSlot = GaitSlotOf(creature);
                    if (GaitDiagnosticsEnabled && animBase != 0 && gaitSlot >= 0
                        && GaitNameLogged[gaitSlot] < 20)
                    {
                        GaitNameLogged[gaitSlot]++;
                        byte weaponClass = *(byte*)(animBase + 0xC5);
                        string phantomName = ReadAnimationName(animBase, (ushort)want, out int phantomLen);
                        string model = ReadResRef(animBase + AnimBaseModelName);
                        ushort appearanceRow = *(ushort*)(creature + CreServerAppearanceRow);

                        // The SAME probe on the LOCAL PLAYER's own twin/animbase — the
                        // control this round's brief asked for. GetAnimationName takes no
                        // moveState/velocity input (static disassembly, §10.2/§11: it is a
                        // pure function of (animBase, id)), so sampling it while the local
                        // player happens to be standing still is not a weaker sample than
                        // sampling it mid-stride — there is no "in transit" state for this
                        // specific call to observe.
                        nint myTwin = GetLocalPlayerTwin();
                        nint myAnimBase = myTwin == 0 ? 0 : *(nint*)(myTwin + TwinAnimBase);
                        string myName = "";
                        int myLen = -1;
                        string myModel = "";
                        byte myWeaponClass = 0xFF;
                        if (myAnimBase != 0)
                        {
                            myName = ReadAnimationName(myAnimBase, (ushort)want, out myLen);
                            myModel = ReadResRef(myAnimBase + AnimBaseModelName);
                            myWeaponClass = *(byte*)(myAnimBase + 0xC5);
                        }

                        PluginHost.Log(
                            $"gait-name: id 0x{want:X} phantom-> name='{phantomName}' len={phantomLen} "
                            + $"model='{model}' appearanceRow={appearanceRow} weaponClass={weaponClass} "
                            + $"| player-> name='{myName}' len={myLen} model='{myModel}' "
                            + $"weaponClass={myWeaponClass} animBase=0x{myAnimBase:X}");
                    }
                }

                // The playback rate the engine would have computed from the waypoint list we
                // do not have. 1.0 plays the cycle at its authored rate; scale it by how fast
                // the peer actually covers ground so the feet roughly match the floor.
                float rate = moving
                    ? Math.Clamp(speed / (run ? RunSpeedReference : WalkSpeedReference), 0.6f, 1.5f)
                    : 1.0f;
                *(float*)(twin + TwinAnimationSpeedFactor) = rate;
            }
        }

        // ⚠ WALL CLOCK, NOT A FRAME COUNT — and this is a performance fix, not tidiness.
        //
        // This was `_gaitLogTimer++` against a threshold of 240, on a SINGLE static counter
        // incremented once per DriveCreature call. That is once per DRIVEN BODY per frame, not
        // once per frame, so a client mirroring 32 enemies plus companions crossed the
        // threshold about 33x faster than the number suggests.
        //
        // Measured: ~18,000 gait lines on a client against 21 on the host over the same window
        // (a 2.1 MB log against 98 KB), averaging 22.8 lines/second and peaking at 74. And
        // PluginHost.Log writes each line TWICE — combined log plus channel log — each a full
        // open/write/close, so the client was doing ~46 synchronous file operations a second
        // on the game's frame thread, peaking near 148. On the path whose own contract says
        // allocation-free.
        //
        // A wall-clock gate makes it one line per interval no matter how many bodies are
        // driven, which is what "every so often, tell me what the gait looks like" always
        // meant.
        long nowMs = Environment.TickCount64;
        if (GaitDiagnosticsEnabled && moving && nowMs - _gaitLoggedAtMs >= GaitLogIntervalMs)
        {
            long intervalMs = _gaitLoggedAtMs == 0 ? GaitLogIntervalMs : nowMs - _gaitLoggedAtMs;
            _gaitLoggedAtMs = nowMs;
            nint t = *(nint*)(creature + CreClientTwin);
            nint mine = GetLocalPlayerTwin();
            int syncCalls = _syncPositionCallCount;
            _syncPositionCallCount = 0;
            PluginHost.Log(
                $"gait: speed {speed:F2} -> {(run ? "run" : "walk")} "
                + $"(server 0x{*(int*)(creature + ObjAnimationField):X}, "
                + $"twin 0x{(t == 0 ? 0 : *(ushort*)(t + TwinCurrentAnimation)):X}, "
                + $"rate {(t == 0 ? 0f : *(float*)(t + TwinAnimationSpeedFactor)):F2}, "
                + $"my twin 0x{(mine == 0 ? 0 : *(ushort*)(mine + TwinCurrentAnimation)):X} "
                + $"rate {(mine == 0 ? 0f : *(float*)(mine + TwinAnimationSpeedFactor)):F2}, "
                // TwinPositionSyncSlot (+0x12C) invocations across every driven body in the
                // last interval — the direct call-rate ground truth for the gait-edge-only fix
                // (see docs/research/phantom-locomotion.md §13). Should sit near 0 during
                // sustained straight-line movement.
                + $"sync12c-calls {syncCalls} in {intervalMs}ms)");
        }
    }

    /// <summary>When the gait diagnostic last spoke, and how often it may. Seconds, because
    /// this is a "what does the gait look like" sample, not a trace.</summary>
    private static long _gaitLoggedAtMs;

    private const long GaitLogIntervalMs = 5000;

    /// <summary>Caps the "gait-class" one-time diagnostic (see DriveBody) so it
    /// prints a handful of samples across process lifetime, not once per gait change.</summary>
    private static int _gaitClassLogged;

    /// <summary>Caps the "sync12c" before/after diagnostic (see DriveBody).</summary>
    private static int _syncPositionDiagLogged;

    /// <summary>Counts every invocation of TwinPositionSyncSlot (+0x12C), across every driven
    /// body, since the last time the periodic gait log line reset it — ground-truth evidence
    /// for the gait-edge-only fix (see docs/research/phantom-locomotion.md §13). Should sit
    /// near zero during sustained straight-line movement and only spike on an actual gait
    /// transition.</summary>
    private static int _syncPositionCallCount;

    /// <summary>Last gait asserted per driven creature, so the twin call fires on change
    /// only. Fixed slots rather than a dictionary: at most 8 phantoms plus 32 mirrored
    /// enemies exist, and this runs on the game's frame hook where allocation is unwelcome.</summary>
    private static readonly nint[] GaitCreature = new nint[48];
    private static readonly int[] GaitLast = new int[48];

    /// <summary>Per-creature cap for the "gait-name" diagnostic (see DriveBody) —
    /// parallels <see cref="GaitCreature"/> by slot index, so each driven body gets its own
    /// first-20 budget instead of sharing one global counter.</summary>
    private static readonly int[] GaitNameLogged = new int[48];

}
