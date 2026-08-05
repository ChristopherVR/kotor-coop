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

    /// <summary>While now is before this, a follow-open we requested is still in flight and
    /// the dialogue gate must let StartDialog through for it.</summary>
    private long _followOpenPendingUntilMs;

    private long _followingSinceMs;

    /// <summary>Set once the followed conversation has actually appeared on screen.</summary>
    private bool _sawOpen;

    /// <summary>How long the request has to become a conversation. Generous: the server
    /// walks the player into range first, exactly as it does for a click.</summary>
    private const long FollowOpenTimeoutMs = 15000;

    /// <summary>How long an OPEN conversation may stay empty before we back out.</summary>
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
        _sawOpen = false;
        _hadDialogue = false;
        _lastLineHash = 0;
        _appliedReplySerial = 0;
        _openAttempts = 0;
        _followOpenPendingUntilMs = 0;
        DialogueGateHook.Bypass = false;
        DialogueGateHook.PeerHoldsConversation = false;
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

                // The pick log is per conversation: stale picks surviving into the next one
                // would replay against lines they were never made on.
                for (int i = 0; i < SharedBlock.DialoguePickCapacity; i++)
                {
                    *(uint*)block.LocalDialoguePick(i) = 0;
                }
            }

            return;
        }

        _hadDialogue = true;

        // Who we are talking to — the spectator opens the conversation against this.
        Odyssey.ReadDialoguePartnerTag(
            block.LocalDialoguePartnerTag, SharedBlock.LocalDialoguePartnerTagLength);

        // Replies WE picked, appended to the pick LOG with the hash of the line they were
        // picked on. A pick is only meaningful on its line: replaying a slot number while
        // the spectator's screen shows a different node walks them down the wrong branch,
        // and a spectator that joined late needs the earlier picks too — one latest value
        // cannot carry either. (Not recorded while following: those are replays, not ours.)
        if (!_following && DialogueReplyHook.Sequence != block.LocalDialogueReplySerial)
        {
            uint lineHash = Hash(line, 2166136261u);
            AppendLocalPick(DialogueReplyHook.Sequence, DialogueReplyHook.LastReplySlot, lineHash);

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

    /// <summary>Shifts the pick log up and writes the newest entry at the end, so the block
    /// always carries the most recent <see cref="SharedBlock.DialoguePickCapacity"/> picks
    /// in order.</summary>
    private void AppendLocalPick(uint serial, int slot, uint lineHash)
    {
        for (int i = 0; i < SharedBlock.DialoguePickCapacity - 1; i++)
        {
            byte* from = block.LocalDialoguePick(i + 1);
            byte* to = block.LocalDialoguePick(i);
            *(uint*)to = *(uint*)from;
            *(uint*)(to + 4) = *(uint*)(from + 4);
            *(uint*)(to + 8) = *(uint*)(from + 8);
        }

        byte* last = block.LocalDialoguePick(SharedBlock.DialoguePickCapacity - 1);
        *(uint*)(last + 4) = (uint)slot;
        *(uint*)(last + 8) = lineHash;
        *(uint*)last = serial; // serial last: a half-written entry reads as empty
    }

    /// <summary>Last <see cref="DialogueGateHook.BlockedCount"/> we showed feedback for.</summary>
    private uint _seenBlocked;

    private long _blockedNoticeUntilMs;

    private readonly byte[] _notice = "Another player is in a conversation"u8.ToArray();

    /// <summary>Opens and drives this instance's copy of a peer's conversation.</summary>
    private void FollowRemote()
    {
        // Feed the engine-side gate: while a peer owns the token, StartDialog is refused
        // here (the BG3 rule). The one exception is our own follow of that conversation,
        // whose open arrives from the server several frames after we request it — so the
        // bypass is LEVEL-based: up from the follow request until the follow ends.
        DialogueGateHook.PeerHoldsConversation = block.RemoteDialogueActive;
        DialogueGateHook.Bypass =
            _following || Environment.TickCount64 < _followOpenPendingUntilMs;

        // A refused click deserves a visible answer, not silence: keep a short notice on
        // screen after each blocked attempt.
        if (DialogueGateHook.BlockedCount != _seenBlocked)
        {
            _seenBlocked = DialogueGateHook.BlockedCount;
            _blockedNoticeUntilMs = Environment.TickCount64 + 2500;
            LogFew("dialogue: blocked a local conversation start; a peer holds the token");
        }

        if (Environment.TickCount64 < _blockedNoticeUntilMs)
        {
            fixed (byte* p = _notice)
            {
                Odyssey.PostTextLine(p, 8, 4, 0.5f);
            }
        }

        if (!block.RemoteDialogueActive)
        {
            if (_following)
            {
                LogFew("dialogue: the owner ended the conversation; closing");
                _following = false;
                _sawOpen = false;
                _appliedReplySerial = 0;
                _followOpenPendingUntilMs = 0;
                DialogueReplyHook.Suppressed = false;
                Odyssey.SetCanSendDialog(true);
                Odyssey.CloseDialogue();
            }

            // The retry budget is PER CONVERSATION. Resetting it only after a successful
            // follow left it spent forever once one open failed — measured live as the
            // spectator silently ignoring every later conversation in the module.
            _openAttempts = 0;
            return;
        }

        // Someone else owns a conversation while we are mid-one of our OWN: we lost the
        // race (both sides clicked inside the same token round-trip — the engine-side gate
        // cannot see a token that has not arrived yet). One conversation for the whole
        // session means ours yields: close it and follow the winner's instead. Before this,
        // the loser kept a private conversation whose plot flags raced the owner's.
        if (!_following)
        {
            if (_hadDialogue)
            {
                LogFew("dialogue: a peer won the conversation token; closing our own to follow");
                Odyssey.CloseDialogue();
                return; // follow once our own conversation has actually gone
            }

            if (Environment.TickCount64 < _nextOpenAttemptMs || _openAttempts >= MaxOpenAttempts)
            {
                return;
            }

            // ⚠ An empty tag is not an attempt. The token can go active a second or more
            // before the owner's client resolves WHO they are talking to (the server walks
            // them into range first), and counting those ticks against the budget burned all
            // ten attempts before the tag ever arrived — measured live as the spectator
            // never opening a conversation that then sat fully published in its block.
            ReadOnlySpan<byte> tag = TagSpan(block.RemoteDialoguePartnerTag);
            if (tag.IsEmpty)
            {
                return; // the owner has not published who yet
            }

            _openAttempts++;
            _nextOpenAttemptMs = Environment.TickCount64 + OpenRetryMs;

            // Open the bypass BEFORE requesting: the request queues a server action and the
            // engine only reaches StartDialog frames later (it walks the player into range
            // first), so a scoped bypass around this call would miss its own open. The
            // level-based bypass at the top of this method stays up for as long as the
            // follow is pending or live.
            _followOpenPendingUntilMs = Environment.TickCount64 + FollowOpenTimeoutMs;
            DialogueGateHook.Bypass = true;

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

            // Start from zero so the ENTIRE pick log replays: a spectator that joined after
            // the owner had already picked catches up pick by pick, each on its own line.
            _appliedReplySerial = 0;
            _pendingPickSinceMs = 0;
            LogFew("dialogue: following the peer's conversation");
        }

        // ⚠ "Requested" and "on screen" are different states, with different patience.
        //
        // The request goes to the server, which resolves the participants, walks the player
        // into conversation range and only then opens the conversation — the same approach a
        // click performs. Treating the gap as failure closed conversations that were still
        // arriving (measured: participants had resolved correctly and it was shut anyway).
        // So a conversation that has not opened yet gets a long budget; one that HAS opened
        // but stays empty gets a short one, because that is the frozen shell worth escaping.
        bool open = Odyssey.IsConversationActive;
        long waited = Environment.TickCount64 - _followingSinceMs;

        if (open)
        {
            _sawOpen = true;
        }

        bool giveUp = _sawOpen
            ? open && !Odyssey.DialogueHasContent() && waited > FollowContentTimeoutMs
            : waited > FollowOpenTimeoutMs;

        if (giveUp)
        {
            LogFew(_sawOpen
                ? "dialogue: the followed conversation never delivered a line; backing out"
                : "dialogue: the peer's conversation never opened locally; backing out");
            _following = false;
            _sawOpen = false;
            _followOpenPendingUntilMs = 0;
            _openAttempts = MaxOpenAttempts; // do not thrash retrying the same failure
            DialogueReplyHook.Suppressed = false;
            Odyssey.SetCanSendDialog(true);
            Odyssey.CloseDialogue();
            return;
        }

        if (!open)
        {
            return; // still on its way; nothing to drive yet
        }

        // While following: never allowed to choose, re-asserted every frame because whether
        // the engine restores this flag on its own was never established.
        DialogueReplyHook.Suppressed = true;
        Odyssey.SetCanSendDialog(false);

        // Replay the owner's picks IN ORDER, each one once our own screen shows the line it
        // was picked on. Entries auto-advance on both sides and a reply prompt WAITS, so the
        // follower naturally arrives at each pick's line and stops there; matching on the
        // line hash is what keeps a pick from firing on the wrong node. A pick whose line
        // never appears (a VO skipped locally, a hash rarity) is applied anyway after a
        // grace period — best effort beats stranding the conversation.
        byte* next = null;
        for (int i = 0; i < SharedBlock.DialoguePickCapacity; i++)
        {
            byte* entry = block.RemoteDialoguePick(i);
            uint serial = *(uint*)entry;
            if (serial != 0 && serial > _appliedReplySerial)
            {
                next = entry; // the log is ordered; the first unapplied entry is the oldest
                break;
            }
        }

        if (next == null)
        {
            _pendingPickSinceMs = 0;
            return;
        }

        if (_pendingPickSinceMs == 0)
        {
            _pendingPickSinceMs = Environment.TickCount64;
        }

        uint pickSerial = *(uint*)next;
        int slot = (int)*(uint*)(next + 4);
        uint pickLineHash = *(uint*)(next + 8);

        uint myLineHash = Hash(block.LocalDialogueLine, 2166136261u);
        bool onThatLine = myLineHash == pickLineHash;
        bool overdue = Environment.TickCount64 - _pendingPickSinceMs > PickGraceMs;

        if ((onThatLine || overdue) && Odyssey.SelectDialogueReply(slot))
        {
            _appliedReplySerial = pickSerial;
            _pendingPickSinceMs = 0;
            LogFew($"dialogue: followed the owner's reply {slot}{(onThatLine ? "" : " (line unmatched, applied late)")}");
        }
    }

    /// <summary>How long a pick may wait for its line before it is applied regardless.</summary>
    private const long PickGraceMs = 8000;

    private long _pendingPickSinceMs;

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
