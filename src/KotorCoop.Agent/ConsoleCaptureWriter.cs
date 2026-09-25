using System.Text;

namespace KotorCoop.Agent;

/// <summary>
/// A <see cref="TextWriter"/> that stands in for <see cref="Console.Out"/>/<see cref="Console.Error"/>
/// while the TUI owns the screen (see <see cref="TerminalScreen"/>), so a component that still
/// writes straight to the console — <c>CoopConfig.LogNonPlayerKeys</c>'s "coop.ini: N developer
/// switch(es)..." dump, <c>AgentComposition</c>'s <c>[census] ...</c> diagnostic, a background
/// thread's catch-all <c>Console.WriteLine</c> — lands as an ordinary transcript line instead of
/// being painted straight into the middle of the alternate screen buffer and corrupting it.
///
/// Buffers a partial write (<c>Write(char)</c>/<c>Write(string)</c> with no trailing newline
/// yet) and emits one callback per COMPLETE line; <see cref="FlushPending"/> flushes a trailing
/// unterminated fragment, so a line written right before shutdown with no following newline is
/// not silently dropped. Thread-safe: several background threads (the pump, the dispatcher's
/// own tick loop) already call <c>Console.WriteLine</c> unsynchronized relative to each other,
/// exactly as they did against the real <c>Console.Out</c>.
/// </summary>
public sealed class ConsoleCaptureWriter(Action<string> onLine) : TextWriter
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _pending = new();

    public override Encoding Encoding => Encoding.UTF8;

    // ⚠ Every override takes _gate for its WHOLE call, not per character. The real
    // Console.Out a component thinks it is writing to is a SyncTextWriter that already locks
    // for the whole Write/WriteLine call, so two threads' lines are never interleaved
    // character-by-character — only ever whole calls, in some order. Locking only inside a
    // shared Write(char) helper (each caller of Write(string) looping it WITHOUT holding the
    // lock across the loop) would let a second thread's WriteLine interleave its own
    // characters into _pending between two characters of the first thread's own line,
    // corrupting both. Locking the whole call is what makes that impossible.

    public override void Write(char value)
    {
        lock (_gate)
        {
            WriteLocked(value);
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        lock (_gate)
        {
            foreach (char c in value)
            {
                WriteLocked(c);
            }
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(value))
            {
                foreach (char c in value)
                {
                    WriteLocked(c);
                }
            }

            WriteLocked('\n');
        }
    }

    public override void WriteLine()
    {
        lock (_gate)
        {
            WriteLocked('\n');
        }
    }

    /// <summary>Flushes a trailing fragment that never saw a newline — call once on shutdown,
    /// after the last write a component is going to make.</summary>
    public void FlushPending()
    {
        lock (_gate)
        {
            if (_pending.Length > 0)
            {
                EmitLocked();
            }
        }
    }

    /// <summary>Caller already holds <see cref="_gate"/>.</summary>
    private void WriteLocked(char value)
    {
        switch (value)
        {
            case '\n':
                EmitLocked();
                return;

            case '\r':
                // Swallowed: a preceding \r in \r\n is not a second line, and a bare \r
                // (old-Mac line endings, never actually produced by this codebase) would
                // otherwise show up as a literal character in the transcript.
                return;

            default:
                _pending.Append(value);
                return;
        }
    }

    /// <summary>Caller already holds <see cref="_gate"/>.</summary>
    private void EmitLocked()
    {
        string line = _pending.ToString();
        _pending.Clear();
        onLine(line);
    }
}
