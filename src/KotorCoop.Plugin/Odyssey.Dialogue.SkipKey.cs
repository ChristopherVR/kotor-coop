namespace KotorCoop.Plugin;

// The `skipkey` live-test primitive: the INPUT path into a dialogue skip, as distinct from
// SkipDialogueLine's owner-side flag poke. See docs/research/dialogue-skip-desync.md.
internal static unsafe partial class Odyssey
{
    /// <summary>
    /// <c>CSWGuiDialog::HandleInputEvent</c>'s vtable slot. Read out of both dialogue panel
    /// vtables (<c>CSWGuiDialog 0x7559E0</c> and <c>CSWGuiDialogCinematic 0x755800</c>): index
    /// 15, i.e. +0x3C, and BOTH hold 0x006A7230 — the cinematic panel declares no override, so
    /// one slot covers both classes exactly as <see cref="DialogueSkipHook"/>'s single splice
    /// does. Called through the slot rather than the address for the reason
    /// <see cref="SelectDialogueReply"/>'s own comment gives at length.
    /// </summary>
    private const int PanelHandleInputEventSlot = 0x3C;

    /// <summary>The Enter/click event code the dialogue panel's switch treats as
    /// "advance/skip" — the value docs/research/dialogue-skip-desync.md pins as the branch that
    /// falls through to <c>CServerExoApp::SkipPastDialog</c> (the jump table at 0x6A7374 is
    /// indexed by <c>event - 0x27</c>).</summary>
    private const int DialogueSkipEvent = 0x27;

    /// <summary>
    /// LIVE-TEST PRIMITIVE: presses skip on the dialogue panel THE WAY A KEYPRESS DOES —
    /// <c>HandleInputEvent(panel, 0x27, 1)</c> through the panel's own vtable slot.
    ///
    /// ⚠ WHY THIS EXISTS ALONGSIDE <see cref="SkipDialogueLine"/>, which looks like the same
    /// thing. It is not, and the difference is the whole point: <c>skipline</c> writes
    /// <c>CSWSObject+0x1F4</c> on the resolved conversation OWNER and never enters
    /// <c>HandleInputEvent</c> at all, so it exercises the owner-side primitive and walks
    /// straight past <see cref="DialogueSkipHook"/> — the very gate a spectator's keypress is
    /// supposed to hit. It also REFUSES outright on a spectator, because a spectator owns no
    /// conversation for <c>TryGetActiveDialogue</c> to resolve, which is exactly the instance
    /// the hook exists for. This function therefore resolves the PANEL (which a spectator does
    /// have — the follow machine opened a local copy) and enters the engine's own input
    /// dispatch, so the hook sees it.
    ///
    /// Arguments, from the disassembly at 0x6A7230 (thiscall, <c>ret 8</c>): the FIRST stack
    /// argument is the event code (<c>ebx = [esp+0x10]</c> after three pushes, compared against
    /// 0x3D / 0x27 / 0x2D), the SECOND is a payload the function rejects outright when zero
    /// (<c>ebp = [esp+0xc]</c> after one push, then <c>test ebp,ebp / je</c> straight to the
    /// return). 1 satisfies that test and is never dereferenced on this branch — only the
    /// 0x1F9 event reads through it, and 0x27 is not 0x1F9.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    /// <param name="nodeBefore">The conversation's entry index as this instance sees it, or -1
    /// when it cannot be read (a spectator mid-join, a reply screen). Reported rather than
    /// required: the oracle for this primitive is
    /// <c>DialogueSkipHook.SwallowedCount</c>, and refusing when the node is unknown would
    /// refuse on precisely the instance under test.</param>
    public static bool SendDialogueSkipKey(out int nodeBefore)
    {
        nodeBefore = -1;

        nint panel = GetDialoguePanel();
        if (panel == 0)
        {
            return false;
        }

        nodeBefore = GetDialogueNodeIndex();

        nint vtable = *(nint*)panel;
        var handleInputEvent =
            (delegate* unmanaged[Thiscall]<nint, int, int, void>)(*(nint*)(vtable + PanelHandleInputEventSlot));

        if (!LooksLikeCodePointer((nint)handleInputEvent))
        {
            return false;
        }

        handleInputEvent(panel, DialogueSkipEvent, 1);
        return true;
    }
}
