using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session.Pumps;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — Container-loot removals and materialised (death-drop) loot: reliable, per-op
/// channels so a dropped datagram cannot leave a peer's copy of a chest or a corpse
/// out of sync with the host's. See ReliableLootSender / MaterialisedLootSender /
/// MaterialisedLootHistory for the retry and resync machinery this file drives.
/// </summary>
public sealed partial class CoopHost
{
    private readonly ReliableLootSender _lootSender = new();
    private readonly ContainerLootStore _lootStore = new();
    private readonly Lock _lootResyncGate = new();
    private readonly Dictionary<string, int> _lootResyncCursor = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The container-loot store, exposed the way <see cref="Flags"/>/<see cref="Items"/>
    /// already are, so a caller (a test, the resync tripwire) can read its Version/Digest
    /// without a separate accessor per field.</summary>
    public ContainerLootStore Loot => _lootStore;

    public void SetLootModule(string module)
    {
        if (module.Length == 0 || ModuleNameGuard.IsFallbackModuleName(module)
            || string.Equals(_lootStore.Module, module, StringComparison.OrdinalIgnoreCase))
            return;

        _lootStore.SetModule(module);
        // A packet captured during the previous module is retained under its own identity. The
        // newly active module must be re-offered once, without relabelling that old packet.
        ResyncContainerLoot(SessionClock.NowMs, module);
        ResyncMaterialisedLoot(SessionClock.NowMs);
        _lootSender.ResetBackoff();
    }

    /// <summary>
    /// Someone looted an item out of a shared container here; broadcast the removal reliably so
    /// it vanishes from every client's copy too. Enqueue-only — the tick loop, which owns the
    /// send buffer, does the actual broadcasting and retransmits.
    ///
    /// Gated on the store, which is what keeps a client's removal from bouncing: applying it
    /// here empties the container in the host's game, the host's own scanner then reports the
    /// same item as looted, and the store recognises it and stops it going back out.
    /// </summary>
    public void PublishContainerLoot(uint containerId, uint itemId)
    {
        if (!_lootStore.Add(new ContainerLootRemoval(containerId, itemId)))
        {
            return;
        }

        _lootSender.Enqueue(HostPeerId, containerId, itemId, SessionClock.NowMs, _lootStore.Module);
    }

    /// <summary>Every removal known here, host-discovered and client-reported alike, for the
    /// agent to re-declare to the game.</summary>
    public IReadOnlyList<ContainerLootRemoval> ContainerLootRemovals => _lootStore.Snapshot();
    public IReadOnlyList<ContainerLootRemoval> ContainerLootHistory =>
        _lootStore.HistorySnapshot();

    private readonly MaterialisedLootSender _materialisedLootSender = new();

    /// <summary>
    /// The permanent record <see cref="MaterialisedLootSender"/> deliberately does not keep —
    /// item 11(b), docs/research/enemy-materialisation.md §6: that sender forgets an op the
    /// moment every then-connected peer has acked it, so a peer that reconnects AFTER an op
    /// fully retired never receives it. This is <see cref="ContainerLootStore"/>'s pattern
    /// applied to a channel keyed by owner enemy id — see <see cref="ResyncMaterialisedLoot"/>.
    /// </summary>
    private readonly MaterialisedLootHistory _materialisedLootHistory = new();

    /// <summary>Ops still awaiting an ack from someone, for tests to observe retirement without
    /// depending on wall-clock timing.</summary>
    public int MaterialisedLootPendingCount => _materialisedLootSender.PendingCount;

    /// <summary>How many owner enemy ids the bounded resync history currently remembers.</summary>
    public int MaterialisedLootHistoryCount => _materialisedLootHistory.Count;

