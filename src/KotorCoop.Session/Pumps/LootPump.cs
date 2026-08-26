using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The receiving half of both "an item appeared for me" channels — a materialised enemy's real
/// corpse loot (docs/research/enemy-materialisation.md §6) and a dialogue reward grant
/// (docs/research/dialogue-consequences.md §3) — plus shared-container loot dedup so a chest
/// or corpse does not duplicate its contents between peers.
/// </summary>
internal sealed class LootPump(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    /// <summary>
    /// One item still owed to a target, queued for the automatic give-loot path to submit
    /// through the same arbitrated cheat channel a human's <c>giveloot</c> console command
    /// uses — see <see cref="TryDequeuePendingLoot"/>.
    ///
    /// <c>TargetObjectId</c> carries TWO different id spaces depending on where the give came
    /// from, and <c>CheatController.GiveLoot</c> is what tells them apart: a materialised-loot
    /// give carries the OWNER's enemy object id, translated locally via
    /// <c>MaterialisedEnemies.ToLocal</c>; a dialogue-grant give carries the well-known
    /// local-player sentinel <see cref="AgentWellKnownIds.LocalPlayerObjectId"/>, which needs
    /// no translation because "the local player" means the same thing on every instance.
    /// </summary>
    /// <summary>
    /// A materialised enemy's real corpse can carry several items, and an ambush can kill
    /// several enemies in one wave — bounded generously past any single real encounter, so a
    /// burst is queued rather than dropped, but a queue that never stops growing (a stuck
    /// drain, a malformed peer) must not grow unbounded.
    /// </summary>
    private const int MaxPendingLoot = 256;

    private readonly Queue<CoopAgent.PendingMaterialisedLootGive> _pendingLoot = new();

    /// <summary>The local module <see cref="PumpMaterialisedLoot"/> last issued
    /// <see cref="ICoopSession.ResetMaterialisedLootDedup"/> for. Owner enemy ids are
    /// reassigned per module load — the client-side dedup set that keys on them
    /// (<c>CoopClient._materialisedLootSeen</c>) would otherwise remember an id from the last
    /// module forever and silently swallow a same-numbered enemy's real loot here.</summary>
    private string _materialisedLootDedupModule = string.Empty;

    /// <summary>
    /// Takes the materialised-loot packets a connected host has published since the last
    /// pump and queues one give-request per item. This is deliberately the ONLY writer of
    /// <see cref="_pendingLoot"/> — actually applying an item to the game engine needs the
    /// same single-slot cheat channel a human typing <c>giveloot</c> at the console uses
    /// (docs/research/enemy-materialisation.md §6 item 3), and that channel has exactly one
    /// request in flight at a time. Rather than build a second, parallel engine-write path
    /// that could race the first, this queue is drained from OUTSIDE the session/agent pump
    /// entirely — by whatever already arbitrates that single channel (the console command
    /// dispatcher's own pending-cheat-serial check) — one request per its own tick, so an
    /// automatic give can never collide with, or be silently overwritten by, a human's.
    ///
    /// Also resets the client's dedup set on a module change. Gated on a clean, non-fallback
    /// module read so a brief transition-window fallback label never fires a spurious reset;
    /// a no-op on the host, which never receives this channel
    /// (<see cref="ICoopSession.ResetMaterialisedLootDedup"/> defaults to nothing there).
    /// </summary>
    public void PumpMaterialisedLoot(string module)
    {
        if (module.Length > 0 && !ModuleNameGuard.IsFallbackModuleName(module)
            && !string.Equals(module, _materialisedLootDedupModule, StringComparison.OrdinalIgnoreCase))
        {
            _materialisedLootDedupModule = module;
            session.ResetMaterialisedLootDedup();
        }

        foreach (MaterialisedLootPacket packet in session.DrainMaterialisedLoot())
        {
            log($"materialised loot: received {packet.Items.Count} item(s) for owner 0x{packet.OwnerEnemyId:X}, queueing");

            foreach (MaterialisedLootItem item in packet.Items)
            {
                if (_pendingLoot.Count >= MaxPendingLoot)
                {
                    log($"dropping materialised loot '{item.Resref}' for owner 0x{packet.OwnerEnemyId:X}: "
                        + $"already holding {MaxPendingLoot} queued gives, the drain has fallen behind");
                    return;
                }

                _pendingLoot.Enqueue(new CoopAgent.PendingMaterialisedLootGive(packet.OwnerEnemyId, item));
            }
        }

        // The receiving half of the dialogue item-grant channel: a peer's conversation put
        // something in THEIR inventory, relayed here, still owed a give to ours. Rides the
        // SAME pending-give queue and the SAME single-slot cheat channel as materialised loot
        // — see PendingMaterialisedLootGive's own doc for the two id spaces it carries.
        foreach (DialogueGrantPacket grant in session.DrainDialogueGrants())
        {
            log($"dialogue grant: received {grant.Items.Count} item(s) from peer {grant.OriginPeerId} "
                + $"('{grant.PartnerTag}'), queueing");

            foreach (MaterialisedLootItem item in grant.Items)
            {
                if (_pendingLoot.Count >= MaxPendingLoot)
                {
                    log($"dropping dialogue grant '{item.Resref}' from peer {grant.OriginPeerId}: "
                        + $"already holding {MaxPendingLoot} queued gives, the drain has fallen behind");
                    return;
                }

                _pendingLoot.Enqueue(new CoopAgent.PendingMaterialisedLootGive(
                    AgentWellKnownIds.LocalPlayerObjectId, item));
            }
        }
    }

    /// <summary>
    /// Keeps shared containers (chests, corpses) from duplicating loot. SYMMETRIC on both
    /// sides — a client taking an item from a chest is exactly as real as the host taking one,
    /// so both detect their own removals and publish them, and both re-declare the FULL merged
    /// set (own plus every peer's) to their own game.
    ///
    /// Nothing here can echo. <c>ContainerLootStore.Add</c> (both host and client) reports a
    /// pair already in its set as not-new, so: (1) applying a peer's removal via
    /// <see cref="IGameBridge.TryWriteContainerLoot"/> deletes the item from this instance's
    /// container, which this instance's OWN <see cref="IGameBridge.CollectContainerLootRemovals"/>
    /// scan would otherwise rediscover as a fresh local removal a moment later — but the store
    /// already holds the pair, so the re-publish is silently swallowed; and (2) a client's own
    /// removal that the host relays back down is recognised the same way and never
    /// re-broadcast. Shares the Doors capability gate because it rests on the same
    /// object-id-across-the-same-save assumption doors do.
    /// </summary>
    public void PumpContainerLoot(SessionCapabilities caps)
    {
        if (!bridge.SupportsContainerLoot || (caps & SessionCapabilities.Doors) == 0)
        {
            return;
        }

        // Detect whatever THIS instance's player removed locally and announce it — the host's
        // own scan and a client's own scan are treated identically now.
        foreach (ContainerLootRemoval removal in bridge.CollectContainerLootRemovals())
        {
            session.PublishContainerLoot(removal.ContainerId, removal.ItemId);
        }

        // Re-declare the merged set — everyone's removals, this instance's own plus every peer
        // reported through the session — so each side's copy of every shared container
        // converges. Idempotent: an already-removed item resolves to a no-op.
        bridge.TryWriteContainerLoot(session.ContainerLootRemovals);
    }

    /// <summary>
    /// Called by whatever already arbitrates the single-slot staging-cheat channel (the
    /// command dispatcher's pending-cheat-serial check), at most once per ITS tick, so an
    /// automatic give-loot request is submitted through the exact same call path a human's
    /// typed <c>giveloot</c> takes and can never be filed alongside one in flight. Returns
    /// false with nothing queued to give.
    /// </summary>
    public bool TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive next)
    {
        return _pendingLoot.TryDequeue(out next);
    }
}
