namespace KotorCoop.Plugin;

/// <summary>
/// Makes a conversation shared: when one player talks to an NPC, the other sees the same
/// conversation in the game's own dialogue UI, following along line for line, and cannot
/// touch the replies. The player who started it owns it until they close it.
///
/// It is the real conversation on both screens, not a transcript. The spectator opens the
/// identical dialogue against its own copy of the NPC (resolved by TAG — object ids are
/// per module load and would not match) and replays every reply slot the owner picks. Both
/// instances run the same .dlg from the same install, so the same slot walks the same
/// branch and the two conversations stay on the same node.
///
/// Two rules keep ownership honest:
///   * the spectator's ability to commit a reply is turned OFF for as long as it follows
///     (CanSendDialog), so only the owner's choices ever advance the tree;
///   * replies the spectator's own panel dispatches while replaying are not published back
///     (<see cref="DialogueReplyHook.Suppressed"/>), or the owner would receive its own pick
///     as a new one and advance twice.
///
/// Runs on the game's frame hook. Every engine read is guarded and every engine call is
/// bounds-checked; a conversation that cannot be resolved is skipped, never guessed at.
/// </summary>
internal sealed unsafe class DialogueSpectateController(SharedBlockView block)
{
    private bool _hadDialogue;
    private uint _lastLineHash;

    /// <summary>True while this instance is following a peer's conversation rather than
    /// running its own. Cleared the moment the owner's conversation ends.</summary>
    private bool _following;

    /// <summary>The owner's reply serial we last replayed, so each pick is replayed once.</summary>
    private uint _appliedReplySerial;

    /// <summary>Retry budget for opening the conversation: the NPC's twin may not exist for
    /// a frame or two, and a full creature sweep is too expensive to repeat forever.</summary>
    private int _openAttempts;

    private long _nextOpenAttemptMs;

    private long _followingSinceMs;

    /// <summary>How long a followed conversation may stay empty before we back out.</summary>
    private const long FollowContentTimeoutMs = 3000;

    private int _logged;

    /// <summary>Module changes destroy every creature and every conversation. Drop all of it
    /// rather than act on a tag that now resolves to a different body.</summary>
    public void Invalidate()
    {
        if (_following)
        {
            DialogueReplyHook.Suppressed = false;
            Odyssey.SetCanSendDialog(true);
        }

        _following = false;
        _hadDialogue = false;
        _lastLineHash = 0;
        _appliedReplySerial = 0;
        _openAttempts = 0;
    }

    public void OnFrame()
    {
        PublishLocal();
        FollowRemote();
    }

    /// <summary>Publishes what the local player is saying and picking, for a peer to follow.</summary>
    private void PublishLocal()
    {
        byte* speaker = block.LocalDialogueSpeaker;
        byte* line = block.LocalDialogueLine;

        bool active = Odyssey.TryReadActiveDialogue(
            speaker, SharedBlock.LocalDialogueSpeakerLength,
            line, SharedBlock.LocalDialogueLineLength);

        if (!active)
        {
            if (_hadDialogue)
            {
                _hadDialogue = false;
                _lastLineHash = 0;
                *speaker = 0;
                *line = 0;
                *block.LocalDialoguePartnerTag = 0;
                block.LocalDialogueSerial++;
            }

            return;
        }

        _hadDialogue = true;

        // Who we are talking to — the spectator opens the conversation against this.
        Odyssey.ReadDialoguePartnerTag(
            block.LocalDialoguePartnerTag, SharedBlock.LocalDialoguePartnerTagLength);

        // Replies WE picked. Suppressed while following, so a replayed pick is not echoed.
        if (!_following && DialogueReplyHook.Sequence != block.LocalDialogueReplySerial)
        {
            block.LocalDialogueReplyIndex = DialogueReplyHook.LastReplySlot;
            block.LocalDialogueReplySerial = DialogueReplyHook.Sequence;
        }

        uint hash = Hash(speaker, Hash(line, 2166136261u));
        if (hash != _lastLineHash)
        {
            _lastLineHash = hash;
            block.LocalDialogueSerial++;
        }
    }

