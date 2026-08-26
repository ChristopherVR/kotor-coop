// Odyssey.Dialogue.Bark.cs - part 2 of dialogue node sync: bark strings, closing
// the dialogue, and the small string-reading helpers it uses.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    public static bool PlayBark(
        uint speakerObjectId, ReadOnlySpan<byte> text, ReadOnlySpan<byte> resRef, uint flags,
        bool suppressCapture = true)
    {
        if (speakerObjectId is 0 or ObjectInvalid)
        {
            return false;
        }

        nint clientInternal = GetClientInternal();
        if (clientInternal == 0)
        {
            return false;
        }

        // The speaker has to exist here, or the engine resolves nothing and the bubble is
        // attached to a creature this instance does not have.
        if (ResolveServerObject(speakerObjectId) == 0)
        {
            return false;
        }

        // The engine's argument is the CLIENT id. ServerToClientObjectId (0x005F2AC0) is
        // literally `id == OBJECT_INVALID ? id : id | 0x80000000` — the top bit selects the
        // client half of the 0x2000-slot object array.
        uint clientObjectId = speakerObjectId | 0x80000000;

        // arg3: a CResRef is a fixed char[16], zero-filled — NOT a C string. An empty one is
        // an ordinary case (a line with no voice-over), so there is nothing to fall back to.
        byte* sound = stackalloc byte[BarkHook.ResRefBytes];
        for (int i = 0; i < BarkHook.ResRefBytes; i++)
        {
            sound[i] = i < resRef.Length ? resRef[i] : (byte)0;
        }

        // arg2: build a REAL CExoString with the engine's own constructor and free it after
        // the call — exactly what ExecuteCommandBarkString does at 0x00548A4A. SetBark
        // copy-constructs from it (0x006A9986), so the callee never takes ownership.
        int length = Math.Min(text.Length, BarkHook.TextMax);
        byte* chars = stackalloc byte[BarkHook.TextMax + 1];
        for (int i = 0; i < length; i++)
        {
            chars[i] = text[i];
        }

        chars[length] = 0;

        byte* line = stackalloc byte[8];
        *(ulong*)line = 0;

        BarkHook.Replaying = suppressCapture;
        try
        {
            CExoStringCtorFn(line, chars);
            try
            {
                BarkStringFn(clientInternal, clientObjectId, line, sound, (int)flags);
            }
            finally
            {
                CExoStringDtorFn(line);
            }
        }
        finally
        {
            BarkHook.Replaying = false;
        }

        return true;
    }

    /// <summary>
    /// Allows or forbids this client committing a dialogue reply.
    ///
    /// ⚠ DO NOT CALL — NOT EVEN WITH `true`. Kept, unused, because the address and the trap
    /// are worth not re-discovering.
    ///
    /// CClientExoAppInternal::SetCanSendDialog (0x005F3030, ret 4) writes the flag at +0x228
    /// AND STAMPS +0x22C WITH THE CURRENT WORLD TIME (0x005F305F). GetCanSendDialog
    /// (0x005F3070, ret 0xC) refuses while `now - [+0x22C] <= 1000` (0x005F316C), and a
    /// successful check back-dates the stamp to now-2000 so the gate stays open by itself.
    /// So there is no such thing as "setting it back to true": every call is a fresh
    /// ~1 second during which CSWSObject::SendDialogEntry (0x004CD460) DEFERS entries rather
    /// than sending them — no CSWSDialog::SendDialogEntry, therefore no
    /// CGuiInGame::HandleDialogSound (0x0062E1D0), therefore no voice-over and no lipsync.
    ///
    /// The engine re-stamps it only during area load (MainLoop 0x006031DB). Nothing in this
    /// mod sets it false, so there is nothing to restore.
    /// </summary>
#pragma warning disable IDE0051
    public static void SetCanSendDialog(bool allowed)
    {
        nint client = GetClient();
        if (client != 0)
        {
            SetCanSendDialogFn(client, allowed ? 1 : 0);
        }
    }
