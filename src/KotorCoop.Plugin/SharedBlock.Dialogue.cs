namespace KotorCoop.Plugin;

// NPC barks, dialogue spectate/follow/pick-log/node-sync, per-object interaction locks,
// container-lock target ids and the dialogue forced-conversation bit. See SharedBlock.cs
// for the struct header.
internal partial struct SharedBlock
{
    // v81: actual DLG names, beyond the previous 0x8600 boundary. 16 bytes + NUL.
    public const int DialogueResrefLength = 20;
    public const int OffsetLocalDialogueResref = 0x8600;
    public const int OffsetRemoteDialogueResref = 0x8614;
    // ---- NPC barks (version 27) --------------------------------------------------
    //
    // The one-liners an NPC throws into a bubble without ever opening the dialogue system
    // (the utility droid's beep). They reach no other channel, so one player got a line and
    // everybody else got silence. Captured by BarkHook, replayed by BarkController.
    //
    // Both directions are a small ring of (speaker, three verbatim engine arguments), with
    // the SERIAL WRITTEN LAST per entry so a half-written slot is never acted on — the same
    // discipline the enemy and damage regions use.

    /// <summary>
    /// Plugin -&gt; agent. One captured bark per slot:
    ///   +0x00 speaker (SERVER object id, u32)
    ///   +0x04 flags   (u32, the engine's ignore-range argument)
    ///   +0x08 textLen (u32)
    ///   +0x10 resref  (16 raw bytes — a CResRef is char[16], not a string)
    ///   +0x20 text    (up to <see cref="BarkTextMax"/> bytes, engine encoding, no NUL)
    ///   +0xFC serial  (u32) — LAST DWORD OF THE SLOT, so "serial written last" still holds
    /// Occupies 0x2300..0x2B00 (8 x 0x100).
    ///
    /// ⚠ The old 0x14 slot carried four opaque dwords, two of which were POINTERS into the
    /// producing process. That is what killed the engine on replay; the contents have to
    /// travel, and they do not fit in 0x14. See docs/research/bark-replay.md.
    /// The old rings at 0x2120..0x2260 are left dead rather than reused, so a stale agent
    /// reading the previous layout finds zeros instead of half a record.
    /// </summary>
    public const int OffsetLocalBarkSlots = 0x2300;

    /// <summary>Agent -&gt; plugin. Same shape: barks a peer produced, for this instance to
    /// replay. Occupies 0x2B00..0x3300.</summary>
    public const int OffsetRemoteBarkSlots = 0x2B00;

    public const int BarkStride = 0x100;

    /// <summary>Bytes of bark text carried per slot. Matches <c>BarkHook.TextMax</c>.</summary>
    public const int BarkTextMax = 200;

    /// <summary>A CResRef is a fixed char[16].</summary>
    public const int BarkResRefBytes = 16;

    // Field offsets within a bark slot.
    public const int BarkSpeakerOffset = 0x00;
    public const int BarkFlagsOffset = 0x04;
    public const int BarkTextLengthOffset = 0x08;
    public const int BarkResRefOffset = 0x10;
    public const int BarkTextOffset = 0x20;
    public const int BarkSerialOffset = 0xFC;

    /// <summary>Barks carried per direction. They are occasional and the pump drains them
    /// several times a second; matches <c>BarkHook.Capacity</c>.</summary>
    public const int BarkCapacity = 8;


    // ---- dialogue spectate (version 12, region 0xC80..0xD00) ----------------------
    //
    // Plugin -> agent. On the token owner's instance the DialogueSpectateController reads
    // the current conversation's speaker + line off the live CSWSDialog each frame and
    // writes them here (see docs/research/dialogue-sync.md). The agent hands them to the
    // session's dialogue token so the OTHER player can be shown what the owner is hearing.
    // Reads are all guarded; an unresolvable line is written empty, never guessed.

    /// <summary>Plugin -&gt; agent. Current speaker, null-terminated ascii.</summary>
    public const int OffsetLocalDialogueSpeaker = 0xC80;

    public const int LocalDialogueSpeakerLength = 40; // occupies 0xC80..0xCA8

    /// <summary>Plugin -&gt; agent. Current line, null-terminated ascii. Version 17 moved it
    /// to the 0x1100 region: real dialogue lines run to ~290 characters and the original
    /// 64-byte slot cut them mid-sentence. (0xCA8..0xCE8 is now unused.)</summary>
    public const int OffsetLocalDialogueLine = 0x1100;

    public const int LocalDialogueLineLength = 192; // occupies 0x1100..0x11C0

    /// <summary>Plugin -&gt; agent. Bumped when the speaker/line pair changes; written last.</summary>
    public const int OffsetLocalDialogueSerial = 0xCE8;


