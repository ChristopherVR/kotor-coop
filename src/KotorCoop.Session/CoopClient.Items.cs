using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

internal sealed record ItemContinuation(uint HostSessionId, uint PeerId, uint Nonce,
    uint NextItemSequence, ReliableItemSender Sender);

/// <summary>
/// CoopClient — Ground-item replication: drops/removals applied locally straight away and queued
/// to the reliable sender, plus the host's acks and full-snapshot replace.
/// </summary>
public sealed partial class CoopClient
{
    private ReliableItemSender _itemSender = new();
    private ItemContinuation? _itemContinuation;
    private readonly HashSet<uint> _knownGroundItemIds = [];

    internal void SeedItemContinuation(ItemContinuation? continuation)
    {
        if (continuation is not null) _itemContinuation = continuation;
    }

    internal ItemContinuation? CaptureItemContinuation()
    {
        lock (_gate)
        {
            return _hostSessionId != 0 && _sessionPeerId != 0
                ? new(_hostSessionId, _sessionPeerId, _nonce, _nextItemSequence, _itemSender)
                : _itemContinuation;
        }
    }

    private void RestoreItemContinuation(uint hostSessionId, uint peerId)
    {
        if (_hostSessionId == hostSessionId && _sessionPeerId == peerId)
        {
            // This object already owns the namespace, including operations queued while
            // disconnected. Reading an older disk checkpoint would lose those operations.
            _itemContinuation = null;
            return;
        }
        if (_hostSessionId != 0 && (_hostSessionId != hostSessionId || _sessionPeerId != peerId))
        {
            // A running client may survive a host restart without being recreated.
            // Old pending datagrams must not enter the replacement host's namespace.
            _itemSender = new();
            _nextItemSequence = 1;
            _knownGroundItemIds.Clear();
            Items.ResetNamespace();
        }
        if (_itemContinuation is not { } candidate || candidate.HostSessionId != hostSessionId
            || candidate.PeerId != peerId || candidate.Nonce != _nonce)
        {
            _itemContinuation = ItemContinuationFile.ReadFor(_settings.ItemContinuationPath,
                hostSessionId, peerId, _nonce);
        }
        if (_itemContinuation is { } seed && seed.HostSessionId == hostSessionId
            && seed.PeerId == peerId && seed.Nonce == _nonce)
        {
            lock (_gate)
            {
                _nextItemSequence = seed.NextItemSequence;
                _itemSender = seed.Sender;
            }
        }
        _itemContinuation = null;
        if (_settings.ItemContinuationPath is { Length: > 0 } path)
        {
            // Bind before exposing the connection. The callback runs under the sender's
            // lock, so counters/pending operations reach disk before their first datagram.
            _itemSender.Persist = () => ItemContinuationFile.Write(path,
                new(hostSessionId, peerId, _nonce, _nextItemSequence, _itemSender));
            _itemSender.Persist();
        }
    }

    public GroundItemStore Items { get; } = new();

    public uint DropItem(
        string resref, string module, Vector3f position, ItemIdentity identity = default)
    {
        if (_sessionPeerId == 0)
        {
            return 0;
        }

        uint itemId;
        lock (_gate)
        {
            if (_nextItemSequence is 0 or > 0xFFFF)
                throw new InvalidOperationException("ground-item ID space exhausted for this host session; restart the host before dropping more items");
            itemId = (_sessionPeerId << 16) | _nextItemSequence++;
            _knownGroundItemIds.Add(itemId);
        }

        GroundItem item = new(itemId, resref, module, position, identity);

        // Applied locally straight away so the dropping player is not waiting on a round
        // trip; the host's authoritative version will confirm or correct it.
        if (!Items.Drop(item))
        {
            return 0;
        }

        // Null means the reliable sender refused the op (its pending table is at its cap,
        // logged there) — see RetransmitBackoff's remarks. The local store already has the
        // drop, so this player's own game is still correct even though nothing goes out.
        if (_itemSender.Enqueue(_sessionPeerId, ItemOpKind.Drop, item, SessionClock.NowMs) is { } op
            && IsConnected)
        {
            SendItemOp(op);
        }

        return itemId;
    }

    public void RemoveItem(uint itemId)
    {
        if (itemId == 0 || _sessionPeerId == 0)
        {
            return;
        }

        // A reconnect snapshot can arrive after our local drop and temporarily omit it
        // even though the host has already accepted it. The real pickup still owns this
        // removal; local cache membership must not suppress its reliable tombstone.
        bool removedLocally = Items.Remove(itemId);
        lock (_gate)
        {
            bool allocatedHere = (itemId >> 16) == _sessionPeerId
                && (itemId & 0xFFFF) != 0 && (itemId & 0xFFFF) < _nextItemSequence;
            if (!removedLocally && !allocatedHere && !_knownGroundItemIds.Contains(itemId)) return;
        }

        GroundItem tombstone = new(itemId, string.Empty, string.Empty, Vector3f.Zero);

        if (_itemSender.Enqueue(_sessionPeerId, ItemOpKind.Remove, tombstone, SessionClock.NowMs) is { } op
            && IsConnected)
        {
            SendItemOp(op);
        }
    }

    private void SendItemOp(ItemOpPacket op)
    {
        // ⚠ Called from the CALLER's thread (SetFlag/DropItem/SetJournalEntry are public API
        // the agent pump drives) as well as from Tick's retransmit pass, so it never touches
        // the tick thread's _sendBuffer. See CoopClient._apiBuffer.
        SendFromCaller(op, static (buffer, packet) => PacketCodec.Write(buffer, packet));
    }

    private void HandleItemAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadItemAck(span) is { } itemAck)
        {
            _itemSender.Acknowledge(itemAck.OpSequence);
        }
    }

    private void HandleItemSnapshot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadItemSnapshot(span) is { } itemSnapshot)
        {
            lock (_gate)
            {
                foreach (GroundItem item in itemSnapshot.Items) _knownGroundItemIds.Add(item.ItemId);
            }
            Items.ReplaceAll(itemSnapshot.Items, itemSnapshot.Version);
        }
    }
}
