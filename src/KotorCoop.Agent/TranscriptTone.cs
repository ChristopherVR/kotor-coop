namespace KotorCoop.Agent;

/// <summary>What a transcript line means to the player, for its colour in the agent window.</summary>
public enum Tone
{
    /// <summary>Anything unclassified: light grey.</summary>
    Plain,

    /// <summary>A line the player typed (echoed as "> ..."): white.</summary>
    Command,

    /// <summary>Something worked: connected, rejoined, dropped, saved. Green.</summary>
    Good,

    /// <summary>Degraded or waiting, but not broken: W_ codes, mismatches, holds. Yellow.</summary>
    Warning,

    /// <summary>Something failed or was refused. Red.</summary>
    Bad,

    /// <summary>Guidance and answers the player asked for: help, usage, listings. Cyan.</summary>
    Helpful,

    /// <summary>Engineering detail the player can ignore (traces, census, perf). Dark grey.</summary>
    Diagnostic,
}

/// <summary>
/// Sorts a transcript line into a <see cref="Tone"/> by what it says. Pure and ordered: the
/// first matching rule wins, and the order is the point — "disconnected" must read as bad
/// before "connected" can read as good, and a diagnostic trace that happens to contain
/// "failed" in a field name must stay dim rather than shout.
/// </summary>
public static class TranscriptTone
{
    private static readonly string[] DiagnosticPrefixes =
    [
        "perf:", "party pool trace", "census", "enemy-baseline", "dialogue-wire", "dialogue-gate",
        "dialogue-node", "[resolve]", "[census]", "coop.ini:", "cheat (automatic)", "session-save:",
        "firing ", "scaling:", "first lightsaber", "character capture", "session-saved character",
        "items: ", "  0x",
    ];

    private static readonly string[] BadWords =
    [
        "failed", "error", "refused", "could not", "couldn't", "cannot", "can't", "crash", "gave up",
        "unknown command", "not connected", "disconnected", "lost", "not readable", "[e_", "rejected",
    ];

    private static readonly string[] WarningWords =
    [
        "[w_", "warning", "differs", "mismatch", "holding", "degraded", "stale", "retrying",
        "waiting", "ignored", "not yet", "still ", "are off", "is off", "turned off",
    ];

    private static readonly string[] GoodWords =
    [
        "connected", "joined", "rejoined", "adopted", "applied", "dropped ", "picked up", "landed",
        "resumed", "saved", "loaded", "now sharing", "autofollow on", "hosting", "gave '", "added",
        "removed", "walking to", "moved from",
    ];

    private static readonly string[] HelpfulWords =
    [
        "commands:", "usage", "tip:", "press ", "players (", "co-op staging cheats",
        "autofollow", "following",
    ];

    public static Tone Classify(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return Tone.Plain;
        }

        if (line.StartsWith("> ", StringComparison.Ordinal))
        {
            return Tone.Command;
        }

        string lower = line.ToLowerInvariant();
        foreach (string prefix in DiagnosticPrefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Tone.Diagnostic;
            }
        }

        if (ContainsAny(lower, BadWords))
        {
            return Tone.Bad;
        }

        if (ContainsAny(lower, WarningWords))
        {
            return Tone.Warning;
        }

        if (ContainsAny(lower, GoodWords))
        {
            return Tone.Good;
        }

        return ContainsAny(lower, HelpfulWords) || lower.StartsWith("  ", StringComparison.Ordinal)
            ? Tone.Helpful
            : Tone.Plain;
    }

    private static bool ContainsAny(string haystack, string[] needles)
    {
        foreach (string needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
