using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — this client's side of the class-grant relay
/// (docs/research/class-and-power-grants.md): its own grants reported reliably to the host,
/// and every other peer's (relayed by the host under the true origin) acked, deduplicated and
/// queued for THIS instance's own player. Same shape as <c>CoopClient.Dialogue.cs</c>'s
/// grant channel.
/// </summary>
public sealed partial class CoopClient
{
    private readonly ClassGrantSender _classGrantSender = new();

    /// <summary>Grants a peer's script produced, relayed by the host, still owed an apply to
    /// THIS instance's own player. Self-origin echoes are filtered out in
    /// <see cref="HandleClassGrant"/>, before they ever reach this queue.</summary>
    private readonly Queue<ClassGrantPacket> _classGrantInbox = new();

    /// <summary>(origin, sequence) pairs already queued this session — a peer's own
    /// ever-increasing counter for the life of the connection, never reset on a module change.</summary>
    private readonly HashSet<(uint Origin, uint Sequence)> _classGrantSeen = [];

    /// <summary>A script on this instance just added a class to this player's character;
    /// report it to the host, which relays to every other peer. Queued under the last admitted
    /// identity, like <see cref="PublishDialogueGrant"/>.</summary>
    public void PublishClassGrant(int classId)
    {
        if (_sessionPeerId == 0)
        {
            return;
        }

        _classGrantSender.EnqueueLocal(_sessionPeerId, classId, SessionClock.NowMs);
    }

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

    private void FlushClassGrants(long now)
    {
        foreach (ClassGrantPacket op in _classGrantSender.CollectDue(LootRecipients, now))
        {
            int length = PacketCodec.Write(_sendBuffer, op);
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    /// <summary>
    /// Records a class grant relayed by the host and acks it unconditionally — a duplicate
    /// means our previous ack was lost, not that the script ran twice. Self-origin is filtered
    /// here (the host relays under the TRUE origin, so this instance's own report comes back
    /// on the same broadcast every other client gets).
    /// </summary>
    private void HandleClassGrant(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadClassGrant(span) is not { } grant)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (grant.OriginPeerId != _localPeerId && _classGrantSeen.Add((grant.OriginPeerId, grant.OpSequence)))
            {
                _classGrantInbox.Enqueue(grant);
            }
        }

        int length = PacketCodec.Write(
            _replyBuffer, new ClassGrantAckPacket(_localPeerId, grant.OriginPeerId, grant.OpSequence));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
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
