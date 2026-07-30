using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Executes the session's travel requests: when the agent asks this instance to follow a
/// peer into another module, the move goes through the engine's own move-to-module path —
/// the machinery the shipped `warp` cheat uses — so party collection, the save-out of the
/// old area, the loading screen and entry placement all happen exactly as a normal
/// transition would.
///
/// The dangerous part is not the warp, it is the exit: StoreCurrentModule serialises the
/// outgoing area and skips only party members, so anything the plugin spawned (phantom
/// bodies, replicated ground items) would be baked into the stored module as permanent
/// residents and shift the object-id counter for the rest of the playthrough. PluginHost
/// therefore flushes every spawned body BEFORE calling <see cref="Execute"/>.
/// </summary>
internal sealed unsafe class TravelController(SharedBlockView block)
{
    /// <summary>A request is pending while the agent's serial is ahead of ours.</summary>
    public bool HasPendingRequest => block.TravelSerial != block.TravelApplied;

    /// <summary>
    /// Nothing to forget on a module change — deliberately. A request that raced a
    /// transition stays pending (its serial is still ahead), and the next stable frame
    /// either executes it or acknowledges it as already satisfied.
    /// </summary>
    public void Invalidate()
    {
    }

    /// <summary>
    /// Applies the pending request, once. Called by PluginHost on the game's thread after
    /// every plugin-spawned object has been flushed.
    /// </summary>
    public void Execute()
    {
        uint serial = block.TravelSerial;
        if (serial == block.TravelApplied)
        {
            return;
        }

        byte* module = block.TravelModule;
        int length = 0;
        while (length < SharedBlock.TravelNameLength - 1 && module[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            block.TravelStatus = -1;
            block.TravelApplied = serial;
            return;
        }

        // Already there: acknowledge without touching the engine. Executing anyway would
        // reload the module everyone is standing in.
        if (Odyssey.ModuleNameEquals(module, length))
        {
            block.TravelStatus = 0;
            block.TravelApplied = serial;
            return;
        }

        byte* waypoint = block.TravelWaypoint;
        int waypointLength = 0;
        while (waypointLength < SharedBlock.TravelNameLength - 1 && waypoint[waypointLength] != 0)
        {
            waypointLength++;
        }

        bool ok = Odyssey.TravelToModule(module, length, waypoint, waypointLength, out int failure);
        block.TravelStatus = ok ? 0 : failure;
        block.TravelApplied = serial;

        string name = Encoding.ASCII.GetString(module, length);
        PluginHost.Log(ok
            ? $"travel: moving to module '{name}' on the session's authority"
            : $"travel: move to '{name}' refused (failure {failure})");
    }
}
