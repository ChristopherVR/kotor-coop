using KotorCoop.Plugin;

namespace KotorCoop.Agent;

/// <summary>
/// The dashboard's own always-visible command line: buffer, caret, Tab/Shift+Tab completion
/// and submit history. Pure and console-free — no <c>Console.*</c> call anywhere in this file
/// — so it is unit-testable without a real terminal; <see cref="AgentDashboard"/> drives it
/// from <c>Console.ReadKey</c>/<c>ConsoleKeyInfo</c> and renders whatever it reports back,
/// exactly the split <see cref="KotorCoop.Plugin.ConsoleAssistController"/> already keeps
/// between engine-buffer plumbing and completion decisions.
///
/// Completion and history reuse the SAME pure classes the in-game <c>~</c> console uses —
/// <see cref="ConsoleCompletionEngine"/>, <see cref="ConsoleHistoryRing"/>, over
/// <see cref="ConsoleVerbCatalog"/> — linked into KotorCoop.Agent.csproj by SOURCE, the same
/// way LogRotation.cs already is, not copied: a co-op verb added to one console reaches the
/// other automatically, and the two consoles can never quietly answer a Tab press
/// differently. Only co-op verbs complete here, unlike the in-game console: the engine's own
/// verb table (<c>SetEngineVerbs</c>) is read out of the running game's command table, which
/// only the plugin, in-process, can see — this side of the pipe has no such table to read.
/// </summary>
public sealed class DashboardInputLine
{
    /// <summary>The verb table this input line completes against — literally
    /// <see cref="ConsoleVerbCatalog.Entries"/>, exposed here (rather than left buried inside
    /// <see cref="_completion"/>) so a test can assert the agent's own completion source
    /// really is the plugin's table and not a hand-maintained copy of it.</summary>
    public static IReadOnlyList<(string Verb, string Usage)> Catalog => ConsoleVerbCatalog.Entries;

    /// <summary>How many submitted lines <see cref="_history"/> keeps.</summary>
    private const int HistoryCapacity = 50;

    private readonly ConsoleCompletionEngine _completion = new(ConsoleVerbCatalog.Entries);
    private readonly ConsoleHistoryRing _history = new(HistoryCapacity);

    /// <summary>The line as typed so far.</summary>
    public string Buffer { get; private set; } = string.Empty;

    /// <summary>Caret position within <see cref="Buffer"/>, 0..Buffer.Length inclusive.</summary>
    public int Caret { get; private set; }

    /// <summary>Inserts one character at the caret and advances it. Control characters
    /// (arrow/function-key sentinels a raw <c>ConsoleKeyInfo.KeyChar</c> can carry) are the
    /// caller's job to filter before reaching here — this only ever appends what it is given.</summary>
    public void TypeChar(char c)
    {
        Buffer = Buffer[..Caret] + c + Buffer[Caret..];
        Caret++;
    }

    /// <summary>Deletes the character before the caret, if any.</summary>
    public void Backspace()
    {
        if (Caret == 0)
        {
            return;
        }

        Buffer = Buffer[..(Caret - 1)] + Buffer[Caret..];
        Caret--;
    }

    /// <summary>Deletes the character under the caret, if any.</summary>
    public void Delete()
    {
        if (Caret >= Buffer.Length)
        {
            return;
        }

        Buffer = Buffer[..Caret] + Buffer[(Caret + 1)..];
    }

    public void MoveLeft() => Caret = Math.Max(0, Caret - 1);

    public void MoveRight() => Caret = Math.Min(Buffer.Length, Caret + 1);

    public void MoveHome() => Caret = 0;

    public void MoveEnd() => Caret = Buffer.Length;

    /// <summary>Esc: wipes the line and forgets any completion cycle in progress. Unlike the
    /// in-game console's own Escape (which only dismisses the hint), this dashboard has no
    /// separate "hint dismissed but line intact" state to fall back to, so clearing the line
    /// outright is the whole action — matching a shell's Ctrl+U more than the in-game console's
    /// own Escape.</summary>
    public void ClearLine()
    {
        Buffer = string.Empty;
        Caret = 0;
        _completion.Reset();
    }

    /// <summary>Tab (<paramref name="reverse"/> false) or Shift+Tab (true): completes the
    /// first token and cycles through candidates on repeated presses, same as the in-game
    /// console. A no-op once an argument follows, or when nothing matches.</summary>
    public void Complete(bool reverse = false)
    {
        string? completed = reverse ? _completion.CompleteReverse(Buffer) : _completion.Complete(Buffer);
        if (completed is null)
        {
            return;
        }

        Buffer = completed;
        Caret = Buffer.Length;
    }

    /// <summary>Up: recalls an older submitted line, replacing the buffer.</summary>
    public void HistoryUp()
    {
        if (_history.Older() is not { } older)
        {
            return;
        }

        Buffer = older;
        Caret = Buffer.Length;
        _completion.Reset();
    }

    /// <summary>Down: steps back toward the live line (empty once past the newest entry).
    /// A no-op while not currently browsing history.</summary>
    public void HistoryDown()
    {
        if (_history.Newer() is not { } newer)
        {
            return;
        }

        Buffer = newer;
        Caret = Buffer.Length;
        _completion.Reset();
    }

    /// <summary>The hint line for the buffer as it stands right now: the candidate list while
    /// the first token is incomplete, or the matched verb's usage with the argument currently
    /// being typed marked — see <see cref="ConsoleCompletionEngine.Hint"/>. Empty for an
    /// empty buffer.</summary>
    public string Hint => _completion.Hint(Buffer);

    /// <summary>The ranked candidates for the token being typed, unjoined and uncapped —
    /// for a caller that wants a multi-row dropdown rather than <see cref="Hint"/>'s one
    /// capped line. Empty once an argument follows or nothing matches.</summary>
    public IReadOnlyList<string> Candidates => _completion.CandidatesFor(Buffer);

    /// <summary>
    /// Enter: records the line in history (empty/duplicate-of-previous lines are dropped by
    /// <see cref="ConsoleHistoryRing"/> itself), clears the buffer and completion cycle, and
    /// returns what was submitted — empty when the buffer was blank, so a caller can tell
    /// "nothing to dispatch" from "dispatch this" without a second check.
    /// </summary>
    public string Submit()
    {
        string submitted = Buffer;
        _history.Record(submitted);
        Buffer = string.Empty;
        Caret = 0;
        _completion.Reset();
        return submitted;
    }
}