    // ---- dialogue follow (version 18, region 0x1200..0x1300) ----------------------
    //
    // Spectating is the game's OWN dialogue UI on the non-owner's screen, not a transcript:
    // the spectator opens the identical conversation against its own copy of the NPC and
    // replays every reply the owner picks, so both screens sit on the same line. Only the
    // owner may choose; the spectator's ability to commit a reply is switched off while it
    // follows. See DialogueSpectateController and docs/research/dialogue-sync.md.

    /// <summary>Plugin -&gt; agent. Tag of the NPC the local player is talking to. A TAG, not
    /// an object id: ids are assigned per module load and the peer's copy of the same
    /// creature can hold a different one.</summary>
    public const int OffsetLocalDialoguePartnerTag = 0x1200;

    public const int LocalDialoguePartnerTagLength = 32; // occupies 0x1200..0x1220

    /// <summary>Plugin -&gt; agent. Bumped on every reply the local player picks.</summary>
    public const int OffsetLocalDialogueReplySerial = 0x1220;

    /// <summary>Plugin -&gt; agent. The reply SLOT the local player clicked. Slot rather than
    /// node index: both instances run the same .dlg, so the same slot walks the same
    /// branch, and the slot is what the engine's own reply call takes.</summary>
    public const int OffsetLocalDialogueReplyIndex = 0x1224;

    /// <summary>Agent -&gt; plugin. Nonzero while a PEER owns a conversation to follow.</summary>
    public const int OffsetRemoteDialogueActive = 0x1228;

    /// <summary>Agent -&gt; plugin. The owner's reply serial; a change means "replay a pick".</summary>
    public const int OffsetRemoteDialogueReplySerial = 0x122C;

    /// <summary>Agent -&gt; plugin. The slot to replay.</summary>
    public const int OffsetRemoteDialogueReplyIndex = 0x1230;

    /// <summary>Agent -&gt; plugin. Tag of the NPC to open the conversation against.</summary>
    public const int OffsetRemoteDialoguePartnerTag = 0x1240;

    public const int RemoteDialoguePartnerTagLength = 32; // occupies 0x1240..0x1260


    // ---- dialogue pick log (version 19, region 0x1260..0x13E0) --------------------
    //
    // A reply pick is only meaningful ON THE LINE IT WAS PICKED FROM: replaying "slot 1"
    // while the two screens show different nodes walks the spectator down the wrong branch,
    // and a spectator that opened late (the owner had already picked twice) can never catch
    // up from a single latest-pick value. So the owner publishes a small LOG of its recent
    // picks, each stamped with the FNV hash of the line it was made on, and the spectator
    // replays them in order, each one once its own screen reaches that line.

    /// <summary>Entries: u32 serial, u32 slot, u32 lineHash. Serial 0 = empty.</summary>
    public const int DialoguePickStride = 12;

    public const int DialoguePickCapacity = 8;

    /// <summary>Plugin -&gt; agent. The local player's recent picks. 0x1260..0x12C0.</summary>
    public const int OffsetLocalDialoguePicks = 0x1260;

    /// <summary>Agent -&gt; plugin. The conversation owner's recent picks. 0x12C0..0x1320.</summary>
    public const int OffsetRemoteDialoguePicks = 0x12C0;


    // ---- dialogue node sync (version 32, region 0x1320..0x1330) -------------------
    //
    // ⚠ THIS REPLACES THE PICK LOG ABOVE AS THE THING THE FOLLOWER IS DRIVEN BY. The pick log
    // is still published, and is now DIAGNOSTIC ONLY — it says what the owner pressed, which
    // is worth having in a log, and nothing reads it to decide anything.
    //
    // Replaying the owner's reply SLOT cannot converge, because a slot is not a property of
    // the conversation but of one instance's evaluation of it: the starting entry, the set of
    // replies offered and a reply's outgoing link are each chosen by scripts that run
    // separately on each machine (Odyssey's node-sync section carries the full argument and
    // the addresses). So the owner publishes its POSITION instead — the index of the entry on
    // screen, a plain array index into the .dlg both instances loaded — and the follower is
    // driven onto that entry through the engine's own producer. The engine agrees this is the
    // quantity that matters: CSWSDialog::HandleReply refuses a reply whose claimed entry
    // index does not match the live one.
    //
    // -1 means "no conversation, or the index could not be trusted", and is never driven.

    /// <summary>Plugin -&gt; agent. The entry index the local conversation is showing, or -1.</summary>
    public const int OffsetLocalDialogueNode = 0x1320;

    /// <summary>Plugin -&gt; agent. Bumped when the local node changes; written last, so a
    /// half-written pair reads as the previous node rather than as a torn one.</summary>
    public const int OffsetLocalDialogueNodeSerial = 0x1324;

