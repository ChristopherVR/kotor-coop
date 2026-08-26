// Odyssey.Dialogue.Follow.cs - following a peer into and through a dialogue.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- dialogue follow ---------------------------------------------------------
    //
    // The spectator runs the SAME conversation in the engine's own UI. Three levers:
    //
    //   open   CSWCCreature::ActionInitiateDialog (0x0060F620) on the NPC's client twin —
    //          the exact path a click on that NPC takes, ending in
    //          SendPlayerToServerInput_Dialog, so the server builds a real CSWSDialog.
    //   follow CSWGuiDialog::SelectReply (0x006A7110, thiscall(int)) — the click-reply-N
    //          entry. Replaying the owner's slot walks our copy of the same .dlg down the
    //          same branch.
    //   mute   DialogueReplyHook refuses CSWGuiDialog::SelectReply on the follower. That is
    //          the whole reply block.
    //          ⚠ NOT CClientExoApp::SetCanSendDialog (0x005EDC80), which reads like the
    //          right lever and is not one: its only consumer is IsDialogDelay 0x004CD2D0,
    //          so clearing it stalls the follower's own conversation ADVANCE — killing the
    //          voice-over and the lipsync with it. See DialogueSpectateController.

    /// <summary>CSWSObject + this = the object's tag (CExoString).</summary>
    private const int ObjTag = 0x18;

    /// <summary>
    /// CGuiInGame + this = the live CSWGuiDialog panel.
    ///
    /// ⚠ This was 0x78, which is the PARTY SELECTION panel, and the mistake was silent and
    /// expensive. Verified in the binary: <c>CGuiInGame::ShowDialogEntry 0x0062B730</c> and
    /// <c>ShowDialogReplies 0x006340E0</c> both load the panel from <c>[esi+0x3C]</c>
    /// (0x0062B758, 0x0062B795, 0x0062B7B6, 0x0063411B), while <c>ShowPartySelection
    /// 0x0062DD20</c> is the one that loads <c>[esi+0x78]</c> (0x0062DDC6). The symbol
    /// database agrees — it names +0x78 party_selection and leaves a gap at +0x3C.
    ///
    /// What the wrong offset cost: <see cref="SelectDialogueReply"/> read its reply count out
    /// of the party-selection panel, failed the 1..64 range test and returned false forever,
    /// so a FOLLOWER NEVER REPLAYED THE OWNER'S PICK. The follow then starved, hit the
    /// content timeout, dropped its reply suppression — and both players could answer the
    /// same conversation, independently. That is the reported bug.
    /// </summary>
    private const int GuiDialogPanel = 0x3C;

    /// <summary>CSWGuiDialog + this = how many reply slots are on screen.</summary>
    private const int DialogPanelReplyCount = 0x6C;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> ActionInitiateDialogFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x0060F620;

    /// <summary>
    /// CSWCMessage::SendPlayerToServerInput_Dialog(objectId) — "I want to talk to that
    /// object". ret 4, one argument, and it self-guards on a conversation already being
    /// active (0x00677EB3), so it cannot start a second one.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, void> SendDialogInputFn =
        (delegate* unmanaged[Thiscall]<nint, uint, void>)0x00677EA0;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetCanSendDialogFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x005EDC80;

    private static readonly delegate* unmanaged[Thiscall]<nint, void> CloseDialogFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x006332B0;

    /// <summary>
    /// CSWSObject::StopDialog — the SERVER-side end of a conversation, and the only complete
    /// one. On the object that OWNS the dialog it runs the end-conversation script, clears
    /// the participants' dialog fields and destructs the CSWSDialog, whose destructor calls
    /// <c>CSWSDialog::Cleanup</c> — which is what calls <c>CGuiInGame::CloseDialog</c>
    /// (0x005A2841). On any other participant it forwards to the owner's
    /// <c>RemoveObjectFromDialog</c>, which ends the whole thing once the last player leaves.
    /// It is what the engine itself runs at a natural end (<c>CSWSDialog::HandleReply</c>
    /// 0x005A04AD) and when combat interrupts one (<c>CSWSCreature::ActivityManager</c>).
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> StopDialogFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x004CB4F0;

    /// <summary>CSWSObject + this = the id of the object that owns the conversation this one
    /// is in, or OBJECT_INVALID. Verified against the disassembly of StopDialog, which
    /// compares it with the object's own id at +0x04 to decide owner from participant.</summary>
    private const int ObjDialogOwner = 0x54;

    /// <summary>Copies a creature's tag into an ASCII buffer. Empty when unavailable.</summary>
    public static void ReadObjectTag(nint obj, byte* dest, int cap)
    {
        if (cap <= 0)
        {
            return;
        }

        dest[0] = 0;
        if (obj == 0)
        {
            return;
        }

        ReadExoStringInto(obj + ObjTag, dest, cap);
    }

    /// <summary>
    /// Whether an object carries <paramref name="tag"/>, case-insensitively — the engine
    /// treats tags that way and so does the dialogue gate.
    ///
    /// Used to re-verify a cached pointer before acting on it: an object id and a raw pointer
    /// can both survive into memory that has been reused, and the tag is the thing the caller
    /// actually meant.
    /// </summary>
    public static bool HasTag(nint obj, ReadOnlySpan<byte> tag)
    {
        if (obj == 0 || tag.IsEmpty || tag.Length >= 64)
        {
            return false;
        }

        byte* actual = stackalloc byte[64];
        ReadObjectTag(obj, actual, 64);

        for (int i = 0; i < tag.Length; i++)
        {
            if (actual[i] == 0 || Lower(actual[i]) != Lower(tag[i]))
            {
                return false;
            }
        }

        return actual[tag.Length] == 0;

        static byte Lower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;
    }

    /// <summary>The tag of whoever the local player is currently in conversation with, or
    /// empty. Reads the client GUI's participant ids, skipping the player itself.</summary>
    public static void ReadDialoguePartnerTag(byte* dest, int cap)
    {
        if (cap > 0)
        {
            dest[0] = 0;
        }

        nint gui = GetInGameGui();
        if (gui == 0 || !ConversationLive(gui))
        {
            return;
        }

        uint player = GetPlayerObjectId();
        foreach (uint raw in stackalloc uint[] { *(uint*)(gui + GuiParticipantA), *(uint*)(gui + GuiParticipantB) })
        {
            // ⚠ The participant fields carry FLAG BITS above the object id. Measured live
            // during a Trask conversation: the field read 0x80000127 while the creature's
            // own id was 0x127, and 0xFFFFFFFF stood for the other (player) side. Resolving
            // the raw value found nothing, so the partner tag came out empty and no
            // spectator could ever open the conversation. Mask the top bit off.
            uint id = raw & 0x7FFFFFFF;

            if (id is 0 or ObjectInvalid or 0x7FFFFFFF || id == player)
            {
                continue;
            }

            nint obj = ResolveServerObject(id);
            if (obj != 0)
            {
                ReadObjectTag(obj, dest, cap);
                if (dest[0] != 0)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Opens a conversation against the creature with <paramref name="tag"/>, exactly as a
    /// click on it would. Returns false when the creature is not in this module, a
    /// conversation is already running, or the twin does not exist yet.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool StartDialogueWith(ReadOnlySpan<byte> tag)
    {
        nint gui = GetInGameGui();
        if (gui == 0 || ConversationLive(gui))
        {
            return false; // no HUD, or we are already talking
        }

        nint creature = CreatureFinder.FindByTag(tag);
        if (creature == 0)
        {
            return false;
        }

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint message = GetSWCMessageFn(client);
        if (message == 0)
        {
            return false;
        }

        // ⚠ Send the request DIRECTLY rather than through CSWCCreature::ActionInitiateDialog.
        //
        // The wrapper is what a mouse click runs, and it works for a click — but driven from
        // here it left the conversation half-started: measured live, the client marked a
        // conversation active while both participant ids stayed OBJECT_INVALID and no entry
        // ever arrived. Its client-side gates (an unset selection, the creature's own
        // interaction state) are satisfied by the act of clicking and not by us. The send
        // itself is the part that reaches the server, takes one argument, and self-guards on
        // a conversation already being active, so it is both the smaller and the safer half.
        uint objectId = *(uint*)(creature + ObjIdOffset);
        if (objectId is 0 or ObjectInvalid)
        {
            return false;
        }

        // dialogue_join_teleport (default ON since 2026-08-21 — see docs/research/dialogue-join-latency.md
        // "Far-join live pass"). This is what an automatic join (force_dialogue_party, or the
        // test hook) has that a real mouse click does not: nobody is driving the PC toward the
        // NPC. Measured live: a joiner left standing 14m away never reaches StartDialog at
        // all — CSWSObject::AIActionDialogObject's own range gate (VERIFIED live at 10.0
        // units, `[0x747450]`, disasm 0x0057A7B2-0x0057A7AC) re-queues itself forever because
        // this build's player character is drive-controlled, not action-queue-walked (see
        // TestDriveController.HandleWalk's own finding). So without help the join just sits,
        // silently, until this instance's own 15 s local-open grace gives up on it.
        if (DialogueJoinTeleportEnabled)
        {
            TryTeleportForJoin(creature);
        }

        SendDialogInputFn(message, objectId);
        return true;
    }

    /// <summary>CSWSObject + this = the object's id.</summary>
    private const int ObjIdOffset = 0x04;

    /// <summary><c>coop.ini dialogue_join_teleport</c> (default on). Set once from
    /// <c>PluginConfig</c> at startup, the same way <see cref="DialogueGateHook.Enabled"/> is.</summary>
    public static bool DialogueJoinTeleportEnabled;

    /// <summary>
    /// The conversation-start range check's own threshold, VERIFIED LIVE by reading
    /// <c>[0x747450]</c> out of a running instance (read 10.0f) — the same float
    /// <c>CSWSObject::AIActionDialogObject</c> squares and compares a candidate's distance
    /// against at 0x0057A799-0x0057A7A5 before it will queue the conversation for real. A
    /// player standing farther than this from the NPC never reaches
    /// <c>CSWSObject::StartDialog</c> without either walking there or being placed there.
    /// </summary>
    private const float DialogueStartRange = 10.0f;

    /// <summary>Where <see cref="TryTeleportForJoin"/> found the local player before it moved
    /// them, so the conversation's end can put them back. <see cref="_joinTeleportPending"/>
    /// is false whenever there is nothing to restore — including the ordinary case where the
    /// player was already close enough that no teleport happened.</summary>
    private static Vec3 _joinTeleportOrigin;
    private static bool _joinTeleportPending;

    /// <summary>
    /// Places the local player next to <paramref name="creature"/> when they are farther than
    /// the engine's own start-range check allows, so the join resolves immediately instead of
    /// waiting on a walk nothing in this build will actually perform for a player character.
    /// Remembers the original spot; <see cref="RestoreJoinTeleportIfPending"/> puts it back
    /// once the followed conversation concludes.
    ///
    /// A no-op (and nothing is remembered) when the player is already in range — the ordinary
    /// case for a real mouse click, which this flag must not visibly disturb.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    private static void TryTeleportForJoin(nint creature)
    {
        nint player = GetPlayerCreaturePointer();
        if (player == 0)
        {
            return;
        }

        Vec3 origin = GetPosition(player);
        Vec3 target = GetPosition(creature);

        float dx = target.X - origin.X;
        float dy = target.Y - origin.Y;
        float dz = target.Z - origin.Z;
        if (dx * dx + dy * dy + dz * dz <= DialogueStartRange * DialogueStartRange)
        {
            return; // already close enough; the engine's own check will pass unaided
        }

        // A couple of units short of the NPC, not on top of it — SnapTo's own action clear is
        // what actually matters here (see its doc: setting position alone does not stick while
        // a move action still owns it), the exact offset just keeps the player out of the
        // NPC's collision.
        Vec3 destination = new() { X = target.X + 1.5f, Y = target.Y, Z = target.Z };

        _joinTeleportOrigin = origin;
        _joinTeleportPending = true;
        SnapTo(player, destination);
    }

    /// <summary>
    /// Puts the local player back where <see cref="TryTeleportForJoin"/> found them, once the
    /// followed conversation is over. Idempotent: does nothing when nothing is pending, which
    /// covers every join that never needed to move the player at all.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void RestoreJoinTeleportIfPending()
    {
        if (!_joinTeleportPending)
        {
            return;
        }

        _joinTeleportPending = false;

        nint player = GetPlayerCreaturePointer();
        if (player != 0)
        {
            SnapTo(player, _joinTeleportOrigin);
        }
    }

    /// <summary>
    /// Breaks a conversation shell that <see cref="ForceCloseDialogueUi"/> alone cannot close.
    ///
    /// ⚠ LIVE-VERIFIED, and it is why this exists rather than another retry of the UI close.
    /// A joiner left too far from the NPC (see <see cref="TryTeleportForJoin"/>'s own note)
    /// gets stuck with the engine's own conversation-active flag (<c>gui+0xB4</c>) re-armed
    /// every tick by the still-queued <c>AIActionDialogObject</c>, and <c>ForceCloseDialogueUi</c>
    /// — which only closes the client GUI panel — cannot touch that: measured live, it repeated
    /// every <c>YieldCloseRetryMs</c> for over two minutes straight with no effect.
    /// <c>CheatController</c>'s own <c>tp</c> (via <see cref="SnapTo"/>, which clears the
    /// object's action queue before moving it) stopped the loop within one cycle, every time it
    /// was tried; plain position writes and plain UI closes did not. Snaps the player back to
    /// their OWN current position — nothing about where they stand should change, only that
    /// the stuck action is gone.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void ClearLocalPlayerStuckDialogueAction()
    {
        nint player = GetPlayerCreaturePointer();
        if (player != 0)
        {
            SnapTo(player, GetPosition(player));
        }
    }

    /// <summary>
    /// CGuiInGame holds FOUR dialogue panels, one per class, at their own members (symbol
    /// database): the ordinary one at +0x3C, then cinematic, computer and computer-camera.
    /// A conversation drives exactly one of them.
    /// </summary>
    private static ReadOnlySpan<int> GuiDialogPanels => [0x3C, 0x40, 0x44, 0x48];

    /// <summary>The four dialogue panel vtables, so a candidate pointer is identified rather
    /// than assumed. Order matches nothing — membership is the test. (uint, not nint: a span
    /// of a platform-sized type cannot be a compile-time blob.)</summary>
    private static ReadOnlySpan<uint> DialogPanelVtables =>
    [
        0x007559E0, // CSWGuiDialog
        0x00755800, // CSWGuiDialogCinematic
        0x00755888, // CSWGuiDialogComputer
        0x00755958, // CSWGuiDialogComputerCamera
    ];

    /// <summary>
    /// The live conversation panel, or 0.
    ///
    /// ⚠ This used to read ONE member and assume it. That is wrong twice over: the member it
    /// read was +0x78, the party-selection panel (see <see cref="GuiDialogPanel"/>), and even
    /// the right member only covers ordinary conversations — KOTOR's story dialogue runs on
    /// the CINEMATIC panel, a different object at a different offset. A follower therefore
    /// read its reply count out of a panel that was not in the conversation and never
    /// replayed the owner's pick.
    ///
    /// The panel is now IDENTIFIED: each candidate must carry one of the four dialogue panel
    /// vtables, and the one actually showing replies wins. A panel with a plausible reply
    /// count is the conversation in progress; failing that, the first identified panel is
    /// returned so callers that only test for existence still work.
    /// </summary>
    private static nint GetDialoguePanel()
    {
        nint gui = GetInGameGui();
        if (gui == 0)
        {
            return 0;
        }

        nint identified = 0;

        foreach (int offset in GuiDialogPanels)
        {
            nint panel = Deref(gui + offset);
            if (!LooksLikeHeapPointer(panel))
            {
                continue;
            }

            nint vtable = *(nint*)panel;
            bool known = false;
            foreach (uint candidate in DialogPanelVtables)
            {
                if (vtable == (nint)candidate)
                {
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                continue;
            }

            identified = identified == 0 ? panel : identified;

            int count = *(int*)(panel + DialogPanelReplyCount);
            if (count is > 0 and <= 64)
            {
                return panel; // this one is showing a reply list: it is the live conversation
            }
        }

        return identified;
    }

    /// <summary>
    /// Replays a reply the conversation's owner picked. Bounds-checked against the reply
    /// count the panel is actually showing, so a slot from a diverged tree is dropped
    /// rather than dispatched — SelectReply itself would reject it, but a stale index is a
    /// signal worth not acting on.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool SelectDialogueReply(int slot)
    {
        nint panel = GetDialoguePanel();
        if (panel == 0)
        {
            return false;
        }

        int count = *(int*)(panel + DialogPanelReplyCount);
        if (count is <= 0 or > 64 || slot < 0 || slot >= count)
        {
            return false;
        }

        // ⚠ THROUGH THE PANEL'S OWN VTABLE SLOT, never the hard-coded address. The four
        // dialogue panel classes do not share one SelectReply: the cinematic panel's slot is
        // 0x006A7E20, not 0x006A7110, and calling the base implementation with a cinematic
        // `this` is exactly the class of mistake the engine's own dispatch exists to avoid.
        // The engine itself commits through `call [eax+0x6C]` in HandleSelectReply.
        nint vtable = *(nint*)panel;
        var selectReply = (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)(vtable + PanelSelectReplySlot));
        if (!LooksLikeCodePointer((nint)selectReply))
        {
            return false;
        }

        // Bracketed so the one pick we dispatch passes the follower's own-click block.
        DialogueReplyHook.AllowReplay = true;
        try
        {
            selectReply(panel, slot);
        }
        finally
        {
            DialogueReplyHook.AllowReplay = false;
        }

        return true;
    }

    /// <summary>CSWGuiDialog vtable + this = SelectReply(int) — the slot HandleSelectReply
    /// 0x006A7390 itself dispatches through (<c>call [eax+0x6C]</c>).</summary>
    private const int PanelSelectReplySlot = 0x6C;

}
