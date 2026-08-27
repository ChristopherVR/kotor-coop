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

    /// <summary>
    /// The blueprint resref of a live item, by object id — the same lookup
    /// <c>KotorGameBridge.TryGetItemResref</c> already offers the console's <c>item</c>/
    /// <c>giveloot</c> staging verbs, exposed here so a pump can resolve what a container/
    /// corpse-loot removal (an object id with no resref of its own) is ABOUT to add to the
    /// local PC's inventory, for <see cref="KotorCoop.Protocol.RecentlySyncedItems"/>. False
    /// (with an empty resref) is a safe default: a bridge that cannot answer simply never
    /// feeds the suppression ledger for that path, which only costs an occasional
    /// unsuppressed false-positive report, never a wrong write.
    /// </summary>
    bool TryGetItemResref(uint itemObjectId, out string resref)
    {
        resref = string.Empty;
        return false;
    }

    /// <summary>
    /// Drains the plugin's item-template-spawn ring (docs/research/item-identity.md §13) into
    /// whatever cache <see cref="TryGetItemResref"/> consults first — the (object id -&gt; real
    /// blueprint resref) fact for every item <c>CreateItemOnObject</c> actually creates,
    /// whether a vanilla script asked for it or a `giveloot`/`scriptgive`/`storeinsert`/
    /// `partypooladd` cheat did. Cheap when nothing is new (one dword read); a no-op default
    /// so a bridge with no such ring (a test fake, an older plugin) simply never populates the
    /// extra source and <see cref="TryGetItemResref"/> falls back exactly as it did before.
    /// </summary>
    void DrainItemTemplateSpawns()
    {
    }
}