    /// <summary>Agent -&gt; plugin. The entry index the conversation's OWNER is showing, or -1.</summary>
    public const int OffsetRemoteDialogueNode = 0x1328;

    /// <summary>Agent -&gt; plugin. Bumped when the owner's node changes.</summary>
    public const int OffsetRemoteDialogueNodeSerial = 0x132C;


    // ---- per-object interaction locks (version 33, region 0x1330..0x13E0) -------------
    //
    // "Hold this one still, I am using it." Ownership in this mod is one-directional
    // (docs/research/shared-state-audit.md §4): the host owns companions, enemies, doors and
    // containers, so a client that walks up to Trask and talks to him is talking to a puppet
    // while the HOST's AI — never told — keeps walking the real one around. That is the
    // reported "Trask is still moving around on the host's and other players' screens".
    //
    // The lock is claimed the moment a player starts interacting, which is EARLIER than the
    // conversation token can be granted, and that is what makes it fair: the host's own
    // dialogue claim runs the arbiter in-process at zero latency while a client's costs a
    // round trip, so without this the host wins every contested NPC by construction.
    //
    // A LEASE, re-sent while held. There is no release message to lose, so a client that dies
    // mid-conversation cannot strand an NPC for the rest of the session.

    /// <summary>Plugin -&gt; agent. Tag of the object the local player is interacting with, or
    /// empty. A TAG, not an object id — ids are per module load.</summary>
    public const int OffsetLocalInteractionTag = 0x1330;

    public const int LocalInteractionTagLength = 32; // occupies 0x1330..0x1350

    /// <summary>Plugin -&gt; agent. What kind of interaction (see InteractionKind).</summary>
    public const int OffsetLocalInteractionKind = 0x1350;

    /// <summary>Plugin -&gt; agent. Nonzero while the claim is live. A LEVEL, not an edge.</summary>
    public const int OffsetLocalInteractionActive = 0x1354;

    /// <summary>Agent -&gt; plugin. How many lock slots below are valid.</summary>
    public const int OffsetInteractionLockCount = 0x1358;

    /// <summary>Agent -&gt; plugin. Entries: peerId u32 @0, kind u32 @4, tag[32] @8. Occupies
    /// 0x1360..0x13E0 (4 x 0x28 — the protocol's MaxInteractionLocks).</summary>
    public const int OffsetInteractionLocks = 0x1360;

    public const int InteractionLockStride = 0x28;

    public const int InteractionLockCapacity = 4;

    public const int InteractionLockTagLength = 32;


    // ---- container-lock target ids (version 50, region 0x3F08..0x3F1C, no TotalSize growth) --
    //
    // InteractionKind.Container (docs/research/container-sync.md) keys on a placeable OBJECT
    // ID rather than the tag InteractionKind.Conversation already carries at
    // OffsetLocalInteractionTag/OffsetInteractionLocks — the base game routinely places several
    // placeables with the SAME tag (every generic footlocker in a module, typically), so a
    // container claim needs the id instead. Rather than widen InteractionLockStride (0x28,
    // every one of whose four bytes is already spoken for by peerId/kind/tag) this rides in the
    // free tail after the creature-liveness region: OffsetLivenessAnomalyCount ends at 0x3F04 +
    // 4 = 0x3F08, and TotalSize is 0x4000, leaving 0xF8 bytes free — this region uses 20 of
    // them. No TotalSize growth, no relayout of anything else.

    /// <summary>Plugin -&gt; agent. The object id of the placeable the local player is claiming
    /// via a Container-kind interaction claim, or 0. Meaningless (and left 0) for any other
    /// kind, which uses <see cref="OffsetLocalInteractionTag"/> instead.</summary>
    public const int OffsetLocalInteractionTargetId = 0x3F08;

    /// <summary>Agent -&gt; plugin. Parallel array to <see cref="OffsetInteractionLocks"/>, SAME
    /// index correspondence, one u32 per slot: the placeable object id a peer's Container-kind
    /// lock names. 0 for a slot that is empty or names a non-Container kind. Occupies
    /// 0x3F0C..0x3F1C.</summary>
    public const int OffsetInteractionLockTargetIds = 0x3F0C;

    public const int InteractionLockTargetIdStride = 0x04;


    // ---- dialogue forced-conversation bit (version 51, region 0x3F1C..0x3F24, no TotalSize
    // growth) -------------------------------------------------------------------------------
    //
    // docs/research/dialogue-sync.md's 2026-08-23 Item 1: whether the LOCAL/REMOTE conversation
    // is one the vanilla engine itself forces regardless of distance (an ambush, a scripted
    // cutscene conversation — NWScript's ActionStartConversation with bIgnoreStartRange=TRUE),
    // as opposed to an ordinary player-clicked chat. DialogueAutoJoinRule uses this to
    // auto-join UNCONDITIONALLY, ignoring the configured mode and range, matching what vanilla
    // would already have forced onto a single-player's PC rather than inventing new behaviour.
    // Rides in the free tail after the container-lock-target-ids region (0x3F0C + 0x10 = 0x3F1C,
    // TotalSize is 0x4000, leaving 0xE4 bytes free before this; this region uses 8 of them). No
    // TotalSize growth, no relayout of anything else.

