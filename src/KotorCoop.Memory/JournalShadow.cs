namespace KotorCoop.Memory;

/// <summary>One local journal change: entry <see cref="QuestTag"/> is now at
/// <see cref="State"/>, or — when <see cref="Removed"/> — is gone from the journal
/// entirely (the engine ran <c>RemoveJournalQuestEntry</c>).</summary>
public readonly record struct JournalChange(string QuestTag, int State, bool Removed);

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
///
/// ⚠ PROTOCOL 31: this used to be upward-only in both directions — <see cref="Observe"/>
/// skipped any entry that had not RISEN, and <see cref="ShouldApply"/> refused any apply
/// that was not a rise. That silently hid a genuine local DECREASE (or a
/// `RemoveJournalQuestEntry`) from the session entirely — it never even reached
/// <see cref="Observe"/>'s output, so <c>JournalStore</c>'s own last-writer-wins fix upstream
/// had nothing to work with — and refused to ever WRITE a legitimate session-authorised
/// decrease back into the game. Both are now direction-neutral: any value change is worth
/// reporting/applying, and a tag that DISAPPEARS from the live read (removed by the engine)
/// is reported as a removal rather than silently ignored.
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
    /// every entry that CHANGED since the last read — added, raised, lowered, or (a tag
    /// this shadow knew as live that is no longer in <paramref name="live"/> at all) removed
    /// — the ones worth announcing to the session. Entries this instance itself applied from
    /// a peer come back from the game already known, so they do not echo.
    /// </summary>
    public List<JournalChange> Observe(IReadOnlyList<KeyValuePair<string, int>> live)
    {
        IsPrimed = true;

        List<JournalChange> changes = [];
        HashSet<string> liveTags = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, int> entry in live)
        {
            liveTags.Add(entry.Key);

            if (_known.TryGetValue(entry.Key, out int known) && known == entry.Value)
            {
                continue;
            }

            _known[entry.Key] = entry.Value;
            changes.Add(new JournalChange(entry.Key, entry.Value, Removed: false));
        }

        // A tag this shadow believed was live but is absent from this read was taken out of
        // the journal — RemoveJournalQuestEntry, most likely. Collect first, then remove:
        // mutating _known while enumerating its own Keys is undefined.
        List<string>? gone = null;
        foreach (string tag in _known.Keys)
        {
            if (!liveTags.Contains(tag))
            {
                (gone ??= []).Add(tag);
            }
        }

        if (gone is not null)
        {
            foreach (string tag in gone)
            {
                _known.Remove(tag);
                changes.Add(new JournalChange(tag, 0, Removed: true));
            }
        }

        return changes;
    }

    /// <summary>
    /// Whether an apply request for (tag, state) would change anything. False until primed
    /// (see the class remarks — this is the join-race guard), and false when the game is
    /// already known to hold exactly this state. Direction-neutral: a session-authorised
    /// decrease is just as worth applying as an advance.
    /// </summary>
    public bool ShouldApply(string questTag, int state) =>
        IsPrimed && (!_known.TryGetValue(questTag, out int known) || known != state);

    /// <summary>
    /// Records an issued apply. Direction-neutral: the session is the authority on the
    /// target value (including a legitimate decrease), so this always adopts it rather than
    /// only when it is higher — stops the same request being re-issued every pump while the
    /// plugin catches up.
    /// </summary>
    public void MarkApplied(string questTag, int state) => _known[questTag] = state;

    /// <summary>Whether a removal request for <paramref name="questTag"/> would change
    /// anything: false until primed, and false when the shadow already believes the tag is
    /// gone (never seen, or already removed).</summary>
    public bool ShouldRemove(string questTag) => IsPrimed && _known.ContainsKey(questTag);

    /// <summary>Records an issued removal — the tag drops out of the live set the shadow
    /// tracks, exactly as if the next read had simply not mentioned it.</summary>
    public void MarkRemoved(string questTag) => _known.Remove(questTag);
}
