// Odyssey.Phantom.cs - driving a phantom (an un-controlled peer body) in real time:
// DrivePhantom, DriveEnemy, DriveBody.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
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
    public static void DrivePhantom(nint creature, Vec3 position, float facing, float speed, bool moving)
    {
        (bool wasRunning, float belowIdleSeconds) = AdvanceGaitHysteresis(creature, speed);
        int want = GaitRule.SelectAnimation(speed, moving, wasRunning, belowIdleSeconds);
        DriveBody(creature, position, facing, speed, moving, want, GaitRule.IsRun(speed, wasRunning));
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
    /// <param name="bisectMask">ROUND 47: 0 (default) is today's unconditional behaviour,
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

    private static void DriveBody(nint creature, Vec3 position, float facing, float speed, bool moving, int want, bool run, uint bisectMask = 0)
    {
        if ((bisectMask & BisectBitDriveFacing) == 0)
        {
            Vec3 direction = new() { X = MathF.Cos(facing), Y = MathF.Sin(facing), Z = 0f };
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
                // ROUND 61 FIX, call #2 of 2: ClientServerSyncHandler_Position (+0x12C) is the
                // ONLY function that ever resolves 0x2712/0x2714 into a real, classifier-
                // approved (AnimationWalking/AnimationRunning) animation and plays it by name —
                // see TwinPositionSyncSlot's doc. The engine itself calls this on EVERY frame a
                // moving creature's position dirty bit is set (i.e. continuously while moving),
                // not once per gait change — DriveBody's bSuppressDirty=1 (the SetPositionFn
                // call above) has always kept it from firing at all. Calling it only on
                // GaitChanged (this fix's first live attempt) made walk display CORRECTLY SOME
                // of the time but not reliably, and run not at all — live-measured evidence
                // that, like the engine's own re-assertion loop in AnimationUpdate, this needs
                // to fire every step to stick, not just on the edge. So: every step, not gated
                // — SUPERSEDED by round 67, see that round's comment on the call site below:
                // once round 65's twin+0x58 write (also below) neutralises the reassertion by
                // itself, per-step firing of THIS call became actively harmful (it restarts the
                // cycle every step — the "stutter" defect) rather than merely redundant, so it
                // is edge-triggered again, this time correctly.
                // Its interpolation-step and rate side effects are overwritten by this same
                // function's unconditional position (above) and rate (below) writes before the
                // next frame ever reads them.
                // ROUND 62: the budget arg matters more than "some sane float" — static
                // disassembly of 0x00611F50 (docs/research/phantom-locomotion.md §9.1) shows
                // the RUNNING branch (AnimationRunning(want)==true) only reaches the real,
                // classifier-approved "play by name" step when budget < 0.5 (fcomp against the
                // constant at 0x0073E9AC, verified live-read as 0.5f). Any budget >= 0.5 —
                // which is exactly what creature+0xD8 (ObjAnimationBudget) reads live, ~1.0,
                // per round 60's own measurement — permanently diverts to the
                // "RunLooksAwful"-branched fallback, which has its own further gates (the
                // animBase+0xCC flag this same call computes from a SEPARATE, twin+0x210-keyed
                // comparison) that do not reliably resolve for a body this handler has never
                // driven before. WALK has no such gate at all (AnimationWalking's branch falls
                // straight into the shared play tail unconditionally) — this asymmetry is
                // exactly why round 61 measured walk partially working and run almost never.
                // We call this handler every step to look like a continuously-moving creature,
                // not a fresh onset — 0f is the semantically-correct "still moving, not a new
                // blend" budget for that, and it is what TwinDesiredPositionBudget already uses
                // for the same reason. Deliberately NOT reusing ObjAnimationBudget here — this
                // call's budget is unrelated to what TwinAnimationSyncSlot passes below, which
                // never reaches this gate (see TwinAnimationSyncSlot's doc, round 61 §8.1).
                // ROUND 65 FIX: neutralise CSWCCreature::AnimationUpdate's own per-frame
                // re-assertion instead of racing it. Static disassembly this round
                // (docs/research/phantom-locomotion.md §12) found the exact re-asserter: every
                // AIUpdate tick that does NOT dispatch to DriveAnimationUpdate/
                // FollowAnimationUpdate (neither applies to a directly-driven phantom/mirrored
                // enemy — no drive flag, no follow target) instead calls the DEFAULT
                // AnimationUpdate (0x0061A980), which — whenever twin+0x48 (TwinCurrentAnimation)
                // is NOT already AnimationStationary — calls CSWCObject::SetAnimation
                // (vtable+0x9C) with whatever id GetLoopingAnimation() (0x0063C100, verified a
                // one-line `return this->+0x58`) returns, UNCONDITIONALLY, every single frame.
                // twin+0x58 is the symbol db's own `looping_animation` field
                // (tools/kotor1_0_3.db: CSWCObject+0x058, ushort) and nothing in this codebase
                // (or, per round 61's own disassembly of TwinAnimationSyncSlot's target, anything
                // reachable for gait ids AT ALL) ever seeds it with 0x2712/0x2714 — it sits at
                // whatever idle/pause id it last legitimately held. So every frame our own
                // TwinPositionSyncSlot call below (or, rarely, some other path) manages to paint
                // a real walk/run id onto twin+0x48, THIS SAME OR THE VERY NEXT AnimationUpdate
                // tick reads the stale idle value out of twin+0x58 and reapplies it — and that
                // reassertion succeeds (unlike a direct walk/run write) because
                // CSWCObject::SetAnimation gates on AnimationLooping(id), and idle/pause rows
                // ARE Looping=1 (animations.2da row 6 "pause1": looping=1) while walkSS/runSS are
                // not (round 62's own finding) — the SAME structural rejection that permanently
                // blocks a direct walk/run write through this path is, in reverse, exactly what
                // lets a STALE idle value win the reassertion every time.
                //
                // The fix: keep twin+0x58 in sync with our own intended gait `want` every step,
                // the same "every step, not gated" discipline TwinPositionSyncSlot already needed
                // (see its own doc). Once twin+0x58 holds 0x2712/0x2714 instead of stale idle,
                // AnimationUpdate's reassertion call becomes `SetAnimation(twin, walkSS/runSS,
                // ...)` — which AnimationLooping structurally rejects, so the call bails WITHOUT
                // touching twin+0x48 (round 61 §8.1's own disassembly: "false -> bail, twin+0x48/
                // +0x58 untouched"). The re-asserter degrades from "guaranteed revert to idle"
                // to "guaranteed no-op" for exactly the two ids this bug is about — a plain,
                // verified-offset memory write, no new engine call, no risk to any other
                // consumer of this field (round 65 xref of GetLoopingAnimation's 8 call sites:
                // the other 7 are one-shot appearance/equip/network-update paths, not a per-frame
                // decider, and this is the SAME field DriveBody already relies on AnimationLooping
                // permanently rejecting for these two ids — see TwinAnimationSyncSlot's doc).
                *(ushort*)(twin + TwinLoopingAnimation) = (ushort)want;

                // ROUND 67 FIX (M1, docs/research/phantom-locomotion.md §13): only invoke
                // ClientServerSyncHandler_Position (TwinPositionSyncSlot, +0x12C) on a gait
                // CHANGE, not every drive step. Full disassembly of the call chain this round
                // (CSWCAnimBase::SetAnimationInternal, 0x0069E8D0, the function +0x12C's walk/
                // run branch always ends at — §13.2) found NO "already playing this id, skip"
                // guard anywhere in it: the generic path (0x0069E987, where every gait id
                // lands, per round 63 §10.2) unconditionally resolves the animation's name and
                // makes the "play" virtual call (`nameObj_vtable+0x18`) EVERY time it is
                // reached — regardless of whether the requested id matches what is already
                // playing. Round 61 §8.5's own comment already noted "its interpolation-step
                // and rate side effects are overwritten by this same function's unconditional
                // position and rate writes before the next frame ever reads them" — i.e.
                // nothing from +0x12C survives to matter except the play call itself. Calling
                // it every drive step (60 Hz, PhantomController.StepIntervalSeconds) therefore
                // restarted the walk/run cycle from frame 0 up to 60 times a second — a
                // structural stutter, not a rare glitch: the exact "you can see the run
                // animation initiated but it just never looks correct" the round 67 brief
                // reports, coexisting with round 65's 0% idle-while-moving measurement because
                // that metric only checks WHICH id is showing, never whether it is playing
                // smoothly.
                //
                // This call is safe to gate on the edge now, in a way it was NOT in round 61
                // (§8.5: an edge-triggered first attempt measured WORSE render reliability than
                // per-step, before round 65 existed): round 65's TwinLoopingAnimation write
                // (immediately above, still unconditional every step) is what keeps
                // AnimationUpdate's per-frame reassertion a no-op now — that no longer depends
                // on +0x12C re-firing. Position (TwinDesiredPosition, above) and the playback
                // rate (TwinAnimationSpeedFactor, below) are also both written unconditionally
                // every step already, independently of this call. So +0x12C's only remaining
                // job is the one-time "start playing this cycle" trigger a genuine gait
                // transition needs — exactly what GaitChanged already tracks for the +0x134
                // skip/diagnostics below, so the two now share one evaluation instead of the
                // stateful GaitChanged being called twice.
                bool gaitChanged = GaitChanged(creature, want);

                if (gaitChanged)
                {
                    var syncPosition =
                        (delegate* unmanaged[Thiscall]<nint, Vec3*, uint, float, void>)(*(nint*)(*(nint*)twin + TwinPositionSyncSlot));

                    // ROUND 62 DIAGNOSTIC (capped): does THIS call's own write to twin+0x48 stick
                    // for even one instant, or does it never take effect at all? Distinguishes
                    // "the play step never fires" from "it fires and something else reverts it
                    // before the next sample" — see phantom-locomotion.md §9.3. Only reachable on
                    // a gait edge now (round 67), so the cap fills far more slowly than before.
                    bool diag = moving && _syncPositionDiagLogged < 60;
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
                    // ROUND 63 EXPERIMENT E1 (docs/research/phantom-locomotion.md §10): skip
                    // this call entirely for Walk/Run. Round 61 §8.1 already proved it never
                    // PLAYS a gait id (AnimationLooping structurally rejects 0x2712/0x2714 for
                    // every creature — walking/running are their own animations.2da category,
                    // so CSWCObject::SetAnimation's own AnimationLooping gate always bails
                    // before touching twin+0x48/+0x58 for these two ids specifically) — this
                    // is a provable dead call for Walk/Run, not a guess. The E1 hypothesis was
                    // that it was worse than dead — that re-touching twin+0x58's cache on the
                    // SAME edge frame ClientServerSyncHandler_Position (+0x12C, below) seeds it
                    // stomps that seed before the animbase's per-frame update ever applies it.
                    // Live-measured this round: skipping it changed nothing (96% idle-while-
                    // moving, statistically the same as the pre-E1 baseline) — it is not the
                    // cause, but the call is still proven dead weight for these two ids, so the
                    // skip stays as a no-behaviour-change cleanup. Idle and DriveEnemy's
                    // owner-animation branch (which can pass real combat/overlay ids, not just
                    // the three gait ids) still need this
                    // entry — see PlayOneShotAnimation/MirrorAnimation, which route through it
                    // for one-shots — so the skip is scoped to exactly Walk/Run.
                    if (want != GaitRule.AnimWalk && want != GaitRule.AnimRun)
                    {
                        var syncAnimation =
                            (delegate* unmanaged[Thiscall]<nint, uint, float, void>)(*(nint*)(*(nint*)twin + TwinAnimationSyncSlot));
                        syncAnimation(twin, (uint)want, *(float*)(creature + ObjAnimationBudget));
                    }

                    // ⚠ ROUND 60 DIAGNOSTIC (docs/research/phantom-locomotion.md, live pass
                    // 2026-08-22): +0x134 is confirmed address-correct (live vtable read
                    // matches docs exactly) and confirmed NOT to stick — 132/133 samples at
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
                    if (_gaitClassLogged < 30)
                    {
                        _gaitClassLogged++;
                        int loop = AnimationLoopingFn(twin, (ushort)want);
                        int fireForget = AnimationFireAndForgetFn(twin, (ushort)want);
                        int overlay = AnimationOverlayFn(twin, (ushort)want);

                        // ROUND 61: AnimationLooping/FireAndForget/Overlay (above) do NOT gate
                        // this call — disassembly of ClientServerSyncHandler_Animation
                        // (0x0060C100) and the plain setter it falls through to
                        // (CSWCObject::SetAnimation, 0x0063E930) shows the real gate is
                        // CSWCAnimBase::AnimationExists (animbase vtable +0x50, 0x0069D910),
                        // which refuses everything while the animbase's own Gob* (+0xB8) is
                        // null. Probe both directly. Also probe AnimationWalking/Running —
                        // the predicates ClientServerSyncHandler_Position (the round-61 +0x12C
                        // fix call, above) actually branches on — to see whether that call is
                        // even reaching its animbase-vtable+0x54 "play" step for this creature.
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

                    // ROUND 64 X1 (docs/research/phantom-locomotion.md §11): the actual
                    // resolved NAME §10.3 predicted GetAnimationName would substitute idle
                    // for, ground-truthed instead of inferred from the classifier booleans the
                    // block above already logs. Capped at 20 PER CREATURE (not global) —
                    // deliberately its OWN cap (GaitNameLogged), independent of
                    // _gaitClassLogged: that counter is process-lifetime and a long test
                    // session exhausts it long before this diagnostic was added, which would
                    // silently dead-code this one too if it shared the same gate.
                    nint animBase = *(nint*)(twin + TwinAnimBase);
                    int gaitSlot = GaitSlotOf(creature);
                    if (animBase != 0 && gaitSlot >= 0 && GaitNameLogged[gaitSlot] < 20)
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
        if (moving && nowMs - _gaitLoggedAtMs >= GaitLogIntervalMs)
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
                // ROUND 67: TwinPositionSyncSlot (+0x12C) invocations across every driven body
                // in the last interval — the direct call-rate ground truth for the M1 fix (see
                // docs/research/phantom-locomotion.md §13). Pre-fix this tracked the drive-step
                // rate (up to ~60/s per body); post-fix it should sit near 0 during sustained
                // straight-line movement.
                + $"sync12c-calls {syncCalls} in {intervalMs}ms)");
        }
    }

    /// <summary>When the gait diagnostic last spoke, and how often it may. Seconds, because
    /// this is a "what does the gait look like" sample, not a trace.</summary>
    private static long _gaitLoggedAtMs;

    private const long GaitLogIntervalMs = 5000;

    /// <summary>Caps the round-60 "gait-class" one-time diagnostic (see DriveBody) so it
    /// prints a handful of samples across process lifetime, not once per gait change.</summary>
    private static int _gaitClassLogged;

    /// <summary>Caps the round-62 "sync12c" before/after diagnostic (see DriveBody).</summary>
    private static int _syncPositionDiagLogged;

    /// <summary>ROUND 67 (docs/research/phantom-locomotion.md §13): counts every actual
    /// invocation of TwinPositionSyncSlot (+0x12C), across every driven body, since the last
    /// time the periodic gait log line reset it. Direct, ground-truth evidence of the fix: a
    /// pre-fix trace shows this near the driven-body count times the ~60 Hz step rate over the
    /// log interval (every step re-triggers the play call); post-fix it should sit near zero
    /// during sustained straight-line movement and only spike on an actual walk/idle/run
    /// transition.</summary>
    private static int _syncPositionCallCount;

    /// <summary>Last gait asserted per driven creature, so the twin call fires on change
    /// only. Fixed slots rather than a dictionary: at most 8 phantoms plus 32 mirrored
    /// enemies exist, and this runs on the game's frame hook where allocation is unwelcome.</summary>
    private static readonly nint[] GaitCreature = new nint[48];
    private static readonly int[] GaitLast = new int[48];

    /// <summary>ROUND 64 X1: per-creature cap for the "gait-name" diagnostic (see DriveBody) —
    /// parallels <see cref="GaitCreature"/> by slot index, so each driven body gets its own
    /// first-20 budget instead of sharing one global counter.</summary>
    private static readonly int[] GaitNameLogged = new int[48];

}