    /// <summary>
    /// One of our own enemies just died holding this real loot; broadcast it reliably so every
    /// mirroring peer's stand-in (if it has one — see
    /// docs/research/enemy-materialisation.md §6) can receive the same items. Enqueue-only —
    /// the tick loop, which owns the send buffer, does the actual broadcasting and retransmits.
    /// A duplicate call for an id already in flight is a no-op on the sender (dedup lives in
    /// <see cref="MaterialisedLootSender.Enqueue"/>), so a death-detection loop that fires
    /// twice for the same creature never double-sends — but it is always recorded into the
    /// resync history, so a peer that reconnects long after this op retired can still catch up.
    /// </summary>
    public void PublishMaterialisedLoot(uint ownerEnemyId, IReadOnlyList<MaterialisedLootItem> items)
    {
        _materialisedLootHistory.Record(_lootStore.Module, ownerEnemyId, items);
        _materialisedLootSender.Enqueue(
            HostPeerId, ownerEnemyId, items, SessionClock.NowMs, _lootStore.Module);
    }

    /// <summary>
    /// Retransmits container-loot removals to every client until each has acked. Reliable
    /// rather than snapshot-broadcast: a removal is a one-shot fact, not a level in a state
    /// that the next tick re-sends anyway, so it must survive a dropped packet. An op is
    /// retired once every currently-connected client has confirmed it.
    /// </summary>
    private void BroadcastContainerLoot(long now, List<PeerConnection> recipients)
    {
        List<uint> recipientIds = [.. recipients.Select(p => p.Id)];

        foreach (ContainerLootPacket op in _lootSender.CollectDue(recipientIds, now))
        {
            int length = PacketCodec.Write(_sendBuffer, op);
            if (length == 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    /// <summary>
    /// Retransmits captured materialised-enemy death-loot to every client until each has acked.
    /// Reliable rather than snapshot-broadcast, for the same reason as
    /// <see cref="BroadcastContainerLoot"/>: an enemy dies once, so a dropped packet must not
    /// silently mean a mirroring peer's stand-in never receives it.
    /// </summary>
    private void BroadcastMaterialisedLoot(long now, List<PeerConnection> recipients)
    {
        List<uint> recipientIds = [.. recipients.Select(p => p.Id)];

        foreach (MaterialisedLootPacket op in _materialisedLootSender.CollectDue(recipientIds, now))
        {
            int length = PacketCodec.Write(_sendBuffer, op);
            if (length == 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    /// <summary>
    /// A client emptied a container in its own copy of the world. Record it (so the item goes
    /// from the host's copy too) and relay it onward, because the client only ever talks to the
    /// host — a third player learns about it no other way.
    ///
    /// The relay goes out under the host's own peer id and a fresh sequence rather than the
    /// client's. Loot ops are retired per-recipient by sequence, and two peers number their ops
    /// from 1 independently, so forwarding a client's sequence would collide with the host's.
    /// Recipients de-duplicate on the (container, item) pair regardless of who announced it, so
    /// nothing is lost by re-attributing the relay — including at the client that sent it,
    /// which recognises its own removal coming back and drops it.
    /// </summary>
    private void HandleContainerLoot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadContainerLoot(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        // Capture under the packet's module even when a load transition has already moved the
        // active view.  It is safe to acknowledge immediately: inactive history is never fed to
        // the game bridge, and will be replayed only if that module becomes active later.
        bool added = _lootStore.ApplyOp(op);
        if (added && _lootStore.IsActiveModule(op.Module))
            _lootSender.Enqueue(HostPeerId, op.ContainerId, op.ItemId, now, op.Module);

        // Ack unconditionally: a duplicate op means the previous ack was lost.
        int length = PacketCodec.Write(
            _replyBuffer, new ContainerLootAckPacket(HostPeerId, op.OpSequence, op.Module));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    /// <summary>
    /// A client's flag/item/journal version or digest disagrees, resync it. Unlike those
    /// three, container loot has no separate snapshot packet to reply with — its "full
    /// resync" IS the reliable per-removal channel, so repairing a mismatch is exactly what
    /// <see cref="ResyncContainerLoot"/> already does on peer admission. Re-offering to
    /// EVERY current recipient rather than just the requester is deliberate and free:
    /// <see cref="ReliableLootSender.Enqueue"/> dedups by (container, item) against ops
    /// already in flight, and every recipient dedups the same way on arrival, so a peer that
    /// already agrees is not the one who ends up paying for this.
    /// </summary>
    private void HandleLootSyncRequest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadLootSyncRequest(span) is not { } request)
        {
            _channel.NoteMalformed();
            return;
        }

        ResyncContainerLoot(SessionClock.NowMs, request.Module);
        _lootSender.ResetBackoff();
    }

    /// <summary>
    /// Re-offers every container-loot removal this session knows about. Two callers:
    /// <see cref="HandleHello"/> on (re-)admission, so a peer that was away learns about the
    /// chests it was not there for, and <see cref="HandleLootSyncRequest"/> when a STILL
    /// connected peer's version or digest disagrees with the host's — the tie a bare version
    /// counter cannot always see (docs/research/disconnect-reconnect.md §4.1). See the call
    /// sites for why this channel needs it and the flag/item/journal channels do not.
    /// </summary>
    private void ResyncContainerLoot(long nowMs, string? module = null)
    {
        string target = module is { Length: > 0 } ? module : _lootStore.Module;
        if (!string.Equals(target, _lootStore.Module, StringComparison.OrdinalIgnoreCase))
            return;
        List<ContainerLootRemoval> history = _lootStore.HistorySnapshot(target);
        if (history.Count == 0) return;
        lock (_lootResyncGate)
        {
            int cursor = _lootResyncCursor.GetValueOrDefault(target) % history.Count;
            // Resume where the bounded sender filled up. Restarting at zero can endlessly
            // re-offer already acknowledged old removals and starve the missing tail.
            for (int visited = 0; visited < history.Count && _lootSender.HasCapacity; visited++)
            {
                ContainerLootRemoval removal = history[cursor];
                _lootSender.Enqueue(HostPeerId, removal.ContainerId, removal.ItemId, nowMs, target);
                cursor = (cursor + 1) % history.Count;
            }
            _lootResyncCursor[target] = cursor;
        }
    }

    /// <summary>
    /// Re-offers every materialised-loot death this session's bounded history still remembers —
    /// item 11(b), docs/research/enemy-materialisation.md §6. Mirrors
    /// <see cref="ResyncContainerLoot"/>: called from <see cref="HandleHello"/> on
    /// (re-)admission, so a peer that reconnects after an op already fully retired (every
    /// then-connected peer had acked it, and <see cref="MaterialisedLootSender"/> forgot it) is
    /// not left with an empty stand-in. There is no separate loot-sync-request packet for this
    /// channel the way container loot has, so this is the only call site.
    ///
    /// Re-enqueues through <see cref="MaterialisedLootSender.Enqueue"/> — a fresh sequence each
    /// time, same payload — which is a no-op for an owner id already in flight (that op already
    /// reaches every current recipient, including a fresh joiner, through the ordinary
    /// per-recipient ack check in <see cref="MaterialisedLootSender.CollectDue"/>) and a genuine
    /// new broadcast for one that had fully retired. Re-offering costs nothing to a peer who
    /// already has it: the client's dedup-by-owner-id (<c>CoopClient._materialisedLootSeen</c>,
    /// NOT reset on reconnect — only on a genuine module change, via
    /// <see cref="ICoopSession.ResetMaterialisedLootDedup"/>) turns a re-arrival into a no-op
    /// ack rather than a duplicate give.
    /// </summary>
    private void ResyncMaterialisedLoot(long nowMs)
    {
        foreach ((uint ownerEnemyId, IReadOnlyList<MaterialisedLootItem> items)
            in _materialisedLootHistory.SnapshotForModule(_lootStore.Module))
        {
            _materialisedLootSender.Enqueue(HostPeerId, ownerEnemyId, items, nowMs, _lootStore.Module);
        }
    }

    private void HandleContainerLootAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadContainerLootAck(span) is { } lootAck)
        {
            _lootSender.Acknowledge(lootAck.PeerId, lootAck.OpSequence, lootAck.Module);
        }
    }

    private void HandleMaterialisedLootAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadMaterialisedLootAck(span) is { } materialisedLootAck)
        {
            _materialisedLootSender.Acknowledge(
                materialisedLootAck.PeerId, materialisedLootAck.OpSequence, materialisedLootAck.Module);
        }
    }
}
