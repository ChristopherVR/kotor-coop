namespace KotorCoop.Plugin;

/// <summary>
/// The ~ console's submitted-line history: a fixed ring of recent lines plus the Up/Down
/// navigation cursor. <see cref="FuncInterpHook"/> records EVERY submitted line here (co-op
/// verbs and engine cheats alike — recall is just as useful for <c>giveitem</c> as for
/// <c>connect</c>), and <see cref="ConsoleAssistController"/> navigates it while the console
/// is open.
///
/// Deliberately dependency-free — no pointers, no engine state — so it compiles into the
/// 64-bit test project as-is (see KotorCoop.Tests.csproj). The engine buffer writes stay in
/// the controller; this class only decides WHAT line the recall keys produce.
///
/// ⚠ Lines whose verb is <c>password</c>/<c>pw</c> are never stored: a session secret must
/// not be replayable by anyone who walks up to the keyboard and taps Up.
/// </summary>
internal sealed class ConsoleHistoryRing
{
    private readonly string[] _entries;

    /// <summary>Lines ever recorded (monotonic); the ring index is this modulo capacity.</summary>
    private int _count;

    /// <summary>Navigation cursor as an AGE: -1 = live (not navigating), 0 = newest entry,
    /// rising toward the oldest. An age survives new recordings meaning "n back from the
    /// newest", which is the behaviour a shell user expects.</summary>
    private int _age = -1;

    public ConsoleHistoryRing(int capacity = 32)
    {
        _entries = new string[Math.Max(1, capacity)];
    }

    public int Capacity => _entries.Length;

    /// <summary>How many lines are currently held (saturates at the capacity).</summary>
    public int Count => Math.Min(_count, _entries.Length);

    /// <summary>
    /// Records one submitted line. Empty/whitespace lines and secrets are dropped; a line
    /// identical to the previous entry is not stored twice in a row (retyping `status` five
    /// times should cost one Up, not five). Any submit — stored or not — returns the
    /// navigation cursor to live, because the console line it referred to is gone.
    /// </summary>
    public void Record(string? line)
    {
        _age = -1;

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        string trimmed = line.Trim();
        if (IsSecret(trimmed))
        {
            return;
        }

        if (_count > 0 && string.Equals(_entries[(_count - 1) % _entries.Length], trimmed, StringComparison.Ordinal))
        {
            return;
        }

        _entries[_count % _entries.Length] = trimmed;
        _count++;
    }

    /// <summary>
    /// Up: one step toward the oldest stored line. Returns the line to place in the console
    /// buffer, or null when there is no history at all. Pressing Up at the oldest entry
    /// stays there (returning it again), matching shell behaviour.
    /// </summary>
    public string? Older()
    {
        int stored = Count;
        if (stored == 0)
        {
            return null;
        }

        _age = _age < 0 ? 0 : Math.Min(_age + 1, stored - 1);
        return EntryAt(_age);
    }

    /// <summary>
    /// Down: one step back toward the present. Returns the line to place in the console
    /// buffer; stepping past the newest entry returns an EMPTY string (clear the line, back
    /// to live), and null means "not navigating — leave the buffer alone".
    /// </summary>
    public string? Newer()
    {
        if (_age < 0)
        {
            return null;
        }

        if (_age == 0)
        {
            _age = -1;
            return string.Empty;
        }

        _age--;
        return EntryAt(_age);
    }

    /// <summary>Back to live. Called when the console closes, so the next open starts fresh.</summary>
    public void ResetCursor() => _age = -1;

    private string EntryAt(int age) => _entries[(_count - 1 - age) % _entries.Length];

    /// <summary>
    /// True for lines whose verb is <c>password</c>/<c>pw</c>, including the slash and
    /// <c>coop</c>-prefixed spellings the parser accepts (<c>/pw</c>, <c>coop password</c>).
    /// Only the verb is tested — <c>connect host secret</c> stays recallable, since retyping
    /// the address is the whole point of history and the secret was the player's own choice
    /// to put on that line.
    /// </summary>
    private static bool IsSecret(string trimmed)
    {
        ReadOnlySpan<char> s = trimmed.AsSpan();
        ReadOnlySpan<char> token = NextToken(ref s);
        if (token.Length > 0 && token[0] == '/')
        {
            token = token[1..];
        }

        if (token.Equals("coop", StringComparison.OrdinalIgnoreCase))
        {
            token = NextToken(ref s);
            if (token.Length > 0 && token[0] == '/')
            {
                token = token[1..];
            }
        }

        return token.Equals("password", StringComparison.OrdinalIgnoreCase)
            || token.Equals("pw", StringComparison.OrdinalIgnoreCase);
    }

    private static ReadOnlySpan<char> NextToken(ref ReadOnlySpan<char> s)
    {
        s = s.TrimStart();
        int end = 0;
        while (end < s.Length && !char.IsWhiteSpace(s[end]))
        {
            end++;
        }

        ReadOnlySpan<char> token = s[..end];
        s = s[end..];
        return token;
    }
}
