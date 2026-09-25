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
///   * a follower's ability to commit a reply is turned OFF (DialogueReplyHook), so only the
///     owner's choices ever advance the tree;
///   * a follower publishes no picks of its own, or the two instances would drive each other
///     in a circle.
///
/// ─────────────────────────────────────────────────────────────────────────────────────────
/// ⚠ THIS CLASS IS DELIBERATELY THIN. EVERY DECISION LIVES IN
/// <see cref="DialogueFollowMachine"/> AND <see cref="DialogueNodeFollower"/>.
///
/// What used to be here was one 300-line method with seven independent booleans and
/// timestamps decided at seven different depths. An audit found thirteen state-machine
/// defects in it, and SEVEN OF THEM WERE THE SAME SHAPE — a flag whose clear sat below an
/// early return that a later pass had added above it. Patching the instances one at a time
/// just waits for the next early return, so the structure changed instead: one named state at
/// a time, one place that decides the next one, and every level this instance asserts on the
/// engine derived from the state rather than remembered.
///
/// The split also puts the rules under test. The plugin assembly is x86 NativeAOT and nothing
/// references it, so anything that must be covered has to live in KotorCoop.Protocol — which
/// the plugin shares BY SOURCE (see the csproj) and the test project references. What is left
/// in this file is engine reads, engine writes and shared-block plumbing, and that is the
/// whole intent: if a line here decides something, it is in the wrong file.
/// ─────────────────────────────────────────────────────────────────────────────────────────
///
/// Runs on the game's frame hook. Every engine read is guarded and every engine call is
/// bounds-checked; a conversation that cannot be resolved is skipped, never guessed at.
/// </summary>
internal sealed unsafe class DialogueSpectateController(SharedBlockView block)
{
    private readonly DialogueFollowMachine _machine = new();
    private readonly DialogueNodeFollower _nodes = new();

    /// <summary>"No node." Mirrors <c>DialogueState.NoNode</c> on the wire.</summary>
    private const int NoNode = -1;

    private bool _hadDialogue;

    /// <summary>Latched at the moment a NEW local conversation is detected (the
    /// <c>_hadDialogue</c> false-&gt;true edge) from <see cref="DialogueGateHook.LastStartForced"/>,
    /// and held for the whole conversation — the underlying hook table entry can expire or be
    /// overwritten by an unrelated action long before the conversation itself ends. See
    /// docs/research/dialogue-sync.md's 2026-08-23 Item 1.</summary>
    private bool _localForced;

    // ══════════════════════════════════════════════════════════════════════════════════════
    // Item 2 instrumentation (docs/research/dialogue-skip-desync.md, 2026-08-23): the
    // "skip mid-sentence desyncs" report needs live reproduction to diagnose, which this round
    // does not have the rig for. What it CAN do is make the next round's evidence trail
    // unambiguous — a monotonic, timestamped, capped log of every node transition on BOTH the
    // publish side (here) and the consume/replay side (DriveToPeerNode below), the same
    // ground-truth-instrument discipline round 52's attacker-id log used for combat
    // (shared-state-audit.md). Two independent counters, two independent caps: a flooded
    // publish side must not starve the consume side's own budget or a fast owner-side skip
    // burst would silently blind the very half of the picture it is most likely to stress.
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Total node-transition log lines this conversation, on EITHER side — capped so a
    /// pathological rapid-skip test cannot flood the log file, generous enough that no real live
    /// test session should ever hit it (a .dlg has, at most, a few hundred entries; even mashing
    /// skip through every one of them twice over stays well under this).</summary>
    private const int NodeLogBudget = 4000;

    private uint _publishNodeCounter;
    private int _publishNodeLogged;

    private uint _driveAttemptCounter;
    private int _driveNodeLogged;

    private void LogNodePublish(int fromNode, int toNode, uint serial)
    {
        if (_publishNodeLogged >= NodeLogBudget)
        {
            return;
        }

        _publishNodeLogged++;
        _publishNodeCounter++;
        PluginHost.Log($"dialogue-node-pub: #{_publishNodeCounter} node {fromNode} -> {toNode} "
            + $"serial {serial} at {Environment.TickCount64}");
    }

