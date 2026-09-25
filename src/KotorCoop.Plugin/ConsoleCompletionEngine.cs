namespace KotorCoop.Plugin;

/// <summary>
/// Tab completion and the hint line for the ~ console, as a pure state machine over the
/// verb catalog: given the current buffer text it answers "what should Tab write?" and
/// "what should the hint line under the console say?". Dependency-free — no pointers, no
/// engine state — so it compiles into the 64-bit test project as-is; the engine buffer
/// reads/writes stay in <see cref="ConsoleAssistController"/>.
///
/// Only the FIRST token completes. Once an argument (or even a trailing space) follows the
/// verb, Tab does nothing — the engine's caret could be anywhere in the line, and rewriting
/// an argument the player is mid-way through typing is worse than doing nothing. The hint
/// line switches from candidate list to the verb's usage at the same boundary, and from
/// there it tracks WHICH ARGUMENT is being typed, because "connect &lt;ip&gt; [password]" stops
/// being useful the moment you are three tokens in and cannot remember whether the password
/// came before or after the port.
///
/// Completion covers the ENGINE's verbs as well as ours (see <see cref="SetEngineVerbs"/>).
/// The mod borrows KOTOR's developer console rather than replacing it, so a candidate list
/// that knew only about co-op verbs would quietly imply the ~60 the game registers itself do
/// not exist.
/// </summary>
internal sealed class ConsoleCompletionEngine
{
    private readonly (string Verb, string Usage)[] _entries;

    /// <summary>
    /// Verbs the ENGINE registered, read live from its command table. Kept separate from
    /// <see cref="_entries"/> rather than merged, for two reasons: co-op verbs must sort
    /// FIRST in a candidate list (Tab should reach ours before a stat cheat when both
    /// match), and only ours have a usage string to show.
    /// </summary>
    private string[] _engineVerbs = [];

    /// <summary>Longest hint the queued-text renderer draws without running off the screen
    /// edge; it has no layout, so this is the only wrapping there is.</summary>
    private const int MaxHintLength = 72;

    /// <summary>The candidates being cycled, or null when no cycle is in progress.</summary>
    private string[]? _cycleCandidates;

    /// <summary>The next candidate to produce within <see cref="_cycleCandidates"/>.</summary>
    private int _cycleIndex;

    /// <summary>
    /// What the last Complete() wrote (the bare verb, no lead). If the next Complete() sees
    /// exactly this as the first token, the player has NOT typed since — so cycle onward
    /// from the ORIGINAL prefix instead of treating our own completion as a new, narrower
    /// prefix (which would lock the cycle onto its first match forever).
    /// </summary>
    private string? _lastProduced;

    public ConsoleCompletionEngine((string Verb, string Usage)[] entries)
    {
        _entries = entries;
    }

    /// <summary>
    /// Supplies the engine's own console verbs. Anything already in the co-op catalog is
    /// dropped so a verb we intercept is not offered twice, and the rest are sorted so the
    /// cycle order is stable — the table is sorted by name already, but a mod's late
    /// registration would not be.
    /// </summary>
    public void SetEngineVerbs(IReadOnlyList<string> verbs)
    {
        List<string> kept = new(verbs.Count);
        foreach (string verb in verbs)
        {
            if (verb.Length == 0 || Contains(kept, verb) || IsCatalogVerb(verb))
            {
                continue;
            }

            kept.Add(verb);
        }

        kept.Sort(StringComparer.Ordinal);
        _engineVerbs = [.. kept];
    }

