using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — The local half of the conversation token (sent to the host every tick, idle or
/// not) and this client's side of dialogue-triggered item/recruit grants — both
/// reliable where a grant is a one-shot fact that must survive a dropped packet.
/// </summary>
public sealed partial class CoopClient
{
    /// <summary>The host's authoritative conversation token, as last received.</summary>
    private DialogueState _dialogue = DialogueState.Idle;

    /// <summary>This client's local conversation status, sent to the host each tick.</summary>
    private bool _localDialogueActive;
    private string _localDialogueSpeaker = string.Empty;
    private string _localDialogueLine = string.Empty;
    private string _localDialoguePartnerTag = string.Empty;
    private uint _localDialogueReplySerial;
    private int _localDialogueReplyIndex;
    private byte[]? _localDialoguePickLog;
    private int _localDialogueNode = DialogueState.NoNode;
    private uint _localDialogueNodeSerial;
    private bool _localDialogueForced;
    private string _localDialogueResref = string.Empty;

    public void PublishDialogue(
        bool active, string speakerName, string line,
        string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
        int nodeIndex = DialogueState.NoNode, uint nodeSerial = 0, bool forced = false)
        => PublishDialogueWithResref(active, speakerName, line, partnerTag, replySerial,
            replyIndex, pickLog, nodeIndex, nodeSerial, forced, string.Empty);

    public void PublishDialogueWithResref(
        bool active, string speakerName, string line, string partnerTag,
        uint replySerial, int replyIndex, byte[]? pickLog, int nodeIndex,
        uint nodeSerial, bool forced, string dialogResref)
    {
        lock (_gate)
        {
            _localDialogueActive = active;
            _localDialogueSpeaker = speakerName ?? string.Empty;
            _localDialogueLine = line ?? string.Empty;
            _localDialoguePartnerTag = partnerTag ?? string.Empty;
            _localDialogueReplySerial = replySerial;
            _localDialogueReplyIndex = replyIndex;
            _localDialoguePickLog = pickLog;
            _localDialogueNode = nodeIndex;
            _localDialogueNodeSerial = nodeSerial;
            _localDialogueForced = forced;
            _localDialogueResref = dialogResref;
        }
    }

    public DialogueState GetDialogue()
    {
        lock (_gate)
        {
            return _dialogue;
        }
    }

    // ---- dialogue item/recruit grants ----------------------------------------------------

    private readonly DialogueGrantSender _dialogueGrantSender = new();

    /// <summary>Grants a peer's conversation reported, relayed by the host, still owed a give
    /// to THIS instance's own player. Self-origin echoes are filtered out in
    /// <see cref="HandleDialogueGrant"/>, before they ever reach this queue.</summary>
    private readonly Queue<DialogueGrantPacket> _dialogueGrantInbox = new();

    /// <summary>(origin, sequence) pairs already queued or discarded this session. Unlike
    /// <see cref="_materialisedLootSeen"/>, this never needs a module-change reset: a
    /// dialogue-grant sequence is a peer's own ever-increasing counter for the life of the
    /// connection, not an object id the engine reassigns per module load.</summary>
    private readonly HashSet<(uint Origin, uint Sequence)> _dialogueGrantSeen = [];

    /// <summary>This instance's own conversation just granted something; report it to the
    /// host, which relays to every other peer. Queued under the last admitted identity, like
    /// <see cref="PublishContainerLoot"/>.</summary>
    public void PublishDialogueGrant(string partnerTag, IReadOnlyList<MaterialisedLootItem> items)
    {
        if (_sessionPeerId == 0)
        {
            return;
        }

        _dialogueGrantSender.EnqueueLocal(_sessionPeerId, partnerTag, items, SessionClock.NowMs);
    }

    /// <summary>Every dialogue-grant this instance still owes a give for, oldest first. Matches
    /// <see cref="DrainMaterialisedLoot"/>'s shape: this layer only receives, dedups and
    /// acknowledges; the agent pump applies.</summary>
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

    private void SendDialogueRequest()
    {
        bool active;
        string speaker;
        string line;
        string partnerTag;
        uint replySerial;
        int replyIndex;
        byte[]? pickLog;
        int nodeIndex;
        uint nodeSerial;
        bool forced;
        string dialogResref;
        lock (_gate)
        {
            active = _localDialogueActive;
            speaker = _localDialogueSpeaker;
            line = _localDialogueLine;
            partnerTag = _localDialoguePartnerTag;
            replySerial = _localDialogueReplySerial;
            replyIndex = _localDialogueReplyIndex;
            pickLog = _localDialoguePickLog;
            nodeIndex = _localDialogueNode;
            nodeSerial = _localDialogueNodeSerial;
            forced = _localDialogueForced;
            dialogResref = _localDialogueResref;
        }

        int length = PacketCodec.Write(
            _sendBuffer,
            new DialogueRequestPacket(
                _localPeerId, active, speaker, line, partnerTag, replySerial, replyIndex, pickLog,
                nodeIndex, nodeSerial, forced, dialogResref));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    /// <summary>
    /// Records a dialogue grant relayed by the host and acks it unconditionally, like
    /// <see cref="HandleMaterialisedLoot"/> — a duplicate arriving means our previous ack was
    /// lost, not that the conversation ran twice.
    ///
    /// ⚠ SELF-ORIGIN IS FILTERED HERE, not left for the agent pump to notice. The host relays
    /// under the TRUE origin rather than re-attributing to itself (see
    /// <see cref="CoopHost.HandleDialogueGrant"/>), so a report THIS instance sent comes back
    /// over the same broadcast every other client receives it on. A container-loot echo is
    /// harmless because removing an already-removed item is a no-op; giving an already-given
    /// item is not (<c>GiveItemToInventory</c> mints a fresh one every call), so the echo must
    /// never reach the drain queue.
    /// </summary>
    private void HandleDialogueGrant(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadDialogueGrant(span) is not { } grant)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (grant.OriginPeerId != _localPeerId && _dialogueGrantSeen.Add((grant.OriginPeerId, grant.OpSequence)))
            {
                _dialogueGrantInbox.Enqueue(grant);
            }
        }

        int length = PacketCodec.Write(
            _replyBuffer, new DialogueGrantAckPacket(_localPeerId, grant.OriginPeerId, grant.OpSequence));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
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

    private void HandleDialogueSnapshot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadDialogueSnapshot(span) is { } dialogue)
        {
            lock (_gate)
            {
                _dialogue = dialogue.State;
            }
        }
    }
}