    /// <summary>Every ATTEMPT to drive the follower to a node — not only the ones that land
    /// immediately (the existing <c>LogFew("dialogue: followed the owner ...")</c> line only
    /// fires on arrival or at the deferral-complaint threshold; this fires on every call,
    /// including a deferred one, so a live round can see the full sequence rather than only its
    /// endpoints) — with how long the drive CALL ITSELF took, so a slow engine call is
    /// distinguishable from a slow WIRE update.</summary>
    private void LogDriveAttempt(int beforeNode, int wantedNode, int landedNode, long callMs, bool arrived)
    {
        if (_driveNodeLogged >= NodeLogBudget)
        {
            return;
        }

        _driveNodeLogged++;
        _driveAttemptCounter++;
        PluginHost.Log($"dialogue-node-drive: #{_driveAttemptCounter} wanted {wantedNode} "
            + $"before {beforeNode} landed {landedNode} arrived={arrived} callMs={callMs} "
            + $"at {Environment.TickCount64}");
    }
    private uint _lastLineHash;
    private long _lastLiveMs;

    /// <summary>One content-latency report per follow.</summary>
    private bool _reportedContentMs;

    /// <summary>
    /// The world this controller's state belongs to.
    ///
    /// ⚠ SELF-INVALIDATING, because <see cref="Invalidate"/> IS NOT CALLED. PluginHost's
    /// <c>InvalidateAll</c> resets some thirty controllers and this is not one of them —
    /// checked at the time of writing, and reported. A module change destroys every creature
    /// and every conversation, so a partner tag now resolves to a different body and a node
    /// index names an entry in a completely different .dlg; carrying either across is worse
    /// than useless. The epoch is the same one PluginHost gates its whole frame on
    /// (object-array pointer + module-name hash), so a change here means exactly what a change
    /// there means. Invalidate() is kept and still correct — this simply does not depend on
    /// somebody remembering to call it.
    /// </summary>
    private ulong _epoch;

    /// <summary>Module changes destroy every creature and every conversation. Drop all of it
    /// rather than act on a tag that now resolves to a different body.</summary>
    public void Invalidate()
    {
        _machine.Reset();
        _nodes.Reset();

        _hadDialogue = false;
        _lastLiveMs = 0;
        _lastLineHash = 0;
        _reportedContentMs = false;
        _heldSpeaker[0] = 0;
        _heldLine[0] = 0;
        _epoch = 0;

        // ⚠ The block is cleared HERE, not by letting PublishLocal's end-of-conversation
        // branch do it. That branch is guarded on _hadDialogue, which this method has just
        // set false — so relying on it left the old speaker, line and partner tag published
        // for the rest of the session, and a stale partner tag is what the far side's join
        // gate compares against.
        *block.LocalDialogueSpeaker = 0;
        *block.LocalDialogueLine = 0;
        *block.LocalDialoguePartnerTag = 0;
        block.LocalDialogueSerial++;

        _localForced = false;
        block.LocalDialogueForced = false;

        // Item 2 instrumentation counters are SESSION-lifetime, not per-conversation, so a live
        // round's monotonic numbering stays meaningful across several conversations in one
        // sitting — but a module change is still a clean break worth resetting the BUDGET at,
        // the same reasoning LogFew's own window refill uses one level up.
        _publishNodeCounter = 0;
        _publishNodeLogged = 0;
        _driveAttemptCounter = 0;
        _driveNodeLogged = 0;

        // A node index names an entry in ONE .dlg. Carrying one across a module change points
        // it at a different file's entry list, so it goes with everything else here.
        block.LocalDialogueNode = NoNode;
        block.LocalDialogueNodeSerial++;

        for (int i = 0; i < SharedBlock.DialoguePickCapacity; i++)
        {
            *(uint*)block.LocalDialoguePick(i) = 0;
        }

        // The two engine-side levels are leases now, so they cannot latch across this — but
        // release them explicitly anyway: the frame after a module change should not spend
        // even one lease's worth refusing the player's first click in the new area.
        DialogueReplyHook.AllowReplies();
        DialogueGateHook.ReleaseForPeer();
        DialogueGateHook.PeerPartnerTag = 0;
    }

