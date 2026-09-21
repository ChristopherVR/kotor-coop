using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — the party pool's v2 channel (<c>party_pool_sync=2</c>): publish-don't-detect. See
/// docs/research/shared-inventory-v2.md §2.4/§2.1 and §7's 2026-08-29 defect write-up. The host
/// does EXACTLY two things here — republish whatever <c>Pumps.PartyPoolPumpV2</c> hands it from a
/// fresh engine scan (<see cref="PublishPartyPoolSnapshotV2"/>), and apply an accepted client
/// intent to its own engine at most once, idempotent by (peer, serial)
/// (<see cref="HandlePartyPoolIntentV2"/>'s dedup + <see cref="DrainPartyPoolIntentsV2"/>). It
/// NEVER writes an intent's guessed effect into its own belief ahead of the real engine scan —
/// §7 found that this file used to do exactly that ("write-through"), which meant a broadcast
/// snapshot could carry a value the host's own engine had not actually produced yet, later
/// silently overwritten (or not) by the very next real scan depending on timing: a genuine
/// departure from §2.1's "the host publishes... a direct read of the host's own game", and a
/// second, independent source of belief instability alongside the concurrent-dictionary race §7
/// also fixed here. <see cref="_partyPoolBeliefV2"/> is now written ONLY by
/// <see cref="PublishPartyPoolSnapshotV2"/>, i.e. only ever a verbatim copy of the most recent
/// real scan.
/// </summary>
public sealed partial class CoopHost
{
    /// <summary>This instance's belief of the pool's current contents — written wholesale, ONLY
    /// from a real scan (<see cref="PublishPartyPoolSnapshotV2"/>). Never mutated from an intent
    /// directly (§7, 2026-08-29) — an accepted intent's effect reaches this dictionary only once
    /// the real engine apply has actually landed and the next scan reads it back.</summary>
    private Dictionary<PartyPoolStackKey, int> _partyPoolBeliefV2 = [];

    /// <summary>False until the first <see cref="PublishPartyPoolSnapshotV2"/>, and again after
    /// <see cref="WithdrawPartyPoolSnapshotV2"/> — the host's world was replaced and the belief
    /// above describes a pool that no longer exists. While false nothing is broadcast: a client
    /// holds its last snapshot (never expires) and a rejoining client waits for a real one.
    /// Distinct from an EMPTY belief on purpose — an empty snapshot is a statement ("the pool is
    /// empty") and a withdrawn one is silence.</summary>
    private bool _partyPoolBeliefV2Valid;

    private uint _partyPoolGenerationV2;

    private const int PartyPoolV2BroadcastIntervalMs = 1000;

    private long _lastPartyPoolV2BroadcastMs;

    /// <summary>Set on a fresh (re)admission so the very next cycle goes out immediately —
    /// mirrors <see cref="_forceLivenessBroadcast"/>; see docs/research/shared-inventory-v2.md
    /// §2.7.</summary>
    private bool _forcePartyPoolV2Broadcast;

    private readonly HashSet<(uint Peer, uint Serial)> _partyPoolIntentDedupV2 = [];

    private readonly Queue<(uint Peer, uint Serial)> _partyPoolIntentDedupHistoryV2 = new();

