using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — the class-grant relay (docs/research/class-and-power-grants.md): the host's own
/// grants broadcast reliably to every client, and a client's report acked, queued for THIS
/// instance's own player, and relayed to every other client under the client's true
/// (origin, sequence) identity — the same shape as <c>CoopHost.Dialogue.cs</c>'s grant channel.
/// </summary>
public sealed partial class CoopHost
{
    private readonly ClassGrantSender _classGrantSender = new();

    /// <summary>Client-originated grants this instance's OWN player still owes an apply for.
    /// The host is never told about its own local grants here (its own engine already ran the
    /// script), only a peer's.</summary>
    private readonly Queue<ClassGrantPacket> _classGrantInbox = new();

    /// <summary>The host's own character just gained a class; broadcast it reliably to every
    /// client. Enqueue-only — the tick loop does the sending and retransmits.</summary>
    public void PublishClassGrant(int classId) =>
        _classGrantSender.EnqueueLocal(HostPeerId, classId, SessionClock.NowMs);

    public IReadOnlyList<ClassGrantPacket> DrainClassGrants()
    {
        lock (_gate)
        {
            if (_classGrantInbox.Count == 0)
            {
                return [];
            }

            List<ClassGrantPacket> ready = [];
            while (_classGrantInbox.Count > 0)
            {
                ready.Add(_classGrantInbox.Dequeue());
            }

            return ready;
        }
    }

    private void BroadcastClassGrant(long now, List<PeerConnection> recipients)
    {
        List<uint> recipientIds = [.. recipients.Select(p => p.Id)];

        foreach (ClassGrantPacket op in _classGrantSender.CollectDue(recipientIds, now))
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
    /// A client's script just added a class to that client's character — ack immediately,
    /// queue it for THIS instance's own player, and relay it to every other client under the
    /// client's TRUE identity (see <see cref="ClassGrantSender.EnqueueRelay"/>, which also
    /// drops a retransmit that already went out once). The inbox is deduplicated the same way
    /// so a retransmit arriving before the ack landed is not applied here twice.
    /// </summary>
    private void HandleClassGrant(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadClassGrant(span) is not { } grant)
        {
            _channel.NoteMalformed();
            return;
        }

        int length = PacketCodec.Write(
            _replyBuffer, new ClassGrantAckPacket(HostPeerId, grant.OriginPeerId, grant.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }

        if (grant.OriginPeerId == HostPeerId)
        {
            return; // a spoofed or malformed origin must not be queued as a peer's grant
        }

        bool fresh;
        lock (_gate)
        {
            if (!_peers.ContainsKey(grant.OriginPeerId))
            {
                return; // only a known peer, and it cannot report for another
            }

            fresh = _classGrantSender.EnqueueRelay(grant, SessionClock.NowMs);
            if (fresh)
            {
                _classGrantInbox.Enqueue(grant);
            }
        }
    }

    private void HandleClassGrantAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadClassGrantAck(span) is { } ack)
        {
            _classGrantSender.Acknowledge(ack.OriginPeerId, ack.OpSequence, ack.PeerId);
        }
    }
}
