// Placeable sync: collecting and applying placeable open/locked/trapped state changes.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IPlaceableBridge
{
    /// <summary>False when this bridge cannot see or drive placeable (chest/corpse) state.</summary>
    bool SupportsPlaceableSync => false;

    /// <summary>
    /// Placeable states that advanced locally since the last call (opened, unlocked), as
    /// placeable-typed world flags ready for the reliable flag channel.
    /// </summary>
    List<KeyValuePair<ulong, sbyte>> CollectChangedPlaceableState() => [];

    /// <summary>Publishes the session's placeable truth for the local module into the game.</summary>
    bool TryWritePlaceableStates(IReadOnlyList<KeyValuePair<ulong, sbyte>> flags) => false;
}
