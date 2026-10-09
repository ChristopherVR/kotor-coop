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

    /// <summary>Every body bag (a dead creature's loot) in the loaded world, read fresh from the
    /// engine's object table, with its items' blueprint names. Body bags are created separately
    /// on each instance, so their ids never match; CorpseLootPump pairs them by position.</summary>
    List<BodyBagState> ReadBodyBags() => [];
}

/// <summary>A body bag as <see cref="IContainerLootBridge.ReadBodyBags"/> read it.</summary>
public sealed record BodyBagState(uint Id, float X, float Y, IReadOnlyList<BodyBagItem> Items);

/// <summary>One item in a body bag: its id here, its blueprint and its stack (credits are one
/// item whose stack is the amount).</summary>
public readonly record struct BodyBagItem(uint ItemId, string Resref, ushort Stack);
