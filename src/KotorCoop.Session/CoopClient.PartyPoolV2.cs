using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — the party pool's v2 channel (<c>party_pool_sync=2</c>): the mirrored absolute
/// snapshot, plus this client's own outbound intents (retransmit-until-ack — pure wire
/// reliability; the SEMANTIC "how long do I still trust an unacked intent for" timeout lives in
/// <c>Pumps.PartyPoolPumpV2</c>, not here — see docs/research/shared-inventory-v2.md §2.5).
/// </summary>
public sealed partial class CoopClient
{
    private sealed class PendingIntentV2
    {
        public required object Packet;
        public long LastSentMs;
        public int Attempts;
    }

    private readonly Dictionary<uint, PendingIntentV2> _pendingPartyPoolIntentsV2 = [];

    private uint _nextPartyPoolIntentSerialV2 = 1;

    private const int MaxPendingIntentsV2 = 256;

    private const int IntentRetransmitIntervalMs = 250;

    private const int IntentMaxRetransmitIntervalMs = 4000;

    /// <summary>Every key committed for a FULLY-gathered generation — replaced wholesale on a
    /// new complete generation, never merged into the old one (same rule
    /// <see cref="CoopClient.HandleCreatureLiveness"/> already follows for its own channel).</summary>
    private Dictionary<PartyPoolStackKey, int> _partyPoolSnapshotV2 = [];

    private bool _partyPoolSnapshotV2HasAny;

    /// <summary>Pages gathered so far for the generation currently being assembled — cleared
    /// outright the moment a page from a NEWER generation arrives (an incomplete cycle is
    /// superseded, never committed partially — docs/research/shared-inventory-v2.md §3.1).</summary>
    private readonly Dictionary<PartyPoolStackKey, int> _partyPoolSnapshotV2Gathering = [];

    private uint _partyPoolSnapshotV2GatheringGeneration;

    private bool _partyPoolSnapshotV2GatheringHasGeneration;

    private readonly HashSet<byte> _partyPoolSnapshotV2PagesSeen = [];

    private byte _partyPoolSnapshotV2ExpectedPageCount = 1;

    private readonly Queue<uint> _partyPoolIntentAckDrainV2 = new();

    private readonly Lock _partyPoolIntentAckDrainV2Gate = new();

    /// <summary>A client never publishes a snapshot — the interface default no-op.</summary>
    public void PublishPartyPoolSnapshotV2(IReadOnlyDictionary<PartyPoolStackKey, int> totals)
    {
    }

    public bool TryGetPartyPoolSnapshotV2(out IReadOnlyDictionary<PartyPoolStackKey, int> totals)
    {
        lock (_gate)
        {
            if (!_partyPoolSnapshotV2HasAny)
            {
                totals = EmptyPartyPoolTotals.Instance;
                return false;
            }

            totals = _partyPoolSnapshotV2;
            return true;
        }
    }

    public uint PublishPartyPoolIntentV2(PartyPoolOpKind kind, PartyPoolStackKey key, ushort count)
    {
        if (count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_pendingPartyPoolIntentsV2.Count >= MaxPendingIntentsV2)
            {
                return 0;
            }

            uint serial = _nextPartyPoolIntentSerialV2++;
            PartyPoolIntentPacketV2 packet = new(_localPeerId, serial, kind, key, count);

            // LastSentMs = 0 so the very next Tick sends it immediately.
            _pendingPartyPoolIntentsV2[serial] = new PendingIntentV2 { Packet = packet, LastSentMs = 0 };
            return serial;
        }
    }

    public IReadOnlyList<PartyPoolIntentPacketV2> DrainPartyPoolIntentsV2() => [];

    public IReadOnlyList<uint> DrainAckedPartyPoolIntentsV2()
    {
        lock (_partyPoolIntentAckDrainV2Gate)
        {
            if (_partyPoolIntentAckDrainV2.Count == 0)
            {
                return [];
            }

            List<uint> drained = [.. _partyPoolIntentAckDrainV2];
            _partyPoolIntentAckDrainV2.Clear();
            return drained;
        }
    }

    /// <summary>Retransmits every not-yet-acked intent, same backoff shape as
    /// <see cref="ReliablePartyPoolSender.CollectDue"/> — called from <see cref="Tick"/>.</summary>
    private void RetransmitPartyPoolIntentsV2(long now)
    {
        List<PartyPoolIntentPacketV2> due = [];
        lock (_gate)
        {
            foreach (PendingIntentV2 pending in _pendingPartyPoolIntentsV2.Values)
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
                due.Add((PartyPoolIntentPacketV2)pending.Packet);
            }
        }

        foreach (PartyPoolIntentPacketV2 packet in due)
        {
            int length = PacketCodec.Write(_sendBuffer, packet);
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    private void HandlePartyPoolIntentAckV2(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPartyPoolIntentAckV2(span) is not { } ack)
        {
            return;
        }

        lock (_gate)
        {
            _pendingPartyPoolIntentsV2.Remove(ack.IntentSerial);
        }

        lock (_partyPoolIntentAckDrainV2Gate)
        {
            _partyPoolIntentAckDrainV2.Enqueue(ack.IntentSerial);
        }
    }

    private void HandlePartyPoolSnapshotV2(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPartyPoolSnapshotV2(span) is not { } page)
        {
            return;
        }

        lock (_gate)
        {
            if (!_partyPoolSnapshotV2GatheringHasGeneration
                || page.Generation != _partyPoolSnapshotV2GatheringGeneration)
            {
                // A NEWER generation started — discard whatever partial gather was in progress
                // (docs/research/shared-inventory-v2.md §3.1: an incomplete cycle is simply
                // superseded, never committed partially).
                _partyPoolSnapshotV2Gathering.Clear();
                _partyPoolSnapshotV2PagesSeen.Clear();
                _partyPoolSnapshotV2GatheringGeneration = page.Generation;
                _partyPoolSnapshotV2GatheringHasGeneration = true;
                _partyPoolSnapshotV2ExpectedPageCount = page.PageCount;
            }

            if (_partyPoolSnapshotV2PagesSeen.Add(page.PageIndex))
            {
                foreach (PartyPoolSnapshotEntry entry in page.Entries)
                {
                    _partyPoolSnapshotV2Gathering[entry.Key] = entry.Count;
                }
            }

            if (_partyPoolSnapshotV2PagesSeen.Count >= _partyPoolSnapshotV2ExpectedPageCount)
            {
                // Every page for this generation has arrived — commit wholesale, replacing (not
                // merging into) the previously committed snapshot: a key present before but
                // absent now genuinely left the pool.
                _partyPoolSnapshotV2 = new Dictionary<PartyPoolStackKey, int>(_partyPoolSnapshotV2Gathering);
                _partyPoolSnapshotV2HasAny = true;
                _partyPoolSnapshotV2Gathering.Clear();
                _partyPoolSnapshotV2PagesSeen.Clear();
                _partyPoolSnapshotV2GatheringHasGeneration = false;
            }
        }
    }
}
