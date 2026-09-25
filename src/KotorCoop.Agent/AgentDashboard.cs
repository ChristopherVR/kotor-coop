using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// The agent's own full-screen TUI — a compact status header, a scrolling transcript (chat,
/// session events, command echoes/replies, and any stray console output captured on its way
/// to corrupting the screen), and an always-visible command line, laid out the way Claude
/// Code's own terminal UI is: everything owned, nothing left to the shell's scrollback.
///
/// This class is the ORCHESTRATOR only — it owns no rendering or layout logic of its own.
/// Layout/wrapping/scroll-state are pure, unit-tested classes it drives each tick:
/// <see cref="KotorCoop.Agent.TranscriptBuffer"/> (the scrollback), <see cref="DashboardInputLine"/>
/// (the command line's buffer/caret/history/completion), and <see cref="FrameComposer"/> (turns
/// all of it into exactly <c>height</c> rows for the current window). <see cref="TerminalScreen"/>
/// is the one piece that actually touches the OS: the alternate screen buffer, VT mode, and the
/// <c>Console.Out</c>/<c>Console.Error</c> capture that keeps a stray <c>Console.WriteLine</c>
/// from corrupting the paint.
///
/// Reads the live agent from the <see cref="SessionManager"/> each tick rather than holding a
/// fixed one, so the view follows connect/disconnect/reconnect: between sessions there is no
/// agent and the readout says so instead of rendering stale peers.
/// </summary>
public sealed class AgentDashboard(
    SessionManager manager,
    IGameBridge bridge,
    string? dropFile = null,
    string? logFile = null,
    CoopCommandDispatcher? dispatcher = null)
{
    /// <summary>The always-visible command line: buffer/caret/history/completion, all pure —
    /// see <see cref="DashboardInputLine"/>'s own doc comment.</summary>
    private readonly DashboardInputLine _input = new();

    /// <summary>The scrolling transcript — see <see cref="TranscriptBuffer"/>'s own doc
    /// comment. Fed by <see cref="Note"/> (session events, command replies/echoes) and, once
    /// the TUI is up, by <see cref="TerminalScreen"/>'s captured stray console output.</summary>
    private readonly TranscriptBuffer _transcript = new();

    /// <summary>Non-null only while the interactive TUI is running (<c>canPosition</c> in
    /// <see cref="RunAsync"/>) — owns the alternate screen and the console capture. Painting
    /// goes through <see cref="TerminalScreen.Screen"/>, never <c>Console.Out</c>, which this
    /// class has redirected out from under itself for exactly that reason.</summary>
    private TerminalScreen? _terminal;

    /// <summary>Whether this run can show and drive the command line at all: false when input
    /// is redirected (<c>Console.IsInputRedirected</c>), set once at the top of
    /// <see cref="RunAsync"/>.</summary>
    private bool _showInputLine;

    /// <summary>F1: swaps the transcript for the full details panel (the old NETWORK/GAME
    /// MEMORY/THIS PLAYER/SHARED WORLD readout) in the middle region.</summary>
    private bool _detailsOpen;

    /// <summary>The content width and transcript viewport height <see cref="RenderInteractive"/>
    /// last computed — what a PageUp/PageDown/Ctrl+Home/Ctrl+End key clamps a scroll against,
    /// since <see cref="TranscriptBuffer"/> needs both to answer "how far can this scroll".</summary>
    private int _lastContentWidth = 1;
    private int _lastTranscriptViewportHeight = 1;

    /// <summary>The previous frame's signature (see <see cref="ComputeSignature"/>) — a paint
    /// is skipped entirely when nothing about the composed frame changed since last tick, so
    /// an idle, disconnected dashboard is not rewriting an identical screen five times a
    /// second. Width/height are folded into the signature so a resize always repaints even if
    /// every row's TEXT happens to be unchanged.</summary>
    private string? _lastSignature;

    /// <summary>
    /// What the D key drops, cycling. KOTOR 1 has no native drop-item UI, so the demo
    /// conjures well-known blueprints at the player's feet instead.
    ///
    /// ⚠ EVERY ONE OF THESE MUST EXIST IN THE INSTALL, and the check is not the one you would
    /// expect: `CreateItemOnFloor` 0x00547F60 never tests whether the template loaded
    /// (docs/research/item-identity.md §10), so a resref with a typo spawns a real but EMPTY
    /// item and reports success. All three below were confirmed present in the GOG build's
    /// chitin.key with tools/kotorfiles.py. The neighbouring `g_w_blstrpstl01` — one zero
    /// short — is NOT a blueprint, and a live session spent an afternoon poking the nameless
    /// body it produced.
    /// </summary>
    private static readonly string[] DropCycle =
    [
        "g_w_lghtsbr01",    // blue lightsaber
        "g_w_blstrpstl001", // blaster pistol  (NOT ...pstl01 — that one does not exist)
        "g_i_medeqpmnt01",  // medpac
    ];

    /// <summary>
    /// Rotates <paramref name="logFile"/> once, at construction, before the first line is
    /// ever appended to it — the same "<name>.1.log" scheme PluginHost.RotateLogsIfNeeded
    /// uses for kotorcoop-plugin.log, applied here because <see cref="Note"/> and
    /// <see cref="WriteLogLine"/> below use the same never-truncating File.AppendAllText, and
    /// tools/demo.ps1 launches the agent with <c>--log</c> pointed at the same
    /// $env:TEMP\kotorcoop-demo\agent1.log / agent2.log every run without ever clearing them
    /// — the identical unbounded-growth shape the plugin log had, just for the dashboard's
    /// event and status lines instead.
    ///
    /// No coop.ini to read a cap from here (the --log path is a scratch file, not
    /// necessarily beside a game install), so this always uses
    /// <see cref="KotorCoop.Plugin.LogRotation.DefaultCapMb"/>.
    /// </summary>
    private readonly bool _logRotated = RotateLogIfNeeded(logFile);

    /// <summary>Mid-session rotation's own clock and gate — see <see cref="RotateLogMidSessionIfDue"/>.</summary>
    private readonly Lock _logRotationGate = new();
    private long _lastLogSizeCheckMs;

    private static bool RotateLogIfNeeded(string? path)
    {
        if (path is not null)
        {
            try
            {
                if (File.Exists(path))
                {
                    long cap = KotorCoop.Plugin.LogRotation.CapBytes(KotorCoop.Plugin.LogRotation.DefaultCapMb);
                    FileInfo info = new(path);
                    if (KotorCoop.Plugin.LogRotation.ShouldRotate(info.Length, cap))
                    {
                        KotorCoop.Plugin.LogRotation.ApplyShiftPlan(
                            KotorCoop.Plugin.LogRotation.ShiftPlan(path, KotorCoop.Plugin.LogRotation.DefaultKeepGenerations));
                    }
                }
            }
            catch
            {
                // Best-effort: a failed rotation must not stop the dashboard from starting.
            }
        }

        return true;
    }

    /// <summary>
    /// Rotates the log file MID-SESSION once it grows past the cap — the same periodic check
    /// PluginHost.WorkerLoop runs for kotorcoop-plugin.log (<see cref="KotorCoop.Plugin.LogRotation.PeriodicCheckIntervalMs"/>),
    /// missing here until 2026-09-18: rotation ran only at construction, so one long session
    /// that outlived the cap grew unbounded until the NEXT agent start
    /// (docs/review-seamless-2026-09-18.md §2 "long-session growth"). Every write comes
    /// through here first; a single stat every 30 s is the whole cost. The size check and the
    /// shift are under one gate because Note (pump thread) and WriteLogLine (dashboard thread)
    /// both write, and a shift racing an append would lose the line.
    /// </summary>
    private void RotateLogMidSessionIfDue(string path)
    {
        long now = Environment.TickCount64;
        if (!KotorCoop.Plugin.LogRotation.ShouldCheckPeriodically(
            _lastLogSizeCheckMs, now, KotorCoop.Plugin.LogRotation.PeriodicCheckIntervalMs))
        {
            return;
        }

        lock (_logRotationGate)
        {
            if (!KotorCoop.Plugin.LogRotation.ShouldCheckPeriodically(
                _lastLogSizeCheckMs, now, KotorCoop.Plugin.LogRotation.PeriodicCheckIntervalMs))
            {
                return;
            }

            _lastLogSizeCheckMs = now;
            try
            {
                FileInfo info = new(path);
                long cap = KotorCoop.Plugin.LogRotation.CapBytes(KotorCoop.Plugin.LogRotation.DefaultCapMb);
                if (info.Exists && KotorCoop.Plugin.LogRotation.ShouldRotate(info.Length, cap))
                {
                    KotorCoop.Plugin.LogRotation.ApplyShiftPlan(
                        KotorCoop.Plugin.LogRotation.ShiftPlan(path, KotorCoop.Plugin.LogRotation.DefaultKeepGenerations));
                }
            }
            catch (Exception)
            {
                // Best-effort, like the construction-time rotation: a failed shift only means
                // the file keeps growing until the next check.
            }
        }
    }

    private int _dropIndex;
    private string _lastDropNoteValue = string.Empty;

    /// <summary>
    /// The outcome of the last drop or pick-up. A property rather than a field because every
    /// assignment must ALSO reach the log file: this note is where a drop says why it refused
    /// ("not a readable item here", "ground full"), and it belongs in the transcript exactly
    /// like any other event. An unattended run — which is how this project tests — could
    /// therefore not tell a refusal from nothing having happened at all, which is exactly
    /// what blocked verifying the item-identity apply path live on 2026-08-15.
    /// </summary>
    private string _lastDropNote
    {
        get => _lastDropNoteValue;
        set
        {
            _lastDropNoteValue = value;

            if (value.Length > 0)
            {
                Note(value);
            }
        }
    }

    /// <summary>The last compact status line written in log mode, so an unchanged session
    /// says nothing at all rather than repeating itself five times a second.</summary>
    private string _lastLogLine = string.Empty;

    /// <summary>When that line was last written, so a quiet session still leaves a heartbeat
    /// rather than looking like a dead process.</summary>
    private DateTime _lastLogAt = DateTime.MinValue;

    private static readonly TimeSpan LogHeartbeat = TimeSpan.FromSeconds(30);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        bool canPosition = !Console.IsOutputRedirected;
        bool canReadKeys = !Console.IsInputRedirected;
        _showInputLine = canReadKeys;

        // ⚠ TWO COMPLETELY DIFFERENT PATHS, because a real terminal and a redirected pipe want
        // opposite things. Interactive gets the full TUI: alternate screen, captured stray
        // output, the command line, all of it. Redirected gets exactly what it always has —
        // one compact status line per change, straight to the pipe — because that is what
        // tools/demo.ps1 and any log-scraping automation already parse; growing the TUI's own
        // machinery into that path would change behaviour nothing asked to have changed.
        if (!canPosition)
        {
            await RunNonInteractiveAsync(canReadKeys, cancellationToken).ConfigureAwait(false);
            return;
        }

        _terminal = new TerminalScreen(Note);
        _terminal.Enter();

        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(200));

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (canReadKeys)
                {
                    while (Console.KeyAvailable)
                    {
                        HandleKey(Console.ReadKey(intercept: true));
                    }
                }

                CheckDropFile();
                RenderInteractive();

                if (logFile is not null)
                {
                    WriteLogLine();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _terminal.Exit();
        }
    }

    /// <summary>The pre-TUI behaviour, byte for byte: a compact log line per change (plus a
    /// heartbeat), no screen, no console capture — see <see cref="RunAsync"/>'s own remark on
    /// why this stays separate rather than reusing any TUI machinery.</summary>
    private async Task RunNonInteractiveAsync(bool canReadKeys, CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(200));

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (canReadKeys)
                {
                    while (Console.KeyAvailable)
                    {
                        HandleKey(Console.ReadKey(intercept: true));
                    }
                }

                CheckDropFile();
                WriteLogLine();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Honours a drop request left in <see cref="dropFile"/>: its contents name the
    /// blueprint, or an empty file takes the next entry from the cycle. The file is
    /// deleted first, so a failed drop cannot retrigger on every subsequent tick.
    /// </summary>
    private void CheckDropFile()
    {
        if (dropFile is null || !File.Exists(dropFile))
        {
            return;
        }

        string requested;

        try
        {
            requested = File.ReadAllText(dropFile).Trim();
            File.Delete(dropFile);
        }
        catch (IOException)
        {
            // Half-written by the requester; try again on the next tick.
            return;
        }

        if (requested.Equals("pickup", StringComparison.OrdinalIgnoreCase))
        {
            PickUpNearest();
            return;
        }

        Drop(requested.Length > 0 ? requested : null);
    }

    /// <summary>
    /// Routes one key press. F1/F2/F3 are handled first and unconditionally: function keys
    /// are never something a player would be typing into a command, so there is no ambiguity
    /// in always treating them as hotkeys regardless of what is in the buffer. Everything else
    /// edits <see cref="_input"/> or scrolls <see cref="_transcript"/> — a no-op when
    /// <see cref="_showInputLine"/> is false, since <see cref="RunAsync"/> only ever calls
    /// this from inside its own <c>canReadKeys</c> guard, but the field is checked anyway so
    /// this method stays correct if that ever changes.
    /// </summary>
    private void HandleKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.F1:
                _detailsOpen = !_detailsOpen;
                return;

            case ConsoleKey.F2:
                Drop(null);
                return;

            case ConsoleKey.F3:
                PickUpNearest();
                return;
        }

        if (!_showInputLine)
        {
            return;
        }

        switch (keyInfo.Key)
        {
            case ConsoleKey.Enter:
                SubmitInput();
                return;

            case ConsoleKey.Backspace:
                _input.Backspace();
                return;

            case ConsoleKey.Delete:
                _input.Delete();
                return;

            case ConsoleKey.LeftArrow:
                _input.MoveLeft();
                return;

            case ConsoleKey.RightArrow:
                _input.MoveRight();
                return;

            case ConsoleKey.Home:
                if ((keyInfo.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    _transcript.ScrollToTop(_lastContentWidth, _lastTranscriptViewportHeight);
                }
                else
                {
                    _input.MoveHome();
                }

                return;

            case ConsoleKey.End:
                if ((keyInfo.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    _transcript.ScrollToBottom();
                }
                else
                {
                    _input.MoveEnd();
                }

                return;

            case ConsoleKey.PageUp:
                _transcript.ScrollUp(TranscriptPage(), _lastContentWidth, _lastTranscriptViewportHeight);
                return;

            case ConsoleKey.PageDown:
                _transcript.ScrollDown(TranscriptPage(), _lastContentWidth, _lastTranscriptViewportHeight);
                return;

            case ConsoleKey.Escape:
                _input.ClearLine();
                return;

            case ConsoleKey.UpArrow:
                _input.HistoryUp();
                return;

            case ConsoleKey.DownArrow:
                _input.HistoryDown();
                return;

            case ConsoleKey.Tab:
                _input.Complete(reverse: (keyInfo.Modifiers & ConsoleModifiers.Shift) != 0);
                return;
        }

        char typed = keyInfo.KeyChar;
        if (!char.IsControl(typed))
        {
            _input.TypeChar(typed);
        }
    }

    /// <summary>A page of transcript scroll: the viewport height minus one row of overlap, so
    /// the line at the edge of the previous page is still visible as an anchor — the same
    /// convention a terminal pager uses.</summary>
    private int TranscriptPage() => Math.Max(_lastTranscriptViewportHeight - 1, 1);

    /// <summary>
    /// Enter: echoes the submitted line into the transcript and runs it through the SAME
    /// dispatcher the ~ console feeds — <see cref="CoopCommandDispatcher.RunTypedLine"/> —
    /// so the reply comes back through <see cref="Note"/> exactly as any other console reply
    /// does. Snaps the view back to the bottom even if it had been scrolled up: submitting a
    /// command is a strong signal the player wants to see what happens next. Blank lines
    /// submit nothing (<see cref="DashboardInputLine.Submit"/> already returns empty for one),
    /// matching the in-game console's own "Enter on nothing does nothing" behaviour.
    /// </summary>
    private void SubmitInput()
    {
        string line = _input.Submit();
        if (line.Length == 0)
        {
            return;
        }

        _transcript.Append($"> {line}");
        _transcript.ScrollToBottom();

        if (dispatcher is null)
        {
            _transcript.Append("(no command dispatcher wired to this dashboard)");
            return;
        }

        dispatcher.RunTypedLine(line);
    }

    /// <summary>
    /// Has the local player pick up the first shared ground item, through the engine's own
    /// pick-up action. The point is to exercise the real path: the item leaves the floor
    /// here, this instance notices, and every other instance should destroy its copy.
    /// </summary>
    private void PickUpNearest()
    {
        CoopAgent? agent = manager.Agent;
        if (agent is null)
        {
            _lastDropNote = "pickup: not connected";
            return;
        }

        if (agent.Bridge is not KotorGameBridge kotor)
        {
            _lastDropNote = "pickup needs --kotor";
            return;
        }

        List<GroundItem> items = agent.Session.Items.Snapshot();
        if (items.Count == 0)
        {
            _lastDropNote = "pickup: nothing on the ground";
            return;
        }

        GroundItem item = items[0];
        _lastDropNote = kotor.TryRequestPickUp(item.ItemId)
            ? $"picking up {item.Resref} (0x{item.ItemId:X})"
            : $"pickup refused: 0x{item.ItemId:X} is not on this instance's floor";
    }

    /// <summary>
    /// Drops a blueprint, or a LIVE item named as <c>#&lt;hex&gt;</c>. The decision itself
    /// lives in <see cref="ItemDropRequest"/>, shared with the <c>dropitem</c> console verb,
    /// so the two front doors cannot drift into giving different answers.
    /// </summary>
    private void Drop(string? requestedResref)
    {
        string resref = requestedResref ?? DropCycle[_dropIndex % DropCycle.Length];
        ItemDropRequest.Outcome outcome = ItemDropRequest.Drop(manager, bridge, resref);

        // The cycle only advances on a drop that happened. A refused one used to advance it
        // too, so pressing F2 through a disconnected patch silently skipped blueprints.
        if (outcome.Dropped)
        {
            _dropIndex++;
        }

        _lastDropNote = outcome.Note;
    }

    /// <summary>
    /// An EVENT line from the session layer (a revive, a channel decision, a pump failure), a
    /// command reply/chat, or a captured stray console line: pushed into the transcript,
    /// written to the log file, and — only in the REDIRECTED/non-interactive case, where there
    /// is no transcript being drawn at all — echoed to the (redirected) stdout. Distinct from
    /// <see cref="WriteLogLine"/>'s periodic status line: events are never deduplicated, each
    /// one happened. Thread-safe (<see cref="TranscriptBuffer"/> and <c>File.AppendAllText</c>
    /// both are); callers include the pump thread and, once the TUI is up,
    /// <see cref="TerminalScreen"/>'s own capture callback.
    /// </summary>
    public void Note(string line)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] * {line}";
        _transcript.Append(line);

        if (logFile is not null)
        {
            try
            {
                RotateLogMidSessionIfDue(logFile);
                File.AppendAllText(logFile, entry + Environment.NewLine);
            }
            catch (IOException)
            {
                // A log that cannot be written must never take the session down with it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // Console.IsOutputRedirected is the TRUE OS-level check (unaffected by
        // TerminalScreen's Console.SetOut) and therefore stays false for the entire life of
        // the interactive TUI, so this branch only ever fires in RunNonInteractiveAsync's own
        // path — where there is no transcript to look at, so the (redirected) stdout IS the
        // record, same as it always was.
        if (Console.IsOutputRedirected)
        {
            Console.Out.WriteLine(entry);
            Console.Out.Flush();
        }
    }

    private void WriteLogLine()
    {
        CoopAgent? agent = manager.Agent;

        string line;
        if (agent is null)
        {
            line = "no session (not connected)";
        }
        else
        {
            SessionStats stats = agent.Session.GetStats();
            PlayerState local = agent.LastLocalState;

            string memory = agent.LastReadSucceeded
                ? agent.Bridge.CanWriteRemotes && !agent.LastWriteSucceeded ? "WRITE-FAILING" : "ok"
                : $"READ-FAILING({agent.ReadFailureCount})";

            line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{stats.Status}  peers {stats.PeerCount}  rtt {stats.RoundTripMs:F0}ms  "
                + $"malformed {stats.PacketsDropped}  memory {memory}  "
                + $"module {(local.Module.Length > 0 ? local.Module : "-")}  "
                + $"hp {DescribeHealth(local.Health, local.MaxHealth)}  peers-here {agent.LastPeers.Count}  "
                + $"state {DescribeState(local)}  "
                + $"combat [{DescribeSharedCombat(local, agent.LastPeers)}]");
        }

        DateTime now = DateTime.Now;
        if (line == _lastLogLine && now - _lastLogAt < LogHeartbeat)
        {
            return;
        }

        _lastLogLine = line;
        _lastLogAt = now;
        string entry = $"[{now:HH:mm:ss}] {line}";

        if (logFile is not null)
        {
            try
            {
                RotateLogMidSessionIfDue(logFile);
                File.AppendAllText(logFile, entry + Environment.NewLine);
            }
            catch (IOException)
            {
                // A log that cannot be written must never take the session down with it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (Console.IsOutputRedirected)
        {
            Console.Out.WriteLine(entry);
            Console.Out.Flush();
        }
    }

    // ---- the TUI: header / transcript-or-details / input / footer, composed then painted ---

    /// <summary>
    /// One tick of the interactive TUI: builds the state <see cref="FrameComposer"/> needs,
    /// composes it to the current window size, and paints it (skipping the actual write when
    /// nothing changed — see <see cref="_lastSignature"/>).
    /// </summary>
    private void RenderInteractive()
    {
        int width = Math.Max(Console.WindowWidth, 1);
        int height = Math.Max(Console.WindowHeight, 1);
        int contentWidth = Math.Max(width - 1, 1);

        DashboardFrameState partial = new()
        {
            Header = BuildHeaderLines(),
            DetailsOpen = _detailsOpen,
            ShowInput = _showInputLine,
            DropdownCandidates = _showInputLine ? _input.Candidates : [],
            Hint = _showInputLine ? _input.Hint : string.Empty,
            InputBuffer = _input.Buffer,
            InputCaret = _input.Caret,
            Footer = BuildFooter(),
        };

        int middleHeight = FrameComposer.MiddleHeight(partial, width, height);
        _lastContentWidth = contentWidth;
        _lastTranscriptViewportHeight = middleHeight;

        DashboardFrameState state = _detailsOpen
            ? partial with { Details = BuildDetailsLines() }
            : partial with { TranscriptRows = BuildTranscriptRows(contentWidth, middleHeight) };

        ComposedFrame frame = FrameComposer.Compose(state, width, height);
        Paint(frame, width, height);
    }

    /// <summary>
    /// The transcript's visible slice, tagged with meaning-based colouring, plus a trailing
    /// "N new" marker (replacing the topmost row that would otherwise have shown) when the
    /// view is scrolled up and something arrived below it.
    /// </summary>
    private List<FrameRow> BuildTranscriptRows(int contentWidth, int viewportHeight)
    {
        List<FrameRow> rows = [];
        bool showMarker = !_transcript.IsFollowing && _transcript.NewSinceScrolled > 0 && viewportHeight > 0;
        int rowsHeight = showMarker ? viewportHeight - 1 : viewportHeight;

        foreach (string line in _transcript.VisibleRows(contentWidth, rowsHeight))
        {
            rows.Add(new FrameRow(line, TranscriptKindFor(line)));
        }

        if (showMarker)
        {
            rows.Add(new FrameRow(
                $"── ↓ {_transcript.NewSinceScrolled} new line(s) below ─ Ctrl+End to jump down ──",
                RowKind.Dim));
        }

        return rows;
    }

    /// <summary>Colours a transcript line by MEANING, not by parsing — the same heuristic the
    /// pre-TUI dashboard used: a line naming a failure is red, one naming a healthy state
    /// green, an echoed command stands out, the rest is plain.</summary>
    private static RowKind TranscriptKindFor(string text)
    {
        if (text.StartsWith("> ", StringComparison.Ordinal))
        {
            return RowKind.Heading;
        }

        if (text.Contains("FAIL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not supported", StringComparison.Ordinal)
            || text.Contains("disconnect", StringComparison.OrdinalIgnoreCase))
        {
            return RowKind.Fail;
        }

        if (text.Contains(" ok", StringComparison.Ordinal)
            || text.Contains("authoritative", StringComparison.Ordinal)
            || text.Contains("mirroring", StringComparison.Ordinal))
        {
            return RowKind.Ok;
        }

        return RowKind.Transcript;
    }

    /// <summary>
    /// The compact status header: 2 lines disconnected, 4 connected (role/link/peers/rtt,
    /// this player, other players, a one-line memory+pump summary). Everything this used to
    /// spell out across NETWORK/GAME MEMORY/THIS PLAYER/SHARED WORLD sections is still there —
    /// press F1 for the details panel; the header exists so the transcript is not crowded out
    /// by a readout taller than the window, which is what made the previous dashboard
    /// "impossible to scroll or see anything" in practice.
    /// </summary>
    private List<FrameRow> BuildHeaderLines()
    {
        CoopAgent? agent = manager.Agent;
        List<FrameRow> lines = [];

        if (agent is null)
        {
            lines.Add(new FrameRow(
                $"KOTOR CO-OP — not connected — session {manager.Phase.ToString().ToLowerInvariant()} - {manager.Detail}",
                RowKind.Heading));
            lines.Add(new FrameRow(
                $"attached {bridge.Description}  —  type connect/host below, or press ~ in-game",
                RowKind.Dim));
            return lines;
        }

        SessionStats stats = agent.Session.GetStats();
        PlayerState local = agent.LastLocalState;
        IReadOnlyList<RemotePeer> peers = agent.LastPeers;

        string role = agent.Session.IsEnemyAuthority ? "HOST" : "CLIENT";
        lines.Add(new FrameRow(
            $"KOTOR CO-OP — {role}  link {stats.Status}  peers {stats.PeerCount}  rtt {stats.RoundTripMs:F0}ms"
            + (stats.PacketsDropped > 0 ? $"  {stats.PacketsDropped} MALFORMED" : string.Empty),
            stats.PacketsDropped > 0 ? RowKind.Fail : RowKind.Heading));

        lines.Add(new FrameRow(
            $"you: {(local.Module.Length > 0 ? local.Module : "(unknown)")}"
            + $"  hp {DescribeHealth(local.Health, local.MaxHealth)}  {DescribeState(local)}",
            RowKind.Plain));

        string peersLine = peers.Count == 0
            ? "players: nobody else connected yet"
            : "players: " + string.Join(", ", peers.Select(peer =>
                $"{peer.Name} ({(peer.IsInModule(local.Module) ? "here" : peer.State.Module)}, "
                + $"{DescribeHealth(peer.State.Health, peer.State.MaxHealth)})"));
        lines.Add(new FrameRow(peersLine, RowKind.Plain));

        string memory = agent.LastReadSucceeded
            ? agent.Bridge.CanWriteRemotes && !agent.LastWriteSucceeded ? "WRITE-FAILING" : "ok"
            : $"READ-FAILING({agent.ReadFailureCount})";

        string compatFlag = string.Empty;
        CompatibilityVerdict? verdict = agent.Session.Compatibility;
        if (verdict is not null)
        {
            SessionCapabilities off = SessionCapabilities.All & ~verdict.Allowed;
            if (off != SessionCapabilities.None || verdict.Warnings.Count > 0)
            {
                compatFlag = "  content: see F1 for what's off/warned";
            }
        }

        lines.Add(new FrameRow(
            $"memory {memory}  pump {agent.PumpAverageMs:F1}ms avg ({agent.PumpPeakMs:F1}ms peak){compatFlag}",
            memory == "ok" ? RowKind.Ok : RowKind.Fail));

        return lines;
    }

    /// <summary>
    /// F1's details panel: the full readout the header no longer has room for — network
    /// stats, content-compatibility verdict, game-memory read/write health, this player's
    /// exact position, every other player with their state, the shared-world combat/enemy
    /// census, and the ground-item/last-drop demo affordance. Replaces the transcript in the
    /// middle region while open; the transcript itself keeps recording underneath.
    /// </summary>
    private List<FrameRow> BuildDetailsLines()
    {
        List<FrameRow> rows = [];
        void Row(string text, RowKind kind = RowKind.Plain) => rows.Add(new FrameRow(text, kind));

        CoopAgent? agent = manager.Agent;
        Row("=== DETAILS (F1 to close) ============================================", RowKind.Heading);
        Row($" attached   {bridge.Description}");

        if (agent is null)
        {
            Row(string.Empty);
            Row($" session    {manager.Phase.ToString().ToLowerInvariant()} - {manager.Detail}");
            Row(string.Empty);
            Row(" not connected. In-game: press ~ and type  connect <ip> [password] | reconnect");
            Row(" Or type it in the command line below - it reaches the same dispatcher.");
            return rows;
        }

        SessionStats stats = agent.Session.GetStats();
        PlayerState local = agent.LastLocalState;
        IReadOnlyList<RemotePeer> peers = agent.LastPeers;

        string role = agent.Session.IsEnemyAuthority
            ? "HOST - this game owns the enemies, doors and companions"
            : "CLIENT - this game mirrors the host's world";

        Row(string.Empty);
        Row(" NETWORK", RowKind.Heading);
        Row($" role       {role}");
        Row($" link       {stats.Status}");
        Row($" players    {stats.PeerCount} other connected      round trip {stats.RoundTripMs:F0} ms");
        Row($" traffic    {stats.PacketsSent} sent / {stats.PacketsReceived} received      "
            + (stats.PacketsDropped > 0 ? $"{stats.PacketsDropped} MALFORMED" : "none malformed"),
            stats.PacketsDropped > 0 ? RowKind.Fail : RowKind.Plain);

        CompatibilityVerdict? verdict = agent.Session.Compatibility;
        if (verdict is not null)
        {
            SessionCapabilities off = SessionCapabilities.All & ~verdict.Allowed;
            if (off != SessionCapabilities.None || verdict.Warnings.Count > 0)
            {
                Row(string.Empty);
                Row($" content    {verdict.Summary}");

                if (off != SessionCapabilities.None)
                {
                    Row($"            OFF: {off.ToString().Replace(", ", " ")}  (a mod/save difference makes these unsafe)",
                        RowKind.Fail);
                }

                for (int i = 0; i < verdict.Warnings.Count && i < 3; i++)
                {
                    Row($"            {verdict.Warnings[i]}", RowKind.Fail);
                }
            }
        }

        string readStatus = agent.LastReadSucceeded ? "ok" : $"FAILING ({agent.ReadFailureCount} failures)";
        string writeStatus = agent.Bridge.CanWriteRemotes
            ? agent.LastWriteSucceeded ? "ok" : "FAILING"
            : "not supported by this profile (read-only)";
        bool memoryOk = agent.LastReadSucceeded && (!agent.Bridge.CanWriteRemotes || agent.LastWriteSucceeded);

        Row(string.Empty);
        Row(" GAME MEMORY", RowKind.Heading);
        Row($" reading    {readStatus}          writing    {writeStatus}", memoryOk ? RowKind.Ok : RowKind.Fail);
        Row($" pump cost  {agent.PumpAverageMs:F1} ms average, {agent.PumpPeakMs:F1} ms peak   (budget {agent.PumpBudgetMs:F0} ms)");

        Row(string.Empty);
        Row(" THIS PLAYER", RowKind.Heading);
        Row($" area       {(local.Module.Length > 0 ? local.Module : "(unknown)")}"
            + $"      at {local.Position}  facing {local.Facing:F2}");
        Row($" health     {DescribeHealth(local.Health, local.MaxHealth)}");
        Row($" state      {DescribeState(local)}");

        Row(string.Empty);
        Row($" OTHER PLAYERS ({peers.Count})", RowKind.Heading);
        for (int i = 0; i < Math.Max(peers.Count, 1); i++)
        {
            if (i < peers.Count)
            {
                RemotePeer peer = peers[i];
                bool here = peer.IsInModule(local.Module);
                string where = here ? "here with you" : $"in {peer.State.Module}";

                // ⚠ A MIRRORING PEER NEVER SETS FlagPaused, so reading its state alone shows
                // "exploring" while its game is visibly frozen — reported from play. The flag
                // deliberately carries only a player's OWN pause, so the shared half has to be
                // supplied here: if THIS player holds a pause, every peer standing in this
                // module is frozen by it, whatever their flags say.
                string state = DescribeState(peer.State);
                if (here && local.IsPaused && !peer.State.IsPaused)
                {
                    state = state.StartsWith("exploring", StringComparison.Ordinal)
                        ? "PAUSED (mirroring yours)"
                        : $"{state}, PAUSED (mirroring yours)";
                }

                Row($"   {peer.Name,-12} {where,-18} {DescribeHealth(peer.State.Health, peer.State.MaxHealth),-20}" + state);
            }
            else
            {
                Row("   (nobody else connected yet)");
            }
        }

        // Display only, so the census is read from this player's own vantage point (no peer
        // positions) and the module gate is asked with the module we are actually in.
        IReadOnlyList<EnemyState> enemies = agent.Session.IsEnemyAuthority
            ? agent.Bridge.ReadEnemies([])
            : agent.Session.GetEnemies(agent.LastLocalState.Module);

        int alive = 0;
        int dead = 0;
        foreach (EnemyState enemy in enemies)
        {
            if (enemy.Dead)
            {
                dead++;
            }
            else
            {
                alive++;
            }
        }

        Row(string.Empty);
        Row(" SHARED WORLD", RowKind.Heading);
        Row($" combat     {DescribeSharedCombat(local, peers)}");
        Row($" enemies    {enemies.Count} tracked - {alive} alive, {dead} dead"
            + (agent.Session.IsEnemyAuthority ? "   (published by this game)" : "   (received from the host)"));

        DialogueState convo = agent.Session.GetDialogue();
        string convoWho = convo.OwnerPeerId == agent.Session.LocalPeerId
            ? "you are"
            : $"peer {convo.OwnerPeerId} is";
        Row($" talking    {(convo.Active ? $"{convoWho} talking" + (convo.SpeakerName.Length > 0 ? $" to {convo.SpeakerName}" : string.Empty) : "nobody is talking")}");

        List<GroundItem> items = agent.Session.Items.Snapshot();
        string itemNames = items.Count == 0
            ? "none"
            : string.Join(", ", items.Take(3).Select(item => item.Resref)) + (items.Count > 3 ? ", ..." : string.Empty);
        Row($" items      {items.Count} on the ground: {itemNames}");

        if (_lastDropNoteValue.Length > 0)
        {
            Row($" last drop  {_lastDropNoteValue}");
        }

        return rows;
    }

    private string BuildFooter() => _showInputLine
        ? "F1 details    F2 drop    F3 pick up    PgUp/PgDn scroll    Tab complete    Ctrl+C quit"
        : "(no command line: input is redirected)    F1 details    F2 drop    F3 pick up    Ctrl+C to stop";

    /// <summary>
    /// Paints one composed frame in a SINGLE write to the real terminal (never
    /// <c>Console.Out</c> — see <see cref="_terminal"/>'s own doc comment): cursor home, each
    /// row's colour/text/reset/clear-to-end-of-line, then the real caret placed on the input
    /// row. Skips the write entirely when <see cref="ComputeSignature"/> matches the previous
    /// frame — an idle dashboard between the 200 ms ticks costs nothing extra.
    /// </summary>
    private void Paint(ComposedFrame frame, int width, int height)
    {
        string signature = ComputeSignature(frame, width, height);
        if (signature == _lastSignature)
        {
            return;
        }

        _lastSignature = signature;

        System.Text.StringBuilder sb = new();
        sb.Append("\x1b[H");

        for (int i = 0; i < frame.Rows.Count; i++)
        {
            FrameRow row = frame.Rows[i];
            sb.Append(SgrFor(row.Kind)).Append(row.Text).Append("\x1b[0m\x1b[K");
            if (i < frame.Rows.Count - 1)
            {
                sb.Append("\r\n");
            }
        }

        if (frame.CaretRow >= 0 && frame.CaretColumn >= 0)
        {
            sb.Append($"\x1b[{frame.CaretRow + 1};{frame.CaretColumn + 1}H\x1b[?25h");
        }
        else
        {
            sb.Append("\x1b[?25l");
        }

        // _terminal is always set by the time this runs — RenderInteractive is only ever
        // called from inside RunAsync's canPosition branch, after TerminalScreen.Enter().
        _terminal!.Screen.Write(sb.ToString());
        _terminal.Screen.Flush();
    }

    private static string ComputeSignature(ComposedFrame frame, int width, int height)
    {
        System.Text.StringBuilder sb = new();
        sb.Append(width).Append('x').Append(height).Append('|')
          .Append(frame.CaretRow).Append(',').Append(frame.CaretColumn).Append('|');

        foreach (FrameRow row in frame.Rows)
        {
            sb.Append((int)row.Kind).Append(':').Append(row.Text).Append('\u0001');
        }

        return sb.ToString();
    }

    private static string SgrFor(RowKind kind) => kind switch
    {
        RowKind.Heading => "\x1b[1;97m",
        RowKind.Fail => "\x1b[91m",
        RowKind.Ok => "\x1b[92m",
        RowKind.Dim => "\x1b[90m",
        RowKind.Dropdown => "\x1b[96m",
        RowKind.Hint => "\x1b[90m",
        RowKind.InputPrompt => "\x1b[97m",
        RowKind.Footer => "\x1b[90m",
        RowKind.Transcript => "\x1b[37m",
        _ => "\x1b[0m",
    };

    /// <summary>
    /// The player's situation in words: fighting, paused, talking, down. These are the
    /// states that change what every other channel is allowed to do — the enemy census
    /// pauses during a conversation, damage is held, movement stops when paused — so a
    /// readout that omits them cannot explain what it is showing.
    /// </summary>
    private static string DescribeState(PlayerState state)
    {
        List<string> parts = [];

        if ((state.Flags & PlayerState.FlagDowned) != 0)
        {
            parts.Add("DOWNED");
        }

        if ((state.Flags & PlayerState.FlagInCombat) != 0)
        {
            parts.Add("IN COMBAT");
        }

        if ((state.Flags & PlayerState.FlagStealth) != 0)
        {
            parts.Add("STEALTH");
        }

        if ((state.Flags & PlayerState.FlagMenuGuard) != 0)
        {
            parts.Add("MENU-GUARD");
        }

        if ((state.Flags & PlayerState.FlagPaused) != 0)
        {
            parts.Add("PAUSED");
        }

        if ((state.Flags & PlayerState.FlagInDialogue) != 0)
        {
            parts.Add("in a conversation");
        }

        return parts.Count == 0 ? "exploring (not in combat, not paused)" : string.Join(", ", parts);
    }

    /// <summary>
    /// The fight as the SESSION sees it, rather than as one engine sees it.
    ///
    /// Every peer already publishes its own in-combat flag, but nothing ever combined them,
    /// so two players standing in the same room during the same fight produced "IN COMBAT"
    /// on one readout and "exploring" on the other with no explanation. That is not a
    /// reporting bug, and saying so is the point of this line: on a client the enemies are
    /// MIRRORS — driven puppets, not creatures its own AI is fighting — so the client's
    /// engine genuinely never enters combat, and it never will until the owner-side
    /// engagement is replicated rather than just the bodies.
    ///
    /// Until then the honest thing is to name the fight, name who their engine has actually
    /// engaged, and mark the rest as mirroring so nobody reads it as a dropped packet.
    /// </summary>
    private static string DescribeSharedCombat(PlayerState local, IReadOnlyList<RemotePeer> peers)
    {
        List<string> engaged = [];
        List<string> mirroring = [];

        (local.IsInCombat ? engaged : mirroring).Add("you");

        foreach (RemotePeer peer in peers)
        {
            // Somebody fighting in another module is their own business, not this fight.
            if (!peer.IsInModule(local.Module))
            {
                continue;
            }

            (peer.State.IsInCombat ? engaged : mirroring).Add(peer.Name);
        }

        if (engaged.Count == 0)
        {
            return "nobody here is fighting";
        }

        string where = local.Module.Length > 0 ? local.Module : "this area";
        string line = $"FIGHT in {where} - engaged: {string.Join(", ", engaged)}";

        // One name here is the reported symptom, so spell out what it means rather than
        // leaving a bare absence.
        return mirroring.Count == 0
            ? line
            : $"{line}; mirroring only: {string.Join(", ", mirroring)} (their engine is not in combat)";
    }

    /// <summary>Health in a form that says what it means. A max of 0 means the profile has
    /// not resolved the field, which "12/0" reads as a bug in the game rather than a gap in
    /// what this agent can see.</summary>
    private static string DescribeHealth(int current, int max) =>
        max > 0 ? $"{current}/{max}" : $"{current} (maximum unknown)";
}