    private bool IsCatalogVerb(string verb)
    {
        foreach ((string candidate, _) in _entries)
        {
            if (string.Equals(candidate, verb, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(List<string> list, string value)
    {
        foreach (string item in list)
        {
            if (string.Equals(item, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The line Tab should replace the buffer with, or null to leave the buffer alone
    /// (arguments present, or no verb matches). An empty buffer cycles through every verb.
    /// Leading whitespace and a leading '/' are preserved in the returned line.
    /// </summary>
    public string? Complete(string buffer)
    {
        Split(buffer, out string lead, out string token, out string rest);
        if (rest.Length > 0)
        {
            // The verb is finished (even a trailing space counts): nothing to complete.
            Reset();
            return null;
        }

        if (_cycleCandidates is { } cycling
            && string.Equals(token, _lastProduced, StringComparison.Ordinal))
        {
            // Untouched since our last completion: advance the existing cycle.
            string next = cycling[_cycleIndex];
            _cycleIndex = (_cycleIndex + 1) % cycling.Length;
            _lastProduced = next;
            return lead + next;
        }

        string prefix = token.ToLowerInvariant();
        string[] candidates = Candidates(prefix);
        if (candidates.Length == 0)
        {
            Reset();
            return null;
        }

        _cycleCandidates = candidates;
        string first = candidates[0];
        _cycleIndex = candidates.Length > 1 ? 1 : 0;
        _lastProduced = first;
        return lead + first;
    }

    /// <summary>
    /// Shift+Tab's step: the same cycle <see cref="Complete"/> drives, but backward — sharing
    /// its candidate list and cursor, so a forward Tab followed immediately by a backward
    /// Shift+Tab lands back on the candidate the forward step started from, the way a shell's
    /// reverse-cycle normally behaves. Typing since the last completion restarts the cycle
    /// exactly the same way <see cref="Complete"/>'s own check does. The first backward step
    /// of a fresh cycle starts from the LAST candidate rather than the first, so Shift+Tab on
    /// an untouched prefix reaches the end of the list in one press instead of walking there
    /// forward.
    /// </summary>
    public string? CompleteReverse(string buffer)
    {
        Split(buffer, out string lead, out string token, out string rest);
        if (rest.Length > 0)
        {
            // The verb is finished: nothing to complete, same as Complete.
            Reset();
            return null;
        }

        if (_cycleCandidates is { } cycling
            && string.Equals(token, _lastProduced, StringComparison.Ordinal))
        {
            // _cycleIndex is "one past whatever Complete/CompleteReverse last produced", so
            // stepping backward TWO positions from it (mod length) is the candidate before
            // that one.
            int at = (((_cycleIndex - 2) % cycling.Length) + cycling.Length) % cycling.Length;
            string previous = cycling[at];
            _cycleIndex = (at + 1) % cycling.Length;
            _lastProduced = previous;
            return lead + previous;
        }

        string prefix = token.ToLowerInvariant();
        string[] candidates = Candidates(prefix);
        if (candidates.Length == 0)
        {
            Reset();
            return null;
        }

        _cycleCandidates = candidates;
        string last = candidates[^1];
        _cycleIndex = 0;
        _lastProduced = last;
        return lead + last;
    }

    /// <summary>
    /// The full ranked candidate list for the token currently being typed — co-op verbs
    /// first, then the engine's, same order <see cref="Candidates"/> already produces for
    /// <see cref="Hint"/> — for a caller that wants more than one capped line (e.g. a
    /// multi-row dropdown). Same boundary as <see cref="Hint"/>: empty once the first token is
    /// complete and an argument follows, or the buffer has nothing typed yet.
    /// </summary>
    public string[] CandidatesFor(string buffer)
    {
        Split(buffer, out _, out string token, out string rest);
        if (token.Length == 0 || rest.Length > 0)
        {
            return [];
        }

        return Candidates(token.ToLowerInvariant());
    }

    /// <summary>
    /// The one hint line to draw under the console, or empty for none. While the first
    /// token is being typed this is the candidate list ("connect  clear  cls"); once the
    /// token is a complete verb (arguments follow, or it is the only match) it becomes that
    /// verb's usage. An empty buffer hints nothing — a 25-verb wall before the player has
    /// typed anything is noise, and `help` already exists.
    /// </summary>
    public string Hint(string buffer)
    {
        Split(buffer, out _, out string token, out string rest);
        if (token.Length == 0)
        {
            return string.Empty;
        }

        string key = token.ToLowerInvariant();
        string? usage = null;
        foreach ((string verb, string verbUsage) in _entries)
        {
            if (verb == key)
            {
                usage = verbUsage;
                break;
            }
        }

        if (rest.Length > 0)
        {
            // Mid-arguments. A co-op verb shows its usage with the argument being typed
            // called out; an ENGINE verb has no usage we could invent, so it says only that
            // the console owns it — which is still the useful fact (the line will not be
            // intercepted, so a typo will be reported by the engine, not by us).
            if (usage is { } known)
            {
                return WithArgumentMarker(known, rest);
            }

            return IsEngineVerb(key) ? $"{key}  (game console command)" : string.Empty;
        }

        string[] candidates = Candidates(key);
        if (candidates.Length == 0)
        {
            return string.Empty;
        }

        if (candidates.Length == 1)
        {
            // Exactly one place this token can land — show where, arguments and all, even
            // before the last letters are typed ("ho" already means host and nothing else).
            foreach ((string verb, string verbUsage) in _entries)
            {
                if (verb == candidates[0])
                {
                    return verbUsage;
                }
            }

            // The sole candidate is one of the engine's.
            return $"{candidates[0]}  (game console command)";
        }

        return JoinCapped(candidates);
    }

    private bool IsEngineVerb(string verb)
    {
        foreach (string candidate in _engineVerbs)
        {
            if (string.Equals(candidate, verb, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The usage line with the argument currently being typed called out — the "what should
    /// the NEXT token be" half of the assist.
    ///
    /// The specs come from the usage string itself rather than a second table, so a verb can
    /// never grow an argument in one place and not the other. A trailing parenthetical note
    /// ("(blank = nearest hostile)") is prose, not an argument, and stops the scan.
    /// </summary>
    private static string WithArgumentMarker(string usage, string rest)
    {
        string[] parts = usage.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int specCount = 0;
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i][0] == '(')
            {
                break;
            }

            specCount++;
        }

        if (specCount == 0)
        {
            return usage;
        }

        // How many arguments are BEHIND the caret. A trailing space means the token just
        // finished, so the next one is being started; without it the last token is still
        // being typed and IS the current argument.
        string[] typed = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool started = rest.Length > 0 && char.IsWhiteSpace(rest[^1]);
        int index = started ? typed.Length : Math.Max(0, typed.Length - 1);

        string marker = index < specCount ? parts[1 + index] : "(all given)";
        string line = $"{usage}  <- {marker}";

        // The renderer has no layout: an overlong line just draws off the screen edge, so a
        // hint that no longer fits loses the marker rather than the usage.
        return line.Length <= MaxHintLength ? line : usage;
    }

    /// <summary>Forgets the cycle, e.g. when the console closes or history rewrites the line.</summary>
    public void Reset()
    {
        _cycleCandidates = null;
        _cycleIndex = 0;
        _lastProduced = null;
    }

    /// <summary>
    /// Everything the prefix could become: the co-op catalog in its declared order first,
    /// then the engine's own verbs. Ours lead deliberately — Tab from "s" should reach
    /// <c>say</c>, <c>slay</c> and <c>status</c> before it walks the eight <c>set*</c> stat
    /// cheats.
    /// </summary>
    private string[] Candidates(string prefix)
    {
        int count = 0;
        foreach ((string verb, _) in _entries)
        {
            if (verb.StartsWith(prefix, StringComparison.Ordinal))
            {
                count++;
            }
        }

        foreach (string verb in _engineVerbs)
        {
            if (verb.StartsWith(prefix, StringComparison.Ordinal))
            {
                count++;
            }
        }

        string[] result = new string[count];
        int i = 0;
        foreach ((string verb, _) in _entries)
        {
            if (verb.StartsWith(prefix, StringComparison.Ordinal))
            {
                result[i++] = verb;
            }
        }

        foreach (string verb in _engineVerbs)
        {
            if (verb.StartsWith(prefix, StringComparison.Ordinal))
            {
                result[i++] = verb;
            }
        }

        return result;
    }

    /// <summary>
    /// Joins candidates with two spaces, capped to one queued-text line — the renderer has
    /// no layout, so an overlong hint would just draw off the screen edge.
    /// </summary>
    private static string JoinCapped(string[] candidates)
    {
        const int maxLength = MaxHintLength;
        System.Text.StringBuilder sb = new(maxLength + 8);
        foreach (string candidate in candidates)
        {
            int needed = (sb.Length > 0 ? 2 : 0) + candidate.Length;
            if (sb.Length + needed > maxLength)
            {
                sb.Append(sb.Length > 0 ? "  ..." : "...");
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append("  ");
            }

            sb.Append(candidate);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Splits the buffer into the preserved lead (leading whitespace plus an optional '/'),
    /// the first token, and everything after it. The '/' is lead rather than token so that
    /// "/co" completes to "/connect" — the parser strips it the same way.
    /// </summary>
    private static void Split(string buffer, out string lead, out string token, out string rest)
    {
        int i = 0;
        while (i < buffer.Length && char.IsWhiteSpace(buffer[i]))
        {
            i++;
        }

        if (i < buffer.Length && buffer[i] == '/')
        {
            i++;
        }

        lead = buffer[..i];

        int start = i;
        while (i < buffer.Length && !char.IsWhiteSpace(buffer[i]))
        {
            i++;
        }

        token = buffer[start..i];
        rest = buffer[i..];
    }
}
