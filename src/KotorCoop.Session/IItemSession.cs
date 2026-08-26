// Ground items shared across the session: drops and pickups.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IItemSession
{
    /// <summary>Items currently lying on the ground, shared across the session.</summary>
    GroundItemStore Items { get; }

    /// <summary>
    /// Announces an item dropped by the local player. Returns the minted item id, or 0
    /// when the drop was refused (not connected yet, or the ground is full).
    ///
    /// <paramref name="identity"/> is the per-instance state read off the live item — stack
    /// size, charges, upgrades, identified — so the far side re-creates THIS item rather
    /// than a pristine one off the blueprint. Defaulting it to
    /// <see cref="ItemIdentity.Unknown"/> is deliberate: a caller that only knows a
    /// blueprint name must be able to say so, because inventing a stock identity would look
    /// exactly like a genuine reading of a stock item.
    /// </summary>
    uint DropItem(
        string resref, string module, Vector3f position, ItemIdentity identity = default);

    /// <summary>Announces that a ground item was picked up (or otherwise vanished) here.</summary>
    void RemoveItem(uint itemId);
}
