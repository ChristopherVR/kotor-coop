// Ground-item sync: declaring the desired item set and collecting vanished items.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IGroundItemBridge
{
    /// <summary>
    /// False when this bridge has no way to materialise ground items in the game. The
    /// defaults make item sync purely opt-in so profile bridges need no changes.
    /// </summary>
    bool SupportsGroundItems => false;

    /// <summary>
    /// Declares the desired set of items on the ground; the bridge reconciles the game
    /// toward it (spawn what is missing, despawn what is no longer wanted).
    /// </summary>
    bool TryWriteGroundItems(IReadOnlyList<GroundItem> items) => false;

    /// <summary>
    /// Item ids the game reports as locally vanished (picked up by this player) since the
    /// last call. The caller announces these to the session.
    /// </summary>
    List<uint> CollectVanishedItems() => [];
}