#pragma warning restore IDE0051

    /// <summary>
    /// True when the live conversation has actually been populated — a resolved participant
    /// or something on the panel. A conversation the client has marked active but which no
    /// entry ever reached reads as false, which is how a half-started one is detected.
    /// </summary>
    public static bool DialogueHasContent()
    {
        nint gui = GetInGameGui();
        if (gui == 0 || !ConversationLive(gui))
        {
            return false;
        }

        foreach (uint raw in stackalloc uint[] { *(uint*)(gui + GuiParticipantA), *(uint*)(gui + GuiParticipantB) })
        {
            uint id = raw & 0x7FFFFFFF;
            if (id is not (0 or ObjectInvalid or 0x7FFFFFFF))
            {
                return true;
            }
        }

        nint panel = GetDialoguePanel();
        return panel != 0 && *(int*)(panel + DialogPanelReplyCount) > 0;
    }

    /// <summary>
    /// Ends the local conversation (used when the owner ends theirs, when a follow times out,
    /// and when we lose the token race).
    ///
    /// ⚠ This goes through the SERVER, not through <c>CGuiInGame::CloseDialog</c> directly.
    /// Closing only the client UI leaves the server's CSWSDialog running: it keeps walking
    /// the tree on its own timers and, one line later, sends the next reply list to a client
    /// whose participants it has just set to OBJECT_INVALID. <c>ShowDialogReplies</c> then
    /// calls <c>UpdateShotCamera</c>, which resolves both participants with NO null check
    /// (0x006307DD and 0x006307FB) and faults on the second. That crash was caught live
    /// ~2 s after a close — exactly one dialogue line's worth of delay.
    ///
    /// <see cref="StopDialogFn"/> tears the server dialog down first and reaches
    /// <c>CloseDialog</c> through the engine's own <c>Cleanup</c>, so nothing is left to send.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    /// <summary>
    /// Shuts the dialogue UI directly, without going through the server at all.
    ///
    /// ⚠ LAST RESORT, and only for a conversation <see cref="CloseDialogue"/> has already
    /// failed to end — a conversation whose participants resolve, so CloseDialogue calls
    /// StopDialog and returns, but whose UI is still up afterwards. It adds nothing on the
    /// path where no participant resolves, because CloseDialogue already falls back to this
    /// same call there.
    ///
    /// It is NOT a cure for a wedged client: where the server half still holds an unfinished
    /// conversation, this clears the flag and the next frame sets it straight back. That case
    /// belongs to <see cref="CloseDialogue"/>, which stops the dialog on the player itself.
    /// </summary>
    public static void ForceCloseDialogueUi()
    {
        nint gui = GetInGameGui();
        if (gui != 0 && ConversationLive(gui))
        {
            CloseDialogFn(gui);
        }
    }

    public static void CloseDialogue()
    {
        nint gui = GetInGameGui();
        if (gui == 0 || !ConversationLive(gui))
        {
            return;
        }

        // The GUI names both participants; either may be the owner. Prefer the owner (one
        // call does the whole teardown) and settle for a participant (which routes to the
        // owner through RemoveObjectFromDialog) when the owner is not one of the two.
        nint participant = 0;

        foreach (uint raw in stackalloc uint[] { *(uint*)(gui + GuiParticipantA), *(uint*)(gui + GuiParticipantB) })
        {
            uint id = raw & 0x7FFFFFFF; // the fields carry flag bits above the id
            if (id is 0 or ObjectInvalid or 0x7FFFFFFF)
            {
                continue;
            }

            nint obj = ResolveServerObject(id);
            if (obj == 0)
            {
                continue;
            }

            uint owner = *(uint*)(obj + ObjDialogOwner);
            if (owner == ObjectInvalid)
            {
                continue; // this one is not in a conversation as far as the server knows
            }

            if (owner == *(uint*)(obj + ObjIdOffset))
            {
                StopDialogFn(obj);
                return;
            }

            participant = obj;
        }

        if (participant != 0)
        {
            StopDialogFn(participant);
            return;
        }

        // ⚠ TRIED AND REVERTED: stopping the dialog on the PLAYER here as a last resort
        // (self+0x54 naming a dialog owner). The theory was that a conversation the server
        // started but the client never finished opening leaves the GUI naming 0x7F000000
        // twice while the player's own object still holds the dialog. It did NOT clear the
        // wedge — the flag came straight back on the next frame — and +0x54 is not reliably
        // reset to OBJECT_INVALID when a conversation ends, so the fallback fires on
        // conversations that are already over. Do not reintroduce it without first
        // establishing what re-sets gui+0xB4 every frame in that state.

        // No resolvable server side left — the conversation is a client-only shell (the
        // half-started case the follow timeout exists for). Closing the UI is then both
        // safe and the only thing left to do: there is no dialog to keep sending.
        CloseDialogFn(gui);
    }

    /// <summary>CClientExoApp::GetGameObject(objectId) — the CLIENT's object lookup, the one
    /// UpdateShotCamera itself uses. Returns 0 for an id the client does not hold.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint> ClientGetGameObjectFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x005ED580;

    /// <summary>
    /// True when both of a CGuiInGame's dialogue participants still resolve to client
    /// objects — the precondition <c>CGuiInGame::UpdateShotCamera</c> assumes and never
    /// checks. Asked with the RAW field values, flag bits and all, because that is exactly
    /// what the engine passes (0x006307D8 / 0x006307F6).
    ///
    /// MAIN THREAD ONLY. Used by <see cref="ShotCameraGuardHook"/>.
    /// </summary>
    public static bool ShotCameraParticipantsResolvable(nint gui)
    {
        if (gui == 0)
        {
            return true; // nothing we can judge; let the engine run
        }

        nint client = GetClient();
        if (client == 0)
        {
            return true;
        }

        return ClientGetGameObjectFn(client, *(uint*)(gui + GuiParticipantA)) != 0
            && ClientGetGameObjectFn(client, *(uint*)(gui + GuiParticipantB)) != 0;
    }

    /// <summary>Resolves an object id to its live CSWSDialog*, or 0 (vtable-guarded).</summary>
    private static nint FindActiveDialog(uint objectId)
    {
        if (objectId is 0 or ObjectInvalid)
        {
            return 0;
        }

        nint obj = ResolveServerObject(objectId);
        if (obj == 0)
        {
            return 0;
        }

        nint dialog = Deref(obj + ObjActiveDialog);
        return LooksLikeHeapPointer(dialog) && *(nint*)dialog == DialogVtable ? dialog : 0;
    }

    /// <summary>Copies a CExoString {char* @0, len @4} at <paramref name="field"/> into an
    /// ASCII buffer. <paramref name="field"/> is a struct member address (already valid).</summary>
    private static void ReadExoStringInto(nint field, byte* dest, int cap)
    {
        if (cap <= 0)
        {
            return;
        }

        dest[0] = 0;
        CopyAsciiBounded(*(nint*)field, *(int*)(field + 4), dest, cap);
    }

    /// <summary>
    /// Copies the first inline substring of a CExoLocString at <paramref name="field"/> into
    /// an ASCII buffer. Chain (docs/research/name-plates.md): loc+0 -&gt; internal ; internal+4
    /// count ; internal+0 list* ; list+0 head ; head+8 entry ; entry+4 char* ; entry+8 cap.
    /// A StrRef-only line (no substring) leaves the buffer empty. Every hop is guarded.
    /// </summary>
    private static void ReadLocStringInto(nint field, byte* dest, int cap)
    {
        if (cap <= 0)
        {
            return;
        }

        dest[0] = 0;

        nint intern = *(nint*)field;
        if (!LooksLikeHeapPointer(intern))
        {
            return;
        }

        int subCount = *(int*)(intern + 4);
        if (subCount is <= 0 or > 64)
        {
            return; // StrRef-only (or implausible) -> nothing inline to read
        }

        nint list = *(nint*)intern;
        if (!LooksLikeHeapPointer(list))
        {
            return;
        }

        nint head = *(nint*)list;
        if (!LooksLikeHeapPointer(head))
        {
            return;
        }

        nint entry = *(nint*)(head + 8);
        if (!LooksLikeHeapPointer(entry))
        {
            return;
        }

        int capacity = *(int*)(entry + 8); // strlen + 1
        CopyAsciiBounded(*(nint*)(entry + 4), capacity > 0 ? capacity - 1 : 0, dest, cap);
    }

    /// <summary>Copies up to <paramref name="len"/> printable ASCII bytes from a guarded char
    /// pointer into <paramref name="dest"/>, null-terminated. Bounds every length.</summary>
    private static void CopyAsciiBounded(nint chars, int len, byte* dest, int cap)
    {
        dest[0] = 0;
        if (!LooksLikeDataPointer(chars) || len <= 0)
        {
            return;
        }

        if (len > cap - 1)
        {
            len = cap - 1;
        }

        if (len > 512)
        {
            len = 512; // hard ceiling regardless of a garbage capacity field
        }

        int n = 0;
        for (int i = 0; i < len; i++)
        {
            byte b = *(byte*)(chars + i);
            if (b == 0)
            {
                break;
            }

            dest[n++] = b is >= 0x20 and < 0x7F ? b : (byte)'?';
        }

        dest[n] = 0;
    }

}
