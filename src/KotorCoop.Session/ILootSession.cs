// Shared containers and materialised-enemy death loot.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface ILootSession
{
    /// <summary>Publishes the settled local module to the loot channels. Empty or transitional
    /// names are ignored by the implementations so old object ids cannot be reused early.</summary>
    void SetLootModule(string module) { }

    /// <summary>
    /// Announces that THIS peer looted <paramref name="itemId"/> out of container
    /// <paramref name="containerId"/>, delivered reliably to every OTHER peer so the item
    /// vanishes from their copy of the container too. Symmetric: a client calls this exactly
    /// like the host does. <c>CoopHost</c> broadcasts directly; <c>CoopClient</c> sends to the
    /// host, which relays to every other client (including back down to the reporting client
    /// itself, harmlessly — see <see cref="ContainerLootRemovals"/>).
    /// </summary>
    void PublishContainerLoot(uint containerId, uint itemId) { }

    /// <summary>
    /// The (container, item) removals known here — this peer's own plus every other peer's,
    /// merged — which the agent re-declares to the game each tick so every side's copy of every
    /// shared container converges. Symmetric: non-empty on both host and client. A removal
    /// already applied locally is never re-reported by <see cref="PublishContainerLoot"/>
    /// (the underlying store recognises it as already known and swallows the echo), which is
    /// what keeps a peer's own removal from bouncing back to it as a "new" one.
    /// </summary>
    IReadOnlyList<ContainerLootRemoval> ContainerLootRemovals => [];

    /// <summary>Full current-module history for the acknowledged game mailbox. Unlike the
    /// bounded compatibility view, this can replay pages older than the last 512 arrivals.</summary>
    IReadOnlyList<ContainerLootRemoval> ContainerLootHistory => ContainerLootRemovals;

    /// <summary>
    /// Host only: announces that <paramref name="ownerEnemyId"/> — one of the host's own
    /// enemies — just died holding <paramref name="items"/>, delivered reliably to every client
    /// so a mirroring peer's materialised stand-in (if it has one for this id — see
    /// docs/research/enemy-materialisation.md §6) can receive the same real loot. A client is a
    /// no-op: only the host's engine ever holds the real corpse to read this from.
    /// </summary>
    void PublishMaterialisedLoot(uint ownerEnemyId, IReadOnlyList<MaterialisedLootItem> items) { }

    /// <summary>
    /// Client only: every materialised-enemy death-loot packet received since the last drain,
    /// oldest first, for the agent to apply and clear. Empty on the host, which never receives
    /// this (it is the only sender). Matches <see cref="DrainTransitionMovies"/>'s shape.
    /// </summary>
    IReadOnlyList<MaterialisedLootPacket> DrainMaterialisedLoot() => [];

    /// <summary>Client only: confirms a materialised-loot packet after its owner mapping has
    /// been resolved and every item has been queued for the local give channel. Keeping this
    /// separate from receipt prevents a low-FPS mapping delay from ACKing and losing the loot.</summary>
    void AcknowledgeMaterialisedLoot(MaterialisedLootPacket packet) { }

    /// <summary>Returns module-tagged materialised loot to the inbox when the local stand-in
    /// table is still being published. Legacy untagged packets retain their one-shot behavior.</summary>
    void RequeueMaterialisedLoot(IReadOnlyList<MaterialisedLootPacket> packets) { }

    /// <summary>
    /// Client only: forgets every owner-enemy id already deduped by
    /// <see cref="DrainMaterialisedLoot"/>'s receive path so far. Owner enemy ids are
    /// reassigned per module load; without this, an id reused in the next module would be
    /// silently treated as "already seen" and its real loot would never reach the queue. The
    /// agent calls this exactly once, right after it notices the local module has changed (see
    /// docs/research/enemy-materialisation.md §6). A no-op on the host, which is never the
    /// receiving side of this channel.
    /// </summary>
    void ResetMaterialisedLootDedup() { }

    /// <summary>Shared corpse loot: this player's body-bag takes in <paramref name="module"/>.</summary>
    void PublishCorpseTakes(string module, IReadOnlyList<CorpseTakeEntry> entries) { }

    /// <summary>Shared corpse loot: every other player's body-bag takes in <paramref name="module"/>.</summary>
    IReadOnlyList<(uint Origin, IReadOnlyList<CorpseTakeEntry> Entries)> GetRemoteCorpseTakes(string module) => [];

    /// <summary>Host: every one of this game's body bags in <paramref name="module"/>, for joiners
    /// to fill theirs to match (shared corpse loot).</summary>
    void PublishHostBodyBags(string module, IReadOnlyList<(float X, float Y, IReadOnlyList<BodyBagItemEntry> Items)> bags) { }

    /// <summary>Client: the host's body bags in <paramref name="module"/> heard about recently.</summary>
    IReadOnlyList<HostBodyBagPacket> GetHostBodyBags(string module) => [];
}
