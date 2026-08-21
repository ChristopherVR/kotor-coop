namespace KotorCoop.Session;

/// <summary>
/// The allowlist gate for <c>TriggerRelay</c>: <c>coop.ini</c>'s <c>trigger_relay_patterns</c>
/// (default <c>k_p*_*</c>) decides which trigger OnEnter script names are relayed at all.
///
/// Checked against docs/research/trigger-relay.md's inventory of the 83 generic trigger
/// placements across the nine early-game modules: <c>k_p*_*</c> matches every one of the 69
/// story-cutscene scripts (<c>k_pend_bridge04</c>, <c>k_ptar_homint_en</c>, …) and none of the
/// 10 non-story generic scripts (<c>k_trg_partyinit</c>, <c>k_bant_trig</c>,
/// <c>k_trg_stealth</c>) or the 4 no-op zone-catalogue markers — exactly the split the
/// pattern is meant to draw.
///
/// This is the SAME class of glob (<c>*</c> only, no <c>?</c> or character classes) written
/// twice on purpose: the plugin project takes zero project references (NativeAOT publish
/// constraints — see KotorCoop.Plugin.csproj), so it cannot reference this assembly and
/// carries its own copy (<c>OdysseyTriggers.MatchesPattern</c>). Keep the two in lockstep;
/// this copy is the one the test suite actually exercises.
/// </summary>
public static class TriggerScriptPatterns
{
    /// <summary>Splits a comma-separated pattern list, trimming blanks. Matches how every
    /// other coop.ini list field in this codebase is read.</summary>
    public static string[] Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return [.. raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>True if <paramref name="scriptName"/> matches ANY of <paramref name="patterns"/>.
    /// An empty or null script name never matches — a trigger with no OnEnter script has
    /// nothing to relay regardless of pattern. Case-insensitive: KOTOR resrefs are.</summary>
    public static bool Matches(string? scriptName, IReadOnlyList<string> patterns)
    {
        if (string.IsNullOrEmpty(scriptName) || patterns.Count == 0)
        {
            return false;
        }

        foreach (string pattern in patterns)
        {
            if (MatchesOne(scriptName, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One glob against one name, <c>*</c> = "zero or more characters", nothing
    /// else special. Anchored at both ends (a pattern with no <c>*</c> at all must equal the
    /// name exactly).</summary>
    public static bool MatchesOne(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        return MatchesFrom(name.AsSpan(), pattern.AsSpan());
    }

    private static bool MatchesFrom(ReadOnlySpan<char> name, ReadOnlySpan<char> pattern)
    {
        while (pattern.Length > 0 && pattern[0] != '*')
        {
            if (name.Length == 0
                || char.ToLowerInvariant(name[0]) != char.ToLowerInvariant(pattern[0]))
            {
                return false;
            }

            name = name[1..];
            pattern = pattern[1..];
        }

        if (pattern.Length == 0)
        {
            return name.Length == 0;
        }

        // pattern[0] == '*': try consuming 0, 1, 2, ... characters of name before it.
        pattern = pattern[1..];
        for (int consumed = 0; consumed <= name.Length; consumed++)
        {
            if (MatchesFrom(name[consumed..], pattern))
            {
                return true;
            }
        }

        return false;
    }
}
