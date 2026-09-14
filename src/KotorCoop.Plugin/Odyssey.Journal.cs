// Odyssey.Journal.cs - death pose application and journal entry state.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- death pose --------------------------------------------------------------

    /// <summary>
    /// Ambient animation states. CSWSCreature::SetAnimation remaps the idle (0x2710)
    /// through the state byte at creature+0x4C4 using the jump table at 0x004F0F50:
    /// states 3, 4 and 14 produce the three death poses (0x2718, 0x2716, 0x27AC); values
    /// outside the 2..14 table fall through to the plain idle, so 1 is "normal".
    /// </summary>
    public const int AmbientStateNormal = 1;
    public const int AmbientStateDead = 4;

    // CSWSCreature::SetAmbientAnimationState(byte). Plain store to +0x4C4, with a quirk:
    // for states 3/4 it substitutes 14 when the current animation is one of the
    // kneeling/prone set, which is exactly the behaviour we want anyway.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetAmbientAnimationStateFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x004EFEF0;

    /// <summary>
    /// Lays a creature down in a death pose, or stands it back up. The animation field
    /// cannot do this: the client's animation sync explicitly rejects all three death
    /// constants (trap 21b), so a raw 0x2716 write does nothing at all. The engine's own
    /// route is the ambient state plus a re-issued idle, which SetAnimation then remaps
    /// into the pose — and pushes to the render side.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void SetDownState(nint creature, bool down)
    {
        if (creature == 0)
        {
            return;
        }

        SetAmbientAnimationStateFn(creature, down ? AmbientStateDead : AmbientStateNormal);
        SetAnimation(creature, AnimIdle);
    }

    /// <summary>Empties a creature's action queue (a queued move would stand a "dead"
    /// phantom back up mid-pose). MAIN THREAD ONLY.</summary>
    public static void ClearActions(nint creature)
    {
        if (creature != 0)
        {
            ClearAllActionsFn(creature, 1);
        }
    }

    // ---- journal -----------------------------------------------------------------

    /// <summary>CServerExoAppInternal + this = party table (embedded CSWPartyTable).</summary>
    private const int PartyTableOffset = 0x1B770;

    /// <summary>
    /// CSWPartyTable + this = a POINTER to the CSWSJournal — not the journal itself.
    ///
    /// ⚠ Passing the ADDRESS OF the pointer (`partyTable + 0x11C`) instead of dereferencing
    /// it caused a live crash: `CSWSJournal::SetState` walked whatever followed it in the
    /// party table as an entry array and faulted on a garbage tag. The engine's own accessor
    /// settles the correct layout: `CSWPartyTable::GetPartyJournal 0x005633C0` is
    /// `mov eax,[esi+0x11C]` (VERIFIED), reached from `CSWSCreature::GetJournal 0x004EDB00`
    /// -> `CServerExoApp::GetPartyTable 0x004AEE70` (`[server+4] + 0x1B770`, confirming the
    /// party-table base too). Verified live after the fix: `*(partyTable + 0x11C)` -> journal
    /// whose array list at +0 reads { data, size 1, capacity 16 } with entry 0 tagged
    /// 'end_attack' state 20.
    /// </summary>
    private const int PartyTableJournal = 0x11C;

    // CSWSJournal::SetState(CExoString* tag, int state, int bOverrideHigher). Walks the
    // entry list comparing tags case-insensitively, updates or appends, and sends the
    // journal-updated message so the GUI refreshes — which is exactly why applying a
    // peer's quest state must go through this call and not a raw entry write.
    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, int, int, void> JournalSetStateFn =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, int, int, void>)0x005C5A40;

    /// <summary>
    /// Sets a quest journal entry to the given state through the engine's own path. The
    /// CALLER never has to say which direction: this walks the journal's own entry list (it
    /// already has to, to find the tag) and decides how to reach the target via <see
    /// cref="JournalApplyRule"/>.
    ///
    /// ⚠ <c>CSWSJournal::SetState</c>'s own bOverrideHigher=1 argument is NOT used for a
    /// genuine decrease: it reports success and logs the lower number, but live probing showed
    /// the journal array's own <c>+0x20</c> state field never actually moves — the argument
    /// only suppresses a notification send, and exactly which internal branch refuses the
    /// write was not pinned down to one instruction. So a decrease is instead realised by
    /// composing two ALREADY-LIVE-VERIFIED primitives — <see cref="RemoveJournalEntry"/>'s
    /// <c>CSWSJournal::Destroy</c> (confirmed live to genuinely shrink the array), followed by
    /// a plain add (override=0), the same path ordinary quest advancement has always used.
    /// Full investigation: docs/research/quest-sync.md.
    ///
    /// <paramref name="allowDecreaseViaRemove"/> mirrors <c>JournalController</c>'s
    /// <c>journal_remove_sync</c> gate: a decrease fundamentally needs the removal primitive,
    /// so when that flag is off, a decrease is refused (failure 7) rather than silently
    /// reusing the disproven override path — the SESSION side still tracks the value either
    /// way (see <c>JournalStore</c>).
    ///
    /// ⚠ SetState is NEVER called when the journal already has the tag at EXACTLY the target
    /// (<paramref name="alreadyCurrent"/> comes back true instead) — this used to be "at or
    /// past" the target, which was correct only because a lower target used to be impossible
    /// to ask for. This is not an optimisation: SetState sends the server→player journal
    /// message — the "Journal Entry Added" feedback box — even when nothing changed.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool SetJournalState(
        byte* tagAscii, int tagLength, int state, bool allowDecreaseViaRemove,
        out int failure, out bool alreadyCurrent)
    {
        failure = 0;
        alreadyCurrent = false;

        if (tagLength <= 0)
        {
            failure = 1;
            return false;
        }

        nint server = GetServer();
        if (server == 0)
        {
            failure = 2;
            return false;
        }

        nint internalServer = Deref(server + AppServerOffsetInternal);
        if (internalServer == 0)
        {
            failure = 3;
            return false;
        }

        // Dereference: +0x11C HOLDS the journal pointer (see PartyTableJournal).
        nint journal = Deref(internalServer + PartyTableOffset + PartyTableJournal);
        if (journal == 0)
        {
            failure = 6;
            return false;
        }

        // ⚠ VALIDATE THE LIST BEFORE HANDING IT TO THE ENGINE. A prior live crash showed why:
        // CSWSJournal::SetState walks every existing entry comparing tags, and faulted inside
        // CExoString::CompareNoCase_2 at 0x005E6491 — the strlen over an ENTRY's own tag
        // pointer, reached from the compare at 0x005C5AFF. That is the signature of a data
        // pointer that is readable but not a journal array: the walk then dereferences a
        // garbage tag. SetState has no validation of its own, so the guard has to live here.
        //
        // { SJournalEntry* data; int size; int capacity }, entries 0x3C bytes (the caller's
        // own stride at 0x005C5B11), tag CExoString at +0x18 (its `lea ecx,[eax+edx+0x18]`).
        if (!JournalMemoryGuard.TryValidate(journal, out nint data, out int size, out failure))
        {
            return false;
        }

        // Validate the complete list first: remove-then-add can walk entries beyond the
        // matching tag. This second pass finds JournalApplyRule.Decide's current state.
        ReadOnlySpan<byte> requestTag = new(tagAscii, tagLength);

        for (int i = 0; i < size; i++)
        {
            nint entry = data + (i * JournalEntryStride);
            nint entryTag = *(nint*)(entry + JournalEntryTag);
            if (entryTag == 0)
            {
                continue;
            }

            // CExoString { char* @0, len @4 }; the length INCLUDES the null (the length
            // trap). JournalMemoryGuard already checked the bounded range and terminator.
            uint entryTagLength = *(uint*)(entry + JournalEntryTag + 4);
            int bounded = (int)Math.Min(entryTagLength, 64);

            if (bounded <= 0
                || !JournalTags.EqualsNoCase(new ReadOnlySpan<byte>((void*)entryTag, bounded), requestTag))
            {
                continue;
            }

            int currentState = *(int*)(entry + JournalEntryState);
            JournalApplyAction action = JournalApplyRule.Decide(hasExisting: true, currentState, state);

            if (action == JournalApplyAction.Skip)
            {
                alreadyCurrent = true;
                return true;
            }

            // Length is capacity (strlen + 1), the engine's own CExoString convention.
            ExoString foundTag = new() { CString = (nint)tagAscii, Length = (uint)(tagLength + 1) };

            if (action == JournalApplyAction.RemoveThenAdd)
            {
                if (!allowDecreaseViaRemove)
                {
                    failure = 7;
                    return false;
                }

                // The disproven direct path (see the method remarks): a genuine decrease is
                // realised as remove-then-add, composing two primitives that ARE each
                // live-verified, instead of SetState's own bOverrideHigher=1 argument, which
                // reports success without visibly moving the array's own state field.
                JournalDestroyEntryFn(journal, &foundTag);
                JournalSetStateFn(journal, &foundTag, state, 0);
                return true;
            }

            // A plain advance on an existing entry — the path ordinary quest progression has
            // always used; not in question.
            JournalSetStateFn(journal, &foundTag, state, 0);
            return true;
        }

        // Tag not found at all: a fresh entry, always a plain advance from nothing
        // (JournalApplyRule.Decide(hasExisting: false, ...) is always Add).
        ExoString newTag = new() { CString = (nint)tagAscii, Length = (uint)(tagLength + 1) };
        JournalSetStateFn(journal, &newTag, state, 0);
        return true;
    }

    /// <summary>SJournalEntry stride, from SetState's own walk (add ecx,0x3C).</summary>
    private const int JournalEntryStride = JournalMemoryGuard.JournalEntryStride;

    /// <summary>SJournalEntry + this = the tag CExoString (lea ecx,[eax+edx+0x18]).</summary>
    private const int JournalEntryTag = JournalMemoryGuard.JournalEntryTag;

    /// <summary>SJournalEntry + this = the quest state. VERIFIED twice over: SetState
    /// itself compares and stores its state argument at [entry+0x20] (the cmp/mov pair at
    /// 0x5C5E23–0x5C5E30), and the agent's read path walked the same layout live —
    /// 'end_attack' state 20. Read-only here.</summary>
    private const int JournalEntryState = 0x20;

    // CSWSJournal + this, CExoString* tag (thiscall, one stack arg — VERIFIED against the
    // binary's own `ret 8` at 0x5C68CA, param_size_bytes=4 in the symbol DB). NOT verified
    // live — see RemoveJournalEntry's remarks for how this was found and why it ships
    // behind an opt-in flag instead of a live-tested offset.
    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, void> JournalDestroyEntryFn =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, void>)0x005C68B0;

    /// <summary>
    /// Takes a quest entry out of the journal entirely — KOTOR's own
    /// <c>RemoveJournalQuestEntry</c>, reached here the way <see cref="SetJournalState"/>
    /// reaches <c>AddJournalQuestEntry</c>.
    ///
    /// ⚠ NEEDS-LIVE, UNVERIFIED, OFF BY DEFAULT (see <c>JournalController</c>'s
    /// <c>journal_remove_sync</c> gate). The call site is <c>CSWSJournal::Destroy</c> at
    /// 0x005C68B0 — found by static disassembly of
    /// <c>ExecuteCommandRemoveJournalQuestEntry</c> (0x0054AF60, the VM wrapper the symbol DB
    /// names for NWScript routine 368), not by any live probe. The disassembly is
    /// self-consistent (same player-list walk and <c>CSWSCreature::GetJournal</c> lookup as
    /// <see cref="SetJournalState"/>'s caller chain, a one-argument thiscall matching its own
    /// `ret 8`) but this journal pointer chain's live crash history (see
    /// <see cref="SetJournalState"/>'s remarks) is reason enough not to ship an unverified
    /// call unguarded — hence the same array-walk validation <see cref="SetJournalState"/>
    /// uses is applied here: a readable but non-journal pointer must fail closed, not fault
    /// the process. See docs/research/quest-sync.md for the routine-number derivation.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool RemoveJournalEntry(byte* tagAscii, int tagLength, out int failure)
    {
        failure = 0;

        if (tagLength <= 0)
        {
            failure = 1;
            return false;
        }

        nint server = GetServer();
        if (server == 0)
        {
            failure = 2;
            return false;
        }

        nint internalServer = Deref(server + AppServerOffsetInternal);
        if (internalServer == 0)
        {
            failure = 3;
            return false;
        }

        nint journal = Deref(internalServer + PartyTableOffset + PartyTableJournal);
        if (journal == 0)
        {
            failure = 6;
            return false;
        }

        if (!JournalMemoryGuard.TryValidate(journal, out nint data, out int size, out failure))
        {
            return false;
        }

        ReadOnlySpan<byte> requestTag = new(tagAscii, tagLength);
        bool found = false;

        for (int i = 0; i < size; i++)
        {
            nint entry = data + (i * JournalEntryStride);
            nint entryTag = *(nint*)(entry + JournalEntryTag);
            if (entryTag == 0)
            {
                continue;
            }

            uint entryTagLength = *(uint*)(entry + JournalEntryTag + 4);
            int bounded = (int)Math.Min(entryTagLength, 64);

            if (bounded > 0
                && JournalTags.EqualsNoCase(new ReadOnlySpan<byte>((void*)entryTag, bounded), requestTag))
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            // Nothing to remove — not a failure, just idempotent (the caller's own shadow
            // should already have refused this, but a defensive no-op costs nothing).
            return true;
        }

        ExoString tag = new() { CString = (nint)tagAscii, Length = (uint)(tagLength + 1) };
        JournalDestroyEntryFn(journal, &tag);
        return true;
    }

}
