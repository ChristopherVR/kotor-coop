// Odyssey.Dialogue.NodeSync.cs - part 1 of dialogue node synchronisation: active
// dialogue/owner lookup and driving the UI to a given node.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- dialogue node sync ------------------------------------------------------
    //
    // ⚠ REPLAYING THE OWNER'S REPLY SLOT CANNOT CONVERGE, AND NO AMOUNT OF GATING FIXES IT.
    //
    // A reply slot is not a property of the conversation; it is a property of ONE INSTANCE'S
    // EVALUATION of the conversation. Three separate decisions inside a dialogue are taken
    // independently on each machine, by scripts reading that machine's own world state:
    //
    //   * WHERE IT STARTS. CSWSDialog::GetStartEntry 0x005A3640 takes the first starting
    //     entry whose Active script passes. end_trask01.dlg alone has 19 starting entries, 18
    //     of them conditional — so two players at different tutorial progress begin on
    //     different nodes with no pick involved at all.
    //   * WHICH REPLIES ARE OFFERED. The panel's slots are COMPACTED over the links that
    //     passed locally (the real link index lives at panel+0x1970[slot]). One
    //     differently-filtered link shifts every later slot, so "slot 1" is simply not the
    //     same branch on the two screens.
    //   * WHERE A REPLY GOES. Its outgoing link is first-that-passes as well.
    //
    // So the owner publishes WHERE IT IS rather than WHAT IT PRESSED: the index of the entry
    // on screen. That is a plain array index into the .dlg's entry list — identical on both
    // instances because both load the same file — and it is the quantity the engine itself
    // treats as the conversation's position (HandleReply refuses a reply whose claimed index
    // does not match it). Node sync also dissolves the start-node divergence for free: the
    // follower is driven to wherever the owner actually began.
    //
    // It replaces the pick log outright. The two must never run together: a pick gate waiting
    // on a line hash and a node gate driving the node would each undo the other's work, which
    // is the stranding this is here to end.

    /// <summary>
    /// CSWSDialog::SendDialogEntry(CSWSObject* speaker, int, int entryIndex, int* status) —
    /// __thiscall on the DIALOG, <c>ret 0x10</c> (four epilogues, 0x005A414A / 0x005A41B3 /
    /// 0x005A41F7 / 0x005A4259; and the engine's call site at 0x004CD4B5 does not clean the
    /// four arguments up itself).
    ///
    /// This is the engine's actual producer of a dialogue line: the single call site of
    /// CGuiInGame::HandleDialogSound (the voice-over) and of the lipsync arming that follows
    /// it, as well as of the subtitle. Driving a node through it therefore brings the line,
    /// its audio and its mouth together, which is the whole reason node sync is the right
    /// shape — "subtitles stop, lips not synced, audio not synced" is one defect, not three.
    ///
    /// ⚠ CALLED DIRECTLY, NOT THROUGH CSWSObject::SendDialogEntry 0x004CD460 — deliberately,
    /// and this is the difference between a follower that moves and one that does not.
    ///
    /// The outer wrapper consults CSWSObject::IsDialogDelay first and, when that says "wait",
    /// QUEUES the entry (+0x4C, flag +0x48) and returns SUCCESS without sending anything.
    /// Measured on a live follower: `pending 1:29 sendGate 0 isDialogDelay 1` — the request
    /// for node 29 sat queued, the call kept reporting success, and the node never moved,
    /// through twelve hundred retries.
    ///
    /// That gate is not wrong; it is simply not ours. It exists to pace an OWNER's
    /// conversation against its own audio, so the next line does not tread on the current
    /// one. A follower's pacing authority is the other player: it should show the line the
    /// owner is on, when the owner is on it. Deferring to a local audio clock as well means
    /// deferring to the wrong clock, and the queue it deferred into is only ever flushed by
    /// UpdateDialog — which is to say, by the pacing this instance is not supposed to be
    /// running.
    ///
    /// The producer bounds-checks the entry index itself (0x005A41FD, against the entry count
    /// at +0x04) and is the same function the deferral would eventually have called, so going
    /// straight to it is the narrower path rather than the riskier one.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int, int*, int> SendDialogEntryFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int, int, int*, int>)0x005A4010;

    /// <summary>The status the producer writes back when it declined to send after all. The
    /// engine's own caller tests for exactly this and re-queues (0x004CD4BA).</summary>
    private const int DialogEntryDeferred = 0xA;

    /// <summary>CSWSObject + this = a dialog scratch field the engine clears immediately
    /// before every call to the producer (0x004CD4AB). Replicated rather than reasoned about:
    /// it is part of the call's contract at the only site that reaches it.</summary>
    private const int ObjDlgSendScratch = 0x1F4;

    /// <summary>
    /// The object that OWNS the live conversation, and its CSWSDialog. (0, 0) when there is
    /// no conversation on screen or the owner cannot be identified.
    ///
    /// "Owner" is the engine's own test, not ours: <c>CSWSObject::UpdateDialog</c> 0x004CD583
    /// refuses to run unless <c>[obj+0x54] == [obj+0x04]</c> — the dialog-owner id equals the
    /// object's own id — so that equality IS the definition of the object a conversation
    /// hangs off. Everything else is a mere participant, and driving an entry on one would
    /// write +0x1F4 on an object the engine never treats this way.
    ///
    /// The walk starts at the GUI's two participant ids, masking the flag bits exactly as
    /// <see cref="ReadDialoguePartnerTag"/> does, then hops once to the owner each names.
    /// Every pointer is guarded and the dialog is vtable-verified.
    /// </summary>
    public static bool TryGetActiveDialogue(out nint owner, out nint dialog)
    {
        owner = 0;
        dialog = 0;

        nint gui = GetInGameGui();
        if (gui == 0 || !ConversationLive(gui))
        {
            return false;
        }

        foreach (uint raw in stackalloc uint[]
                 { *(uint*)(gui + GuiParticipantA), *(uint*)(gui + GuiParticipantB) })
        {
            uint id = raw & 0x7FFFFFFF;
            if (id is 0 or ObjectInvalid or 0x7FFFFFFF)
            {
                continue;
            }

            nint participant = ResolveServerObject(id);
            if (participant == 0)
            {
                continue;
            }

            if (IsDialogueOwner(participant, out dialog))
            {
                owner = participant;
                _lastDialogueOwnerId = id;
                return true;
            }

            // A participant names its owner at +0x54. Follow it exactly ONCE — a chain would
            // mean the field is not what the engine says it is, and looping on a bad read is
            // how a frame hook becomes a hang.
            uint ownerId = *(uint*)(participant + ObjDialogOwner);
            if (ownerId is 0 or ObjectInvalid || ownerId == id)
            {
                continue;
            }

            nint candidate = ResolveServerObject(ownerId);
            if (candidate != 0 && IsDialogueOwner(candidate, out dialog))
            {
                owner = candidate;
                _lastDialogueOwnerId = ownerId;
                return true;
            }
        }

        // The owner found last time, while it still owns a conversation. A participant's link to
        // its owner is not steady: on the Star Forge the conversation hangs off an invisible
        // placeholder (sta_invis_bastila) and Carth's +0x54 dropped to OBJECT_INVALID between
        // his lines, so a follower lost the conversation it was in and sat on its first choice.
        if (_lastDialogueOwnerId is not (0 or ObjectInvalid))
        {
            nint remembered = ResolveServerObject(_lastDialogueOwnerId);
            if (remembered != 0 && IsDialogueOwner(remembered, out dialog))
            {
                owner = remembered;
                return true;
            }
        }

        return false;
    }

    /// <summary>The object id <see cref="TryGetActiveDialogue"/> last found owning the
    /// conversation through a participant's link. Only ever used again after
    /// <see cref="IsDialogueOwner"/> confirms it still owns one.</summary>
    private static uint _lastDialogueOwnerId;

    /// <summary>
    /// Why <see cref="TryGetActiveDialogue"/> found no conversation, as one line for the log:
    /// the GUI's live flag, each participant id it names, and for each one that resolves its
    /// own id, the owner id it names and whether its dialog pointer carries the CSWSDialog
    /// vtable. Read-only. MAIN THREAD ONLY.
    /// </summary>
    public static string DescribeActiveDialogueLookup()
    {
        nint gui = GetInGameGui();
        if (gui == 0)
        {
            return "no in-game GUI";
        }

        System.Text.StringBuilder line = new($"live {(ConversationLive(gui) ? 1 : 0)}");
        foreach (int offset in stackalloc int[] { GuiParticipantA, GuiParticipantB })
        {
            uint raw = *(uint*)(gui + offset);
            line.Append($"; participant 0x{raw:X}");
            uint id = raw & 0x7FFFFFFF;
            nint obj = id is 0 or ObjectInvalid or 0x7FFFFFFF ? 0 : ResolveServerObject(id);
            if (obj == 0)
            {
                line.Append(" (no server object)");
                continue;
            }

            nint dialogPtr = Deref(obj + ObjActiveDialog);
            bool isDialog = LooksLikeHeapPointer(dialogPtr) && *(nint*)dialogPtr == DialogVtable;
            line.Append($" id 0x{*(uint*)(obj + ObjIdOffset):X} owner 0x{*(uint*)(obj + ObjDialogOwner):X} dialog {(isDialog ? "yes" : "no")}");
        }

        return line.ToString();
    }

    /// <summary>Whether an object is the owner of a live conversation: it names itself as the
    /// dialog owner AND carries a pointer with the CSWSDialog vtable.</summary>
    private static bool IsDialogueOwner(nint obj, out nint dialog)
    {
        dialog = 0;

        if (*(uint*)(obj + ObjDialogOwner) != *(uint*)(obj + ObjIdOffset))
        {
            return false;
        }

        nint candidate = Deref(obj + ObjActiveDialog);
        if (!LooksLikeHeapPointer(candidate) || *(nint*)candidate != DialogVtable)
        {
            return false;
        }

        dialog = candidate;
        return true;
    }

    /// <summary>Sanity bound on a .dlg's entry count. Generous — the largest base-game file is
    /// nowhere near it — and its job is only to reject a pointer that is not a dialog.</summary>
    private const int MaxDialogueEntries = 4096;

    /// <summary>
    /// CSWSObject::IsDialogDelay — __thiscall, no stack arguments, plain <c>ret</c> (verified
    /// at 0x004CD41E). Returns 1 when the engine will DEFER an entry instead of sending it.
    /// It is the gate <see cref="DriveDialogueToNode"/> loses to, so being able to ask it
    /// directly is the difference between diagnosing a stuck follower and guessing at it.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> IsDialogDelayFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x004CD2D0;

    // The dialog bookkeeping on a CSWSObject, from the symbol database (+0x38..+0x44 are
    // named; +0x48..+0x50 are the unnamed neighbours SendDialogEntry uses for its deferral).
    private const int ObjDlgDelayDay = 0x38;
    private const int ObjDlgDelayMs = 0x3C;
    private const int ObjDlgLastDay = 0x40;
    private const int ObjDlgLastMs = 0x44;
    private const int ObjDlgPendingFlag = 0x48;
    private const int ObjDlgPendingEntry = 0x4C;
    private const int ObjDlgSendGate = 0x50;

    /// <summary>
    /// Why a node drive is or is not landing, as one line for the log. Read-only apart from
    /// the IsDialogDelay call, which is a pure predicate.
    ///
    /// This exists because "SendDialogEntry returned success" and "the line arrived" are
    /// different facts, and the first was mistaken for the second once already.
    /// </summary>
    public static string DescribeDialogueDrive()
    {
        if (!TryGetActiveDialogue(out nint owner, out nint dialog))
        {
            return "no conversation here";
        }

        return $"owner 0x{*(uint*)(owner + ObjIdOffset):X}"
            + $" node {*(int*)(dialog + DialogCurrentEntryIndex)}/{*(int*)(dialog + DialogEntriesCount)}"
            + $" pending {*(int*)(owner + ObjDlgPendingFlag)}:{*(int*)(owner + ObjDlgPendingEntry)}"
            + $" sendGate {*(int*)(owner + ObjDlgSendGate)}"
            // dialog+0x3C, NOT owner+0x3C (ObjDlgDelayMs is a different field on a different
            // struct despite the same numeric offset): this is the SECOND guard
            // CSWSObject::IsDialogDelay's skip branch reads at 0x004CD3BE — SkipDialogueLine's
            // flag is a no-op unless this is also nonzero. skipFlag is the raw ObjDlgSendScratch
            // byte itself, read BEFORE IsDialogDelayFn's own call can consume it below.
            + $" dialogAt0x3C {*(int*)(dialog + 0x3C)} skipFlag {*(int*)(owner + ObjDlgSendScratch)}"
            + $" isDialogDelay {IsDialogDelayFn(owner)}"
            + $" delay {*(uint*)(owner + ObjDlgDelayDay)}:{*(uint*)(owner + ObjDlgDelayMs)}"
            + $" last {*(uint*)(owner + ObjDlgLastDay)}:{*(uint*)(owner + ObjDlgLastMs)}";
    }

    /// <summary>
    /// The index of the dialogue entry on screen, or -1 when there is no conversation here or
    /// the index does not sit inside this instance's entry array.
    ///
    /// Bounds-checked against the dialog's OWN entry count, so a misread is published as
    /// "unknown" rather than as a node the peer would then be driven to. -1 is a first-class
    /// value on the wire for exactly that reason.
    /// </summary>
    public static int GetDialogueNodeIndex()
    {
        if (!TryGetActiveDialogue(out _, out nint dialog))
        {
            return -1;
        }

        int count = *(int*)(dialog + DialogEntriesCount);
        int index = *(int*)(dialog + DialogCurrentEntryIndex);
        return count is > 0 and <= MaxDialogueEntries && index >= 0 && index < count ? index : -1;
    }

    /// <summary>
    /// Drives the local conversation onto <paramref name="entryIndex"/> — the follower's half
    /// of node sync. False when there is no conversation here, when the index is not inside
    /// this instance's copy of the .dlg, or when we are already on that node.
    ///
    /// ⚠ ALREADY-THERE IS A REFUSAL, NOT A NO-OP WORTH DOING ANYWAY. Re-sending the current
    /// entry restarts the line and its voice-over, so a controller that drove every frame
    /// would hold the conversation on one node forever, stuttering its audio from the top —
    /// the same shape as the old blind pick replay, in a new place.
    ///
    /// ⚠ Bounds-checked against the LOCAL entry count. Both instances load the same file so
    /// the counts agree; the check is what turns a mismatched install (a mod on one side
    /// only) into a conversation that stops following, rather than the engine indexing a
    /// 0xB0-stride array past its end.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool DriveDialogueToNode(int entryIndex)
    {
        if (entryIndex < 0 || !TryGetActiveDialogue(out nint owner, out nint dialog))
        {
            return false;
        }

        int count = *(int*)(dialog + DialogEntriesCount);
        if (count is <= 0 or > MaxDialogueEntries || entryIndex >= count)
        {
            return false;
        }

        if (*(int*)(dialog + DialogCurrentEntryIndex) == entryIndex)
        {
            return false;
        }

        *(int*)(owner + ObjDlgSendScratch) = 0;
        // Discard a locally queued entry. Otherwise UpdateDialog sends it on the
        // next frame and undoes the acknowledged owner node (live: 28 -> 0).
        *(int*)(owner + ObjDlgPendingFlag) = 0;
        *(int*)(owner + ObjDlgPendingEntry) = -1;

        int status = 0;
        SendDialogEntryFn(dialog, owner, -1, entryIndex, &status);

        // The status, not the return value, is what the engine's own caller tests. Callers
        // here verify by RE-READING the node anyway — a producer that reports success is
        // still not proof a line arrived, which is the lesson that produced this comment.
        return status != DialogEntryDeferred;
    }

    /// <summary>
    /// Forces the OWNER's current dialogue line/VO to finish right now — the in-process
    /// equivalent of the retail skip keypress/click, and the mechanism
    /// docs/research/dialogue-skip-desync.md's earlier rounds could never reach because
    /// injected OS input never lands on this game. No OS input is involved here at all.
    ///
    /// Verified by disassembly:
    ///
    ///   * <c>CSWGuiDialog::HandleInputEvent 0x006A7230</c> is the panel's own input router.
    ///     Its case for event codes 0x27/0x2D (Enter and the click-equivalent — the SAME case
    ///     that selects the highlighted reply when the panel is showing the reply list) reads
    ///     a flag byte at <c>panel+0x1DF8</c>: bit0=0 selects the highlighted reply
    ///     (<c>*(panel+0x1C8A)</c> through vtable+0x6C, matching <see cref="SelectDialogueReply"/>'s
    ///     own mechanism); bit0=1,bit1=1 is a no-op; **bit0=1,bit1=0 — i.e. showing a LINE, not
    ///     a reply list, and not already mid-skip — calls
    ///     <c>CServerExoApp::SkipPastDialog 0x004AE970</c>**, passing the object id cached at
    ///     <c>panel+0x19C0</c> (defaults to the generic PLAYER sentinel <c>0x7F000000</c>,
    ///     confirmed by <c>fieldwrites.py 0x19C0</c> and the SAME constant
    ///     <c>CSWSObject::UpdateDialog</c>'s own owner-id gate uses).
    ///   * <c>SkipPastDialog</c> is a two-hop thunk (<c>0x004AE970 -> CServerExoAppInternal::
    ///     SkipPastDialog 0x004B1F90</c>) whose ENTIRE body, once it resolves the object id
    ///     through the server's own hashtable and vtable-confirms it as a <c>CSWSObject</c>
    ///     (<c>AsSWSObject</c>, vtable+0x10 — the SAME cast <c>docs/research/shared-state-audit.md</c>
    ///     already names), is <c>[resolved+0x1F4] = 1</c> — this repo's own
    ///     <see cref="ObjDlgSendScratch"/>, already named and already read/written by
    ///     <see cref="DriveDialogueToNode"/> above, from independent RE done before this pass
    ///     connected the two.
    ///   * <c>CSWSObject::IsDialogDelay 0x004CD2D0</c> is the SOLE consumer: at 0x004CD3B1 it
    ///     reads <c>[this+0x1F4]</c>, and when nonzero AND the active <c>CSWSDialog</c>'s own
    ///     <c>+0x3C</c> is ALSO nonzero (a "there really is a wait in progress" guard — a skip
    ///     with nothing playing is a genuine no-op, exactly like the retail keypress), it runs
    ///     the SAME finish-the-entry bookkeeping the natural end-of-VO path runs (marks
    ///     <c>+0x220</c>, clears the per-tick delay timers via the identical helper calls the
    ///     normal-completion branch makes) and returns 0 ("not delayed") instead of 1 — which
    ///     is precisely what lets the next <c>UpdateDialog</c> tick's own
    ///     <c>SendDialogEntry</c> call proceed to the NEXT entry, as if the VO had simply
    ///     finished on its own. Confirmed rather than assumed: a flag the per-tick driver
    ///     checks, not a raw call and not a timer zero.
    ///
    /// Writing the flag directly on the ALREADY-RESOLVED, ALREADY-VTABLE-VERIFIED owner
    /// <see cref="TryGetActiveDialogue"/> hands back is deliberately smaller than replaying
    /// <c>SkipPastDialog</c>'s own id-hashtable lookup: same effect, no second resolution path
    /// to get wrong, and it matches this codebase's own "prefer a flag-set over a raw call"
    /// rule (docs' engine-write-rules memory). The one thing this does NOT replicate is
    /// <c>panel+0x19C0</c>'s object id, which only matters if that id could ever differ from
    /// the owner <see cref="TryGetActiveDialogue"/> resolves — it defaults to the generic
    /// player id and <see cref="TryGetActiveDialogue"/>'s own owner test is the engine's
    /// definition of the ticked object, so in practice they name the same object; not
    /// independently proven live that they always would (NEEDS-LIVE, but low risk — a wrong
    /// object here means the flag is a no-op, not a crash, exactly like a stale
    /// <see cref="ObjDlgSendScratch"/> write is elsewhere).
    ///
    /// Meaningful only for the DIALOGUE OWNER (see <see cref="TryGetActiveDialogue"/>'s own
    /// definition) — a follower's instance does not drive VO timing at all, so setting this
    /// flag on a non-owner would touch a field <c>IsDialogDelay</c> never reads for that
    /// object. Returns false (no-op) when there is no active conversation to skip.
    /// </summary>
    public static bool SkipDialogueLine(out int nodeBefore, out int entryCount)
    {
        nodeBefore = -1;
        entryCount = 0;

        if (!TryGetActiveDialogue(out nint owner, out nint dialog))
        {
            return false;
        }

        entryCount = *(int*)(dialog + DialogEntriesCount);
        nodeBefore = *(int*)(dialog + DialogCurrentEntryIndex);

        *(int*)(owner + ObjDlgSendScratch) = 1;
        return true;
    }

    /// <summary>
    /// CClientExoAppInternal::BarkString(objectId, a2, a3, a4) — <c>ret 0x10</c>. The real
    /// implementation, and the point every bark converges on.
    ///
    /// ⚠ Called DIRECTLY, with the client internal as `this` — not through the
    /// <c>CServerExoApp::BarkString 0x005EDBB0</c> thunk. The thunk is
    /// <c>mov ecx,[ecx+4]; jmp here</c>, i.e. it expects a `this` whose +0x04 is a
    /// CClientExoAppInternal. Handing it the SERVER app (whose +0x04 is a server internal)
    /// makes this function run against an object of the wrong class, and the first
    /// vtable dispatch inside it goes somewhere arbitrary. That is not theoretical: the
    /// first version did exactly that, and the instance it was replayed on died with no
    /// crash-probe line and no event-log entry — the replay never even reached its own log
    /// statement. The internal is the same pointer <see cref="GetInGameGui"/> already walks
    /// to (<c>[client + 0x04]</c>).
    /// </summary>
    /// <summary>
    /// CClientExoAppInternal::BarkString(uint clientObjectId, CExoString* text,
    /// CResRef* sound, int ignoreRange) — <c>ret 0x10</c>, verified at 0x005F2CCD.
    ///
    /// ⚠ ARGUMENTS 2 AND 3 ARE POINTERS. Forwarding them across processes is what killed the
    /// engine twice; both are dereferenced on BOTH branches of 0x005F2CA8, so the
    /// speaker-not-found path is no safer. They are rebuilt locally instead — see
    /// <see cref="PlayBark"/> and docs/research/bark-replay.md.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, byte*, byte*, int, void> BarkStringFn =
        (delegate* unmanaged[Thiscall]<nint, uint, byte*, byte*, int, void>)0x005F2C90;

    /// <summary>CClientExoAppInternal, or 0. Exists from startup, menu included.</summary>
    private static nint GetClientInternal()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return 0;
        }

        nint internalApp = Deref(client + AppClientOffset);
        return LooksLikeHeapPointer(internalApp) ? internalApp : 0;
    }

    /// <summary>
    /// Replays a bark a peer's instance produced, on this instance's copy of the same
    /// creature. The arguments are the captured ones, forwarded verbatim — see
    /// <see cref="BarkHook"/> for why they are not interpreted.
    ///
    /// Bracketed with <see cref="BarkHook.Replaying"/> so the capture hook ignores the bark
    /// we are playing back; without it a bark would bounce between instances forever.
    ///
    /// <paramref name="suppressCapture"/> is false ONLY for the test hook, which needs a bark
    /// this instance produces to be captured exactly as a game-produced one is — that round
    /// trip is the only way the capture stub's stack arithmetic can be checked without a
    /// human playing the game, and it shipped wrong twice for want of it.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>

}
