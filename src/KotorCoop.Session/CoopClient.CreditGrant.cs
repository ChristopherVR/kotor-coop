using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — the credits-reward relay (docs/research/personal-inventory.md §4), mirroring
/// <see cref="CoopClient"/>'s own dialogue-grant channel shape for a scalar credits amount.
/// </summary>
public sealed partial class CoopClient
{
    private readonly CreditGrantSender _creditGrantSender = new();

    /// <summary>Grants a peer's conversation reported, relayed by the host, still owed to THIS
    /// instance's own purse. Self-origin echoes are filtered out in
    /// <see cref="HandleCreditGrant"/>, before they ever reach this queue.</summary>
    private readonly Queue<CreditGrantPacket> _creditGrantInbox = new();

    /// <summary>(origin, sequence) pairs already queued or discarded this session — the same
    /// "never double-pay on resync" guard <see cref="CoopHost"/>'s inbox now also has.</summary>
    private readonly HashSet<(uint Origin, uint Sequence)> _creditGrantSeen = [];

    /// <summary>This instance's own conversation just handed its player credits; report it to
    /// the host, which relays to every other peer.</summary>
    public void PublishCreditGrant(int amount)
    {
        if (_sessionPeerId == 0)
        {
            return;
        }

        _creditGrantSender.EnqueueLocal(_sessionPeerId, amount, SessionClock.NowMs);
    }

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

    /// <summary>
    /// Records a credit grant relayed by the host and acks it unconditionally, like
    /// <see cref="HandleDialogueGrant"/> — a duplicate arriving means our previous ack was
    /// lost, not that the conversation ran twice. Self-origin is filtered here, not left for
    /// the agent pump to notice, for the identical reason
    /// <see cref="HandleDialogueGrant"/>'s own remarks give for items: crediting an already-
    /// credited amount is not idempotent.
    /// </summary>
    private void HandleCreditGrant(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadCreditGrant(span) is not { } grant)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (grant.OriginPeerId != _localPeerId && _creditGrantSeen.Add((grant.OriginPeerId, grant.OpSequence)))
            {
                _creditGrantInbox.Enqueue(grant);
            }
        }

        int length = PacketCodec.Write(
            _replyBuffer, new CreditGrantAckPacket(_localPeerId, grant.OriginPeerId, grant.OpSequence));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
        }
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
