using KotorCoop.Protocol;

namespace KotorCoop.Agent;

/// <summary>
/// Turns a content-compatibility verdict into lines a player actually sees.
///
/// WHY THIS EXISTS. A degradation is invisible by construction: the whole point of the
/// per-channel policy is that the session keeps running, so a player whose enemy sync was
/// switched off because their partner has a module mod sees exactly what a player with a
/// broken build sees — enemies that do not line up and no explanation. The verdict was
/// already machine-readable and already on the agent's console readout; neither of those is
/// on the screen of someone playing the game.
///
/// Announces on CHANGE only, and only the notes that are new, so a session whose verdict is
/// stable says its piece once. The channels coming back — walking out of a mod-edited module
/// into one both installs agree on — is itself news, and is announced too: a player told
/// "enemies are off here" needs to be told when "here" ends.
/// </summary>
public sealed class CompatibilityAnnouncer
{
    private readonly HashSet<CompatibilityCode> _announced = [];
    private RefusalReason _refusal = RefusalReason.None;
    private SessionCapabilities _dropped = SessionCapabilities.None;
    private bool _seen;

    /// <summary>
    /// Diffs <paramref name="verdict"/> against the last observation and announces what
    /// changed. Null (no session, or a session that has not compared yet) says nothing.
    /// </summary>
    public void Observe(CompatibilityVerdict? verdict, Action<string> announce)
    {
        ArgumentNullException.ThrowIfNull(announce);

        if (verdict is null)
        {
            return;
        }

        if (verdict.Refusal != _refusal)
        {
            _refusal = verdict.Refusal;
            if (verdict.Refusal != RefusalReason.None)
            {
                // The refusal line carries its code (E_SAVE, E_EXE...) so a player can be
                // told what to do about it, and a script can match on it.
                announce($"co-op refused this world: {verdict.Summary}");
            }
        }

        foreach (CompatibilityNote note in verdict.Notes)
        {
            if (_announced.Add(note.Code))
            {
                announce($"co-op [{note.Tag}] {note.Text}");
            }
        }

        // Runtime settings may move back into agreement and diverge again later in the
        // same session. Re-arm these findings once the mismatch clears.
        foreach (CompatibilityCode code in RepeatableCodes)
        {
            if (!verdict.Notes.Any(note => note.Code == code))
            {
                _announced.Remove(code);
            }
        }

        SessionCapabilities dropped = verdict.Dropped;
        SessionCapabilities restored = _dropped & ~dropped;

        if (_seen && restored != SessionCapabilities.None)
        {
            announce($"co-op: {Describe(restored)} shared again");

            // A channel that came back may go again in the next module, and the player
            // deserves the same warning the second time.
            foreach (CompatibilityCode code in RepeatableCodes)
            {
                _announced.Remove(code);
            }
        }

        _dropped = dropped;
        _seen = true;
    }

    /// <summary>Codes whose finding is about WHERE the players are standing, so it can
    /// legitimately fire again after it has cleared. Everything else is a property of the two
    /// installs and is true for the whole session, so it is said exactly once.</summary>
    private static readonly CompatibilityCode[] RepeatableCodes =
        [CompatibilityCode.ModuleGit, CompatibilityCode.Difficulty];

    /// <summary>The session went away: forget everything, so a reconnect re-announces its
    /// verdict — which a fresh session should do.</summary>
    public void Reset()
    {
        _announced.Clear();
        _refusal = RefusalReason.None;
        _dropped = SessionCapabilities.None;
        _seen = false;
    }

    /// <summary>Names channels the way a player thinks of them, not the way the enum spells
    /// them ("enemies, doors and items", not "Enemies, Doors, GroundItems, Party").</summary>
    public static string Describe(SessionCapabilities caps)
    {
        List<string> parts = [];

        if ((caps & SessionCapabilities.Enemies) != 0)
        {
            parts.Add("enemies");
        }

        if ((caps & SessionCapabilities.Doors) != 0)
        {
            parts.Add("doors");
        }

        if ((caps & SessionCapabilities.GroundItems) != 0)
        {
            parts.Add("items");
        }

        if ((caps & SessionCapabilities.Party) != 0)
        {
            parts.Add("party");
        }

        if ((caps & SessionCapabilities.WorldState) != 0)
        {
            parts.Add("quest state");
        }

        if ((caps & SessionCapabilities.Journal) != 0)
        {
            parts.Add("journal");
        }

        if ((caps & SessionCapabilities.Appearance) != 0)
        {
            parts.Add("appearance");
        }

        if ((caps & SessionCapabilities.Equipment) != 0)
        {
            parts.Add("equipment");
        }

        return parts.Count switch
        {
            0 => "nothing",
            1 => parts[0],
            _ => string.Join(", ", parts[..^1]) + " and " + parts[^1],
        };
    }
}
