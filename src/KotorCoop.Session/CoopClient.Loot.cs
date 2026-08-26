using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — Container-loot removals: this client's own removals queued to the reliable
/// sender, and the host's reliable removals/acks applied here.
/// </summary>
public sealed partial class CoopClient
{
    private readonly ContainerLootStore _lootStore = new();
    private readonly ReliableLootSender _lootSender = new();

    /// <summary>The container-loot store, exposed the way <see cref="Flags"/>/<see cref="Items"/>
    /// already are, so a caller (a test, the resync tripwire) can read its Version/Digest
    /// without a separate accessor per field.</summary>
    public ContainerLootStore Loot => _lootStore;

    /// <summary>
    /// The loot sender's one recipient: the host, which applies the removal, re-attributes
    /// it under its own id, and relays it to the other clients (see the host's
    /// HandleContainerLoot). The shared sender retires an op once every listed recipient has
    /// acked, so a client's list is just this.
    /// </summary>
    private static readonly uint[] LootRecipients = [CoopHost.HostPeerId];

    /// <summary>
    /// This player emptied a container here. A container is a private copy in each process, so
    /// unless the host is told, its copy still holds the item and whoever opens it there takes
    /// the same loot a second time — the dupe this whole channel exists to prevent.
    ///
    /// Reliable, like the host's own removals: a removal is a one-shot fact, not a level the
    /// next tick re-sends. Enqueue-only; the tick loop owns the send buffer.
    ///
    /// Gated on the store, which is what stops the host's removals bouncing back: applying one
    /// empties a container here, this instance's scanner then reports the same item as looted,
    /// and the store recognises it and swallows the announcement.
    /// </summary>
    public void PublishContainerLoot(uint containerId, uint itemId)
    {
        if (!_lootStore.Add(new ContainerLootRemoval(containerId, itemId)))
        {
            return;
        }

        // Queued under the last admitted identity so a chest emptied during an outage still
        // reaches the host afterwards — see SetFlag.
        _lootSender.Enqueue(_sessionPeerId, containerId, itemId, SessionClock.NowMs);
    }

    /// <summary>The container-loot removals received from the host, for the agent to apply.</summary>
    public IReadOnlyList<ContainerLootRemoval> ContainerLootRemovals => _lootStore.Snapshot();

    private void SendContainerLoot(ContainerLootPacket op)
    {
        int length = PacketCodec.Write(_sendBuffer, op);
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    /// <summary>
    /// Records a container-loot removal from the host and acks it. Acked unconditionally — a
    /// duplicate op means our previous ack was lost — and the store dedups by op sequence, so
    /// applying the removal twice is impossible even though the ack repeats.
    /// </summary>
    private void HandleContainerLoot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadContainerLoot(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        _lootStore.ApplyOp(op);

        int length = PacketCodec.Write(
            _replyBuffer, new ContainerLootAckPacket(_localPeerId, op.OpSequence));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
        }
    }

    /// <summary>An owner enemy id we have already queued (or discarded) a
    /// <see cref="MaterialisedLootPacket"/> for this session. Dedup by OWNER ENEMY ID, not op
    /// sequence: unlike a container removal, applying this loot is not idempotent
    /// (<c>GiveItemToInventory</c> mints a fresh item every call), so a retransmit that slips
    /// in before our ack lands must never reach the drain queue a second time.
    ///
    /// Reset on module change via <see cref="ResetMaterialisedLootDedup"/> — an id space is
    /// per-module, so without the reset a reused id after a transition would be silently
    /// ignored here forever (docs/research/enemy-materialisation.md §6). Guarded by
    /// <see cref="_gate"/>, the same lock <see cref="_materialisedLootInbox"/> uses: this set is
    /// mutated from the receive loop and now also cleared from the agent's thread via the
    /// session interface, so an unsynchronised HashSet is no longer safe.</summary>
    private readonly HashSet<uint> _materialisedLootSeen = [];

    private readonly Queue<MaterialisedLootPacket> _materialisedLootInbox = new();

    /// <summary>
    /// Records a materialised enemy's captured death-loot from the host and acks it.
    /// Unconditionally, like <see cref="HandleContainerLoot"/> — a duplicate arriving means our
    /// previous ack was lost, not that the enemy died twice. Queued for the agent's own pump to
    /// drain and actually apply; this layer only receives, dedups and acknowledges.
    /// </summary>
    private void HandleMaterialisedLoot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadMaterialisedLoot(span) is not { } loot)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (_materialisedLootSeen.Add(loot.OwnerEnemyId))
            {
                _materialisedLootInbox.Enqueue(loot);
            }
        }

        int length = PacketCodec.Write(
            _replyBuffer, new MaterialisedLootAckPacket(_localPeerId, loot.OpSequence));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
        }
    }

    /// <summary>
    /// Forgets every owner-enemy id deduped so far — called once by the agent right after it
    /// notices the local module changed (<see cref="ICoopSession.ResetMaterialisedLootDedup"/>),
    /// since ids are reassigned per module load and a reused id here would otherwise be
    /// silently swallowed forever as "already seen" (docs/research/enemy-materialisation.md §6).
    /// </summary>
    public void ResetMaterialisedLootDedup()
    {
        lock (_gate)
        {
            _materialisedLootSeen.Clear();
        }
    }

    /// <summary>Every materialised-loot death received since the last drain, oldest first.
    /// Matches <see cref="DrainTransitionMovies"/>'s shape: the agent pump owns applying
    /// these, this layer only queues them.</summary>
    public IReadOnlyList<MaterialisedLootPacket> DrainMaterialisedLoot()
    {
        lock (_gate)
        {
            if (_materialisedLootInbox.Count == 0)
            {
                return [];
            }

            List<MaterialisedLootPacket> ready = [];
            while (_materialisedLootInbox.Count > 0)
            {
                ready.Add(_materialisedLootInbox.Dequeue());
            }

            return ready;
        }
    }

    private void HandleContainerLootAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadContainerLootAck(span) is { } lootAck)
        {
            // The ack names its sender (the host) so the shared per-recipient
            // sender can mark exactly that recipient done.
            _lootSender.Acknowledge(lootAck.PeerId, lootAck.OpSequence);
        }
    }
}
