// Shared quest journal, converged across every player.
namespace KotorCoop.Session;

public interface IJournalSession
{
    /// <summary>The shared quest journal, converged across every player by per-key Lamport
    /// clock (see <see cref="JournalStore"/>).</summary>
    JournalStore Journal { get; }

    /// <summary>Announces a quest journal change — an advance, or (since protocol 31) a
    /// legitimate decrease with override. Delivered reliably.</summary>
    void SetJournalEntry(string questTag, int state);

    /// <summary>Announces that this instance's engine removed <paramref name="questTag"/>
    /// from the journal entirely (KOTOR's own <c>RemoveJournalQuestEntry</c>). Delivered
    /// reliably, and — like <see cref="SetJournalEntry"/> — a no-op if this session did not
    /// hold the tag as live already.</summary>
    void RemoveJournalEntry(string questTag) { }
}
