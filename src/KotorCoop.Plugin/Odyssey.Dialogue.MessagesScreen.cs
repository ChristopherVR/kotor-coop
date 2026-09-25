// Odyssey.Dialogue.MessagesScreen.cs - the WRITE side of the retail Messages screen's
// dialogue-history ring. Odyssey.Dialogue.Spectate.cs documents itself as read-only; this file
// is deliberately separate because it calls into the engine instead of only reading its memory.
//
// docs/research/chat-ui.md has the full research trail. Summary of what is VERIFIED here by
// static disassembly + the symbol database (tools/q.py, tools/disasm.py), cross-checked against
// each other (the DB's own field names/types for CGuiInGameDialogMessage match the byte offsets
// the disassembly reads):
//
//   CGuiInGame::AppendToDialogBuffer  0x0062B680  __thiscall, ret 8
//     void AppendToDialogBuffer(CGuiInGame* this, CExoString* message, CExoString* speaker);
//
// Guard (0x0062B683-0x0062B693): calls CExoString::operator!=_2 (0x005E5390) comparing
// *message* against a global empty CExoString at 0x0073D71C; a message that compares equal
// (empty) makes the whole call a no-op. A real, non-empty chat line always passes.
//
// Ring behaviour (0x0062B699-0x0062B726): count lives at CGuiInGame+0x104
// (dialog_buffer_count, matches Odyssey.Dialogue.Spectate.cs's GuiDialogueBufferPtr/+0x104
// reader). While count < 0x40 (64) it copies straight into buffer[count] via
// CExoString::operator=_2 (0x005E5C50) at +0x0 (message) and +0x8 (speaker) - matching the DB's
// own CGuiInGameDialogMessage layout {message CExoString @0, speaker CExoString @8} exactly -
// then increments count. Once count reaches 64 it shifts every slot down one first (oldest
// entry falls off) and holds count at 63 before appending, so the ring never grows past the
// 64-slot array the engine already owns; AppendToDialogBuffer never allocates (no operator_new
// call in its body), so this is always a fixed-size in-place write into memory CGuiInGame
// itself owns - nothing here grows or moves the array.
//
// This is the SAME array CSWGuiInGameMessages::ShowDialogMessages/AddDialogMessages
// (0x624DF0/0x626B10) paint into the pause-menu Messages screen's "dialog_listbox"
// (CSWGuiInGameMessages+0x344) - confirmed via the symbol database's own offsets for
// CSWGuiInGameMessages, not inferred. A line appended here is what a player sees by pressing
// Escape -> Messages -> the dialogue tab, at any point during the current module.
//
// Two things this does NOT give: persistence across a save/reload or module transition (the
// whole CGuiInGame chain is torn down and rebuilt on every load - see
// Odyssey.Dialogue.Spectate.cs's GetInGameGui comment - so the ring is empty again the moment a
// new one is built; UNVERIFIED whether that specific claim holds for THIS ring, since nothing
// has watched it live yet, but it follows the same object lifetime as every other CGuiInGame
// field this mod already treats as transition-volatile) and a "Feedback" tab entry (that is a
// SEPARATE ring - CGuiInGame::AppendToMsgBuffer 0x0062B5C0 into message_buffer at +0xF8, a
// different record shape (CGuiInGameMessage, not CGuiInGameDialogMessage) - not used here).
//
// NEEDS-LIVE: nothing in this file has been run against the real game. The call shape is
// exactly the CExoString marshalling idiom already shipping in PlayBark (Odyssey.Dialogue.Bark.cs)
// and half a dozen other Odyssey.*.cs call sites, and the callee itself is a small, guarded,
// non-allocating array write - see docs/research/chat-ui.md's risk section for the full case.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, byte*, void> AppendToDialogBufferFn =
        (delegate* unmanaged[Thiscall]<nint, byte*, byte*, void>)0x0062B680;

    /// <summary>Defensive caps on the ASCII text handed to the engine's CExoString ctor. The
    /// ctor itself has no fixed limit (it heap-allocates to fit), so these exist only to bound
    /// what this mod ever asks it to copy - the shared-block chat line is already
    /// SharedBlock.ChatLineLength (128) bytes including the null, so 127 covers it exactly.</summary>
    private const int MessagesScreenTextMax = 127;

    private const int MessagesScreenSpeakerMax = 31;

    /// <summary>
    /// Appends one line to the retail Messages screen's dialogue-history ring
    /// (CGuiInGame::AppendToDialogBuffer, verified above). No-op (returns false) when there is
    /// no loaded module (CGuiInGame does not exist yet/anymore) - a line that arrives at the
    /// main menu is simply never written here; the always-on chat overlay is what still shows
    /// it in that case.
    ///
    /// <paramref name="speaker"/> may be empty (the engine's own dialogue lines never are, but
    /// nothing here requires it); <paramref name="text"/> is expected non-empty - the engine's
    /// own guard silently no-ops an empty message, so an empty call costs nothing but is not
    /// worth making.
    ///
    /// MAIN THREAD ONLY (calls into the engine).
    /// </summary>
    public static bool AppendChatLineToMessagesScreen(ReadOnlySpan<byte> speaker, ReadOnlySpan<byte> text)
    {
        nint gui = GetInGameGui();
        if (gui == 0 || text.Length == 0)
        {
            return false;
        }

        int textLen = Math.Min(text.Length, MessagesScreenTextMax);
        byte* textBuf = stackalloc byte[MessagesScreenTextMax + 1];
        for (int i = 0; i < textLen; i++)
        {
            textBuf[i] = text[i];
        }

        textBuf[textLen] = 0;

        int speakerLen = Math.Min(speaker.Length, MessagesScreenSpeakerMax);
        byte* speakerBuf = stackalloc byte[MessagesScreenSpeakerMax + 1];
        for (int i = 0; i < speakerLen; i++)
        {
            speakerBuf[i] = speaker[i];
        }

        speakerBuf[speakerLen] = 0;

        // Two real CExoStrings, built with the engine's own ctor and freed with its own dtor -
        // the exact same idiom PlayBark uses for the bark line (Odyssey.Dialogue.Bark.cs).
        // AppendToDialogBuffer copy-assigns from both (CExoString::operator=_2), so it never
        // takes ownership of these temporaries; they are safe to free right after the call.
        byte* message = stackalloc byte[8];
        byte* speakerStr = stackalloc byte[8];
        *(ulong*)message = 0;
        *(ulong*)speakerStr = 0;

        CExoStringCtorFn(message, textBuf);
        CExoStringCtorFn(speakerStr, speakerBuf);
        try
        {
            AppendToDialogBufferFn(gui, message, speakerStr);
        }
        finally
        {
            CExoStringDtorFn(speakerStr);
            CExoStringDtorFn(message);
        }

        return true;
    }
}