    public void OnFrame()
    {
        // Self-invalidate on a module change. See _epoch: nothing calls Invalidate() for us.
        if (Odyssey.TryGetWorldEpoch(out ulong epoch))
        {
            if (epoch != _epoch)
            {
                if (_epoch != 0)
                {
                    LogFew("dialogue: the module changed; dropping all conversation state");
                    Invalidate();
                }

                _epoch = epoch;
            }
        }
        else if (_epoch != 0)
        {
            LogFew("dialogue: the world went away; dropping all conversation state");
            Invalidate();
        }

        PublishLocal();
        ReportCounters();
        FollowRemote();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // Publishing: what the local player is saying, for a peer to follow.
    // ══════════════════════════════════════════════════════════════════════════════════════

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

    /// <summary>Last non-empty speaker/line, restored into the block across a flicker hold —
    /// <see cref="Odyssey.TryReadActiveDialogue"/> blanks its output buffers before it reads,
    /// so holding without this would publish an empty line for the gap.</summary>
    private readonly byte[] _heldSpeaker = new byte[SharedBlock.LocalDialogueSpeakerLength];
    private readonly byte[] _heldLine = new byte[SharedBlock.LocalDialogueLineLength];

    /// <summary>Publishes what the local player is saying and picking, for a peer to follow.</summary>
    private void PublishLocal()
    {
        // The Jedi-choice popup (k_coop_jediclass.dlg) is a private, per-player prompt, never a
        // shared world conversation — docs/research/class-and-power-grants.md's 2026-09-25
        // section explicitly requires it never be mirrored to another player. Skipping the whole
        // publish while it is up (rather than publishing it and filtering it on the OTHER side)
        // means a peer's spectate/follow machinery sees nothing happen at all: no serial bump,
        // no partner tag, nothing to adopt or follow. JediChoicePromptController owns the flag
        // and clears it the same frame the popup closes.
        if (JediChoicePromptController.IsPromptActive)
        {
            return;
        }

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
            // nothing else — no serial bump, no cleared tag, no wiped pick log. _hadDialogue
            // stays true, which is what makes it the DEBOUNCED "we are in a conversation"
            // input the state machine reasons about.
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

                _localForced = false;
                block.LocalDialogueForced = false;

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

                PluginEvents.RaiseDialogueEnded();
            }

            return;
        }

        // Latched ONCE, at the false->true edge: DialogueForceFlagHook's table entry for this
        // object can be overwritten by an unrelated later action (someone else being talked at,
        // an NPC's own ambient AI) long before this conversation itself ends, so re-reading it
        // every frame would let the bit flicker mid-conversation. It must be read here, before
        // _hadDialogue flips, or every frame looks like a continuation rather than a start.
        if (!_hadDialogue)
        {
            _localForced = DialogueGateHook.LastStartForced;
            block.LocalDialogueForced = _localForced;
            PluginEvents.RaiseDialogueStarted();
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
            int publishedFrom = block.LocalDialogueNode;
            block.LocalDialogueNode = node;
            block.LocalDialogueNodeSerial++; // serial last: the far side reads it first

            // Item 2 instrumentation (docs/research/dialogue-skip-desync.md): every node
            // transition the OWNER's own instance publishes, monotonically counted and
            // timestamped, so a live round can see exactly which transitions happened and how
            // close together — the question a fixed-cadence poll (150 ms by default,
            // dialogue_fast_join=0) cannot answer from the wire alone, since the wire only ever
            // carries the LATEST node, never the sequence a fast skip passed through.
            LogNodePublish(publishedFrom, node, block.LocalDialogueNodeSerial);
        }

        // Replies WE picked, appended to the pick LOG with the hash of the line they were
        // picked on.
        //
        // ⚠ DIAGNOSTIC ONLY NOW — nothing is driven from this. It says what the owner
        // pressed, which is worth having in a log when a conversation goes wrong. It is NOT
        // what the follower replays, and reinstating that would undo the node drive: see the
        // node-sync note in Odyssey for why a reply slot cannot mean the same thing on two
        // machines. (Not recorded while following: those are replays, not ours.)
        if (!_machine.IsFollowing && DialogueReplyHook.Sequence != block.LocalDialogueReplySerial)
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

    // ══════════════════════════════════════════════════════════════════════════════════════
    // Diagnostics. Every one of these must run before any decision can return, so they live
    // in their own pass rather than at the top of one that has early exits under it.
    // ══════════════════════════════════════════════════════════════════════════════════════

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