    // An intent is not acknowledged until the engine answer arrives. Keep the sender and the
    // verdict while the dedup entry is retained so a retransmit after a lost ACK gets the same
    // result without queueing a second engine apply.
    private readonly Dictionary<(uint Peer, uint Serial), IPEndPoint> _partyPoolIntentSourcesV2 = [];
    private readonly Dictionary<(uint Peer, uint Serial), bool> _partyPoolIntentResultsV2 = [];
    private readonly Dictionary<(uint Peer, uint Serial), PartyPoolIntentPacketV2> _partyPoolIntentPacketsV2 = [];

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
            _partyPoolBeliefV2Valid = true;
        }
    }

    /// <summary>See <see cref="IPartyPoolSession.WithdrawPartyPoolSnapshotV2"/>.</summary>
    public void WithdrawPartyPoolSnapshotV2()
    {
        lock (_gate)
        {
            _partyPoolBeliefV2Valid = false;
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
    /// A client's intent arrived — <c>ApplyClientIntent(serial)</c>, the ONLY host-side
    /// apply-to-engine path (§7, 2026-08-29). Dedups by (peer, serial): a retransmitted copy of
    /// an already-accepted intent is acked again (idempotent to the client, which only needs ONE
    /// ack to eventually arrive) but never queued for a second engine apply. A genuinely new
    /// intent is queued for <c>Pumps.PartyPoolPumpV2</c> to apply to the real engine through the
    /// staging-cheat channel, exactly once — and is NOT also written into
    /// <see cref="_partyPoolBeliefV2"/> here; that dictionary is touched only by
    /// <see cref="PublishPartyPoolSnapshotV2"/>, so the next broadcast reflects this intent only
    /// once the real apply has actually landed and a real scan reads it back (§2.1's "a direct
    /// read of the host's own game", now literal).
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
        bool haveResult;
        bool result = false;
        lock (_gate)
        {
            _partyPoolIntentSourcesV2[dedupKey] = from;
            _partyPoolIntentPacketsV2[dedupKey] = intent;
            haveResult = _partyPoolIntentResultsV2.TryGetValue(dedupKey, out result);
            isNew = _partyPoolIntentDedupV2.Add(dedupKey);
            if (isNew)
            {
                _partyPoolIntentDedupHistoryV2.Enqueue(dedupKey);
                while (_partyPoolIntentDedupHistoryV2.Count > MaxRememberedIntentsV2)
                {
                    (uint Peer, uint Serial) evicted = _partyPoolIntentDedupHistoryV2.Dequeue();
                    _partyPoolIntentDedupV2.Remove(evicted);
                    _partyPoolIntentSourcesV2.Remove(evicted);
                    _partyPoolIntentResultsV2.Remove(evicted);
                    _partyPoolIntentPacketsV2.Remove(evicted);
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

        // New and still-pending intents have no ACK yet. A duplicate of a completed intent is
        // answered from the retained result cache, which makes the delayed ACK reliable.
        if (haveResult)
        {
            SendPartyPoolIntentAck(from, intent, result);
        }
    }

    public void CompletePartyPoolIntentV2(uint peerId, uint intentSerial, bool accepted)
    {
        (uint Peer, uint Serial) key = (peerId, intentSerial);
        IPEndPoint? destination;
        PartyPoolIntentPacketV2? intent;
        lock (_gate)
        {
            if (!_partyPoolIntentDedupV2.Contains(key))
            {
                return;
            }

            _partyPoolIntentResultsV2[key] = accepted;
            _partyPoolIntentSourcesV2.TryGetValue(key, out destination);
            if (!_partyPoolIntentPacketsV2.TryGetValue(key, out intent))
            {
                return;
            }
        }

        if (destination is not null)
        {
            SendPartyPoolIntentAck(destination, intent, accepted);
        }
    }

    private void SendPartyPoolIntentAck(IPEndPoint destination, PartyPoolIntentPacketV2 intent, bool accepted)
    {
        SendFromCaller(destination,
            new PartyPoolIntentAckPacketV2(intent.PeerId, intent.IntentSerial, accepted),
            static (buffer, packet) => PacketCodec.Write(buffer, packet));
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

        // §7 (2026-08-29): defence in depth, alongside removing HandlePartyPoolIntentV2's own
        // write-through above. Before that removal, a bare reference copy here handed Paginate —
        // running OUTSIDE this lock — the SAME live Dictionary instance the intent handler
        // mutated IN PLACE concurrently (the receive loop's own task, racing the Tick task here):
        // Dictionary<TKey,TValue> is explicitly not safe for a reader to enumerate while another
        // thread mutates it (root cause 5's own "unguarded cross-thread queue" class,
        // reintroduced here). PublishPartyPoolSnapshotV2 now only ever REPLACES the field's
        // reference wholesale under the lock, never mutates a previously-captured reference in
        // place, so this clone is no longer covering an active race — kept anyway so a future
        // in-place mutator (a resync helper, a debug tool) can never reintroduce one silently.
        Dictionary<PartyPoolStackKey, int> belief;
        lock (_gate)
        {
            if (!_partyPoolBeliefV2Valid)
            {
                return;
            }

            belief = new Dictionary<PartyPoolStackKey, int>(_partyPoolBeliefV2);
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
