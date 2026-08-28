using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — the shared party item-pool channel: reliable, content-keyed add/remove ops.
/// See <see cref="PartyPoolStore"/>/<see cref="ReliablePartyPoolSender"/> for the retry and
/// dedup machinery this file drives, and docs/research/party-pool-sync.md for the design.
///
/// Unlike <see cref="CoopHost.Loot"/>'s container channel, applying a change to THIS
/// instance's own engine is not driven by receiving an op — it is
/// <c>Pumps.PartyPoolPump</c>'s own job, every tick, to reconcile whatever the local engine's
/// pool scan shows against <see cref="PartyPoolTotals"/> (the merged cross-instance belief this
/// file maintains). That split keeps this file — and its client twin — pure "keep the STORE
/// converged across the wire," with no engine-facing responsibility at all.
/// </summary>
public sealed partial class CoopHost
{
    private readonly ReliablePartyPoolSender _partyPoolSender = new();
    private readonly PartyPoolStore _partyPoolStore = new();

    public PartyPoolStore PartyPool => _partyPoolStore;

    /// <summary>Someone's local pool scan found a change; broadcast it reliably. Gated on the
    /// store: a delta the store already reflects (the echo of this host's own earlier publish,
    /// or a peer op already applied) reports no change and is never re-sent.</summary>
    public void PublishPartyPoolOp(PartyPoolOpKind kind, PartyPoolStackKey key, uint countDelta)
    {
        ushort clamped = (ushort)Math.Min(countDelta, ushort.MaxValue);
        if (clamped == 0 || !_partyPoolStore.Record(kind, key, clamped))
        {
            return;
        }

        _partyPoolSender.Enqueue(HostPeerId, kind, key, clamped, SessionClock.NowMs);
    }

    public IReadOnlyDictionary<PartyPoolStackKey, int> PartyPoolTotals => _partyPoolStore.Snapshot();

    /// <summary>Retransmits party-pool ops to every client until each has acked — the same
    /// shape as <see cref="BroadcastContainerLoot"/>.</summary>
    private void BroadcastPartyPool(long now, List<PeerConnection> recipients)
    {
        List<uint> recipientIds = [.. recipients.Select(p => p.Id)];

        foreach (PartyPoolOpPacket op in _partyPoolSender.CollectDue(recipientIds, now))
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
    /// A client's own pool scan found a change; apply it to the host's own store (so it
    /// converges too) and relay it onward under the SAME origin peer id but a FRESH host-minted
    /// sequence.
    ///
    /// ⚠ The origin id is deliberately NOT re-attributed to the host (unlike
    /// <see cref="HandleContainerLoot"/>'s relay, which re-attributes because a container
    /// removal is a SET member and dedups by content regardless of origin). A party-pool total
    /// is not naturally idempotent — applying the same add twice really is twice as much — so
    /// the relay keeps the ORIGINAL contributor's id specifically so that peer's own receive
    /// handler (<see cref="CoopClient.HandlePartyPoolOp"/>) can recognise "this is my own change
    /// echoing back" by origin and skip re-applying it, rather than relying on a content-dedup
    /// property this aggregate-count channel does not have. Only the SEQUENCE is re-minted,
    /// because <see cref="ReliablePartyPoolSender"/>'s own pending table is keyed by a bare
    /// <c>uint</c> sequence — forwarding the client's own sequence risks colliding with the
    /// host's unrelated in-flight ops in that same table (the same reason
    /// <see cref="HandleContainerLoot"/> re-sequences).
    /// </summary>
    private void HandlePartyPoolOp(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPartyPoolOp(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        if (_partyPoolStore.ApplyOp(op))
        {
            _partyPoolSender.Enqueue(op.OriginPeerId, op.Kind, op.Key, op.CountDelta, now);
        }

        // Ack unconditionally: a duplicate op means the previous ack was lost.
        int length = PacketCodec.Write(_replyBuffer, new PartyPoolAckPacket(HostPeerId, op.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    /// <summary>A client's version/digest disagrees; re-offer everything, the same way
    /// <see cref="HandleLootSyncRequest"/> does for container loot — this channel has no
    /// separate snapshot packet either.</summary>
    private void HandlePartyPoolSyncRequest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPartyPoolSyncRequest(span) is null)
        {
            _channel.NoteMalformed();
            return;
        }

        ResyncPartyPool(SessionClock.NowMs);
        _partyPoolSender.ResetBackoff();
    }

    /// <summary>
    /// Re-offers every party-pool stack this session knows about, as absolute
    /// <see cref="PartyPoolOpKind.Set"/> facts under the host's own id — there is no
    /// per-contributor history to attribute a resync to (the store only tracks aggregate
    /// totals), and a peer being resynced may already hold a DIFFERENT nonzero total for a key
    /// (that is exactly why it needs resyncing), so replaying the history as more
    /// <see cref="PartyPoolOpKind.Add"/>s would double-count it. Two callers:
    /// <see cref="HandleHello"/> on (re-)admission, and <see cref="HandlePartyPoolSyncRequest"/>
    /// for a still-connected peer whose digest disagrees. Mirrors
    /// <see cref="ResyncContainerLoot"/>'s call sites.
    /// </summary>
    private void ResyncPartyPool(long nowMs)
    {
        foreach ((PartyPoolStackKey key, int count) in _partyPoolStore.Snapshot())
        {
            _partyPoolSender.Enqueue(
                HostPeerId, PartyPoolOpKind.Set, key, (ushort)Math.Min(count, ushort.MaxValue), nowMs);
        }
    }

    private void HandlePartyPoolAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPartyPoolAck(span) is { } ack)
        {
            _partyPoolSender.Acknowledge(ack.PeerId, ack.OpSequence);
        }
    }

    /// <summary>
    /// <see cref="IPartyPoolSession.RequestPartyPoolResync"/> — the host is already
    /// authoritative, so this just re-runs the same absolute re-broadcast a (re-)admission or a
    /// client's own <see cref="PartyPoolSyncRequestPacket"/> triggers. Safe to call from ANY
    /// thread (the pump's own, in particular): <see cref="ResyncPartyPool"/> only touches
    /// <see cref="_partyPoolStore"/> and <see cref="_partyPoolSender"/>, both internally locked
    /// — unlike <see cref="CoopClient.RequestPartyPoolResync"/>, there is no raw send buffer
    /// here that only the tick thread may touch.
    /// </summary>
    public void RequestPartyPoolResync() => ResyncPartyPool(SessionClock.NowMs);
}