    /// <summary>Plugin -&gt; agent. Nonzero while the local player's own conversation is one
    /// the engine itself forced. Latched for the conversation's whole duration (set once, at
    /// the moment a new local conversation is detected — see
    /// <see cref="DialogueForceFlagHook"/>) and cleared alongside the partner tag when the
    /// conversation ends.</summary>
    public const int OffsetLocalDialogueForced = 0x3F1C;

    /// <summary>Agent -&gt; plugin. Nonzero while the PEER's conversation being followed is
    /// forced. Only meaningful alongside <see cref="OffsetRemoteDialogueActive"/> — read
    /// together with <see cref="OffsetRemoteDialoguePartnerTag"/>, which names WHICH
    /// conversation this bit describes (the session's single arbitrated token).</summary>
    public const int OffsetRemoteDialogueForced = 0x3F20;


    // ---- container-lock explicit arbitration (version 57, region 0x40D0..0x40E0, no
    // TotalSize growth) — docs/roadmap-seamless.md Tier 1 item 4, docs/research/container-sync.md
    // -----------------------------------------------------------------------------------------
    //
    // Closes the container-loot race: the ordinary per-tick claim/lock pair above
    // (OffsetLocalInteractionTargetId / OffsetInteractionLockTargetIds) is EVENTUALLY
    // consistent — two clicks inside one RTT both see "not held yet" and both let the engine's
    // own OpenInventory run. This region is a SEPARATE, synchronous-feeling request/response
    // pair the plugin's ContainerLockArbiter deferral state machine drives: the plugin publishes
    // a request the instant a click is deferred, and the agent (which is the only half of this
    // process pair that can actually talk to the session/network) answers it by forwarding to
    // ICoopSession.RequestContainerLock and polling TryGetLockResponse — the exact same
    // "cheat-style request/serial" shape every console command's own ack channel already uses,
    // reused here because the plugin can only ever ASK the agent for something across this
    // boundary, never call into the session directly.
    //
    // Rides in the free tail after the companion-equipment region (0x40D0, after the override dwords at 0x40C0 and the solo-segment dword at 0x40C8,
    // TotalSize is 0x4100, leaving 0x40 bytes free before this; this region uses 16 of them). No
    // TotalSize growth, no relayout of anything else.

    /// <summary>Plugin -&gt; agent. The placeable object id a DEFERRED container click wants
    /// arbitrated, or 0 while nothing is pending. Written alongside
    /// <see cref="OffsetLocalContainerLockRequestSerial"/> — the SERIAL is what the agent
    /// actually keys a fresh request on (see that field's own remarks for the write order).</summary>
    public const int OffsetLocalContainerLockRequestTargetId = 0x40D0;

    /// <summary>Plugin -&gt; agent. A monotonic id for the request named by
    /// <see cref="OffsetLocalContainerLockRequestTargetId"/>, written LAST (after the target id)
    /// so the agent never sees a bumped serial pointing at an id that has not landed yet — the
    /// same ordering rule <see cref="OffsetLocalDialogueNodeSerial"/> already uses for the
    /// identical reason. 0 means "nothing pending"; never 0 for a real request
    /// (<c>ContainerLockArbiter.BeginRequest</c> refuses to start one at serial 0). The agent
    /// forwards a NEW serial (one it has not already forwarded) to
    /// <c>ICoopSession.RequestContainerLock</c> exactly once.</summary>
    public const int OffsetLocalContainerLockRequestSerial = 0x40D4;

    /// <summary>Agent -&gt; plugin. The serial <see cref="OffsetContainerLockResponseGranted"/>
    /// answers, written LAST (after the granted bit) for the identical reason the request serial
    /// above is written last on the plugin's side — the plugin must never see a serial match
    /// with a stale granted bit still sitting behind it.</summary>
    public const int OffsetContainerLockResponseSerial = 0x40D8;

    /// <summary>Agent -&gt; plugin. Nonzero if the request named by
    /// <see cref="OffsetContainerLockResponseSerial"/> was granted, zero if it was denied.
    /// Meaningless until the plugin sees that serial change from what it last read — a stale
    /// answer to an OLD request must never be misread as an answer to a new one with the same
    /// (or a not-yet-updated) serial value, which is exactly the ordering the write-order
    /// remarks above exist to prevent.</summary>
    public const int OffsetContainerLockResponseGranted = 0x40DC;
}
