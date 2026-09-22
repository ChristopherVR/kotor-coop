// Odyssey.Dialogue.Spectate.cs - spectating a peer's in-progress dialogue.
// Owns: locating the live in-game GUI/HUD and the "conversation active" flag
// (GetInGameGui, GetMainInterface, ConversationLive, IsConversationActive), the CSWSDialog/
// CGuiInGame field offsets that back node sync and spectate reads, and the read-only dialogue
// history ring reader (TryReadActiveDialogue) used to show a spectator what line is on screen.
// Read-only, main-thread; no reply suppression is implemented here (spectate only).

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- dialogue spectate -------------------------------------------------------
    //
    // Read-only, main-thread. Per docs/research/dialogue-sync.md: the client
    // "conversation active" flag is *(CGuiInGame + 0xB4); the two participants are object
    // ids at gui+0x170 / gui+0x174; the live CSWSDialog is *(CSWSObject_owner + 0x34)
    // (vtable 0x0074A618); each entry node carries a speaker CExoString @ +0x00 and a text
    // CExoLocString @ +0x08. Every pointer is guarded — a null or garbage read bails to
    // empty and never faults, because a crashing conversation is unacceptable (spectate
    // only; the suppression lever is deliberately NOT implemented — it is NEEDS-LIVE).

    /// <summary>CGuiInGame + this = nonzero while a conversation is on screen (client).</summary>
    private const int GuiConversationActive = 0xB4;

    /// <summary>CGuiInGame + this = the two dialog participants, as object ids.</summary>
    private const int GuiParticipantA = 0x170;
    private const int GuiParticipantB = 0x174;

    /// <summary>CSWSObject + this = active CSWSDialog* while it runs a conversation, else null.</summary>
    private const int ObjActiveDialog = 0x34;

    /// <summary>CSWSDialog vtable — the guard that a +0x34 pointer really is a live dialog.</summary>
    private const nint DialogVtable = 0x0074A618;

    private const int DialogEntriesCount = 0x04; // int
    private const int DialogEntries = 0x08;       // CSWSDialogEntry*, stride 0xB0
    private const int DialogEntryStride = 0xB0;

    /// <summary>
    /// CSWSDialog + this = the index of the entry currently on screen.
    ///
    /// ⚠ NOT +0x60. That was a candidate picked out of the "+0x60..+0x74 band" without a
    /// proof, and it is the wrong dword. The right one is settled twice over, both times by
    /// the engine using it as an index rather than by inference:
    ///
    ///   * <c>CSWSDialog::SendDialogEntry</c> loads it at 0x005A4045 and indexes the entry
    ///     array with it — <c>imul eax, eax, 0xB0</c> against <c>[esi+0x08]</c>, and 0xB0 is
    ///     exactly <c>sizeof(CSWSDialogEntry)</c> in the symbol database, with +0x08 exactly
    ///     the <c>entries</c> pointer;
    ///   * <c>CSWSDialog::HandleReply</c> compares the caller's claimed entry index against
    ///     it at 0x005A0403 and refuses the reply when they differ. That is the engine's OWN
    ///     desync check, on this field, which makes it the field two instances have to agree
    ///     on by the engine's own definition of agreement.
    /// </summary>
    private const int DialogCurrentEntryIndex = 0x70;

    // CSWSDialogNode (at the start of each entry): speaker CExoString @0, text CExoLocString @8.
    private const int NodeSpeaker = 0x00;
    private const int NodeText = 0x08;

    /// <summary>Range-only pointer sanity (a char buffer need not be 4-aligned).</summary>
    private static bool LooksLikeDataPointer(nint p) => p is > 0x10000 and < 0x7FFF0000;

    /// <summary>The live in-game GUI (CGuiInGame), or 0 when not in a loaded module.</summary>
    private static nint GetInGameGui()
    {
        // Mid-session loads tear down and replace this chain. A pointer that merely falls in
        // the process heap range can still name an uncommitted/freed region, so validate each
        // field before reading it. This is a readability check, not proof of object identity.
        if (!NativeMethods.IsReadable(AppManagerPtr, (nuint)sizeof(nint)))
        {
            return 0;
        }

        nint appManager = Deref(AppManagerPtr);
        if (!LooksLikeHeapPointer(appManager)
            || !NativeMethods.IsReadable(appManager + AppClientOffset, (nuint)sizeof(nint)))
        {
            return 0;
        }

        nint client = Deref(appManager + AppClientOffset);
        if (!LooksLikeHeapPointer(client)
            || !NativeMethods.IsReadable(client + AppClientOffset, (nuint)sizeof(nint)))
        {
            return 0;
        }

        nint clientInternal = Deref(client + AppClientOffset);
        if (!LooksLikeHeapPointer(clientInternal)
            || !NativeMethods.IsReadable(clientInternal + ClientInternalGuiOffset, (nuint)sizeof(nint)))
        {
            return 0;
        }

        nint gui = Deref(clientInternal + ClientInternalGuiOffset);
        // DialogueHasContent reads participant ids through +0x174. Requiring that complete
        // prefix makes the flag and participant fields safe for every dialogue reader.
        return LooksLikeHeapPointer(gui) && NativeMethods.IsReadable(gui, MinimumDialogueGuiReadableBytes)
            ? gui
            : 0;
    }

    private const int ClientInternalGuiOffset = 0x40;
    private const nuint MinimumDialogueGuiReadableBytes = 0x178;

    /// <summary>True while the player is in a loaded module (not a menu/load screen), so a chat
    /// key should open the chat prompt rather than do nothing at the main menu.</summary>
    public static bool IsInGame => GetInGameGui() != 0;

    /// <summary>CGuiInGame + this = CSWGuiMainInterface* — the HUD panel: minimap, party
    /// portraits, action bar (docs/research/ingame-gui-transition.md §"main_interface").</summary>
    private const int GuiMainInterface = 0x90;

    /// <summary>CSWGuiMainInterface's vtable (symbol database). Checking it EXACTLY is what
    /// lets the widget offsets inside the object be trusted: a pointer with this vtable is
    /// this class, laid out the way the database says.</summary>
    private const nint MainInterfaceVtable = 0x00753F50;

    /// <summary>The live HUD object, vtable-verified, or 0 (no module loaded, HUD torn
    /// down for a transition, or a pointer that is not what the layout says it is).</summary>
    public static nint GetMainInterface()
    {
        nint gui = GetInGameGui();
        if (gui == 0)
        {
            return 0;
        }

        nint mainInterface = Deref(gui + GuiMainInterface);
        return mainInterface != 0
            && LooksLikeHeapPointer(mainInterface)
            && *(nint*)mainInterface == MainInterfaceVtable
            ? mainInterface
            : 0;
    }

    /// <summary>True while a conversation is on screen, so the chat key defers to dialogue.</summary>
    public static bool IsConversationActive
    {
        get
        {
            nint gui = GetInGameGui();
            return gui != 0 && ConversationLive(gui);
        }
    }

    /// <summary>
    /// Whether the client GUI has a conversation on screen.
    ///
    /// MEASURED, because <c>GetIsConversationActive 0x0053EF70</c> reads this field and then
    /// passes it to an object lookup (0x0053EF83), which reads like an object id. It is not:
    /// sampled live across a whole conversation it only ever holds 0 or 1, and the engine's
    /// own teardown clears it — a host that ended a conversation properly read
    /// <c>0xB0=0, 0xB4=0</c> on the next sample.
    ///
    /// ⚠ Do not "improve" this into a lookup of <c>+0xB4</c> as an id. That was tried: it
    /// resolves only because object id 1 happens to exist, and asking the wrong table
    /// (the 0x007A39FC client chain rather than 0x007A3A00) made every conversation in the
    /// game read as inactive. A flag test is what the field supports.
    ///
    /// A NON-ZERO VALUE HERE IS NOT PROOF OF A HEALTHY CONVERSATION. A client whose server
    /// half still holds an unfinished conversation re-sets this every frame, with the GUI's
    /// participants both reading OBJECT_INVALID — see <see cref="CloseDialogue"/>, which is
    /// where that case has to be ended.
    /// </summary>
    private static bool ConversationLive(nint gui) => gui != 0
        && NativeMethods.IsReadable(gui + GuiConversationActive, (nuint)sizeof(int))
        && *(int*)(gui + GuiConversationActive) != 0;

    /// <summary>
    /// Reads the speaker and line of the conversation the local player is currently in, into
    /// two caller-owned null-terminated ASCII buffers. Returns true when a conversation is on
    /// screen (the buffers may still be empty if the current line could not be resolved),
    /// false when the player is not in a conversation.
    ///
    /// Base-game lines are TLK StrRefs with no inline substring; those resolve to empty here
    /// (resolving the TLK is out of scope). Custom / inline-text lines are read in full.
    /// MAIN THREAD ONLY.
    /// </summary>
    // The read surface is CGuiInGame's own dialogue HISTORY ring — the log every displayed
    // line is appended to. Located live by finding the on-screen sentence in
    // the heap and walking back to who held it (docs/research/dialogue-sync.md):
    //
    //   gui + 0x1040  capacity (reads 0x40 = 64)
    //   gui + 0x1054  first record; stride 0x10
    //   record: { char* text; u32 len; char* speaker; u32 speakerLen }   (lens include the null)
    //
    // This beats reading the CSWSDialog tree on every axis: the text is already
    // TLK-RESOLVED (a base-game line's CExoLocString carries only a StrRef, which is why
    // the tree read produced empty lines), the speaker is the display name, and it works
    // for every dialog variant — the cinematic letterbox panel stores its message somewhere
    // else entirely, but every variant appends here.
    //
    // "Which line is current" falls out of change tracking: the newest line is the
    // highest-offset slot that changed most recently. An append changes one slot (the
    // newest); a shift-when-full changes many, of which the newest is the highest; a wrap
    // changes exactly the newest. All three cases agree with this rule.

    /// <summary>CGuiInGame + this = first record of the dialogue history ring.</summary>
    private const int GuiDialogueRing = 0x1054;

    private const int GuiDialogueRingSlots = 64;

    private const int GuiDialogueRecordStride = 0x10;

    private static readonly uint[] RingSignatures = new uint[GuiDialogueRingSlots];

    private static int _ringNewest = -1;

    public static bool TryReadActiveDialogue(byte* speaker, int speakerCap, byte* line, int lineCap)
    {
        if (speaker != null && speakerCap > 0)
        {
            speaker[0] = 0;
        }

        if (line != null && lineCap > 0)
        {
            line[0] = 0;
        }

        nint gui = GetInGameGui();
        if (gui == 0)
        {
            _ringNewest = -1;
            return false;
        }

        // Track ring changes EVERY frame, active or not, so the first frame of a
        // conversation already knows which slot is newest (all populated slots differ from
        // the zeroed signature table on the first pass; the highest wins, which is the
        // newest under the append order observed live).
        int changed = -1;
        for (int i = 0; i < GuiDialogueRingSlots; i++)
        {
            nint record = gui + GuiDialogueRing + i * GuiDialogueRecordStride;
            uint signature = (uint)*(nint*)record ^ (uint)(*(int*)(record + 4) * 2654435761);
            if (signature != RingSignatures[i])
            {
                RingSignatures[i] = signature;
                changed = i;
            }
        }

        if (changed >= 0)
        {
            _ringNewest = changed;
        }

        if (!ConversationLive(gui))
        {
            return false; // no conversation on screen
        }

        if (_ringNewest < 0)
        {
            return true; // active but no line seen yet -> empty
        }

        nint newest = gui + GuiDialogueRing + _ringNewest * GuiDialogueRecordStride;

        if (line != null)
        {
            nint text = *(nint*)newest;
            int textLen = *(int*)(newest + 4);
            if (LooksLikeDataPointer(text) && textLen is > 1 and <= 0x400)
            {
                CopyAsciiBounded(text, textLen - 1, line, lineCap);
            }
        }

        if (speaker != null)
        {
            nint name = *(nint*)(newest + 8);
            int nameLen = *(int*)(newest + 12);
            if (LooksLikeDataPointer(name) && nameLen is > 1 and <= 0x40)
            {
                CopyAsciiBounded(name, nameLen - 1, speaker, speakerCap);
            }
        }

        return true;
    }

}