    /// <summary>Opens and drives this instance's copy of a peer's conversation.</summary>
    private void FollowRemote()
    {
        if (!block.RemoteDialogueActive)
        {
            if (_following)
            {
                LogFew("dialogue: the owner ended the conversation; closing");
                _following = false;
                _appliedReplySerial = 0;
                _openAttempts = 0;
                DialogueReplyHook.Suppressed = false;
                Odyssey.SetCanSendDialog(true);
                Odyssey.CloseDialogue();
            }

            return;
        }

        // Someone else owns a conversation. If we are already in one of our own, leave it
        // alone — whoever got there first keeps their conversation, and the token arbiter
        // has already decided that this instance is not the owner.
        if (!_following)
        {
            if (_hadDialogue)
            {
                return;
            }

            if (Environment.TickCount64 < _nextOpenAttemptMs || _openAttempts >= MaxOpenAttempts)
            {
                return;
            }

            _openAttempts++;
            _nextOpenAttemptMs = Environment.TickCount64 + OpenRetryMs;

            ReadOnlySpan<byte> tag = TagSpan(block.RemoteDialoguePartnerTag);
            if (tag.IsEmpty)
            {
                return; // the owner has not published who yet
            }

            if (!Odyssey.StartDialogueWith(tag))
            {
                if (_openAttempts >= MaxOpenAttempts)
                {
                    LogFew("dialogue: could not open the peer's conversation locally; not following");
                }

                return;
            }

            _following = true;
            _followingSinceMs = Environment.TickCount64;
            _appliedReplySerial = block.RemoteDialogueReplySerial;
            LogFew("dialogue: following the peer's conversation");
        }

        // ⚠ Bail out of a conversation that opened but never filled in.
        //
        // Measured live: the spectator's request reaches the point where the client marks a
        // conversation active, but no entry ever arrives — no participants, no line, no
        // replies — leaving a frozen shell that is WORSE than showing nothing, because the
        // player's input class is in dialogue mode with nothing to click. Rather than trap
        // them there, give it a moment and then back out cleanly.
        if (!Odyssey.DialogueHasContent()
            && Environment.TickCount64 - _followingSinceMs > FollowContentTimeoutMs)
        {
            LogFew("dialogue: the followed conversation never delivered a line; backing out");
            _following = false;
            _openAttempts = MaxOpenAttempts; // do not thrash retrying the same failure
            DialogueReplyHook.Suppressed = false;
            Odyssey.SetCanSendDialog(true);
            Odyssey.CloseDialogue();
            return;
        }

        // While following: never allowed to choose, re-asserted every frame because whether
        // the engine restores this flag on its own was never established.
        DialogueReplyHook.Suppressed = true;
        Odyssey.SetCanSendDialog(false);

        // The owner picked: replay the same slot so our copy advances to the same node.
        uint serial = block.RemoteDialogueReplySerial;
        if (serial != _appliedReplySerial)
        {
            int slot = block.RemoteDialogueReplyIndex;
            if (Odyssey.SelectDialogueReply(slot))
            {
                _appliedReplySerial = serial;
                LogFew($"dialogue: followed the owner's reply {slot}");
            }
            else
            {
                // Our panel is not showing that slot (yet, or ever). Leave the serial
                // unapplied so the next frame retries — a line that arrived late is normal,
                // and dropping the pick would strand the two conversations apart.
                LogFew($"dialogue: reply {slot} not selectable yet; will retry");
            }
        }
    }

    /// <summary>Attempts to open the peer's conversation before giving up. The NPC's client
    /// twin can lag its creature by a frame or two, and each attempt is a full sweep.</summary>
    private const int MaxOpenAttempts = 10;

    private const long OpenRetryMs = 250;

    /// <summary>Bounds a null-terminated tag buffer into a span, empty if unset.</summary>
    private static ReadOnlySpan<byte> TagSpan(byte* tag)
    {
        int length = 0;
        while (length < SharedBlock.RemoteDialoguePartnerTagLength && tag[length] != 0)
        {
            length++;
        }

        return new ReadOnlySpan<byte>(tag, length);
    }

    private void LogFew(string message)
    {
        if (_logged < 40)
        {
            _logged++;
            PluginHost.Log(message);
        }
    }

    /// <summary>FNV-1a over a null-terminated buffer, seeded so speaker+line combine.</summary>
    private static uint Hash(byte* text, uint hash)
    {
        for (int i = 0; text[i] != 0 && i < 256; i++)
        {
            hash = (hash ^ text[i]) * 16777619u;
        }

        return hash;
    }
}
