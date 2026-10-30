// Odyssey.Phantom.Gait.cs - gait/facing/animation helpers used while driving a phantom.
// Owns: per-creature gait-change memory and walk/run hysteresis (GaitChanged,
// AdvanceGaitHysteresis, ForgetGait); the twin/animbase vtable-slot and field offsets the gait
// write path depends on (TwinLoopingAnimation, TwinAnimationSyncSlot, TwinPositionSyncSlot,
// TwinAnimationSpeedFactor); and the walkmesh-aware step helpers (SetFacing, SetAnimation,
// StepToward) used outside the real-time phantom drive path. See DriveBody in
// Odyssey.Phantom.cs for where most of these offsets are actually consumed.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    private static bool GaitChanged(nint creature, int want)
    {
        int free = -1;
        for (int i = 0; i < GaitCreature.Length; i++)
        {
            if (GaitCreature[i] == creature)
            {
                if (!GaitRule.ShouldReissue(GaitLast[i], want))
                {
                    return false;
                }

                GaitLast[i] = want;
                return true;
            }

            if (GaitCreature[i] == 0 && free < 0)
            {
                free = i;
            }
        }

        int slot = free >= 0 ? free : 0;
        GaitCreature[slot] = creature;
        GaitLast[slot] = want;
        GaitNameLogged[slot] = 0;
        return true;
    }

    /// <summary>The slot <see cref="GaitCreature"/> is holding this creature in,
    /// or -1. Only meaningful right after <see cref="GaitChanged"/> returned true for the same
    /// creature (which guarantees a slot exists).</summary>
    private static int GaitSlotOf(nint creature)
    {
        for (int i = 0; i < GaitCreature.Length; i++)
        {
            if (GaitCreature[i] == creature)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Forget a creature's gait memory (call when a phantom is destroyed, so a
    /// recycled pointer does not inherit stale state).</summary>
    public static void ForgetGait(nint creature)
    {
        for (int i = 0; i < GaitCreature.Length; i++)
        {
            if (GaitCreature[i] == creature)
            {
                GaitCreature[i] = 0;
                GaitLast[i] = 0;
                GaitNameLogged[i] = 0;
            }
        }

        for (int i = 0; i < GaitHysteresisCreature.Length; i++)
        {
            if (GaitHysteresisCreature[i] == creature)
            {
                GaitHysteresisCreature[i] = 0;
                GaitBelowIdleAccum[i] = 0f;
                GaitHysteresisLastStepMs[i] = 0;
            }
        }
    }

    /// <summary>Per-driven-creature walk/run hysteresis memory, parallel to but separate from
    /// GaitCreature/GaitLast — those only populate once a client twin has been read inside
    /// DriveBody (see the ClientTwin gate above GaitChanged's call site), while this must exist
    /// from the very first drive step so the idle-dwell accumulator starts counting immediately
    /// rather than after the first twin-bearing frame. See docs/research/phantom-locomotion.md
    /// §14.</summary>
    private static readonly nint[] GaitHysteresisCreature = new nint[48];

    /// <summary>Seconds this creature's speed has been continuously under
    /// GaitRule.IdleSpeedThreshold — see GaitRule.AdvanceBelowIdle.</summary>
    private static readonly float[] GaitBelowIdleAccum = new float[48];

    /// <summary>Wall-clock time (Environment.TickCount64) of this creature's previous
    /// AdvanceGaitHysteresis call, 0 meaning "no previous step" (first-ever sample gets dt=0,
    /// never a spurious huge dt from an uninitialised slot).</summary>
    private static readonly long[] GaitHysteresisLastStepMs = new long[48];

    private static int GaitHysteresisSlotOf(nint creature)
    {
        int free = -1;
        for (int i = 0; i < GaitHysteresisCreature.Length; i++)
        {
            if (GaitHysteresisCreature[i] == creature)
            {
                return i;
            }

            if (GaitHysteresisCreature[i] == 0 && free < 0)
            {
                free = i;
            }
        }

        int slot = free >= 0 ? free : 0;
        GaitHysteresisCreature[slot] = creature;
        GaitBelowIdleAccum[slot] = 0f;
        GaitHysteresisLastStepMs[slot] = 0;
        return slot;
    }

    /// <summary>
    /// Reads and advances a driven creature's hysteresis memory for this step:
    /// whether it was previously classified as running — from GaitLast, which only changes on
    /// an actual reissue (the last gait genuinely asserted to the engine), not a value
    /// computed and then discarded on an early return — and the updated "seconds continuously
    /// under GaitRule.IdleSpeedThreshold" accumulator (GaitRule.AdvanceBelowIdle). A creature
    /// with no GaitLast slot yet (no client twin read for it this session) reads as
    /// wasRunning=false, the same "no history" default GaitRule.IsRun already documents.
    /// MAIN THREAD ONLY, same as DriveBody/DrivePhantom/DriveEnemy.
    /// </summary>
    private static (bool WasRunning, float BelowIdleSeconds) AdvanceGaitHysteresis(nint creature, float speed)
    {
        int gaitSlot = GaitSlotOf(creature);
        bool wasRunning = gaitSlot >= 0 && GaitRule.IsRunningAnimation(GaitLast[gaitSlot]);

        int slot = GaitHysteresisSlotOf(creature);
        long nowMs = Environment.TickCount64;
        long lastMs = GaitHysteresisLastStepMs[slot];
        float dt = lastMs == 0 ? 0f : (nowMs - lastMs) / 1000f;
        GaitHysteresisLastStepMs[slot] = nowMs;

        float accum = GaitRule.AdvanceBelowIdle(GaitBelowIdleAccum[slot], speed, dt);
        GaitBelowIdleAccum[slot] = accum;

        return (wasRunning, accum);
    }

    /// <summary>CSWCObject + this = the animation actually being rendered (u16).</summary>
    private const int TwinCurrentAnimation = 0x48;

    /// <summary>
    /// CSWCObject + this = <c>looping_animation</c> (u16), the symbol db's own field
    /// name for tools/kotor1_0_3.db's CSWCObject offset table. This is exactly what
    /// <c>CSWCObject::GetLoopingAnimation</c> (0x0063C100, verified live-disassembled:
    /// <c>mov ax,[ecx+0x58]; ret</c>, a one-line getter of this field and nothing else)
    /// returns, and it is what <c>CSWCCreature::AnimationUpdate</c>'s per-frame re-assertion
    /// reads and reapplies via <c>SetAnimation</c> (vtable +0x9C) whenever
    /// <see cref="TwinCurrentAnimation"/> is not already "stationary" — see the write site in
    /// <c>DriveBody</c> (just above <see cref="TwinPositionSyncSlot"/>'s call) for the full
    /// mechanism, and docs/research/phantom-locomotion.md §12.
    /// </summary>
    private const int TwinLoopingAnimation = 0x58;

    /// <summary>
    /// CSWCObject vtable +0x134 = ClientServerSyncHandler_Animation(anim, budget) — the entry
    /// the engine's server→client dirty-push calls. Routes looping animations to the setter
    /// that owns twin+0x48; calling the plain setter (+0x9C) instead does not stick.
    /// </summary>
    private const int TwinAnimationSyncSlot = 0x134;

    /// <summary>
    /// CSWCObject vtable +0x12C = ClientServerSyncHandler_Position(Vector*
    /// desiredPos, uint serverAnimId, float budget), ret 0xC — 0x00611F50, the entry the
    /// engine's server→client dirty-push calls for the POSITION bit (dirty bit 1), which
    /// DriveBody has always deliberately suppressed (see the comment on
    /// <see cref="BisectBitDrivePosition"/>'s call site below) because its OWN interpolation
    /// step and its <c>ComputeSpeedFactor</c> re-derivation return 0.0 for a waypoint-less
    /// phantom — the historical "flying" bug.
    ///
    /// That suppression also throws out its other job: static disassembly of 0x00611F50 shows
    /// it is the ONLY place in the engine that calls <c>CSWCObject::AnimationWalking</c>
    /// (0x0063C240) / <c>AnimationRunning</c> (0x0063C2A0) against the SERVER's animation id
    /// (the third arg) and, on a match, resolves and plays the walk/run animation BY NAME
    /// through the "Walking"/"Running" columns of animations.2da — a completely different
    /// mechanism from <see cref="TwinAnimationSyncSlot"/>'s id-based <c>AnimationLooping</c>
    /// gate, which structurally rejects gait ids for every creature (see
    /// <see cref="AnimationExistsFn"/>'s doc): <c>animations.2da</c> rows 338 "walkSS" and 345
    /// "runSS" (what <c>ServerToClientAnimation</c> resolves 0x2712/0x2714 to for weapon class
    /// 2, live-confirmed) both have <c>Looping = 0</c> — walking/running are their own 2DA
    /// category, never "looping". <c>CSWCCreature::AnimationUpdate</c> (0x0061A980) never
    /// selects a new gait itself; it only re-asserts whatever is already cached at twin+0x58
    /// each frame, so if nothing ever seeds that cache with the walk/run row, nothing ever will
    /// — exactly the "moves in sync but slides" symptom, on every driven body, always.
    ///
    /// Calling this handler ourselves — with our OWN position/id/budget, immediately followed
    /// by the SAME authoritative twin+0x1DC/+0x1E8/+0x1F8 overwrites <see cref="DriveBody"/>
    /// already performs every step — lets its internal AnimationWalking/Running dispatch run
    /// while the bad interpolation/rate side effects it also produces are overwritten before
    /// the next frame ever reads them. See docs/research/phantom-locomotion.md §8.
    /// </summary>
    private const int TwinPositionSyncSlot = 0x12C;

    /// <summary>
    /// The budget passed to <see cref="TwinPositionSyncSlot"/>'s call specifically —
    /// NOT <see cref="ObjAnimationBudget"/> (which reads ~1.0 live and permanently fails the
    /// run-branch's budget &lt; 0.5 gate at 0x0073E9AC, docs/research/phantom-locomotion.md
    /// §9.1/§9.2). 0f matches every other "this is a snap/continuous update, not a fresh
    /// blend" budget already used in this method (<see cref="TwinDesiredPositionBudget"/>).
    /// </summary>
    private const float TwinPositionSyncBudget = 0f;

    /// <summary>CSWSObject + this = float forwarded with the engine's animation sync.</summary>
    private const int ObjAnimationBudget = 0xD8;

    /// <summary>
    /// CSWCCreature + this = animation playback-rate scale. The engine computes it from the
    /// waypoint list (ComputeSpeedFactor) and gets 0.0 for a creature without one — frozen
    /// legs — so a phantom's is written by us every frame instead.
    /// </summary>
    private const int TwinAnimationSpeedFactor = 0x1F8;

    /// <summary>appearance.2da row 114 rundist / walkdist — world units per second at rate 1.0.</summary>
    private const float RunSpeedReference = 3.96f;
    private const float WalkSpeedReference = 1.6f;

    /// <summary>Faces a creature along a world-space angle (0 = +X, counter-clockwise).</summary>
    public static void SetFacing(nint creature, float angle)
    {
        Vec3 direction = new() { X = MathF.Cos(angle), Y = MathF.Sin(angle), Z = 0f };
        SetOrientationFn(creature, &direction);
    }

    /// <summary>Object id of the local player creature, or 0 before a world is loaded.</summary>
    public static uint GetPlayerObjectId()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint player = GetPlayerCreatureFn(server);
        return player == 0 ? 0 : *(uint*)(player + 0x04);
    }

    public static void SetAnimation(nint creature, int animation)
    {
        // Always dispatch through the vtable. The only direct call to the implementation in
        // the whole image is from the constructor.
        nint vtable = *(nint*)creature;
        var fn = (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)(vtable + VtableSetAnimation));
        fn(creature, animation);
    }

    // Fire-and-forget animations (draw weapon, attack swings, flourishes — the "x" key) live
    // on the client twin's queue, not the server +0xD4 field, so SetAnimation cannot replay
    // them reliably. CSWCCreature::AddFireForgetAnimation is the engine's own replication call
    // for these (docs/research/animation-sync.md): thiscall(this=twin, uint anim, void* opts).
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint, void> AddFireForgetAnimationFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint, void>)0x006183D0;

    // Removed on purpose, and worth not reintroducing:
    //
    //   GetTwinCurrentAnimation — read `*(uint*)(twin + 0x48)`. That field is a USHORT and
    //     +0x4A is a separate ushort (the queued animation), so the read returned
    //     queued<<16 | played. Use a 16-bit read if this is ever needed again.
    //   PlayFireForget — queued any id on the twin with no classification. A looping or
    //     overlay id sent down the fire-and-forget path parks with no expiry and wedges the
    //     body. PlayOneShotAnimation is the guarded replacement and the only caller's route.

    /// <summary>
    /// Advances a creature toward a target using the engine's own walk step, so the move is
    /// ground-snapped, walkmesh-aware and animated rather than a teleport.
    ///
    /// The step function holds no goal of its own: it steers by the creature's orientation.
    /// That is why a destination that changes 20 times a second costs nothing here.
    /// </summary>
    public static bool StepToward(
        nint creature, Vec3 target, float maxDistance, out Vec3 newPosition, out StepDiagnostics diagnostics)
    {
        Vec3 current = GetPosition(creature);
        newPosition = current;
        diagnostics = default;

        float dx = target.X - current.X;
        float dy = target.Y - current.Y;
        float planar = MathF.Sqrt((dx * dx) + (dy * dy));

        if (planar < 0.05f)
        {
            // Arrived: stand still in the idle pose.
            *(int*)(creature + CreMoveState) = 1;
            SetAnimation(creature, AnimIdle);
            return false;
        }

        // Face the target. Orientation is a direction vector, not an angle.
        Vec3 direction = new() { X = dx / planar, Y = dy / planar, Z = 0f };
        SetOrientationFn(creature, &direction);

        // Walking state must be set before the animation, and both before the step, which
        // is the order the engine's own callers use.
        *(int*)(creature + CreRunFlag) = 0;
        *(int*)(creature + CreMoveState) = -1;
        SetAnimation(creature, AnimWalk);

        float distance = MathF.Min(maxDistance, planar);

        Vec3 stepped = current;
        Vec3 spare = default;
        int spareInt = 0;
        int walkResult = WalkStep(creature, distance, &stepped, &spare, &spareInt);

        // Commit through SetPosition rather than poking the field, so the position dirty
        // bit is raised and the engine's own client sync runs (which is what reselects the
        // walk animation on the render side).
        int setResult = SetPositionFn(creature, &stepped, 1, 0, 0);

        diagnostics = new StepDiagnostics(
            distance, walkResult, setResult, current, stepped, spare, spareInt, GetPosition(creature));

        newPosition = stepped;
        return true;
    }

}
