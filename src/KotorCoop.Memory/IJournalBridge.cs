// Journal (quest) sync: collecting, applying and removing journal entries.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IJournalBridge
{
    /// <summary>False when this bridge cannot see or drive the quest journal.</summary>
    bool SupportsJournalSync => false;

    /// <summary>Journal entries that changed locally since the last call — added, raised,
    /// lowered, or removed. See <see cref="JournalChange"/>.</summary>
    List<JournalChange> CollectChangedJournal() => [];

    /// <summary>
    /// Sets a journal entry to a peer's state through the game's own path. Direction-neutral
    /// since protocol 31 — the session is the authority, including on a legitimate decrease.
    /// Must be a no-op (returning false) when the game already holds exactly that state.
    /// </summary>
    bool TryApplyJournalEntry(string questTag, int state) => false;

    /// <summary>
    /// Takes a journal entry out of the game entirely — the removal twin of
    /// <see cref="TryApplyJournalEntry"/>, for a peer's `RemoveJournalQuestEntry`. Must be a
    /// no-op (returning false) when the game does not currently hold the tag. See
    /// docs/research/quest-sync.md for why the concrete implementation is gated behind an
    /// opt-in flag: the native call it uses was found by static disassembly, not verified
    /// live.
    /// </summary>
    bool TryRemoveJournalEntry(string questTag) => false;
}
