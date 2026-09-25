using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

internal static unsafe partial class PluginHost
{
    /// <summary>
    /// Holds a session-commanded warp until the world this engine just entered has built its
    /// client half and settled (<see cref="FollowWarpSettleRule"/>). While held, the frame runs
    /// normally; the request stays pending and fires on the first ready frame.
    /// </summary>
    private static bool FollowWarpMayStart()
    {
        long now = Environment.TickCount64;
        nint player = Odyssey.GetPlayerCreaturePointer();
        bool hasBody = player != 0 && Odyssey.HasRenderedBody(player);
        if (FollowWarpSettleRule.MayStart(hasBody, _worldUpAtMs, now))
        {
            return true;
        }

        if (_followWarpHeldEpoch != _epoch)
        {
            _followWarpHeldEpoch = _epoch;
            Log($"travel: holding the follow warp - this world came up {now - _worldUpAtMs} ms ago "
                + $"(rendered body {hasBody}); a second module load this soon crashes the engine");
        }

        return false;
    }
}
