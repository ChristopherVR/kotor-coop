using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Applies the session's quest-journal truth to this instance: when a peer's journal
/// entry differs from ours, the agent writes (tag, state) into the shared block and this
/// controller applies it through CSWSJournal::SetState on the game's thread — the call
/// that also refreshes the journal GUI. One request at a time; journal changes are rare
/// and the agent holds the queue.
///
/// ⚠ PROTOCOL 31: "differs from", not "is ahead of". The session is now the authority in
/// EITHER direction — a genuine decrease with override, or a removal — and
/// <see cref="Odyssey.SetJournalState"/> decides the engine's own override bit for a
/// decrease by comparing against what it finds live in the journal. A removal is signalled
/// through the SAME <c>JournalState</c> field via <see cref="RemoveSentinel"/> rather than a
/// new <c>SharedBlock</c> field, since that struct's layout is owned by another pass — see
/// docs/research/quest-sync.md.
/// </summary>
internal sealed unsafe class JournalController(SharedBlockView block)
{
    /// <summary>"Remove this tag" rather than "set this tag to this state" — mirrors
    /// <c>KotorGameBridge.JournalRemoveSentinel</c> exactly (both sides of the same shared
    /// int field must agree on what it means); a real quest state is never negative, so
    /// this cannot collide with one.</summary>
    private const int RemoveSentinel = int.MinValue;

    private int _logged;

    /// <summary>
    /// Gates <see cref="Odyssey.RemoveJournalEntry"/>, whose native call
    /// (<c>CSWSJournal::Destroy</c>) was found by static disassembly. Live-verified
    /// 2026-08-22 (docs/research/quest-sync.md's dated live-pass section): a removal
    /// request against a real live journal entry dropped the array's own size by one and
    /// the tag genuinely vanished from a raw read of the engine's own journal array — not
    /// merely marked, actually removed, exactly as the disassembly implied — with no crash
    /// across an extended session. Default ON. With the flag off, a removal request is
    /// still acknowledged (so the agent's request queue does not stall) but left unapplied
    /// to the engine — the SESSION side still tracks the removal correctly either way (see
    /// <c>JournalStore</c>), so the only thing missing while this is off is the LOCAL
    /// engine's own journal UI still showing an entry the peer's has taken out.
    ///
    /// ⚠ A SEPARATE, STILL-OPEN FINDING from the same live pass: <see
    /// cref="Odyssey.SetJournalState"/>'s GENUINE DECREASE path (<c>bOverrideHigher=1</c>)
    /// reported success and logged the lower state, but a raw read of the journal array
    /// afterward showed the entry's state UNCHANGED — the override call did not visibly
    /// take. This is unconditional, existing code with no ini gate, so nothing here flips
    /// it off; it is recorded as a live-verification failure pending its own investigation
    /// (docs/research/quest-sync.md's live-pass section has the exact repro).
    /// </summary>
    private readonly bool _removeSync = PluginConfig.ReadBool("journal_remove_sync", true);

    /// <summary>
    /// Requests survive a module change on purpose: the journal is party state, not area
    /// state, and a request that raced a transition applies on the next stable frame.
    /// </summary>
    public void Invalidate()
    {
    }

    public void OnFrame()
    {
        uint serial = block.JournalSerial;
        if (serial == block.JournalApplied)
        {
            return;
        }

        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        byte* tag = block.JournalTag;
        int length = 0;
        while (length < SharedBlock.JournalTagLength - 1 && tag[length] != 0)
        {
            length++;
        }

        int requestedState = block.JournalState;
        bool isRemoval = requestedState == RemoveSentinel;

        if (isRemoval)
        {
            ApplyRemoval(tag, length, serial);
        }
        else
        {
            ApplySet(tag, length, requestedState, serial);
        }
    }

    private void ApplySet(byte* tag, int length, int state, uint serial)
    {
        bool ok = Odyssey.SetJournalState(tag, length, state, out int failure, out bool alreadyCurrent);
        block.JournalStatus = ok ? 0 : failure;
        block.JournalApplied = serial;

        if (_logged < 20)
        {
            _logged++;
            string name = Encoding.ASCII.GetString(tag, length);
            PluginHost.Log(
                alreadyCurrent
                    ? $"journal: '{name}' already at state {state}; skipped (no feedback popup)"
                    : ok
                        ? $"journal: '{name}' -> state {state} on the session's authority"
                        : $"journal: '{name}' refused (failure {failure})");
        }
    }

    private void ApplyRemoval(byte* tag, int length, uint serial)
    {
        string name = Encoding.ASCII.GetString(tag, length);

        if (!_removeSync)
        {
            // Acknowledge the request slot either way — the agent's one-at-a-time queue
            // must not stall forever waiting for a plugin that has this off — but do not
            // touch the engine.
            block.JournalStatus = 0;
            block.JournalApplied = serial;

            if (_logged < 20)
            {
                _logged++;
                PluginHost.Log(
                    $"journal: '{name}' removal requested but journal_remove_sync is off; "
                    + "acknowledged without touching the local engine (session state still tracks it)");
            }

            return;
        }

        bool ok = Odyssey.RemoveJournalEntry(tag, length, out int failure);
        block.JournalStatus = ok ? 0 : failure;
        block.JournalApplied = serial;

        if (_logged < 20)
        {
            _logged++;
            PluginHost.Log(
                ok
                    ? $"journal: '{name}' removed on the session's authority (journal_remove_sync)"
                    : $"journal: '{name}' removal refused (failure {failure})");
        }
    }
}
