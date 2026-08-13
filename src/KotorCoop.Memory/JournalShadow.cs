namespace KotorCoop.Memory;

/// <summary>
/// What this game's journal is known to hold — the local truth the sync compares against
/// before announcing or applying anything.
///
/// The single most important property here is <see cref="IsPrimed"/>. On a fresh join the
/// session store fills from the host's snapshot while this game is still on a loading
/// screen, so the journal chain is not yet readable and the shadow would be EMPTY. Judging
/// "does the game already have this?" against an empty shadow answers "no" for every entry
/// of a save both players loaded identically — and every one of those redundant applies
/// goes through CSWSJournal::SetState, which sends the journal-updated feedback to the GUI
/// even when nothing changed (verified in the 1.0.3 disassembly: the AddQuest send at
/// 0x5C616D never consults the change flags it accumulated). That was the "Journal Entry
/// Added" box on every load-in. So: no apply may be requested until the live journal has
/// been read successfully at least once.
/// </summary>
public sealed class JournalShadow
{
    private readonly Dictionary<string, int> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True once the live journal has been read successfully at least once. An
    /// EMPTY read still primes — a new game's journal genuinely holds nothing — but a
    /// failed read (loading screen, main menu, unreadable chain) must not.</summary>
    public bool IsPrimed { get; private set; }

    /// <summary>
    /// Takes one successful full read of the live journal, primes the shadow, and returns
    /// only the entries that ADVANCED past what was already known — the ones worth
    /// announcing to the session. Entries this instance itself applied from a peer come
    /// back from the game already known, so they do not echo.
    /// </summary>
    public List<KeyValuePair<string, int>> Observe(IReadOnlyList<KeyValuePair<string, int>> live)
    {
        IsPrimed = true;

        List<KeyValuePair<string, int>> advances = [];

        foreach (KeyValuePair<string, int> entry in live)
        {
            if (_known.TryGetValue(entry.Key, out int known) && known >= entry.Value)
            {
                continue;
            }

            _known[entry.Key] = entry.Value;
            advances.Add(entry);
        }

        return advances;
    }

    /// <summary>
    /// Whether an apply request for (tag, state) would change anything. False until primed
    /// (see the class remarks — this is the join-race guard), and false when the game is
    /// already known to be at or past the state.
    /// </summary>
    public bool ShouldApply(string questTag, int state) =>
        IsPrimed && !(_known.TryGetValue(questTag, out int known) && known >= state);

    /// <summary>
    /// Records an issued apply, optimistically: quest states only rise, so even a failed
    /// apply cannot make this wrong in a way that loses progress — and it stops the same
    /// request being re-issued every pump while the plugin catches up.
    /// </summary>
    public void MarkApplied(string questTag, int state)
    {
        if (!_known.TryGetValue(questTag, out int known) || known < state)
        {
            _known[questTag] = state;
        }
    }
}
