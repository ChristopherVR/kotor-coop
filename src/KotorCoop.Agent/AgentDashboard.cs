using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// Console readout for the run command, plus the demo drop key. Reads the live agent from
/// the <see cref="SessionManager"/> each tick rather than holding a fixed one, so the view
/// follows connect/disconnect/reconnect: between sessions there is no agent and the readout
/// says so instead of rendering stale peers.
/// </summary>
public sealed class AgentDashboard(
    SessionManager manager,
    IGameBridge bridge,
    string? dropFile = null,
    string? logFile = null,
    CoopCommandDispatcher? dispatcher = null)
{
    /// <summary>The always-visible command line: buffer/caret/history/completion, all pure —
    /// see <see cref="DashboardInputLine"/>'s own doc comment. Constructed unconditionally
    /// (it is cheap and holds no console handle); whether it is ever drawn or fed a key
    /// depends on <see cref="_showInputLine"/>, decided once in <see cref="RunAsync"/>.</summary>
    private readonly DashboardInputLine _input = new();

    /// <summary>Whether this run can show and drive the input line at all: false when input
    /// is redirected (<c>Console.IsInputRedirected</c>), set once at the top of
    /// <see cref="RunAsync"/>. Output being redirected already skips <see cref="Render"/>
    /// entirely (see the two-outputs remark on <see cref="RunAsync"/>), so this is the one
    /// remaining case — a real console window fed piped/redirected stdin — that needs its own
    /// guard: keys are never read there, so there is nothing for a drawn prompt to reflect.</summary>
    private bool _showInputLine;

    /// <summary>The frame row the prompt line ("&gt; buffer") landed on this Render() pass, or
    /// -1 when none was drawn — how <see cref="PositionInputCaret"/> finds where to put the
    /// real OS caret after <see cref="FlushFrame"/> repaints the screen.</summary>
    private int _promptFrameRow = -1;

    /// <summary>Row in the frame where the input area starts, or -1 when there is none.
    /// FlushFrame keeps the rows from here on pinned to the bottom of the window.</summary>
    private int _inputAreaStartRow = -1;

    /// <summary>How many of the last replies/chat/event lines (from <see cref="Note"/>) and
    /// echoed commands are kept above the prompt.</summary>
    private const int ScrollbackCapacity = 8;

    /// <summary>How many ranked completion candidates the dropdown shows at once.</summary>
    private const int MaxDropdownRows = 6;

    /// <summary>Guards <see cref="_scrollback"/>: <see cref="Note"/> can be called from the
    /// pump thread (session events) as well as from this dashboard's own command submit, and
    /// <see cref="Render"/> reads it every 200 ms tick.</summary>
    private readonly Lock _scrollbackGate = new();
    private readonly List<string> _scrollback = [];

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
    /// ("not a readable item here", "ground full"), and it used to render only on the
    /// dashboard's own screen. An unattended run — which is how this project tests — could
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

        if (canPosition)
        {
            Console.Clear();
            Console.CursorVisible = false;
        }

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

                // ⚠ TWO DIFFERENT OUTPUTS, because a screen and a log file want opposite
                // things. On a console the readout is redrawn in place five times a second,
                // which is right there and unreadable anywhere else: redirected, every
                // redraw APPENDS another full copy, so a few minutes of running produced
                // thousands of screens of near-identical text — reported from use as the
                // log "spamming". Redirected output now gets one compact line, and only
                // when something actually changed.
                if (canPosition)
                {
                    Render();
                    FlushFrame();
                    PositionInputCaret();
                }

                // The log is written whenever one was asked for, and also when there is no
                // console to draw on — so a windowed run still leaves a record and a
                // redirected one is not silent.
                if (logFile is not null || !canPosition)
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
            if (canPosition)
            {
                Console.CursorVisible = true;
            }
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
    /// Routes one key press. F2/F3 (the drop/pick-up demo hotkeys — moved off the bare D/P
    /// letters once those became ordinary typed characters for the command line below) are
    /// handled first and unconditionally: they are function keys, never something a player
    /// would be typing into a command, so there is no ambiguity in always treating them as
    /// hotkeys regardless of what is in the buffer. Everything else edits
    /// <see cref="_input"/> — a no-op when <see cref="_showInputLine"/> is false, since
    /// <see cref="RunAsync"/> only ever calls this from inside its own
    /// <c>canReadKeys</c> guard, but the field is checked anyway so this method stays correct
    /// if that ever changes.
    /// </summary>
    private void HandleKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
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
                _input.MoveHome();
                return;

            case ConsoleKey.End:
                _input.MoveEnd();
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

    /// <summary>
    /// Enter: echoes the submitted line into the scrollback and runs it through the SAME
    /// dispatcher the ~ console feeds — <see cref="CoopCommandDispatcher.RunTypedLine"/> —
    /// so the reply comes back through <see cref="Note"/> exactly as any other console reply
    /// does. Blank lines submit nothing (<see cref="DashboardInputLine.Submit"/> already
    /// returns empty for one), matching the in-game console's own "Enter on nothing does
    /// nothing" behaviour.
    /// </summary>
    private void SubmitInput()
    {
        string line = _input.Submit();
        if (line.Length == 0)
        {
            return;
        }

        PushScrollback($"> {line}");

        if (dispatcher is null)
        {
            PushScrollback("(no command dispatcher wired to this dashboard)");
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
        // too, so pressing D through a disconnected patch silently skipped blueprints.
        if (outcome.Dropped)
        {
            _dropIndex++;
        }

        _lastDropNote = outcome.Note;
    }

    /// <summary>
    /// One compact, timestamped line for a log file or a pipe — written only when the
    /// session's shape changes, plus a heartbeat so a healthy quiet session still shows it
    /// is alive.
    ///
    /// The fields are the ones worth grepping for after the fact: who is connected, how the
    /// link is behaving, whether memory access is still working, where the player is and how
    /// many bodies each channel is driving. Everything cosmetic stays on the screen version.
    /// </summary>
    /// <summary>
    /// An EVENT line from the session layer (a revive, a channel decision, a pump
    /// failure), written straight to the log file and to a redirected console. Distinct
    /// from the periodic status line: events are never deduplicated — each one happened.
    /// Thread-safe by way of File.AppendAllText; callers are the pump thread.
    /// </summary>
    public void Note(string line)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] * {line}";
        PushScrollback(line);

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

    /// <summary>Appends one line to the small scrollback shown above the prompt, dropping the
    /// oldest once past <see cref="ScrollbackCapacity"/>. Thread-safe — see
    /// <see cref="_scrollbackGate"/>'s own doc comment.</summary>
    private void PushScrollback(string line)
    {
        lock (_scrollbackGate)
        {
            _scrollback.Add(line);
            while (_scrollback.Count > ScrollbackCapacity)
            {
                _scrollback.RemoveAt(0);
            }
        }
    }

    private List<string> ScrollbackSnapshot()
    {
        lock (_scrollbackGate)
        {
            return [.. _scrollback];
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

    private void Render()
    {
        CoopAgent? agent = manager.Agent;

        // ⚠ Every line names WHAT it is and WHAT the number means. The old readout was a
        // grid of bare abbreviations — "tx/rx", "flags v0", "phantoms=4", "hp 12/0" — and
        // was reported straight back as "the format is still very confusing what everything
        // means". A readout nobody can read is not a diagnostic.
        WriteLine("=== KOTOR CO-OP AGENT ============================================");
        WriteLine($" attached   {bridge.Description}");

        if (agent is null)
        {
            RenderDisconnected();
            return;
        }

        SessionStats stats = agent.Session.GetStats();
        PlayerState local = agent.LastLocalState;
        IReadOnlyList<RemotePeer> peers = agent.LastPeers;

        string role = agent.Session.IsEnemyAuthority
            ? "HOST - this game owns the enemies, doors and companions"
            : "CLIENT - this game mirrors the host's world";

        WriteLine(string.Empty);
        WriteLine(" NETWORK");
        WriteLine($" role       {role}");
        WriteLine($" link       {stats.Status}");
        WriteLine($" players    {stats.PeerCount} other connected      round trip {stats.RoundTripMs:F0} ms");
        WriteLine($" traffic    {stats.PacketsSent} sent / {stats.PacketsReceived} received      "
            + (stats.PacketsDropped > 0 ? $"{stats.PacketsDropped} MALFORMED" : "none malformed"));


        WriteContentSection(agent);

        string readStatus = agent.LastReadSucceeded ? "ok" : $"FAILING ({agent.ReadFailureCount} failures)";
        string writeStatus = agent.Bridge.CanWriteRemotes
            ? agent.LastWriteSucceeded ? "ok" : "FAILING"
            : "not supported by this profile (read-only)";

        WriteLine(string.Empty);
        WriteLine(" GAME MEMORY");
        WriteLine($" reading    {readStatus}          writing    {writeStatus}");
        WriteLine($" pump cost  {agent.PumpAverageMs:F1} ms average, {agent.PumpPeakMs:F1} ms peak   (budget {agent.PumpBudgetMs:F0} ms)");
        WriteLine(string.Empty);
        WriteLine(" THIS PLAYER");
        WriteLine($" area       {(local.Module.Length > 0 ? local.Module : "(unknown)")}"
            + $"      at {local.Position}  facing {local.Facing:F2}");
        WriteLine($" health     {DescribeHealth(local.Health, local.MaxHealth)}");
        WriteLine($" state      {DescribeState(local)}");
        WriteLine(string.Empty);
        WriteLine($" OTHER PLAYERS ({peers.Count})");

        for (int i = 0; i < Math.Max(peers.Count, 1); i++)
        {
            if (i < peers.Count)
            {
                RemotePeer peer = peers[i];
                bool here = peer.IsInModule(local.Module);
                string where = here ? "here with you" : $"in {peer.State.Module}";

                // ⚠ A MIRRORING PEER NEVER SETS FlagPaused, so reading its state alone shows
                // "exploring" while its game is visibly frozen — reported from play. The flag
                // deliberately carries only a player's OWN pause (a mirrored one is excluded
                // at the source, or two instances latch each other paused forever), so the
                // shared half has to be supplied here: if THIS player holds a pause, every
                // peer standing in this module is frozen by it, whatever their flags say.
                string state = DescribeState(peer.State);
                if (here && local.IsPaused && !peer.State.IsPaused)
                {
                    state = state.StartsWith("exploring", StringComparison.Ordinal)
                        ? "PAUSED (mirroring yours)"
                        : $"{state}, PAUSED (mirroring yours)";
                }

                WriteLine($"   {peer.Name,-12} {where,-18} {DescribeHealth(peer.State.Health, peer.State.MaxHealth),-20}"
                    + state);
            }
            else
            {
                WriteLine("   (nobody else connected yet)");
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

        WriteLine(string.Empty);
        WriteLine(" SHARED WORLD");
        WriteLine($" combat     {DescribeSharedCombat(local, peers)}");
        WriteLine($" enemies    {enemies.Count} tracked - {alive} alive, {dead} dead"
            + (agent.Session.IsEnemyAuthority ? "   (published by this game)" : "   (received from the host)"));

        DialogueState convo = agent.Session.GetDialogue();
        string convoWho = convo.OwnerPeerId == agent.Session.LocalPeerId
            ? "you are"
            : $"peer {convo.OwnerPeerId} is";
        WriteLine($" talking    {(convo.Active ? $"{convoWho} talking" + (convo.SpeakerName.Length > 0 ? $" to {convo.SpeakerName}" : string.Empty) : "nobody is talking")}");

        // Ground items are a demo affordance, not session health: one summary line, and the
        // resrefs only when there is something to say. The readout has to fit a default
        // console without resizing — reported as "we have to resize it to see everything".
        List<GroundItem> items = agent.Session.Items.Snapshot();
        string itemNames = items.Count == 0
            ? "none"
            : string.Join(", ", items.Take(3).Select(item => item.Resref)) + (items.Count > 3 ? ", ..." : string.Empty);

        WriteLine($" items      {items.Count} on the ground: {itemNames}");

        if (_lastDropNote.Length > 0)
        {
            WriteLine($" last drop  {_lastDropNote}");
        }

        RenderInputArea();
    }

    /// <summary>Steady-state readout while no session is live (between connects).</summary>
    private void RenderDisconnected()
    {
        WriteLine($"session  {manager.Phase.ToString().ToLowerInvariant()} - {manager.Detail}");
        WriteLine(string.Empty);
        WriteLine("not connected.");
        WriteLine(string.Empty);
        WriteLine("In-game: press ~ and type  connect <ip> [password] | reconnect | help");
        WriteLine("Or type it below - this window's own command line reaches the same dispatcher.");

        // A little padding so this shorter screen still fills roughly the same vertical
        // space the connected readout does - not load-bearing for correctness (FlushFrame
        // blanks every row up to the window height on every call regardless of how many
        // lines were actually written), just cosmetic stability. Cut down from the 18 this
        // used before RenderInputArea existed, which would otherwise push the prompt row
        // below a normal-height terminal window and off the visible, clamped frame.
        for (int i = 0; i < 6; i++)
        {
            WriteLine(string.Empty);
        }

        RenderInputArea();
    }

    /// <summary>
    /// The bottom of every frame, connected or not: the scrollback (last few replies, chat
    /// and session-event lines, plus every command this window itself submitted), the
    /// completion hint or dropdown for whatever is typed right now, the prompt line itself,
    /// and the key legend. Shared by <see cref="Render"/> and <see cref="RenderDisconnected"/>
    /// because the dispatcher answers `connect`/`host`/`help` just as well before a session
    /// exists as during one — see <see cref="CoopCommandDispatcher.RunTypedLine"/>.
    /// </summary>
    private void RenderInputArea()
    {
        _promptFrameRow = -1;
        _inputAreaStartRow = _frame.Count;
        WriteLine(string.Empty);

        if (!_showInputLine)
        {
            WriteLine(" (no command line: input is redirected)    F2 drop    F3 pick up    Ctrl+C to stop");
            return;
        }

        foreach (string line in ScrollbackSnapshot())
        {
            WriteLine(" " + line);
        }

        // The dropdown (one candidate per row) replaces the hint line while the first token
        // is still ambiguous - the hint would otherwise just be the same candidates joined
        // onto one capped line, which is redundant with rows already showing them in full.
        IReadOnlyList<string> candidates = _input.Candidates;
        if (candidates.Count > 1)
        {
            foreach (string candidate in candidates.Take(MaxDropdownRows))
            {
                WriteLine($"   {candidate}");
            }

            if (candidates.Count > MaxDropdownRows)
            {
                WriteLine($"   ... {candidates.Count - MaxDropdownRows} more");
            }
        }
        else
        {
            string hint = _input.Hint;
            if (hint.Length > 0)
            {
                WriteLine($" {hint}");
            }
        }

        // Row captured BEFORE the prompt line is appended, so it is exactly the row the
        // prompt lands on once FlushFrame paints _frame - see PositionInputCaret.
        _promptFrameRow = _frame.Count;
        WriteLine($"> {_input.Buffer}");
        WriteLine(" F2 drop    F3 pick up    Tab complete (Shift+Tab back)    Up/Down history    "
            + "Esc clear    Ctrl+C to stop");
    }

    /// <summary>
    /// Puts the real OS caret on the prompt row/column <see cref="RenderInputArea"/> just
    /// drew, once <see cref="FlushFrame"/> has repainted the screen. Cheap (one Win32 call)
    /// and run every tick, so it costs nothing toward the "no flicker" requirement the rest
    /// of this class already meets by redrawing in place — this only ever repositions a
    /// caret, never touches a character cell. Off (and the caret hidden) whenever there is no
    /// prompt to point at: redirected input, or a window too short to show the prompt row at
    /// all.
    /// </summary>
    private void PositionInputCaret()
    {
        if (_promptFrameRow < 0)
        {
            Console.CursorVisible = false;
            return;
        }

        int height = Math.Max(Console.WindowHeight - 1, 1);
        if (_promptFrameRow >= height)
        {
            Console.CursorVisible = false;
            return;
        }

        int width = Math.Max(Console.WindowWidth - 1, 1);
        int column = Math.Min(2 + _input.Caret, width - 1);

        try
        {
            Console.SetCursorPosition(column, _promptFrameRow);
            Console.CursorVisible = true;
        }
        catch (ArgumentOutOfRangeException)
        {
            // A resize landed between computing height/width and setting the position.
            // Missing one frame's caret is harmless; the next tick recomputes both.
            Console.CursorVisible = false;
        }
    }

    /// <summary>
    /// What the two installs agreed on. Without this the headline mod-compatibility feature
    /// is invisible: a channel silently switched off (the peer's appearance, say, because
    /// their appearance.2da differs) looks exactly like a bug, and the player has no way to
    /// tell the difference. Says nothing while everything is on and nothing was warned
    /// about, so the normal case costs no space.
    /// </summary>
    private void WriteContentSection(CoopAgent agent)
    {
        CompatibilityVerdict? verdict = agent.Session.Compatibility;
        if (verdict is null)
        {
            return;
        }

        SessionCapabilities off = SessionCapabilities.All & ~verdict.Allowed;
        if (off == SessionCapabilities.None && verdict.Warnings.Count == 0)
        {
            return;
        }

        WriteLine($"content  {verdict.Summary}");

        if (off != SessionCapabilities.None)
        {
            WriteLine($"         OFF: {off.ToString().Replace(", ", " ")}  (a mod/save difference makes these unsafe)");
        }

        for (int i = 0; i < verdict.Warnings.Count && i < 3; i++)
        {
            WriteLine($"         {verdict.Warnings[i]}");
        }

        WriteLine(string.Empty);
    }

    /// <summary>The frame being built. Lines are collected and flushed together so the
    /// readout can be clamped to the window.</summary>
    private readonly List<string> _frame = [];

    private void WriteLine(string text)
    {
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(text);
            return;
        }

        _frame.Add(text);
    }

    /// <summary>
    /// Paints the collected frame, clamped to the window so it CANNOT scroll.
    ///
    /// ⚠ Scrolling is what makes an in-place readout flicker. Writing one more line than the
    /// window holds scrolls everything up by one, so the next redraw starting at (0,0) lands
    /// against shifted content and the screen alternates between two mis-aligned versions of
    /// itself — reported as "flickering between different views every second". Never write
    /// the last row, and always pad to the full width so a shorter frame cannot leave pieces
    /// of a longer one behind.
    /// </summary>
    private void FlushFrame()
    {
        int height = Math.Max(Console.WindowHeight - 1, 1);
        int width = Math.Max(Console.WindowWidth - 1, 1);

        // Keep the command line visible: the readout is often taller than the window (live
        // 2026-09-25, a 30-row window hid the prompt entirely). When it does not fit, cut
        // the readout, never the input area, and move the prompt row up to match.
        int inputStart = _inputAreaStartRow;
        _inputAreaStartRow = -1;
        if (_frame.Count > height && inputStart >= 0 && inputStart < _frame.Count)
        {
            int inputRows = _frame.Count - inputStart;
            int keep = Math.Max(height - inputRows, 0);
            if (keep < inputStart)
            {
                int removed = inputStart - keep;
                _frame.RemoveRange(keep, removed);
                if (_promptFrameRow >= 0)
                {
                    _promptFrameRow -= removed;
                }
            }
        }

        Console.SetCursorPosition(0, 0);

        ConsoleColor previous = Console.ForegroundColor;
        for (int i = 0; i < height; i++)
        {
            string line = i < _frame.Count ? _frame[i] : string.Empty;
            if (line.Length > width)
            {
                line = line[..width];
            }

            // Colour by MEANING, not by parsing: a line naming a failure is red, one naming
            // a healthy state green, headings white, the rest dim — so a glance answers
            // "is this session alright" without reading any of it.
            Console.ForegroundColor = ColourFor(line);
            Console.Write(line.PadRight(width));
            Console.Write('\n');
        }

        Console.ForegroundColor = previous;
        _frame.Clear();
    }

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

    private static ConsoleColor ColourFor(string text)
    {
        if (text.Contains("FAIL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not supported", StringComparison.Ordinal)
            || text.Contains("disconnect", StringComparison.OrdinalIgnoreCase))
        {
            return ConsoleColor.Red;
        }

        if (text.Contains(" ok", StringComparison.Ordinal)
            || text.Contains("authoritative", StringComparison.Ordinal)
            || text.Contains("mirroring", StringComparison.Ordinal))
        {
            return ConsoleColor.Green;
        }

        // A heading is a line that starts in column 0 with a word; continuation lines are
        // indented, and those stay dim so the shape of the readout is visible at a glance.
        return text.Length > 0 && !char.IsWhiteSpace(text[0])
            ? ConsoleColor.White
            : ConsoleColor.DarkGray;
    }
}