    private void ReportCounters()
    {
        // A refused click deserves a visible answer, not silence: keep a short notice on
        // screen after each blocked attempt.
        if (DialogueGateHook.BlockedCount != _seenBlocked)
        {
            _seenBlocked = DialogueGateHook.BlockedCount;
            _blockedNoticeUntilMs = Environment.TickCount64 + 2500;
            LogFew("dialogue: blocked a local conversation start; a peer holds the token");
        }

        // Diagnostic for the joiner-cannot-talk defect: does the engine reach StartDialog at
        // all here? Reported from the frame hook because the detour itself must not log.
        //
        // The "forced=" suffix is Item 1's own NEEDS-LIVE evidence trail
        // (docs/research/dialogue-sync.md's 2026-08-23 section): the next live round needs to
        // see this read TRUE for a real ambush/forced conversation and FALSE for a real player
        // click, and this is the one line that shows it without any extra tooling.
        if (DialogueGateHook.StartAttempts != _seenStartAttempts)
        {
            _seenStartAttempts = DialogueGateHook.StartAttempts;
            LogFew($"dialogue: engine reached StartDialog ({_seenStartAttempts} so far, "
                + $"forced={DialogueGateHook.LastStartForced})");
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
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // Following: one step of the state machine, then the engine calls it asked for.
    // ══════════════════════════════════════════════════════════════════════════════════════

    private void FollowRemote()
    {
        long now = Environment.TickCount64;

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
        // handled: the machine's yield path closes our own conversation the moment the tags
        // resolve and disagree. The cost of refusing everything is a player who cannot join.
        bool peerActive = block.RemoteDialogueActive;
        bool peerPartnerKnown = peerActive && *block.RemoteDialoguePartnerTag != 0;

        if (peerPartnerKnown)
        {
            DialogueGateHook.HoldForPeer();

            // A pointer into the shared block, which outlives every frame.
            DialogueGateHook.PeerPartnerTag = (nint)block.RemoteDialoguePartnerTag;
        }
        else
        {
            DialogueGateHook.ReleaseForPeer();
            DialogueGateHook.PeerPartnerTag = 0;
        }

        // The two engine reads the machine needs, taken once. IsConversationActive is the raw
        // engine flag (it drops between entries); _hadDialogue is the debounced one.
        bool open = Odyssey.IsConversationActive;
        bool hasContent = open && Odyssey.DialogueHasContent();

        DialogueFollowDecision decision = _machine.Step(new DialogueFollowInputs(
            NowMs: now,
            PeerActive: peerActive,
            PeerPartnerKnown: peerPartnerKnown,
            LocalConversation: _hadDialogue,
            LocalPartnerKnown: *block.LocalDialoguePartnerTag != 0,
            LocalPartnerIsPeers: SameTag(block.LocalDialoguePartnerTag, block.RemoteDialoguePartnerTag),
            EngineConversationOpen: open,
            EngineHasContent: hasContent));

        // ⚠ RENEWED EVERY FRAME FROM THE STATE, NEVER LATCHED, AND NEVER SKIPPED BY AN EARLY
        // RETURN — there is no early return above it any more, and even if one appeared the
        // hook's lease would expire on its own rather than leaving the player unable to answer
        // any conversation for the rest of the session. See DialogueReplyHook.
        //
        // ⚠ SKIP RIDES THE SAME BRANCH, AND THAT IS THE FIX FOR A REAL LIVE BUG: a host that
        // joined a client's conversation could press skip and advance it, because skip is a
        // DIFFERENT engine call one branch away from the reply commit inside the same switch
        // (CSWGuiDialog::HandleInputEvent 0x006A7230). Same predicate, same lease length, same
        // renewal point — see DialogueSkipHook and Protocol's DialogueSkipAuthorityRule for why
        // the width is SuppressReplies' "anything but Idle" rather than IsFollowing.
        if (DialogueSkipAuthorityRule.ShouldSwallowSkip(_machine.State))
        {
            DialogueReplyHook.SuppressReplies();
            DialogueSkipHook.SuppressSkips();
        }
        else
        {
            DialogueReplyHook.AllowReplies();
            DialogueSkipHook.AllowSkips();
        }

        // Position is per follow. Clearing it whenever a follow is not live is what makes a
        // zero applied-serial mean "drive me wherever the owner is" for every new follow, and
        // stops a deferral count being inherited across one.
        if (!_machine.IsFollowing)
        {
            _nodes.Reset();
            _reportedContentMs = false;
        }

        Say(decision.Note);

        switch (decision.Action)
        {
            case DialogueFollowAction.CloseConversation:
                Odyssey.CloseDialogue();
                return;

            case DialogueFollowAction.ForceCloseUi:
                Odyssey.ForceCloseDialogueUi();
                return;

            case DialogueFollowAction.ForceCloseUiAndClearActions:
                Odyssey.ForceCloseDialogueUi();
                Odyssey.ClearLocalPlayerStuckDialogueAction();
                return;

            case DialogueFollowAction.DriveToPeerNode:
                ReportContentLatency(now);
                DriveToPeerNode(hasContent);
                return;

            default:
                return;
        }
    }

    /// <summary>How long content actually took, reported once per follow. This is the number
    /// the content timeout should be set from, and nothing had ever measured it.</summary>
    private void ReportContentLatency(long now)
    {
        if (_reportedContentMs || _machine.OpenedAtMs is not { } openedAt)
        {
            return;
        }

        _reportedContentMs = true;
        LogFew($"dialogue: followed conversation delivered content {now - openedAt} ms after opening "
            + $"({now - _machine.RequestedAtMs} ms after the request)");
    }

    /// <summary>
    /// ⚠ DRIVEN TO THE OWNER'S NODE. THE PICK REPLAY THAT USED TO BE HERE IS GONE, AND MUST
    /// NOT COME BACK ALONGSIDE THIS — the two gates would each undo the other's work.
    ///
    /// What was here: wait until our own line hashes to the line the owner's pick was made on,
    /// then dispatch that reply slot. It deadlocked completely, and the deadlock was measured
    /// off a frozen session rather than reasoned about. DialogueReplyHook is a PREFIX hook, so
    /// when it records a pick the engine's own SelectReply body has not run yet; by the time
    /// the frame hook samples the history ring, that body has appended BOTH the player's reply
    /// and the next NPC entry. So every pick was stamped with the hash of the line it PRODUCED,
    /// and the follower sat waiting for a line it could only reach by applying the very pick it
    /// was gating on. From the live ring: prompt 0x74851E0A, pick stamped 0xC1EFCE18, two
    /// records apart. No pick made after a follower joined had ever been applied, in either
    /// direction.
    ///
    /// Fixing the stamp would not have been enough, which is why this is a replacement and not
    /// a repair: a reply slot is a per-instance quantity (Odyssey's node-sync note has the
    /// three reasons and their addresses), so a correctly-stamped slot still walks the two
    /// screens down different branches. Publishing POSITION instead sidesteps all of it, and
    /// dissolves the start-node divergence for free.
    /// </summary>
    private void DriveToPeerNode(bool hasContent)
    {
        int wantedNode = block.RemoteDialogueNode;
        uint wantedSerial = block.RemoteDialogueNodeSerial;

        if (!_nodes.NeedsNode(wantedNode, wantedSerial, hasContent))
        {
            return; // no position published, already sent there, or nothing on screen to drive
        }

        int before = Odyssey.GetDialogueNodeIndex();

        // Already there — the common case, because entries auto-advance identically on both
        // instances through any stretch of a conversation without a choice in it.
        if (_nodes.Arrived(wantedSerial, before, wantedNode))
        {
            // Item 2 instrumentation: this is the branch a "skip mid-sentence" report would hit
            // if the follower's OWN local advance happened to already be sitting on the node the
            // (possibly several-nodes-ahead) wire update names — worth distinguishing from a
            // real drive call in the log, not folding into the same line.
            LogDriveAttempt(before, wantedNode, before, callMs: 0, arrived: true);
            return;
        }

        // ⚠ A SUCCESSFUL CALL IS NOT AN ARRIVAL, AND BANKING THE SERIAL ON ONE STRANDS THE
        // FOLLOWER FOR THE REST OF THE CONVERSATION. See DialogueNodeFollower for the
        // measurement; nothing is banked until a re-read shows the engine actually moved.
        long callStartMs = Environment.TickCount64;
        bool called = Odyssey.DriveDialogueToNode(wantedNode);
        long callMs = Environment.TickCount64 - callStartMs;

        if (!called)
        {
            LogDriveAttempt(before, wantedNode, before, callMs, arrived: false);
            return; // could not even be asked for yet; try again next frame
        }

        int landed = Odyssey.GetDialogueNodeIndex();
        int deferrals = _nodes.Deferrals;
        bool arrived = _nodes.Arrived(wantedSerial, landed, wantedNode);

        LogDriveAttempt(before, wantedNode, landed, callMs, arrived);

        if (arrived)
        {
            LogFew($"dialogue: followed the owner {before} -> {wantedNode}"
                + (deferrals > 0 ? $" (after {deferrals} deferred attempts)" : string.Empty));
            return;
        }

        // Deferred. Say so once it has gone on long enough to be a fault rather than the
        // ordinary one-second gate, and keep retrying either way — a follower that gives up is
        // a follower watching a conversation it is not in.
        if (_nodes.NoteDeferral() == DeferralsBeforeComplaining)
        {
            PluginHost.Log($"dialogue: the engine has deferred {DeferralsBeforeComplaining} attempts to "
                + $"move from {landed} to {wantedNode}; still trying — {Odyssey.DescribeDialogueDrive()}");
        }
    }

    /// <summary>Frames of deferral before the log says so. The engine's own gate clears in
    /// about a second, and the frame hook runs at roughly 300 Hz, so this is several times
    /// the ordinary wait — it fires for a gate that is stuck, not for one that is working.</summary>
    private const int DeferralsBeforeComplaining = 1200;

    /// <summary>The machine names the event; the sentence lives here, where the log does.</summary>
    private void Say(DialogueFollowNote note)
    {
        switch (note)
        {
            case DialogueFollowNote.OwnerEnded:
                LogFew("dialogue: the owner ended the conversation; closing");
                Odyssey.RestoreJoinTeleportIfPending();
                break;
            case DialogueFollowNote.Adopted:
                LogFew("dialogue: the engine started the same conversation here; adopting it to follow");
                break;
            case DialogueFollowNote.Yielding:
                LogFew("dialogue: a peer won the conversation token; closing our own to follow");
                break;
            case DialogueFollowNote.ForcingUiShut:
                LogFew("dialogue: our conversation would not close after the server's turns; forcing the UI shut");
                break;
            case DialogueFollowNote.ClearingStuckAction:
                // See ForceCloseUiAndClearActions' and ClearLocalPlayerStuckDialogueAction's own
                // docs: forcing the UI shut alone measured as an infinite loop for a joiner left
                // too far from the NPC, because the engine keeps re-arming the conversation flag
                // from a still-queued action the UI close cannot reach.
                LogFew("dialogue: the UI-only close did not stick; clearing the stuck action too");
                break;
            case DialogueFollowNote.UnstickingShell:
                LogFew("dialogue: an empty conversation shell is blocking new ones here; ending it "
                    + "so a click can join the peer's");
                break;
            case DialogueFollowNote.GaveUpNoContent:
                LogFew("dialogue: the followed conversation never delivered a line; backing out");
                Odyssey.RestoreJoinTeleportIfPending();
                break;
            case DialogueFollowNote.GaveUpNeverOpened:
                LogFew("dialogue: the peer's conversation never opened locally; backing out");
                Odyssey.RestoreJoinTeleportIfPending();
                break;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // Buffer and tag helpers.
    // ══════════════════════════════════════════════════════════════════════════════════════

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

    /// <summary>
    /// Lines this controller may write per BUDGET WINDOW.
    ///
    /// ⚠ It refills, and it did not used to. A flat lifetime cap of 60 lines meant a session
    /// that had already seen sixty dialogue events went permanently silent — including for the
    /// next genuinely new state — which is the same failure mode as the absolute repeat
    /// suppression above, one level up. The cap is here to stop a per-frame loop flooding the
    /// log, and a refill on a long window does that just as well while keeping a session that
    /// runs for an hour observable.
    /// </summary>
    private const int LogBudget = 60;

    private const long LogBudgetWindowMs = 120000;

    private int _logged;
    private long _budgetWindowStartedAtMs;

    private void LogFew(string message)
    {
        long now = Environment.TickCount64;
        if (message == _lastLogged && now - _lastLoggedAtMs < RepeatAfterMs)
        {
            return;
        }

        if (now - _budgetWindowStartedAtMs >= LogBudgetWindowMs)
        {
            _budgetWindowStartedAtMs = now;
            _logged = 0;
        }

        if (_logged < LogBudget)
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
