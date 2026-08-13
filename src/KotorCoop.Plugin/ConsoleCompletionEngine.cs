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
/// line switches from candidate list to the verb's usage at the same boundary.
/// </summary>
internal sealed class ConsoleCompletionEngine
{
    private readonly (string Verb, string Usage)[] _entries;

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
            // Mid-arguments: only an exact verb has anything useful to say.
            return usage ?? string.Empty;
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
        }

        return JoinCapped(candidates);
    }

    /// <summary>Forgets the cycle, e.g. when the console closes or history rewrites the line.</summary>
    public void Reset()
    {
        _cycleCandidates = null;
        _cycleIndex = 0;
        _lastProduced = null;
    }

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

        string[] result = new string[count];
        int i = 0;
        foreach ((string verb, _) in _entries)
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
        const int maxLength = 72;
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
