using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — the merchant/store stock's v2 channel (<c>store_sync=2</c>) — the store's mirror
/// of <c>CoopClient.PartyPoolV2.cs</c>, identical reasoning, widened by the store's own object
/// id. Reuses the same nested <c>PendingIntentV2</c> type declared in that file (this is one
/// partial class).
/// </summary>
public sealed partial class CoopClient
{
    private readonly Dictionary<uint, PendingIntentV2> _pendingStoreIntentsV2 = [];

    private uint _nextStoreIntentSerialV2 = 1;

    private Dictionary<StoreStackKey, int> _storeSnapshotV2 = [];

    private bool _storeSnapshotV2HasAny;

    private readonly Dictionary<StoreStackKey, int> _storeSnapshotV2Gathering = [];

    private uint _storeSnapshotV2GatheringGeneration;

    private bool _storeSnapshotV2GatheringHasGeneration;

    private readonly HashSet<byte> _storeSnapshotV2PagesSeen = [];

    private byte _storeSnapshotV2ExpectedPageCount = 1;

    private readonly Queue<uint> _storeIntentAckDrainV2 = new();

    private readonly Lock _storeIntentAckDrainV2Gate = new();

    public void PublishStoreSnapshotV2(uint storeObjectId, IReadOnlyDictionary<StoreStackKey, int> totals)
    {
    }

    public bool TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> totals)
    {
        lock (_gate)
        {
            if (!_storeSnapshotV2HasAny)
            {
                totals = EmptyStoreTotals.Instance;
                return false;
            }

            totals = _storeSnapshotV2;
            return true;
        }
    }

    public uint PublishStoreIntentV2(StoreOpKind kind, StoreStackKey key, ushort count)
    {
        if (count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_pendingStoreIntentsV2.Count >= MaxPendingIntentsV2)
            {
                return 0;
            }

            uint serial = _nextStoreIntentSerialV2++;
            StoreIntentPacketV2 packet = new(_localPeerId, serial, kind, key, count);
            _pendingStoreIntentsV2[serial] = new PendingIntentV2 { Packet = packet, LastSentMs = 0 };
            return serial;
        }
    }

    public IReadOnlyList<StoreIntentPacketV2> DrainStoreIntentsV2() => [];

    public IReadOnlyList<uint> DrainAckedStoreIntentsV2()
    {
        lock (_storeIntentAckDrainV2Gate)
        {
            if (_storeIntentAckDrainV2.Count == 0)
            {
                return [];
            }

            List<uint> drained = [.. _storeIntentAckDrainV2];
            _storeIntentAckDrainV2.Clear();
            return drained;
        }
    }

    private void RetransmitStoreIntentsV2(long now)
    {
        List<StoreIntentPacketV2> due = [];
        lock (_gate)
        {
            foreach (PendingIntentV2 pending in _pendingStoreIntentsV2.Values)
            {
                if (pending.Attempts > 0
                    && now - pending.LastSentMs
                        < RetransmitBackoff.IntervalMs(
                            pending.Attempts, IntentRetransmitIntervalMs, IntentMaxRetransmitIntervalMs))
                {
                    continue;
                }

                pending.Attempts++;
                pending.LastSentMs = now;
                due.Add((StoreIntentPacketV2)pending.Packet);
            }
        }

        foreach (StoreIntentPacketV2 packet in due)
        {
            int length = PacketCodec.Write(_sendBuffer, packet);
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    private void HandleStoreIntentAckV2(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadStoreIntentAckV2(span) is not { } ack)
        {
            return;
        }

        lock (_gate)
        {
            _pendingStoreIntentsV2.Remove(ack.IntentSerial);
        }

        lock (_storeIntentAckDrainV2Gate)
        {
            _storeIntentAckDrainV2.Enqueue(ack.IntentSerial);
        }
    }

    private void HandleStoreSnapshotV2(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadStoreSnapshotV2(span) is not { } page)
        {
            return;
        }

        lock (_gate)
        {
            if (!_storeSnapshotV2GatheringHasGeneration || page.Generation != _storeSnapshotV2GatheringGeneration)
            {
                _storeSnapshotV2Gathering.Clear();
                _storeSnapshotV2PagesSeen.Clear();
                _storeSnapshotV2GatheringGeneration = page.Generation;
                _storeSnapshotV2GatheringHasGeneration = true;
                _storeSnapshotV2ExpectedPageCount = page.PageCount;
            }

            if (_storeSnapshotV2PagesSeen.Add(page.PageIndex))
            {
                foreach (StoreSnapshotEntry entry in page.Entries)
                {
                    _storeSnapshotV2Gathering[entry.Key] = entry.Count;
                }
            }

            if (_storeSnapshotV2PagesSeen.Count >= _storeSnapshotV2ExpectedPageCount)
            {
                _storeSnapshotV2 = new Dictionary<StoreStackKey, int>(_storeSnapshotV2Gathering);
                _storeSnapshotV2HasAny = true;
                _storeSnapshotV2Gathering.Clear();
                _storeSnapshotV2PagesSeen.Clear();
                _storeSnapshotV2GatheringHasGeneration = false;
            }
        }
    }
}
