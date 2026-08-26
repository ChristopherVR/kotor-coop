using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — The single conversation token (who holds it, and the arbiter that decides) and
/// its sibling channel, dialogue-triggered item/recruit grants — both host-
/// authoritative, both reliable where a grant is a one-shot fact that must survive
/// a dropped packet.
/// </summary>
public sealed partial class CoopHost
{
    private readonly DialogueGrantSender _dialogueGrantSender = new();

    /// <summary>Client-originated grants this instance's OWN player still owes a give-loot
    /// request for — see <see cref="HandleDialogueGrant"/>. The host is never told about its
    /// own local grants here (its own engine already ran the reward script), only a peer's.
    /// </summary>
    private readonly Queue<DialogueGrantPacket> _dialogueGrantInbox = new();

    /// <summary>The host's own conversation just granted something; broadcast it reliably to
    /// every client. Enqueue-only — the tick loop, which owns the send buffer, does the actual
    /// broadcasting and retransmits.</summary>
    public void PublishDialogueGrant(string partnerTag, IReadOnlyList<MaterialisedLootItem> items) =>
        _dialogueGrantSender.EnqueueLocal(HostPeerId, partnerTag, items, SessionClock.NowMs);

    /// <summary>Every dialogue-grant a CLIENT'S conversation reported, still owed a give to this
    /// instance's own player (docs/research/dialogue-consequences.md §3.4) — the host's half of
    /// the symmetric channel; a client drains the same shape from <see cref="CoopClient"/>.
    /// </summary>
    public IReadOnlyList<DialogueGrantPacket> DrainDialogueGrants()
    {
        lock (_gate)
        {
            if (_dialogueGrantInbox.Count == 0)
            {
                return [];
            }

            List<DialogueGrantPacket> ready = [];
            while (_dialogueGrantInbox.Count > 0)
            {
                ready.Add(_dialogueGrantInbox.Dequeue());
            }

            return ready;
        }
    }

    /// <summary>The single conversation token; the host is the sole arbiter.</summary>
    private DialogueState _dialogue = DialogueState.Idle;

    public void PublishDialogue(
        bool active, string speakerName, string line,
        string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
        int nodeIndex = DialogueState.NoNode, uint nodeSerial = 0, bool forced = false)
    {
        lock (_gate)
        {
            // The host's own claim goes through the same arbiter as a client's, so ownership
            // and the serial are decided in exactly one place.
            _dialogue = DialogueArbiter.Apply(
                _dialogue, HostPeerId, active, speakerName, line, partnerTag, replySerial, replyIndex, pickLog,
                nodeIndex, nodeSerial,
                _interactionLocks.HolderOf(_interactionModule, partnerTag ?? string.Empty, SessionClock.NowMs),
                forced);
        }
    }

    public DialogueState GetDialogue()
    {
        lock (_gate)
        {
            return _dialogue;
        }
    }

    /// <summary>Drops the token if the peer that held it has gone. Called on disconnect.</summary>
    private void ReleaseDialogueIfOwnedBy(uint peerId)
    {
        if (_dialogue.IsOwnedBy(peerId))
        {
            _dialogue = new DialogueState(false, 0, _dialogue.Serial + 1, string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Retransmits dialogue grants — this instance's own and every client's relayed report —
    /// to every client until each has acked. Reliable rather than snapshot-broadcast, for the
    /// same reason as <see cref="BroadcastMaterialisedLoot"/>: a reward node fires once, so a
    /// dropped packet must not silently mean the item or recruit never reaches the other screen.
    /// </summary>
    private void BroadcastDialogueGrant(long now, List<PeerConnection> recipients)
    {
        List<uint> recipientIds = [.. recipients.Select(p => p.Id)];

        foreach (DialogueGrantPacket op in _dialogueGrantSender.CollectDue(recipientIds, now))
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
    /// A client's conversation just granted item(s) (and/or made a companion available) — echo
    /// an ack immediately, queue the grant for THIS instance's own player to receive (the host
    /// is a peer like any other here: it never ran the client's HandleReply either), and relay
    /// it to every other connected client under the client's TRUE (origin, sequence) identity
    /// (see <see cref="DialogueGrantSender.EnqueueRelay"/>) rather than re-numbering it the way
    /// <see cref="HandleContainerLoot"/> does — a re-attributed relay would look, to a receiver
    /// filtering by "is this my own echo", identical to a genuinely new host-originated grant.
    /// </summary>
    private void HandleDialogueGrant(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadDialogueGrant(span) is not { } grant)
        {
            _channel.NoteMalformed();
            return;
        }

        int length = PacketCodec.Write(
            _replyBuffer, new DialogueGrantAckPacket(HostPeerId, grant.OriginPeerId, grant.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }

        if (grant.OriginPeerId == HostPeerId)
        {
            // Should never happen — the host does not send itself packets — but a malformed or
            // spoofed origin must not be queued as if a peer had granted the host something.
            return;
        }

        lock (_gate)
        {
            _dialogueGrantInbox.Enqueue(grant);
        }

        _dialogueGrantSender.EnqueueRelay(grant, SessionClock.NowMs);
    }

    /// <summary>
    /// Broadcasts the conversation token every tick, unconditionally (even when idle), so a
    /// client that joined or reconnected mid-conversation converges on the current owner and
    /// a released token is not missed. One tiny packet per tick is negligible.
    /// </summary>
    private void BroadcastDialogue(long now, List<PeerConnection> recipients)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        DialogueState state;
        lock (_gate)
        {
            state = _dialogue;
        }

        int length = PacketCodec.Write(_sendBuffer, new DialogueSnapshotPacket(now, state));
        if (length == 0)
        {
            return;
        }

        foreach (PeerConnection peer in recipients)
        {
            _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void HandleDialogueRequest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadDialogueRequest(span) is not { } request)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            // Only a known peer may move the token, and only for its own peer id.
            if (_peers.ContainsKey(request.PeerId))
            {
                _dialogue = DialogueArbiter.Apply(
                    _dialogue, request.PeerId, request.Active, request.SpeakerName, request.Line,
                    request.PartnerTag, request.ReplySerial, request.ReplyIndex, request.PickLog,
                    request.NodeIndex, request.NodeSerial,
                    _interactionLocks.HolderOf(
                        _interactionModule, request.PartnerTag ?? string.Empty, SessionClock.NowMs),
                    request.Forced);
            }
        }
    }

    private void HandleDialogueGrantAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadDialogueGrantAck(span) is { } dialogueGrantAck)
        {
            _dialogueGrantSender.Acknowledge(
                dialogueGrantAck.OriginPeerId, dialogueGrantAck.OpSequence, dialogueGrantAck.PeerId);
        }
    }
}
