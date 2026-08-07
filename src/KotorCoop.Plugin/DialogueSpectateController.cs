using KotorCoop.Protocol;

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

    /// <summary>When it appeared — the clock the content timeout runs on.</summary>
    private long _sawOpenAtMs;

    /// <summary>One content-latency report per follow.</summary>
    private bool _reportedContentMs;

    /// <summary>Rate limit on the yield close, which executes the conversation's end script.</summary>
    private long _nextYieldCloseMs;

    /// <summary>Yield closes issued for the conversation we are currently trying to give up.</summary>
    private int _yieldCloseAttempts;

    /// <summary>How long the request has to become a conversation. Generous: the server
    /// walks the player into range first, exactly as it does for a click.</summary>
    private const long FollowOpenTimeoutMs = 15000;

    /// <summary>How long an OPEN conversation may stay empty before we back out, counted
    /// from when it opened. Generous on purpose: backing out of a conversation that was
    /// merely slow is worse than waiting, and the log now reports the real latency so this
    /// can be set from measurement rather than from guessing.</summary>
    private const long FollowContentTimeoutMs = 8000;

    /// <summary>Pause after a failed follow before trying that conversation again.</summary>
    private const long ReopenCooldownMs = 4000;

    /// <summary>Pause between yield-closes of our own losing conversation.</summary>
    private const long YieldCloseRetryMs = 2000;

    /// <summary>Yield closes to attempt the proper (server) way before forcing the UI shut.</summary>
    private const int YieldCloseServerAttempts = 2;

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
        _sawOpenAtMs = 0;
        _reportedContentMs = false;
        _hadDialogue = false;
        _lastLiveMs = 0;
        _heldSpeaker[0] = 0;
        _heldLine[0] = 0;
        _lastLineHash = 0;
        _appliedReplySerial = 0;
        _openAttempts = 0;
        _nextYieldCloseMs = 0;
        _yieldCloseAttempts = 0;
        _followOpenPendingUntilMs = 0;
        DialogueGateHook.BypassGrants = 0;
        DialogueGateHook.PeerHoldsConversation = false;
    }

    public void OnFrame()
    {
        PublishLocal();
        FollowRemote();
    }

    /// <summary>
    /// The engine's <c>conversation active</c> flag drops for a few frames BETWEEN ENTRIES,
    /// and every read here goes through it. The agent has always debounced that gap
    /// (<c>CoopAgent.DialogueLingerMs</c>) so the session token does not flap; this side did
    /// not, so on every flicker it cleared the partner tag, wiped the whole pick log and
    /// bumped the serial twice — mid-conversation. The two halves now hold the same opinion
    /// of when a conversation is over, which is the only way the payload the follower needs
    /// survives an ordinary line change.
    /// </summary>
    private const long PublishLingerMs = 1500;

    private long _lastLiveMs;

    /// <summary>Last non-empty speaker/line, restored into the block across a flicker hold —
    /// <see cref="Odyssey.TryReadActiveDialogue"/> blanks its output buffers before it reads,
    /// so holding without this would publish an empty line for the gap.</summary>
    private readonly byte[] _heldSpeaker = new byte[SharedBlock.LocalDialogueSpeakerLength];
    private readonly byte[] _heldLine = new byte[SharedBlock.LocalDialogueLineLength];

    /// <summary>Publishes what the local player is saying and picking, for a peer to follow.</summary>
    private void PublishLocal()
    {
        byte* speaker = block.LocalDialogueSpeaker;
        byte* line = block.LocalDialogueLine;

        bool active = Odyssey.TryReadActiveDialogue(
            speaker, SharedBlock.LocalDialogueSpeakerLength,
            line, SharedBlock.LocalDialogueLineLength);

        long nowMs = Environment.TickCount64;
        if (active)
        {
            _lastLiveMs = nowMs;
            if (*speaker != 0 || *line != 0)
            {
                Remember(speaker, _heldSpeaker);
                Remember(line, _heldLine);
            }
        }
        else if (_hadDialogue && _lastLiveMs != 0 && nowMs - _lastLiveMs < PublishLingerMs)
        {
            // A gap between entries, not an ending. Republish what we last saw and change
            // nothing else — no serial bump, no cleared tag, no wiped pick log.
            Restore(_heldSpeaker, speaker);
            Restore(_heldLine, line);
            return;
        }

        if (!active)
        {
            if (_hadDialogue)
            {
                _hadDialogue = false;
                _lastLiveMs = 0;
                _heldSpeaker[0] = 0;
                _heldLine[0] = 0;
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

    /// <summary>Last <see cref="ShotCameraGuardHook.RefusedCount"/> we reported.</summary>
    private uint _seenShotRefusals;

    /// <summary>Last <see cref="DialogueGateHook.StartAttempts"/> we reported.</summary>
    private uint _seenStartAttempts;

    /// <summary>Shown when a click was refused and this instance IS being brought into the
    /// conversation anyway — which is the normal case, and the one the old flat refusal
    /// described as a dead end. "Another player is in a conversation" was all a player saw
    /// when they clicked an NPC to listen in, so the feature read as broken even on the runs
    /// where the follow then opened correctly.</summary>
    private readonly byte[] _joiningNotice = "Joining the other player's conversation..."u8.ToArray();

    /// <summary>Shown when the follow is genuinely not coming (retries exhausted).</summary>
    private readonly byte[] _notice = "Another player is in a conversation"u8.ToArray();

    /// <summary>Opens and drives this instance's copy of a peer's conversation.</summary>
    private void FollowRemote()
    {
        // Feed the engine-side gate: while a peer owns the token, StartDialog is refused
        // here (the BG3 rule). The one exception is our own follow of that conversation,
        // whose open arrives from the server several frames after we request it — so the
        // bypass is LEVEL-based: up from the follow request until the follow ends.
        DialogueGateHook.PeerHoldsConversation = block.RemoteDialogueActive;

        // The grant is issued at the request and consumed by the open (see BypassGrants).
        // Expire it if the open never arrives, so a stale grant cannot let an unrelated
        // conversation through minutes later.
        if (_followOpenPendingUntilMs != 0 && Environment.TickCount64 >= _followOpenPendingUntilMs)
        {
            _followOpenPendingUntilMs = 0;
            DialogueGateHook.BypassGrants = 0;
        }

        // A refused click deserves a visible answer, not silence: keep a short notice on
        // screen after each blocked attempt.
        if (DialogueGateHook.BlockedCount != _seenBlocked)
        {
            _seenBlocked = DialogueGateHook.BlockedCount;
            _blockedNoticeUntilMs = Environment.TickCount64 + 2500;
            LogFew("dialogue: blocked a local conversation start; a peer holds the token");
        }

        // Evidence that the crash window is still being entered at all. The guard itself
        // cannot log (it runs inside the engine's dialogue message handling), so its counter
        // is reported from here, on the frame hook.
        // Diagnostic for the joiner-cannot-talk defect: does the engine reach StartDialog at
        // all here? Reported from the frame hook because the detour itself must not log.
        if (DialogueGateHook.StartAttempts != _seenStartAttempts)
        {
            _seenStartAttempts = DialogueGateHook.StartAttempts;
            LogFew($"dialogue: engine reached StartDialog ({_seenStartAttempts} so far)");
        }

        if (ShotCameraGuardHook.RefusedCount != _seenShotRefusals)
        {
            _seenShotRefusals = ShotCameraGuardHook.RefusedCount;
            LogFew($"dialogue: shot camera refused a torn-down conversation ({_seenShotRefusals} so far)");
        }

        if (Environment.TickCount64 < _blockedNoticeUntilMs)
        {
            bool joining = _following
                || Environment.TickCount64 < _followOpenPendingUntilMs
                || _openAttempts < MaxOpenAttempts;

            fixed (byte* p = joining ? _joiningNotice : _notice)
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
                _reportedContentMs = false;
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
                // Both players standing in the same trigger get the SAME conversation from
                // the engine, independently — and the loser's copy is exactly the copy a
                // follow would have opened. Adopt it instead of closing and reopening: the
                // teardown ran the end script, and the reopen took the seconds the player
                // spent staring at an empty dialogue box with no subtitles. The owner's pick
                // log replays from the start against line hashes, which is the same
                // resynchronisation a cold follow performs, so an adopted conversation
                // converges the same way.
                if (SameTag(block.LocalDialoguePartnerTag, block.RemoteDialoguePartnerTag))
                {
                    LogFew("dialogue: the engine started the same conversation here; adopting it to follow");
                    _following = true;
                    _followingSinceMs = Environment.TickCount64;
                    _sawOpen = true;
                    _sawOpenAtMs = Environment.TickCount64;
                    _reportedContentMs = false;
                    _appliedReplySerial = 0;
                    _pendingPickSinceMs = 0;
                    _nextYieldCloseMs = 0;
                    _yieldCloseAttempts = 0;
                    return; // next frame drives it through the follow path
                }
                else
                {
                    // ⚠ ONCE, not every frame. CloseDialogue routes to
                    // CSWSObject::StopDialog, which runs the conversation's END SCRIPT — and
                    // an NPC whose end script re-initiates (the tutorial deck's Trask is one)
                    // turns a per-frame close into a conversation that opens and closes
                    // forever. The local conversation also takes a few frames to actually go,
                    // and _hadDialogue is debounced on top of that, so the flag is still set
                    // long after one close has landed.
                    if (Environment.TickCount64 >= _nextYieldCloseMs)
                    {
                        _nextYieldCloseMs = Environment.TickCount64 + YieldCloseRetryMs;
                        _yieldCloseAttempts++;

                        if (_yieldCloseAttempts <= YieldCloseServerAttempts)
                        {
                            LogFew("dialogue: a peer won the conversation token; closing our own to follow");
                            Odyssey.CloseDialogue();
                        }
                        else
                        {
                            // The server route has had its turns and this conversation is
                            // still here, which means there is nothing server-side left to
                            // end — a client-only shell. Shut the UI directly, or this
                            // instance sits out the whole conversation retrying forever.
                            LogFew($"dialogue: our conversation would not close after "
                                + $"{YieldCloseServerAttempts} tries; forcing the UI shut");
                            Odyssey.ForceCloseDialogueUi();
                        }
                    }

                    return; // follow once our own conversation has actually gone
                }
            }
            else
            {
                _nextYieldCloseMs = 0;
                _yieldCloseAttempts = 0;
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
            DialogueGateHook.BypassGrants = 1; // exactly this request, and nothing else

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
            _sawOpen = false;
            _sawOpenAtMs = 0;
            _reportedContentMs = false;

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
        long now = Environment.TickCount64;

        if (open && !_sawOpen)
        {
            _sawOpen = true;
            _sawOpenAtMs = now;

            // The open has landed, so any unused grant is spent: from here the spectator is
            // gated like anyone else, which is what stops a stray click inside the
            // conversation from knocking them out of it.
            _followOpenPendingUntilMs = 0;
            DialogueGateHook.BypassGrants = 0;
        }

        bool hasContent = open && Odyssey.DialogueHasContent();

        // How long content actually took, reported once per follow. This is the number the
        // content timeout should be set from, and nothing had ever measured it.
        if (hasContent && !_reportedContentMs)
        {
            _reportedContentMs = true;
            LogFew($"dialogue: followed conversation delivered content {now - _sawOpenAtMs} ms after opening "
                + $"({now - _followingSinceMs} ms after the request)");
        }

        DialogueFollowVerdict verdict = DialogueFollowClock.Evaluate(
            now, _followingSinceMs, _sawOpen ? _sawOpenAtMs : null, open, hasContent,
            FollowOpenTimeoutMs, FollowContentTimeoutMs);

        if (verdict is DialogueFollowVerdict.GiveUpNeverOpened or DialogueFollowVerdict.GiveUpNoContent)
        {
            LogFew(verdict == DialogueFollowVerdict.GiveUpNoContent
                ? "dialogue: the followed conversation never delivered a line; backing out"
                : "dialogue: the peer's conversation never opened locally; backing out");
            _following = false;
            _sawOpen = false;
            _reportedContentMs = false;
            _followOpenPendingUntilMs = 0;

            // ⚠ Back off, do NOT poison. This used to set _openAttempts to the cap, and the
            // cap is only cleared when the peer's conversation ENDS — so one timeout meant
            // the spectator ignored that conversation for as long as it ran, while the gate
            // kept refusing the player's own clicks with "another player is in a
            // conversation". A failure to follow is worth retrying a few times; it is not
            // worth locking the player out of the whole conversation.
            _nextOpenAttemptMs = Environment.TickCount64 + ReopenCooldownMs;
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

    /// <summary>Copies a null-terminated block buffer into managed storage, bounded.</summary>
    private static void Remember(byte* source, byte[] destination)
    {
        int i = 0;
        while (i < destination.Length - 1 && source[i] != 0)
        {
            destination[i] = source[i];
            i++;
        }

        destination[i] = 0;
    }

    /// <summary>The reverse, for republishing across a between-entries gap. The bound is
    /// <c>Length - 1</c> so the terminator has somewhere to go: stopping at Length would put
    /// it one byte PAST the block field, into whichever field follows.</summary>
    private static void Restore(byte[] source, byte* destination)
    {
        int i = 0;
        while (i < source.Length - 1 && source[i] != 0)
        {
            destination[i] = source[i];
            i++;
        }

        destination[i] = 0;
    }

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

    /// <summary>
    /// Whether two partner tags name the same NPC. Tags are ASCII and the engine treats them
    /// case-insensitively, so this does too. Two EMPTY tags are never "the same": an unset
    /// tag means nobody has published who they are talking to yet, and treating that as a
    /// match would adopt an unrelated conversation as the peer's.
    /// </summary>
    private static bool SameTag(byte* a, byte* b)
    {
        ReadOnlySpan<byte> left = TagSpan(a);
        ReadOnlySpan<byte> right = TagSpan(b);

        if (left.IsEmpty || right.IsEmpty || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (Lower(left[i]) != Lower(right[i]))
            {
                return false;
            }
        }

        return true;

        static byte Lower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;
    }

    /// <summary>Last line logged and when, so a repeating state does not spend the whole
    /// budget on one message and hide the next distinct thing that happens.
    ///
    /// ⚠ The repeat suppression is TIME-BOUNDED, not absolute. Absolute suppression hid a
    /// real defect: an instance retrying the same failed close every two seconds logged the
    /// attempt once and then went quiet, which reads exactly like a state that resolved.
    /// A loop should look like a loop in the log.</summary>
    private string _lastLogged = string.Empty;

    private long _lastLoggedAtMs;

    private const long RepeatAfterMs = 10000;

    private void LogFew(string message)
    {
        long now = Environment.TickCount64;
        if (message == _lastLogged && now - _lastLoggedAtMs < RepeatAfterMs)
        {
            return;
        }

        if (_logged < 60)
        {
            _logged++;
            _lastLogged = message;
            _lastLoggedAtMs = now;
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
