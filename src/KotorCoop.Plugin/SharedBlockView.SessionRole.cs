using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

internal sealed unsafe partial class SharedBlockView
{
    /// <summary>Agent-written role proof. A false return means there is no current session
    /// role publication; callers must not infer host from that state.</summary>
    public bool TryGetLocalSessionRole(out bool isHost)
    {
        return LocalSessionRoleRule.TryDecode(
            *U32(SharedBlock.OffsetLocalSessionRole),
            out isHost);
    }
}
