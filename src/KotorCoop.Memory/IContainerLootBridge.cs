// Container-loot sync: collecting and applying item removals from shared containers.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IContainerLootBridge
{
    /// <summary>False when this bridge cannot see or drive container (chest/corpse) contents.</summary>
    bool SupportsContainerLoot => false;

    /// <summary>
    /// Symmetric on both host and client: items that left a shared container on THIS instance
    /// since the last call, as (container, item) object-id pairs. The caller announces these so
    /// the same item is removed from every other instance's copy of the container and cannot be
    /// taken twice.
    /// </summary>
    List<ContainerLootRemoval> CollectContainerLootRemovals() => [];

    /// <summary>
    /// Symmetric on both host and client: declares the merged container-loot removals the
    /// session knows about (this instance's own plus every peer's), which the bridge applies to
    /// the game's own containers (deleting each looted item). Idempotent.
    /// </summary>
    bool TryWriteContainerLoot(IReadOnlyList<ContainerLootRemoval> removals) => false;
}
