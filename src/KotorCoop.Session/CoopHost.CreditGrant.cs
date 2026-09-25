using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — the credits-reward relay (docs/research/personal-inventory.md §4), the same
/// host-authoritative-broadcast/client-relay shape as <see cref="CoopHost"/>'s own dialogue-
/// grant channel, but for a scalar credits amount instead of an item list.
/// </summary>
public sealed partial class CoopHost
{
    private readonly CreditGrantSender _creditGrantSender = new();

    /// <summary>Client-originated grants this instance's OWN player still owes a purse credit
    /// for — see <see cref="HandleCreditGrant"/>.</summary>
    private readonly Queue<CreditGrantPacket> _creditGrantInbox = new();

    /// <summary>(origin, sequence) pairs already queued into <see cref="_creditGrantInbox"/> —
    /// unlike the dialogue-grant channel's own host-side inbox (which has no such guard, a
    /// pre-existing gap this channel does not inherit — see the design doc's §4), a retransmit
    /// of a client's report arriving before its ack does must never be queued twice, or the
    /// host's own purse would be credited twice for one reward. Never reset: a credit-grant
    /// sequence is a peer's own ever-increasing counter for the life of the connection.</summary>
    private readonly HashSet<(uint Origin, uint Sequence)> _creditGrantInboxSeen = [];

    /// <summary>The host's own conversation just handed its player credits; broadcast it
    /// reliably to every client.</summary>
    public void PublishCreditGrant(int amount) =>
        _creditGrantSender.EnqueueLocal(HostPeerId, amount, SessionClock.NowMs);

    /// <inheritdoc cref="ICreditGrantSession.DrainCreditGrants" />
    public IReadOnlyList<CreditGrantPacket> DrainCreditGrants()
    {
        lock (_gate)
        {
            if (_creditGrantInbox.Count == 0)
            {
                return [];
            }

            List<CreditGrantPacket> ready = [];
            while (_creditGrantInbox.Count > 0)
            {
                ready.Add(_creditGrantInbox.Dequeue());
            }

            return ready;
        }
    }

    /// <summary>Retransmits credit grants — this instance's own and every client's relayed
    /// report — to every client until each has acked. Reliable rather than snapshot-broadcast:
    /// a reward fires once, so a dropped packet must not silently mean the credits never reach
    /// the other player's purse.</summary>
    private void BroadcastCreditGrant(long now, List<PeerConnection> recipients)
    {
        List<uint> recipientIds = [.. recipients.Select(p => p.Id)];

        foreach (CreditGrantPacket op in _creditGrantSender.CollectDue(recipientIds, now))
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
    /// A client's conversation just handed its player credits — ack immediately, queue the
    /// grant for THIS instance's own player to receive exactly once
    /// (<see cref="_creditGrantInboxSeen"/>), and relay it to every other connected client under
    /// the client's TRUE (origin, sequence) identity — same reasoning as
    /// <see cref="HandleDialogueGrant"/>.
    /// </summary>
    private void HandleCreditGrant(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadCreditGrant(span) is not { } grant)
        {
            _channel.NoteMalformed();
            return;
        }

        int length = PacketCodec.Write(
            _replyBuffer, new CreditGrantAckPacket(HostPeerId, grant.OriginPeerId, grant.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }

        if (grant.OriginPeerId == HostPeerId)
        {
            // Should never happen — the host does not send itself packets.
            return;
        }

        lock (_gate)
        {
            if (_creditGrantInboxSeen.Add((grant.OriginPeerId, grant.OpSequence)))
            {
                _creditGrantInbox.Enqueue(grant);
            }
        }

        _creditGrantSender.EnqueueRelay(grant, SessionClock.NowMs);
    }

    private void HandleCreditGrantAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadCreditGrantAck(span) is { } creditGrantAck)
        {
            _creditGrantSender.Acknowledge(
                creditGrantAck.OriginPeerId, creditGrantAck.OpSequence, creditGrantAck.PeerId);
        }
    }
}
