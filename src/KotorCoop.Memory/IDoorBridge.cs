// Door sync: collecting and applying door open/locked state changes.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IDoorBridge
{
    /// <summary>False when this bridge cannot see or drive door state.</summary>
    bool SupportsDoorSync => false;

    /// <summary>
    /// Door states that advanced locally since the last call (opened, unlocked,
    /// destroyed), as door-typed world flags ready for the reliable flag channel.
    /// </summary>
    List<KeyValuePair<ulong, sbyte>> CollectChangedDoorState() => [];

    /// <summary>
    /// Publishes the session's door truth for the local module into the game. The bridge
    /// filters: flags for doors in other modules are held back until that module loads.
    /// </summary>
    bool TryWriteDoorStates(IReadOnlyList<KeyValuePair<ulong, sbyte>> flags) => false;
}
