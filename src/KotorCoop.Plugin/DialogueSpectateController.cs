using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// Makes a conversation shared: when one player talks to an NPC, the other sees the same
/// conversation in the game's own dialogue UI, following along line for line, and cannot
/// touch the replies. The player who started it owns it until they close it.
///
/// It is the real conversation on both screens, not a transcript. The spectator opens the
/// identical dialogue against its own copy of the NPC (resolved by TAG — object ids are per
/// module load and would not match) and is then driven to whichever NODE the owner is on:
/// the index of the dialogue entry showing, sent through the engine's own entry producer, so
/// the line, its voice-over and its lipsync all arrive together.
///
/// ⚠ It does NOT replay the reply slot the owner pressed. That is what it used to do, and it
/// could not work: a slot is a property of one instance's evaluation of the conversation, not
/// of the conversation. Which entry it starts on, which replies are offered and where a reply
/// leads are each chosen by scripts that run separately on each machine. An entry index is a
/// plain array position in a file both instances loaded, and it is the engine's own notion of
/// where a conversation is — see the node-sync note in <see cref="Odyssey"/> for the argument
/// and the addresses.
///
/// Two rules keep ownership honest:
///   * the spectator's ability to commit a reply is turned OFF for as long as it follows
///     (CanSendDialog), so only the owner's choices ever advance the tree;
///   * a follower publishes neither picks nor its own node, or the two instances would drive
///     each other in a circle.
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

    /// <summary>The owner's node serial we have already been driven to, so one node change
    /// is acted on once. Zero means "drive me wherever the owner is", which is exactly what a
    /// fresh follow wants — a joiner lands on the owner's CURRENT line rather than replaying
    /// its way there.</summary>
    private uint _appliedNodeSerial;

    /// <summary>"No node." Mirrors <c>DialogueState.NoNode</c> on the wire; the plugin does
    /// not reference the protocol assembly.</summary>
    private const int NoNode = -1;

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

    /// <summary>When the local conversation first read ACTIVE without naming a partner.</summary>
    private long _localUnidentifiedSinceMs;

    /// <summary>How long a local conversation may stay unidentified before it counts as a
    /// competitor. Same budget as a follow-open, for the same reason: the server walks the
    /// player the whole way into range before the conversation exists.</summary>
    private const long LocalOpenGraceMs = FollowOpenTimeoutMs;

    /// <summary>How long an OPEN conversation may stay empty before we back out, counted
    /// from when it opened. Generous on purpose: backing out of a conversation that was
    /// merely slow is worse than waiting, and the log now reports the real latency so this
    /// can be set from measurement rather than from guessing.</summary>
    private const long FollowContentTimeoutMs = 8000;

    /// <summary>Pause after a failed follow before trying that conversation again.</summary>
    private const long ReopenCooldownMs = 4000;

    /// <summary>Pause between attempts to end an empty conversation shell.</summary>
    private const long UnstickRetryMs = 3000;

    private long _nextUnstickMs;

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
        _appliedNodeSerial = 0;

        // A node index names an entry in ONE .dlg. Carrying one across a module change points
        // it at a different file's entry list, so it goes with everything else here.
        block.LocalDialogueNode = NoNode;
        block.LocalDialogueNodeSerial++;

        _nextYieldCloseMs = 0;
        _yieldCloseAttempts = 0;
        _localUnidentifiedSinceMs = 0;
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
                _localUnidentifiedSinceMs = 0;
                _heldSpeaker[0] = 0;
                _heldLine[0] = 0;
                _lastLineHash = 0;
                *speaker = 0;
                *line = 0;
                *block.LocalDialoguePartnerTag = 0;
                block.LocalDialogueSerial++;

                // The node is per conversation, like the pick log: a stale index surviving
                // into the next one names an entry in a different .dlg entirely.
                block.LocalDialogueNode = NoNode;
                block.LocalDialogueNodeSerial++;

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

        // ⚠ WHERE WE ARE — the thing a follower is actually driven by.
        //
        // The index of the entry on screen, which is a plain array position in the .dlg both
        // instances loaded and therefore means the same thing on both.
        //
        // Written EVEN WHILE FOLLOWING, unlike the pick log below. The feedback loop this
        // looks like it would create is already cut a layer up: the agent publishes nothing
        // from an instance that is following a peer's token (CoopAgent.PumpDialogue's
        // `rawInDialogue = local.IsInDialogue && !followingPeer`), so a follower's node never
        // reaches the wire whatever this writes. Guarding here as well cost the one thing
        // that makes the whole mechanism observable: with it in place the field froze at
        // whatever the follower published just before it adopted, so a probe watching the two
        // nodes converge saw a stale number and could not tell a working drive from a broken
        // one. Measured — it read 19 for thirty seconds after being driven to 29.
        //
        // A read of -1 is never published over a good node while the conversation is still
        // live. The engine's active flag drops for a few frames between entries and the node
        // reads unknown for that gap; writing it through would tell the follower "I have lost
        // my place" on every line change, which is the exact moment it most needs a node. The
        // clear happens once, at the end of the conversation, in the !active branch above.
        int node = Odyssey.GetDialogueNodeIndex();
        if (node >= 0 && node != block.LocalDialogueNode)
        {
            block.LocalDialogueNode = node;
            block.LocalDialogueNodeSerial++; // serial last: the far side reads it first
        }

        // Replies WE picked, appended to the pick LOG with the hash of the line they were
        // picked on.
        //
        // ⚠ DIAGNOSTIC ONLY NOW — nothing is driven from this. It says what the owner
        // pressed, which is worth having in a log when a conversation goes wrong. It is NOT
        // what the follower replays, and reinstating that would undo the node drive: see the
        // node-sync note in Odyssey for why a reply slot cannot mean the same thing on two
        // machines. (Not recorded while following: those are replays, not ours.)
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

    /// <summary>Distinct gui+0xB4 writers already reported.</summary>
    private int _seenDialogStateWriters;

    /// <summary>
    /// Shown when a click on someone ELSE was refused because a peer owns the conversation.
    /// It names the way in rather than only the refusal: the player is not stuck, they are
    /// one click away from the same conversation.
    ///
    /// ⚠ There is no "Joining..." notice any more. There used to be, because this instance
    /// opened the peer's conversation by itself — which is exactly the forced join that was
    /// reported from play, and the message was the only sign of it. Joining is a click now,
    /// so the only thing worth saying is which NPC to click.
    /// </summary>
    private readonly byte[] _notice = "Another player is in a conversation - click the same character to join"u8.ToArray();

    /// <summary>Opens and drives this instance's copy of a peer's conversation.</summary>
    private void FollowRemote()
    {
        // Feed the engine-side gate: while a peer owns the token, StartDialog is refused
        // here (the BG3 rule). The one exception is our own follow of that conversation,
        // whose open arrives from the server several frames after we request it — so the
        // bypass is LEVEL-based: up from the follow request until the follow ends.
        //
        // ⚠ THE GATE ONLY CLOSES ONCE WE KNOW WHO THE PEER IS TALKING TO.
        //
        // The gate's whole job is to tell a JOIN (a click on the peer's own partner, which
        // must pass) from a competing conversation (which must not). With an EMPTY partner tag
        // it can do neither: SameTag refuses an empty tag by design, so every start was
        // refused — including the join the player was trying to make. And the tag is empty for
        // a real window at the start of every conversation, because the participant ids at
        // gui+0x170/0x174 stay OBJECT_INVALID until the server has resolved them. Measured in
        // play as a click at :23 doing nothing and the identical click at :26 working.
        //
        // So while the peer's identity is unknown, do not gate at all. The cost of letting a
        // competing start through for those few hundred milliseconds is bounded and already
        // handled: the adopt/yield path below closes our own conversation the moment the tags
        // resolve and disagree. The cost of refusing everything is a player who cannot join.
        bool peerPartnerKnown = block.RemoteDialogueActive && *block.RemoteDialoguePartnerTag != 0;

        DialogueGateHook.PeerHoldsConversation = peerPartnerKnown;

        // A pointer into the shared block, which outlives every frame.
        DialogueGateHook.PeerPartnerTag = peerPartnerKnown
            ? (nint)block.RemoteDialoguePartnerTag
            : 0;

        // ⚠ A LEVEL, RE-DECIDED EVERY FRAME — never a latch.
        //
        // It is cleared here and set true at exactly one place below, the point where a
        // follow is genuinely live and on screen. It used to be set true while following and
        // cleared on three named exits, and the exits did not cover the ways a follow really
        // ends: our own conversation closing locally, or the peer staying in one while ours
        // never reopened, both return early WITHOUT passing a clear. A stuck flag is not a
        // cosmetic bug — DialogueReplyHook swallows CSWGuiDialog::SelectReply outright, so
        // the player can never answer ANY conversation again, including their own, and the
        // dialogue just sits there. Reported from play as the client's dialogue not showing
        // its questions and then taking seconds to do anything.
        DialogueReplyHook.Suppressed = false;

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

        // Who writes CGuiInGame+0xB4, reported from here because the detour itself must not
        // log. One line per distinct (call site, value) pair — the question is WHICH sites
        // fire, and a per-frame loop logged per call is what buried this the first time.
        if (DialogStateWatchHook.Enabled && DialogStateWatchHook.Known != _seenDialogStateWriters)
        {
            for (int i = _seenDialogStateWriters; i < DialogStateWatchHook.Known; i++)
            {
                LogFew($"dialogue: gui+0xB4 set to {DialogStateWatchHook.ValueAt(i)} "
                    + $"from 0x{DialogStateWatchHook.CallerAt(i):X} "
                    + $"({DialogStateWatchHook.CountAt(i)} so far)");
            }

            _seenDialogStateWriters = DialogStateWatchHook.Known;
        }

        if (ShotCameraGuardHook.RefusedCount != _seenShotRefusals)
        {
            _seenShotRefusals = ShotCameraGuardHook.RefusedCount;
            LogFew($"dialogue: shot camera refused a torn-down conversation ({_seenShotRefusals} so far)");
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
                _reportedContentMs = false;
                _appliedNodeSerial = 0;
                _followOpenPendingUntilMs = 0;
                Odyssey.CloseDialogue();
            }

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
                // ⚠ AN UNIDENTIFIED LOCAL CONVERSATION IS NOT A COMPETITOR — IT IS USUALLY
                // THIS PLAYER'S OWN CLICK-TO-JOIN, STILL IN FLIGHT. This is why click-to-join
                // never worked, through three passes that went looking for an engine guard
                // refusing it. Nothing refuses it. We annulled it ourselves, one frame after
                // the click.
                //
                // gui+0xB4 goes up the INSTANT the player clicks, seconds before any
                // conversation exists:
                //   * CSWCCreature::ActionInitiateDialog (0x0060F620) calls
                //     SetGlobalDialogState(1) at 0x0060F76D, straight after
                //     SendPlayerToServerInput_Dialog — on the click itself;
                //   * CSWSObject::AIActionDialogObject (0x0057A470) sets it again at
                //     0x0057A70E, which is BEFORE its range test (0x0057A7AC / 0x0057A923),
                //     and re-queues itself to the front for every tick the server spends
                //     walking the player into range.
                //
                // For that whole window gui+0x170/0x174 are still OBJECT_INVALID, so
                // ReadDialoguePartnerTag publishes an EMPTY tag; SameTag refuses an empty tag
                // by design; and the yield-close below therefore ran on the FIRST FRAME after
                // the click, with _nextYieldCloseMs starting at 0. CloseDialogue found no
                // resolvable participant and went straight to CGuiInGame::CloseDialog, which
                // clears the flag again at 0x00633423. DialogueGateHook never ran at all —
                // which is exactly why gui+0xB4 kept measuring 0 on the joiner and why gate
                // hunting found nothing refusing anything.
                //
                // So wait for the conversation to say who it is. The moment the tag resolves,
                // the tests below run unchanged and a genuine competing conversation is still
                // yielded at the first instant it is identifiable.
                if (*block.LocalDialoguePartnerTag == 0)
                {
                    if (_localUnidentifiedSinceMs == 0)
                    {
                        _localUnidentifiedSinceMs = Environment.TickCount64;
                    }

                    if (Environment.TickCount64 - _localUnidentifiedSinceMs < LocalOpenGraceMs)
                    {
                        return; // still arriving — do not tear down our own request
                    }
                }
                else
                {
                    _localUnidentifiedSinceMs = 0;
                }

                if (SameTag(block.LocalDialoguePartnerTag, block.RemoteDialoguePartnerTag))
                {
                    _localUnidentifiedSinceMs = 0;
                    LogFew("dialogue: the engine started the same conversation here; adopting it to follow");
                    _following = true;
                    _followingSinceMs = Environment.TickCount64;
                    _sawOpen = true;
                    _sawOpenAtMs = Environment.TickCount64;
                    _reportedContentMs = false;

                    // ⚠ Start from where the owner IS — which is now simply what a zero here
                    // MEANS, rather than something that had to be computed.
                    //
                    // Under pick replay this had to skip the owner's history by hand, or the
                    // follower fired every pick already made, back to back, and walked through
                    // several nodes in a second (the conversation "skipping"). Position has no
                    // history to skip: an unapplied serial drives us straight to the single
                    // node the owner is on, so a mid-conversation joiner lands on the line
                    // being spoken and nowhere else.
                    _appliedNodeSerial = 0;
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
                _localUnidentifiedSinceMs = 0;
            }

            // ⚠ UNSTICK A STALE CONVERSATION SHELL, or the player can never join anything.
            //
            // Verified in the binary: CSWCMessage::SendPlayerToServerInput_Dialog 0x00677EA0
            // reads [gui+0xB4] at 0x00677EB3 and, if it is set, JUMPS STRAIGHT TO ITS EXIT.
            // That call is the tail of the ordinary click path too, so while that flag is up
            // this instance cannot start a conversation with anybody — the request is dropped
            // before the engine ever reaches StartDialog, which is why the dialogue gate's
            // attempt counter never moved and the join looked refused when nothing had
            // refused it.
            //
            // A client can be left holding a shell like that: its server half still has an
            // unfinished conversation and re-sets the flag every frame, with the GUI's
            // participants both reading OBJECT_INVALID (see Odyssey.CloseDialogue). Ending it
            // is what makes the next click work. Rate-limited, because closing runs the
            // conversation's end script.
            if (Odyssey.IsConversationActive && !Odyssey.DialogueHasContent()
                && Environment.TickCount64 >= _nextUnstickMs)
            {
                _nextUnstickMs = Environment.TickCount64 + UnstickRetryMs;
                LogFew("dialogue: an empty conversation shell is blocking new ones here; ending it "
                    + "so a click can join the peer's");
                Odyssey.CloseDialogue();
                return;
            }

            // ⚠ NOTHING IS OPENED HERE, DELIBERATELY.
            //
            // This used to request the peer's conversation itself, so a player standing
            // anywhere in the module was yanked into a dialogue they had not asked for —
            // reported from play, and the wrong shape for a co-op game besides: walking away
            // while your partner talks to a shopkeeper is a legitimate thing to want.
            //
            // Joining is now the player's own click. The gate hook lets a start through when
            // the clicked NPC is the one the peer is talking to, the engine opens the same
            // conversation locally, and the `_hadDialogue` branch above adopts it as a follow
            // — the same adopt path that already handled two players clicking at once, which
            // is why this is a deletion rather than a new mechanism. Anyone the player clicks
            // who is NOT the peer's partner is still refused, so the session keeps exactly
            // one conversation.
            return;
        }

        // ⚠ SUPPRESSED THE MOMENT WE ARE FOLLOWING — not once the conversation is on screen.
        //
        // This assertion used to sit AFTER the `if (!open) return;` below, which meant the
        // whole join window ran unsuppressed: the follower's conversation takes up to
        // FollowOpenTimeoutMs to arrive (the server walks the player into range first), and
        // for every one of those seconds the joiner could answer. Both players could pick, on
        // two copies of the same conversation, and the owner's copy was the only one anyone
        // else could see — so the second player's answers went into a branch nobody was
        // watching. That is the "both players can still click the options" report, and it was
        // never a hole in the reply hook; it was this line being one early-return too low.
        //
        // It stays a LEVEL, re-decided from false at the top of every frame (see the note
        // there). Asserting it here rather than at the bottom widens the window it covers
        // without making it a latch.
        DialogueReplyHook.Suppressed = true;

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

            // ⚠ NO SetCanSendDialog HERE, IN EITHER DIRECTION — this used to say `true`, and
            // that is not a restore, it is a fresh one-second gag on the voice-over.
            //
            // CClientExoAppInternal::SetCanSendDialog (0x005F3030, ret 4) writes the flag at
            // +0x228 AND STAMPS +0x22C WITH THE CURRENT WORLD TIME (0x005F305F).
            // GetCanSendDialog (0x005F3070, ret 0xC) then refuses while
            // `now - [+0x22C] <= 1000` (0x005F316C jbe -> 0x005F32C8 `xor eax,eax; ret 0xC`),
            // and on success back-dates the stamp to now-2000 so the gate holds itself open.
            // The engine only ever re-stamps it during AREA LOAD (MainLoop 0x006031DB, inside
            // the area_not_ready_to_display branch at 0x00602EFC).
            //
            // While that gate is shut, CSWSObject::IsDialogDelay (0x004CD2D0) returns 1 and
            // CSWSObject::SendDialogEntry (0x004CD460) DEFERS the entry instead of calling
            // CSWSDialog::SendDialogEntry — which is the only producer of
            // CGuiInGame::HandleDialogSound (0x0062E1D0), the voice-over, and of the lipsync
            // arming that follows it. Nothing sets the flag false any more, so there was
            // nothing to restore and every call was pure cost. One of them fired on the
            // "owner ended the conversation" edge, exactly as the follower was about to be
            // pulled into the next one.
            Odyssey.CloseDialogue();
            return;
        }

        if (!open)
        {
            return; // still on its way; nothing to drive yet
        }

        // (Suppressed is asserted above, before the open check, so the join window is covered
        // too — it used to be asserted here and that was the hole.)

        // ⚠ SetCanSendDialog(false) used to be asserted here too, as a second "cannot commit
        // a reply" lever. It is not that lever, and it was the reason a followed conversation
        // was silent and dead-faced. Traced in the binary: GetCanSendDialog 0x005EDC90 has
        // exactly ONE caller, CSWSObject::IsDialogDelay 0x004CD2D0, which returns "delayed"
        // when the flag is clear; CSWSObject::UpdateDialog 0x004CD580 then SKIPS the branch
        // that reaches SendDialogEntry. And SendDialogEntry 0x005A4010 is what passes the
        // node's sound resref (CSWSDialogBase+0x34) to CGuiInGame::HandleDialogSound
        // 0x0062E1D0 — the voice-over — while UpdateDialogLipSync 0x0062FA30 drives the mouth
        // ONLY while that stream is playing. So holding the flag down froze the follower's
        // own conversation advance: no VO, therefore no lipsync, and lines that only moved
        // when something else nudged them. The reply block above is the real and sufficient
        // one — it refuses CSWGuiDialog::SelectReply itself.

        // ⚠ DRIVEN TO THE OWNER'S NODE. THE PICK REPLAY THAT USED TO BE HERE IS GONE, AND
        // MUST NOT COME BACK ALONGSIDE THIS — the two gates would each undo the other's work.
        //
        // What was here: wait until our own line hashes to the line the owner's pick was made
        // on, then dispatch that reply slot. It deadlocked completely, and the deadlock was
        // measured off a frozen session rather than reasoned about. DialogueReplyHook is a
        // PREFIX hook, so when it records a pick the engine's own SelectReply body has not run
        // yet; by the time the frame hook samples the history ring, that body has appended
        // BOTH the player's reply and the next NPC entry. So every pick was stamped with the
        // hash of the line it PRODUCED, and the follower sat waiting for a line it could only
        // reach by applying the very pick it was gating on. From the live ring: prompt
        // 0x74851E0A, pick stamped 0xC1EFCE18, two records apart. No pick made after a
        // follower joined had ever been applied, in either direction.
        //
        // Fixing the stamp would not have been enough, which is why this is a replacement and
        // not a repair: a reply slot is a per-instance quantity (Odyssey's node-sync note has
        // the three reasons and their addresses), so a correctly-stamped slot still walks the
        // two screens down different branches. Publishing POSITION instead sidesteps all of
        // it, and dissolves the start-node divergence for free.
        int wantedNode = block.RemoteDialogueNode;
        uint wantedSerial = block.RemoteDialogueNodeSerial;

        if (wantedNode < 0 || wantedSerial == _appliedNodeSerial)
        {
            return; // the owner has not said where it is, or we have already been sent there
        }

        // Wait for our own conversation to have produced a line before driving it. Driving a
        // shell that has not delivered anything races the open, and the open is the thing that
        // decides which panel the conversation is even running on.
        if (!hasContent)
        {
            return;
        }

        int before = Odyssey.GetDialogueNodeIndex();

        // Already there — the common case, because entries auto-advance identically on both
        // instances through any stretch of a conversation without a choice in it. Bank it, or
        // every such node would be re-examined on every frame for as long as the owner sat
        // on it.
        if (before == wantedNode)
        {
            _appliedNodeSerial = wantedSerial;
            _driveAttempts = 0;
            return;
        }

        // ⚠ A SUCCESSFUL CALL IS NOT AN ARRIVAL, AND BANKING THE SERIAL ON ONE STRANDS THE
        // FOLLOWER FOR THE REST OF THE CONVERSATION.
        //
        // CSWSObject::SendDialogEntry returns 1 from its DEFER path as well as its send path:
        // when the dialog-delay gate is shut it stores the entry at +0x4C, raises +0x48 and
        // reports success. And that gate is shut exactly when a follower first tries to use
        // it — CSWSObject::IsDialogDelay asks GetCanSendDialog, which refuses for about a
        // second after the stamp at +0x22C, and a conversation opening is one of the things
        // that stamps it. Measured: the client joined, drove 19 -> 29 three milliseconds
        // after its conversation delivered content, was told "success", and sat on 19 for the
        // next seventeen seconds with the serial already banked so it never tried again.
        //
        // So the node itself is the acknowledgement. Nothing is banked until the engine has
        // actually moved, and until then this simply runs again next frame — which is the
        // same level-triggered shape as everything else in this controller, and it turns the
        // engine's transient gate into a few frames of latency instead of a dead follow.
        if (!Odyssey.DriveDialogueToNode(wantedNode))
        {
            return; // could not even be asked for yet; try again next frame
        }

        int landed = Odyssey.GetDialogueNodeIndex();
        if (landed == wantedNode)
        {
            _appliedNodeSerial = wantedSerial;
            LogFew($"dialogue: followed the owner {before} -> {wantedNode}"
                + (_driveAttempts > 0 ? $" (after {_driveAttempts} deferred attempts)" : string.Empty));
            _driveAttempts = 0;
            return;
        }

        // Deferred. Say so once it has gone on long enough to be a fault rather than the
        // ordinary one-second gate, and keep retrying either way — a follower that gives up
        // is a follower watching a conversation it is not in.
        if (++_driveAttempts == DriveAttemptsBeforeComplaining)
        {
            PluginHost.Log($"dialogue: the engine has deferred {DriveAttemptsBeforeComplaining} attempts to "
                + $"move from {landed} to {wantedNode}; still trying — {Odyssey.DescribeDialogueDrive()}");
        }
    }

    /// <summary>Deferred drives of the CURRENT node request, reset once one lands.</summary>
    private int _driveAttempts;

    /// <summary>Frames of deferral before the log says so. The engine's own gate clears in
    /// about a second, and the frame hook runs at roughly 300 Hz, so this is several times
    /// the ordinary wait — it fires for a gate that is stuck, not for one that is working.</summary>
    private const int DriveAttemptsBeforeComplaining = 1200;



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
