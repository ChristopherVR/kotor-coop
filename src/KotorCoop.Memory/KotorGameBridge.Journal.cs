// Journal (quest) sync: reading/applying/removing journal entries against a per-module
// shadow, keyed by quest tag.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- journal ---------------------------------------------------------------

    /// <summary>CServerExoAppInternal + this = party table (embedded CSWPartyTable).</summary>
    private const int PartyTableOffset = 0x1B770;

    /// <summary>CSWPartyTable + this = a POINTER to the CSWSJournal (whose own array list
    /// { SJournalEntry* data; int size; int cap } sits at journal+0). Dereference it —
    /// see the note in ReadJournalEntries for what reading it in place cost.</summary>
    private const int PartyTableJournal = 0x11C;

    private const int JournalEntryStride = 0x3C;
    private const int JournalEntryTag = 0x18;    // CExoString { char* @0, len @4 }
    private const int JournalEntryState = 0x20;

    /// <summary>What this game's journal held when last scanned (or what we last applied),
    /// so only genuine advances are announced, nothing echoes — and, critically, nothing
    /// is applied before the live journal has been read once (see JournalShadow: judging
    /// against an unprimed shadow was the "Journal Entry Added" popup on every join).</summary>
    private readonly JournalShadow _journalShadow = new();

    /// <summary>Reading is external; applying goes through the plugin (SetState must run
    /// on the game's thread, and it is also what refreshes the journal GUI).</summary>
    public bool SupportsJournalSync => _pluginBlock != 0;

    /// <summary>
    /// The sentinel written into <see cref="BlockJournalState"/> to mean "remove this tag"
    /// rather than "set this tag to this state" — a real quest state is never negative, so
    /// this cannot collide with one. Reusing the existing int field this way needs no
    /// SharedBlock layout change (that struct is owned elsewhere in this pass — see the
    /// task's own file-ownership list — so adding a field to it was not an option anyway).
    /// The plugin's JournalController interprets it; see docs/research/quest-sync.md.
    /// </summary>
    private const int JournalRemoveSentinel = int.MinValue;

    public List<JournalChange> CollectChangedJournal()
    {
        // A failed read (loading screen, chain not yet resolvable) must return empty
        // WITHOUT priming the shadow — an empty read result and "could not read" are
        // different facts, and conflating them is what replayed the whole store on join.
        if (!TryReadJournalEntries(out List<KeyValuePair<string, int>> live))
        {
            return [];
        }

        return _journalShadow.Observe(live);
    }

    /// <summary>
    /// Asks the plugin to set a quest entry's state. One request in flight at a time —
    /// journal changes are rare, and the pump retries every tick until the plugin has
    /// caught up. Direction-neutral since protocol 31: the session is the authority,
    /// including on a legitimate decrease (the plugin decides whether the engine call needs
    /// its override flag — see <c>JournalController</c>). Returns false when the game
    /// already holds exactly this state.
    /// </summary>
    public bool TryApplyJournalEntry(string questTag, int state)
    {
        if (_pluginBlock == 0
            || string.IsNullOrEmpty(questTag)
            || questTag.Length >= BlockJournalTagLength
            || state == JournalRemoveSentinel)
        {
            return false;
        }

        // Covers both halves of idempotence: never before the live journal has been read
        // (the join race), and never for a state the game already has (redundant apply —
        // the engine's SetState fires the "Journal Entry Added" GUI feedback even when
        // nothing changes, so a redundant apply is a guaranteed spurious popup).
        if (!_journalShadow.ShouldApply(questTag, state))
        {
            return false;
        }

        // Only one request slot; wait for the plugin to acknowledge the previous one.
        if (!_memory.TryReadUInt32(_pluginBlock + BlockJournalSerial, out uint serial)
            || !_memory.TryReadUInt32(_pluginBlock + BlockJournalApplied, out uint appliedSerial)
            || serial != appliedSerial)
        {
            return false;
        }

        bool ok = _memory.TryWriteAsciiString(_pluginBlock + BlockJournalTag, questTag, BlockJournalTagLength);
        ok &= _memory.TryWrite(_pluginBlock + BlockJournalState, state);

        // Serial last, so the plugin never sees a half-written request.
        ok &= _memory.TryWrite(_pluginBlock + BlockJournalSerial, serial + 1);

        if (ok)
        {
            // The session is the authority on the target value in either direction, so this
            // always adopts it rather than only when it is higher.
            _journalShadow.MarkApplied(questTag, state);
        }

        return ok;
    }

    /// <summary>
    /// Asks the plugin to remove a quest entry entirely — the removal twin of
    /// <see cref="TryApplyJournalEntry"/>, using the same one-request-slot protocol via the
    /// <see cref="JournalRemoveSentinel"/> value in <see cref="BlockJournalState"/>. Returns
    /// false when the game does not currently hold the tag (nothing to remove) or the
    /// previous request has not been acknowledged yet.
    /// </summary>
    public bool TryRemoveJournalEntry(string questTag)
    {
        if (_pluginBlock == 0
            || string.IsNullOrEmpty(questTag)
            || questTag.Length >= BlockJournalTagLength)
        {
            return false;
        }

        if (!_journalShadow.ShouldRemove(questTag))
        {
            return false;
        }

        if (!_memory.TryReadUInt32(_pluginBlock + BlockJournalSerial, out uint serial)
            || !_memory.TryReadUInt32(_pluginBlock + BlockJournalApplied, out uint appliedSerial)
            || serial != appliedSerial)
        {
            return false;
        }

        bool ok = _memory.TryWriteAsciiString(_pluginBlock + BlockJournalTag, questTag, BlockJournalTagLength);
        ok &= _memory.TryWrite(_pluginBlock + BlockJournalState, JournalRemoveSentinel);
        ok &= _memory.TryWrite(_pluginBlock + BlockJournalSerial, serial + 1);

        if (ok)
        {
            _journalShadow.MarkRemoved(questTag);
        }

        return ok;
    }

    /// <summary>
    /// Reads the live journal. Returns FALSE when the chain is not readable (loading
    /// screen, menu, mid-transition) — which is a different fact from "readable and
    /// empty" (true with an empty list, e.g. a new game), and the shadow priming above
    /// depends on the distinction.
    /// </summary>
    private bool TryReadJournalEntries(out List<KeyValuePair<string, int>> entries)
    {
        entries = [];

        if (!EngineGlobals.TryGetServerInternal(_memory, out nint serverInternal))
        {
            return false;
        }

        // ⚠ +0x11C HOLDS a pointer to the CSWSJournal; it is not the journal. Reading it as
        // the array list itself made this enumerate whatever followed it in the party
        // table — the same mistake that crashed the plugin's WRITE path on 2026-08-06.
        // CSWPartyTable::GetPartyJournal 0x005633C0 is `mov eax,[esi+0x11C]` (VERIFIED),
        // and the corrected chain was confirmed live: 1 entry, 'end_attack', state 20.
        if (!_memory.TryReadPointer(serverInternal + PartyTableOffset + PartyTableJournal, out nint journal)
            || journal == 0)
        {
            return false;
        }

        if (!_memory.TryReadPointer(journal, out nint data)
            || !_memory.TryReadInt32(journal + 4, out int count)
            || count is < 0 or > 512
            || (count > 0 && data == 0))
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            nint entry = data + (i * JournalEntryStride);
            string tag = ReadExoString(entry + JournalEntryTag);

            if (tag.Length is 0 or >= BlockJournalTagLength)
            {
                continue;
            }

            if (!_memory.TryReadInt32(entry + JournalEntryState, out int state))
            {
                continue;
            }

            entries.Add(new KeyValuePair<string, int>(tag, state));
        }

        return true;
    }

}
