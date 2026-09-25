namespace KotorCoop.Protocol;

/// <summary>
/// When a session-commanded follow warp may start on this engine.
///
/// 2026-09-25 crash: the host walked out of the Enclave and straight onto the Ebon Hawk. The
/// joiner followed the first leg and, 0.8 s after its danm14aa world came up and before the
/// client half of that world was built ("phantom spawn deferred: the client half of this world
/// is not built yet"), was told to follow the second leg. The retail engine faulted inside
/// the second StartNewModule (access violation at 0x64C20D). Back-to-back module loads crash
/// the retail engine on their own (the warp-crash lesson), so the follower must never start a
/// load until the world it just entered has finished building and settled.
///
/// Ready = the local player has a rendered client body AND the world has been up for
/// <see cref="MinSettleMs"/>. <see cref="MaxWaitMs"/> is the ceiling, so a world whose body
/// never appears (a cutscene module, a menu-only area) cannot hold a follow forever.
/// </summary>
public static class FollowWarpSettleRule
{
    public const long MinSettleMs = 2500;
    public const long MaxWaitMs = 20000;

    /// <param name="hasRenderedBody">Whether the local player's client twin is built.</param>
    /// <param name="worldUpAtMs">Tick when the current world came up; 0 if unknown.</param>
    /// <param name="nowMs">Current tick.</param>
    public static bool MayStart(bool hasRenderedBody, long worldUpAtMs, long nowMs)
    {
        if (worldUpAtMs <= 0)
        {
            // No world-up recorded (first frame after attach): nothing to protect.
            return true;
        }

        long up = nowMs - worldUpAtMs;
        if (up >= MaxWaitMs)
        {
            return true;
        }

        return hasRenderedBody && up >= MinSettleMs;
    }
}
