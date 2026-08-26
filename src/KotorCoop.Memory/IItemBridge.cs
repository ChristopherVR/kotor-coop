// Item identity: reading possessed items by owner, with full per-instance identity.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IItemBridge
{
    /// <summary>
    /// Every item any of <paramref name="possessorIds"/> is carrying, by possessor plus resref
    /// plus full per-instance identity — the same sweep the <c>items</c>/<c>setitem</c> staging
    /// cheats use (<see cref="KotorGameBridge.ListItems"/>). Used at the moment one or more
    /// owner-authoritative enemies die to capture their real corpses' actual loot for
    /// <see cref="ICoopSession.PublishMaterialisedLoot"/> — see
    /// docs/research/enemy-materialisation.md §6.
    ///
    /// ⚠ TAKES A SET, NOT ONE ID. <see cref="KotorGameBridge.ListItems"/>'s own doc warns it is
    /// a full world memory sweep, "not free" — "call it from a command surface, never from the
    /// pump." A death-detection loop that fires per-creature would run one full sweep PER DEAD
    /// ENEMY on the same tick; an ambush wave dying together would stall the pump repeatedly.
    /// One sweep, filtered to however many died this tick, costs the same as one.
    /// </summary>
    IReadOnlyList<(uint Possessor, string Resref, ItemIdentity Identity)> ReadPossessedItems(
        IReadOnlySet<uint> possessorIds) => [];
}
