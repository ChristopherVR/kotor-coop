using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// Per-object local-variable replication (coop.ini <c>local_var_sync</c>,
/// docs/research/quest-sync.md §8.3). Publishes the allowlisted writes the plugin captured as
/// <see cref="LocalVarSyncRule"/> keys on the reliable flag channel, and hands the session's
/// values for the current module back to the plugin.
///
/// No new transport: the key is opaque to <c>EventFlagStore</c>, so ordering is the store's own
/// per-key Lamport clock (last writer wins; a genuine tie goes to the host, peer 1) and a late
/// joiner or rejoiner receives every entry in the ordinary flag snapshot. A key names its module
/// by hash and the plugin applies only entries of the module it is standing in, stamped with its
/// current world generation, so a write from an earlier world never lands on a reused id.
/// </summary>
internal sealed class LocalVarPump(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    private const int MaxLoggedChanges = 40;
    private int _loggedChanges;
    private uint _loggedLost;
    private int _loggedApplyCount;

    public void Pump(SessionCapabilities caps)
    {
        // Same capability gate as doors, placeables and mines: it rests on the same
        // "GIT object ids agree across one save" assumption.
        if (!bridge.SupportsLocalVarSync || (caps & SessionCapabilities.Doors) == 0 || bridge.IsWorldBetweenLives)
        {
            return;
        }

        foreach (KeyValuePair<ulong, sbyte> change in bridge.CollectLocalVarCaptures())
        {
            session.SetFlag(change.Key, change.Value);
            if (_loggedChanges < MaxLoggedChanges
                && LocalVarSyncRule.TryParseKey(change.Key, out uint module, out ushort objectId, out LocalVarKind kind, out byte slot))
            {
                _loggedChanges++;
                log($"localvar: published module 0x{module:X8} object 0x{objectId:X} "
                    + $"{(kind == LocalVarKind.Boolean ? "b" : "n")}{slot} = {change.Value} (key 0x{change.Key:X16})");
            }
        }

        if (bridge.LocalVarCapturesLost != _loggedLost)
        {
            log($"localvar: {bridge.LocalVarCapturesLost - _loggedLost} capture(s) overwritten before the agent read them");
            _loggedLost = bridge.LocalVarCapturesLost;
        }

        if (bridge.TryWriteLocalVarStates(session.Flags.Snapshot()) && bridge.LocalVarApplyCount != _loggedApplyCount)
        {
            _loggedApplyCount = bridge.LocalVarApplyCount;
            log($"localvar: handing the plugin {_loggedApplyCount} session value(s) for this module");
        }
    }
}
