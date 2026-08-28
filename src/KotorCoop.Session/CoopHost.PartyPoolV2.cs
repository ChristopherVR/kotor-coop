using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — the party pool's v2 channel (<c>party_pool_sync=2</c>): publish-don't-detect. See
/// docs/research/shared-inventory-v2.md §2.4. The host never compares anything here — it just
/// republishes whatever <c>Pumps.PartyPoolPumpV2</c> hands it from a fresh engine scan, and
/// write-throughs an accepted intent's effect into that same belief immediately so the next
/// publish cycle already reflects it, without waiting for the engine apply to actually land.
/// </summary>
public sealed partial class CoopHost
{
    /// <summary>This instance's belief of the pool's current contents — written wholesale from a
    /// real scan (<see cref="PublishPartyPoolSnapshotV2"/>) and write-through-adjusted the moment
    /// an intent is accepted (<see cref="HandlePartyPoolIntentV2"/>). Never compared against
    /// anything; the next real scan simply overwrites whatever an intent's write-through
    /// guessed.</summary>
    private Dictionary<PartyPoolStackKey, int> _partyPoolBeliefV2 = [];

    private uint _partyPoolGenerationV2;

    private const int PartyPoolV2BroadcastIntervalMs = 1000;

    private long _lastPartyPoolV2BroadcastMs;

    /// <summary>Set on a fresh (re)admission so the very next cycle goes out immediately —
    /// mirrors <see cref="_forceLivenessBroadcast"/>; see docs/research/shared-inventory-v2.md
    /// §2.7.</summary>
    private bool _forcePartyPoolV2Broadcast;

    private readonly HashSet<(uint Peer, uint Serial)> _partyPoolIntentDedupV2 = [];

    private readonly Queue<(uint Peer, uint Serial)> _partyPoolIntentDedupHistoryV2 = new();

    private const int MaxRememberedIntentsV2 = 4096;

    /// <summary>Accepted intents this instance's own engine still owes an apply, drained by
    /// <c>Pumps.PartyPoolPumpV2</c> every tick.</summary>
    private readonly Queue<PartyPoolIntentPacketV2> _partyPoolIntentDrainV2 = new();

    private readonly Lock _partyPoolIntentDrainV2Gate = new();

    /// <summary>Host only: publishes this cycle's ABSOLUTE totals — see the class remarks. A
    /// client's own call is the interface default no-op.</summary>
    public void PublishPartyPoolSnapshotV2(IReadOnlyDictionary<PartyPoolStackKey, int> totals)
    {
        lock (_gate)
        {
            _partyPoolBeliefV2 = new Dictionary<PartyPoolStackKey, int>(totals);
        }
    }

    /// <summary>The host IS the truth; nothing to mirror.</summary>
    public bool TryGetPartyPoolSnapshotV2(out IReadOnlyDictionary<PartyPoolStackKey, int> totals)
    {
        totals = EmptyPartyPoolTotals.Instance;
        return false;
    }

    /// <summary>The host never sends intents — the interface default no-op (returns 0).</summary>
    public uint PublishPartyPoolIntentV2(PartyPoolOpKind kind, PartyPoolStackKey key, ushort count) => 0;

    public IReadOnlyList<PartyPoolIntentPacketV2> DrainPartyPoolIntentsV2()
    {
        lock (_partyPoolIntentDrainV2Gate)
        {
            if (_partyPoolIntentDrainV2.Count == 0)
            {
                return [];
            }

            List<PartyPoolIntentPacketV2> drained = [.. _partyPoolIntentDrainV2];
            _partyPoolIntentDrainV2.Clear();
            return drained;
        }
    }

    /// <summary>The host never has an outstanding intent of its own to have acked.</summary>
    public IReadOnlyList<uint> DrainAckedPartyPoolIntentsV2() => [];

    /// <summary>
    /// A client's intent arrived. Dedups by (peer, serial) — a retransmitted copy of an already
    /// -accepted intent is acked again (idempotent to the client, which only needs ONE ack to
    /// eventually arrive) but not applied a second time or written through twice. A genuinely new
    /// intent is write-through-applied into <see cref="_partyPoolBeliefV2"/> immediately (so the
    /// very next publish cycle already reflects it) and queued for
    /// <c>Pumps.PartyPoolPumpV2</c> to apply to the real engine through the staging-cheat channel.
    /// </summary>
    private void HandlePartyPoolIntentV2(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPartyPoolIntentV2(span) is not { } intent)
        {
            _channel.NoteMalformed();
            return;
        }

        (uint Peer, uint Serial) dedupKey = (intent.PeerId, intent.IntentSerial);
        bool isNew;
        lock (_gate)
        {
            isNew = _partyPoolIntentDedupV2.Add(dedupKey);
            if (isNew)
            {
                _partyPoolIntentDedupHistoryV2.Enqueue(dedupKey);
                while (_partyPoolIntentDedupHistoryV2.Count > MaxRememberedIntentsV2)
                {
                    _partyPoolIntentDedupV2.Remove(_partyPoolIntentDedupHistoryV2.Dequeue());
                }

                int before = _partyPoolBeliefV2.GetValueOrDefault(intent.Key);
                int after = intent.Kind == PartyPoolOpKind.Add
                    ? before + intent.Count
                    : Math.Max(0, before - intent.Count);

                if (after > 0)
                {
                    _partyPoolBeliefV2[intent.Key] = after;
                }
                else
                {
                    _partyPoolBeliefV2.Remove(intent.Key);
                }
            }
        }

        if (isNew)
        {
            lock (_partyPoolIntentDrainV2Gate)
            {
                _partyPoolIntentDrainV2.Enqueue(intent);
            }
        }

        int length = PacketCodec.Write(
            _replyBuffer, new PartyPoolIntentAckPacketV2(intent.PeerId, intent.IntentSerial, Accepted: true));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    /// <summary>
    /// Broadcasts the host's current party-pool belief, throttled to roughly once every
    /// <see cref="PartyPoolV2BroadcastIntervalMs"/> — a LEVEL, resent whole every cycle (never a
    /// delta), so a lost datagram costs one cycle of staleness, never a permanently missed fact.
    /// A freshly (re)admitted peer (<see cref="_forcePartyPoolV2Broadcast"/>, set in
    /// <see cref="HandleHello"/>) forces the very next cycle out immediately.
    /// </summary>
    private void BroadcastPartyPoolV2(long now, List<PeerConnection> recipients)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        if (!_forcePartyPoolV2Broadcast && now - _lastPartyPoolV2BroadcastMs < PartyPoolV2BroadcastIntervalMs)
        {
            return;
        }

        _forcePartyPoolV2Broadcast = false;
        _lastPartyPoolV2BroadcastMs = now;
        _partyPoolGenerationV2++;

        Dictionary<PartyPoolStackKey, int> belief;
        lock (_gate)
        {
            belief = _partyPoolBeliefV2;
        }

        foreach (PartyPoolSnapshotPacketV2 page in
            PartyPoolSnapshotPagingV2.Paginate(now, _partyPoolGenerationV2, belief))
        {
            int length = PacketCodec.Write(_sendBuffer, page);
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
}
