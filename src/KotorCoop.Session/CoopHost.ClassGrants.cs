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

    /// <summary>
    /// Whether ANY player's Jedi choice has ever happened this session — the durable fact a
    /// LATE joiner needs (docs/research/class-and-power-grants.md's 2026-09-25 "requirement 3"),
    /// which <see cref="ClassGrantSender"/>'s own retransmit-until-ack table cannot answer once
    /// every CURRENTLY connected peer has acked an op and it retires (the same gap
    /// <c>ResyncContainerLoot</c>'s own remarks describe for container loot: "a removal is
    /// announced once, reliably, and then never mentioned again"). Never cleared for the life of
    /// the process; a module change does not undo a class.
    /// </summary>
    private bool _jediChoiceEverHappened;

    /// <summary>The most recent class id seen, kept ONLY so a late-joiner resync's synthetic
    /// packet has something plausible to log — the receiver never applies it directly any more
    /// (see <c>KotorCoop.Plugin.JediChoicePromptController</c>).</summary>
    private int _lastJediChoiceClassId;

    /// <summary>Unacked class-grant ops still in flight, for tests (mirrors
    /// <see cref="MaterialisedLootPendingCount"/>).</summary>
    public int ClassGrantPendingCount => _classGrantSender.PendingCount;

    /// <summary>The host's own character just gained a class; broadcast it reliably to every
    /// client. Enqueue-only — the tick loop does the sending and retransmits.</summary>
    public void PublishClassGrant(int classId)
    {
        _jediChoiceEverHappened = true;
        _lastJediChoiceClassId = classId;
        _classGrantSender.EnqueueLocal(HostPeerId, classId, SessionClock.NowMs);
    }

    /// <summary>
    /// Re-announces "a Jedi choice happened" for a freshly (re-)admitted peer, so it is checked
    /// against its OWN character even when the original grant already fully retired (every peer
    /// present at the time acked it) before this peer arrived — the gap
    /// <see cref="ClassGrantSender.ResetBackoff"/> alone cannot close, since that only speeds up
    /// an op still PENDING. A brand-new (never-acked) packet under the HOST's identity is queued
    /// regardless of who originally made the choice — attribution in the receiver's log line is
    /// a minor cosmetic cost, and the receiver's own local "do I already have a class" check
    /// makes this a safe no-op for a peer who does not need it. Called from
    /// <c>HandleHello</c> on (re-)admission, alongside the other Resync* calls.
    /// </summary>
    private void ResyncJediChoice(long nowMs)
    {
        if (_jediChoiceEverHappened)
        {
            _classGrantSender.EnqueueLocal(HostPeerId, _lastJediChoiceClassId, nowMs);
        }
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
                _jediChoiceEverHappened = true;
                _lastJediChoiceClassId = grant.ClassId;
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
