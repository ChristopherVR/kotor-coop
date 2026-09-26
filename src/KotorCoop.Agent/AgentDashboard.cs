using System.Text;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// TextView (used below for the transcript and the F1 details panel) is marked obsolete in
// Terminal.Gui 2.5.0 in favour of a SEPARATE companion package (tui-cs/Editor's EditorView) -
// a rope-backed, syntax-highlighting rich editor built for source code, not what a read-only
// scrollable chat/log view needs. TextView itself is fully shipped and functional in 2.5.0,
// not removed and not broken; pulling in an entire second editor package (and its own,
// less-adopted API) for a simple log view would trade a well-understood, verified-against-
// source API for a fresh unknown one, under exactly the time pressure that produced the
// PREVIOUS hand-rolled renderer's bugs. Suppressed narrowly, not disabled solution-wide.
#pragma warning disable CS0618 // TextView is obsolete

namespace KotorCoop.Agent;

/// <summary>
/// The agent's own console UI: a proven TUI library (Terminal.Gui v2) rather than the
/// hand-rolled VT/alternate-screen renderer this replaces — a live pass found that renderer
/// unusable (overlapping/flickering text, and keystrokes only read on the 200 ms status tick,
/// so typing felt broken). Terminal.Gui owns the screen and the input loop: keys are
/// event-driven (no polling), and its <c>TextView</c> is a proven scrollable, word-wrapped,
/// mouse-wheel-and-scrollbar-capable view instead of a hand-rolled wrap/scroll offset.
///
/// Layout, top to bottom, all inside one borderless full-screen <see cref="Window"/>:
/// a STICKY status header (role/link/peers/rtt, this player, other players, memory/pump —
/// never scrolls with the transcript), the transcript (chat/events/command echoes/replies and
/// any captured stray console output), a one-line completion dropdown/hint, the command line
/// (<see cref="DashboardInputLine"/> drives editing/history/completion; this class only
/// mirrors its state into the <c>TextField</c>), and a footer with the key legend (which also
/// carries the "N new lines below" marker while the transcript is scrolled up).
///
/// While this TUI is running, <see cref="Console.Out"/>/<see cref="Console.Error"/> are
/// redirected to a <see cref="ConsoleCaptureWriter"/> so a component that still calls
/// <c>Console.WriteLine</c> (a coop.ini diagnostic, a census note, a background thread's
/// catch-all) lands in the transcript instead of corrupting Terminal.Gui's own screen.
///
/// Threading: Terminal.Gui's own main loop owns the UI thread inside the blocking
/// <c>IApplication.Run</c> call (run on a background <see cref="Task"/> here so this method
/// can still await cancellation). Every touch of a view from another thread — a session event
/// via <see cref="Note"/>, the periodic header refresh, the drop-file check — goes through
/// <c>IApplication.Invoke</c>, never directly.
///
/// Redirected/non-interactive runs and log mode are untouched: <see cref="RunNonInteractiveAsync"/>
/// never constructs a Terminal.Gui application at all.
/// </summary>
public sealed class AgentDashboard(
    SessionManager manager,
    IGameBridge bridge,
    string? dropFile = null,
    string? logFile = null,
    CoopCommandDispatcher? dispatcher = null,
    Action? requestShutdown = null)
{
    /// <summary>The always-visible command line's own editing/history/completion state — see
    /// <see cref="DashboardInputLine"/>'s own doc comment. This class never edits a buffer of
    /// its own; every keystroke is delegated here and the result mirrored into the TextField.</summary>
    private readonly DashboardInputLine _input = new();

    /// <summary>The scrolling transcript's storage and follow/"N new" bookkeeping — see
    /// <see cref="TranscriptBuffer"/>'s own doc comment. Word-wrapping and the actual
    /// scrolling are the TextView's job; this only remembers what to show.</summary>
    private readonly TranscriptBuffer _transcript = new();

    /// <summary>The <see cref="TranscriptBuffer.Version"/> the view last showed; UI thread only.</summary>
    private long _renderedTranscriptVersion = -1;

    /// <summary>Whether this run can show and drive the command line at all: false when input
    /// is redirected (<c>Console.IsInputRedirected</c>), set once at the top of
    /// <see cref="RunAsync"/>.</summary>
    private bool _showInputLine;

    /// <summary>Set for the lifetime of the interactive TUI — the handle every thread-marshalled
    /// update (<see cref="Note"/>, the periodic tick) needs to reach the UI thread via
    /// <c>Invoke</c>. Null outside <see cref="RunTuiAsync"/> (never constructed, or already torn
    /// down), in which case those callers fall back to their non-interactive behaviour.</summary>
    private IApplication? _app;

    private Label? _headerLabel;
    private ColorTextView? _transcriptView;
    private ColorTextView? _dropdownLabel;
    private TextField? _inputField;
    private Label? _promptLabel;
    private Label? _statusRule;

    /// <summary>The dialog currently shown (details or a picker), if any.</summary>
    private Dialog? _openDialog;

    /// <summary>Runs <paramref name="dialog"/> modally, recording it as the open one.</summary>
    private void RunDialog(IApplication app, Dialog dialog)
    {
        _openDialog = dialog;
        try
        {
            app.Run(dialog);
        }
        finally
        {
            _openDialog = null;

            // The terminal title follows the dialog ("Details") and is not put back when it
            // closes; re-set ours (cleared first, since an unchanged value is not re-sent).
            if (_window is { } window)
            {
                window.Title = string.Empty;
                window.Title = BuildTitle();
            }
        }
    }

    /// <summary>F5: show the engineering chatter (traces, census, perf). Off by default.</summary>
    private bool _showDebug;

    /// <summary>Ctrl+F: the input line edits a transcript filter instead of a command.</summary>
    private bool _searchMode;

    /// <summary>The live transcript filter (case-insensitive substring); empty = show all.</summary>
    private string _searchFilter = string.Empty;

    /// <summary>The command being typed when Ctrl+F was pressed, put back afterwards.</summary>
    private string _stashedCommand = string.Empty;
    private Label? _footerLabel;

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
    /// through here first; a single stat every 30 s is the whole cost.
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

    /// <summary>The F2 picker's list, refreshed on the timer thread from the throttled item
    /// cache so opening the picker never waits on a memory sweep. Null until the first fill.</summary>
    private volatile IReadOnlyList<DroppableItem>? _droppableCache;
    private long _droppableCacheAtMs;
    private const long DroppableCacheRefreshMs = 3000;

    private Window? _window;
    private string _lastDropNoteValue = string.Empty;

    /// <summary>
    /// The outcome of the last drop or pick-up. A property rather than a field because every
    /// assignment must ALSO reach the log file and transcript: this note is where a drop says
    /// why it refused ("not a readable item here", "ground full"). An unattended run — which
    /// is how this project tests — could therefore not tell a refusal from nothing having
    /// happened at all, which is exactly what blocked verifying the item-identity apply path
    /// live on 2026-08-15.
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

        // ⚠ TWO COMPLETELY DIFFERENT PATHS. Interactive gets the full Terminal.Gui TUI;
        // redirected/piped gets exactly what it always has — one compact status line per
        // change, straight to the pipe — since that is what tools/demo.ps1 and any
        // log-scraping automation already parse. RunNonInteractiveAsync never touches
        // Terminal.Gui at all.
        if (!canPosition)
        {
            await RunNonInteractiveAsync(canReadKeys, cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunTuiAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The pre-TUI behaviour, byte for byte: a compact log line per change (plus a
    /// heartbeat), no screen. F2/F3 still work (checked directly, without the full
    /// DashboardInputLine machinery — there is no visible command line to reflect typing into).</summary>
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
                        ConsoleKey key = Console.ReadKey(intercept: true).Key;
                        if (key == ConsoleKey.F2)
                        {
                            Drop(null);
                        }
                        else if (key == ConsoleKey.F3)
                        {
                            PickUpNearest();
                        }
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
    /// The interactive TUI's whole lifecycle: redirect Console.Out/Error to a capture writer,
    /// run <see cref="RunTuiOnDedicatedThread"/> to completion, and restore everything in
    /// <c>finally</c> regardless of how it ends — cancellation, Ctrl+C (handled inside the TUI
    /// itself; see <see cref="OnGlobalKeyDown"/>), or a crash.
    /// </summary>
    private async Task RunTuiAsync(CancellationToken cancellationToken)
    {
        TextWriter realOut = Console.Out;
        TextWriter realError = Console.Error;
        ConsoleCaptureWriter outCapture = new(Note);
        ConsoleCaptureWriter errorCapture = new(line => Note($"[stderr] {line}"));

        // Redirected BEFORE Init: Terminal.Gui's own driver renders through the console
        // handle directly, never through System.Console, so this has no effect on the TUI's
        // own painting — it only ever catches OTHER code's stray Console.WriteLine, and
        // catching it from the very first moment is the whole point (see this class's own
        // doc comment and ConsoleCaptureWriter's).
        Console.SetOut(outCapture);
        Console.SetError(errorCapture);

        try
        {
            // ⚠ A DEDICATED thread (TaskCreationOptions.LongRunning), never the shared
            // ThreadPool. Regression fixed 2026-09-26: a live join hung after
            // "pre-disconnect quicksave did not land within the wait ..." with nothing
            // logged for minutes afterward - a genuine deadlock, root-caused and reproduced
            // against the real library in tests/KotorCoop.Tests/AgentDashboardThreadingTests.cs
            // (see TickOnTimerThread's own doc comment for the confirmed mechanism: Terminal.
            // Gui's TimedEvents.RunTimers() holds its own lock for a due callback's whole
            // duration, so a callback blocked on SessionManager's _gate also blocks every
            // OTHER thread's Invoke - including the join-flow thread's own Note() calls,
            // issued from inside the very Disconnect() call that holds _gate: a genuine
            // AB-BA cycle, not just latency). That fix (never gathering manager.Agent inside
            // an Invoke callback) is what actually closes the deadlock.
            //
            // This dedicated thread is a SEPARATE, independently-justified correctness fix
            // found investigating the same regression, not a workaround for it: the previous
            // shape here (await Task.Run(() => app.Run(window))) parked app.Run's whole
            // session-long blocking call on an ORDINARY shared ThreadPool worker, and left
            // IApplication.MainThreadId (stamped once, in Init(), never reassigned by Run())
            // pointing at whatever thread happened to call Init() rather than the thread that
            // actually pumps the main loop, so Invoke's "already on the main thread" fast path
            // could never trigger even for code genuinely running on the pump thread. A
            // dedicated thread costs nothing extra (this app has exactly one) and gives
            // Init()+Run() the same thread, as the library's own docs assume.
            await Task.Factory.StartNew(
                () => RunTuiOnDedicatedThread(cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            outCapture.FlushPending();
            errorCapture.FlushPending();
            Console.SetOut(realOut);
            Console.SetError(realError);
        }
    }

    /// <summary>
    /// Runs on the dedicated thread <see cref="RunTuiAsync"/> starts: builds the Terminal.Gui
    /// application/window, arms the self-rescheduling periodic tick (see
    /// <see cref="TickOnTimerThread"/>), and blocks in <c>app.Run(window)</c> until
    /// <see cref="RequestQuit"/> stops it (Ctrl+C from inside the TUI, or the cancellation
    /// token firing from outside it).
    /// </summary>
    private void RunTuiOnDedicatedThread(CancellationToken cancellationToken)
    {
        IApplication app = Application.Create().Init();
        _app = app;

        System.Threading.Timer? tickTimer = null;

        try
        {
            Window window = BuildWindow(app);

            // Self-rescheduling (re-armed at the END of each tick, in TickOnTimerThread's own
            // finally) rather than a plain repeating Timer: a repeating Timer keeps firing the
            // NEXT tick on schedule even while the PREVIOUS one is still blocked gathering
            // data, and under load that piles up OVERLAPPING ticks - each its own ThreadPool
            // work item - all competing for threads at once. Self-rescheduling guarantees at
            // most one tick in flight, so a single slow tick (SessionManager._gate contended
            // for up to CharacterCaptureCadenceRule.SaveWaitMs, 5s, during a live join) costs
            // exactly one delayed refresh, never a growing backlog.
            tickTimer = new System.Threading.Timer(
                _ => TickOnTimerThread(app, tickTimer!),
                null,
                TimeSpan.FromMilliseconds(200),
                System.Threading.Timeout.InfiniteTimeSpan);

            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                try
                {
                    app.Invoke(() => RequestQuit(app));
                }
                catch
                {
                    // Best-effort: the app may be mid-shutdown.
                }
            });

            app.Run(window);
        }
        finally
        {
            tickTimer?.Dispose();
            _app = null;
            app.Dispose();
        }
    }

    /// <summary>
    /// One periodic tick, running on <see cref="RunTuiOnDedicatedThread"/>'s own dedicated
    /// thread (NOT the Terminal.Gui main loop): gathers the drop-file check and the header/
    /// footer TEXT here, then marshals only the (fast, non-blocking) view-text assignment
    /// onto the UI thread via <c>Invoke</c>.
    ///
    /// ⚠ THE OTHER HALF OF THE REGRESSION FIX <see cref="RunTuiAsync"/> DESCRIBES, and the
    /// half that actually deadlocked a live join (2026-09-26; see
    /// tests/KotorCoop.Tests/AgentDashboardThreadingTests.cs for the confirmed mechanism,
    /// found by running it against the real library). <see cref="BuildHeaderLines"/> reads
    /// <c>manager.Agent</c>, which takes <c>SessionManager</c>'s own lock (<c>_gate</c>) - a
    /// lock a background join-flow thread can legitimately hold for several seconds at a time
    /// (<c>SessionManager.Disconnect</c>/<c>DisconnectCore</c> hold it across
    /// <c>OnDisconnecting</c>, and <c>CoopCommandDispatcher.CaptureCharacterOnDisconnect</c>'s
    /// own blocking save-landed poll runs entirely inside that window).
    ///
    /// Gathering it HERE means a long <c>_gate</c> wait only delays THIS tick's own refresh.
    /// Gathering it inside the <c>Invoke</c> callback instead would run it ON the Terminal.Gui
    /// main loop thread - and Terminal.Gui's own <c>TimedEvents.RunTimers()</c> (what the main
    /// loop calls every iteration to run due callbacks) holds ITS OWN lock
    /// (<c>_timeoutsLockToken</c>) for the callback's ENTIRE duration, not just to dequeue it.
    /// Every OTHER thread's <c>Invoke</c> call needs that exact lock to enqueue
    /// (<c>TimedEvents.Add</c>), so a callback stuck on <c>_gate</c> would ALSO block every
    /// <c>Note()</c> call the join flow itself makes to report its own progress - and since
    /// the join flow calls <c>Note()</c> from INSIDE the very <c>SessionManager.Disconnect</c>
    /// call that holds <c>_gate</c>, that is a genuine AB-BA deadlock between the two locks,
    /// not just added latency. A live join reproduced exactly this: nothing logged past
    /// "pre-disconnect quicksave ..." ever again.
    /// </summary>
    private void TickOnTimerThread(IApplication app, System.Threading.Timer tickTimer)
    {
        try
        {
            CheckDropFile();
            long now = Environment.TickCount64;
            if (dispatcher is not null && now - _droppableCacheAtMs >= DroppableCacheRefreshMs)
            {
                _droppableCacheAtMs = now;
                _droppableCache = dispatcher.ListDroppableItems(fresh: false);
            }

            string titleText = BuildTitle();
            string statusText = BuildStatusRule();
            string headerText = string.Join('\n', BuildHeaderLines());
            string footerText = BuildFooter();

            app.Invoke(() =>
            {
                if (_window is { } window && window.Title != titleText)
                {
                    window.Title = titleText;
                }

                if (_flashText.Length > 0 && Environment.TickCount64 >= _flashUntilMs)
                {
                    _flashText = string.Empty;
                    UpdateDropdown();
                }

                if (_statusRule is { } status && status.Text != statusText)
                {
                    status.Text = statusText;
                }

                if (_footerLabel is { } footer)
                {
                    footer.Text = footerText;
                }

                if (_transcript.Version != _renderedTranscriptVersion)
                {
                    RefreshTranscriptView();
                }
            });
        }
        catch
        {
            // Best-effort: the app may be mid-shutdown, or the game process may have gone
            // away mid-read (manager.Agent/CheckDropFile talk to it) - a missed tick is
            // harmless, the next one 200ms later picks up wherever this one left off.
        }
        finally
        {
            try
            {
                tickTimer.Change(TimeSpan.FromMilliseconds(200), System.Threading.Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Torn down mid-tick (RunTuiOnDedicatedThread's own finally already disposed
                // it); nothing left to reschedule.
            }
        }
    }

    /// <summary>Ctrl+C's whole job: tear down the REST of the agent (dispatcher, session,
    /// sockets — <paramref name="requestShutdown"/>Action wired by AgentComposition to the
    /// SAME CancellationTokenSource RunCommand's own Ctrl+C handler used before this TUI
    /// existed) and stop the Terminal.Gui loop. Both are needed: Terminal.Gui puts the
    /// console in raw input mode, so the OS-level Console.CancelKeyPress that used to drive
    /// this never fires while the TUI is up — this is now the ONLY path to a clean exit.</summary>
    private void RequestQuit(IApplication app)
    {
        requestShutdown?.Invoke();
        app.RequestStop();
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
    /// command reply/chat, or a captured stray console line: pushed into the transcript and
    /// written to the log file. Marshalled onto the UI thread via <c>Invoke</c> when the TUI is
    /// up; when it is not (the non-interactive path, or before the TUI has started), the
    /// REDIRECTED stdout is the record instead, exactly as it always was. Thread-safe — callers
    /// include the pump thread and, once the TUI is up, <see cref="ConsoleCaptureWriter"/>'s own
    /// capture callback from whichever thread wrote to <c>Console</c>.
    /// </summary>
    public void Note(string line)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] * {line}";
        _transcript.Append(WithPlaceNames(line));

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

        // ⚠ No app.Invoke here. Invoke takes Terminal.Gui's timer lock, which the UI thread
        // holds while a RefreshTranscriptView runs — and a full resync of a 2000-line
        // transcript takes seconds once lines are long. One Invoke per line parked the PUMP
        // thread on that lock (live 2026-09-26: 30 -> 0.3 ticks/s after ~12 min, the session
        // visibly lagging) and queued redraws faster than the UI could run them (the dashboard
        // froze as a blank, unscrollable block). TickOnTimerThread redraws on its own 200 ms
        // cadence when _transcript.Version moved; Note only appends.
        if (_app is null && Console.IsOutputRedirected)
        {
            // Only reachable from RunNonInteractiveAsync's own world (no _app was ever set),
            // or a brief window before/after the TUI - the same fallback Note() always had.
            Console.Out.WriteLine(entry);
            Console.Out.Flush();
        }
    }

    /// <summary>A line for the <c>--log</c> file only, in <see cref="Note"/>'s exact format (tools
    /// grep it), kept out of the transcript: the once-a-second <c>perf:</c> line would
    /// otherwise be most of what the player sees.</summary>
    public void LogOnly(string line)
    {
        if (logFile is null)
        {
            return;
        }

        try
        {
            RotateLogMidSessionIfDue(logFile);
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] * {line}" + Environment.NewLine);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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

    // ===================================================================================
    // Terminal.Gui wiring — everything below touches a View and must run on the UI thread.
    // ===================================================================================

    /// <summary>
    /// Terminal.Gui 2.5's default theme draws a READ-ONLY TextView's text in the same light
    /// grey as its background — the transcript and F1 details were a blank grey block with a
    /// scrollbar and nothing legible (reproduced in isolation 2026-09-26), and the input line
    /// sat in the same grey. Plain light-on-black for every text surface, like the header.
    /// </summary>
    private static readonly Scheme TextScheme = new(new Terminal.Gui.Drawing.Attribute(Color.Gray, Color.Black))
    {
        ReadOnly = new Terminal.Gui.Drawing.Attribute(Color.Gray, Color.Black),
        Editable = new Terminal.Gui.Drawing.Attribute(Color.White, Color.Black),
        Focus = new Terminal.Gui.Drawing.Attribute(Color.White, Color.Black),
    };

    /// <summary>Rows the composer block takes under the log: rule, input, rule, hint, footer.</summary>
    private const int ComposerRows = 4 + SuggestionRows;

    /// <summary>Rows reserved under the input for completions (verb + what it takes) or a tip.</summary>
    private const int SuggestionRows = 3;

    private static readonly Terminal.Gui.Drawing.Attribute ToneCommand = new(Color.White, Color.Black);
    private static readonly Terminal.Gui.Drawing.Attribute TonePlain = new(Color.Gray, Color.Black);
    private static readonly Terminal.Gui.Drawing.Attribute ToneGood = new(Color.BrightGreen, Color.Black);
    private static readonly Terminal.Gui.Drawing.Attribute ToneWarning = new(Color.BrightYellow, Color.Black);
    private static readonly Terminal.Gui.Drawing.Attribute ToneBad = new(Color.BrightRed, Color.Black);
    private static readonly Terminal.Gui.Drawing.Attribute ToneHelpful = new(Color.BrightCyan, Color.Black);
    private static readonly Terminal.Gui.Drawing.Attribute ToneDiagnostic = new(Color.DarkGray, Color.Black);

    /// <summary>A two-cell level marker in front of every line, so good/warning/error read at
    /// a glance even without colour (a copied log keeps them).</summary>
    private static string MarkerFor(Tone tone) => tone switch
    {
        Tone.Good => "✓ ",
        Tone.Warning => "! ",
        Tone.Bad => "✗ ",
        Tone.Helpful => "i ",
        Tone.Diagnostic => "· ",
        _ => "  ",
    };

    /// <summary>A short-lived notice shown in the hint rows (Claude-Code-style), not the log.</summary>
    private string _flashText = string.Empty;
    private Terminal.Gui.Drawing.Attribute _flashColour;
    private long _flashUntilMs;
    private const long FlashMs = 2500;

    private void Flash(string text, Terminal.Gui.Drawing.Attribute colour)
    {
        _flashText = text;
        _flashColour = colour;
        _flashUntilMs = Environment.TickCount64 + FlashMs;
        UpdateDropdown();
    }

    private bool _reloadingTranscript;
    private int _renderedRows = -1;
    private string _renderedSignature = string.Empty;

    /// <summary>How many debug lines the last render left out (for the footer).</summary>
    private int _hiddenDebugLines;

    private static Terminal.Gui.Drawing.Attribute AttributeFor(Tone tone) => tone switch
    {
        Tone.Command => ToneCommand,
        Tone.Good => ToneGood,
        Tone.Warning => ToneWarning,
        Tone.Bad => ToneBad,
        Tone.Helpful => ToneHelpful,
        Tone.Diagnostic => ToneDiagnostic,
        _ => TonePlain,
    };

    /// <summary>Wide enough for any terminal; a Label clips to its own width.</summary>
    private static readonly string Rule = new('─', 400);

    /// <summary>Dim grey for rules, the hint row and the footer: chrome, not content.</summary>
    private static readonly Scheme RuleScheme = new(new Terminal.Gui.Drawing.Attribute(Color.DarkGray, Color.Black));

    /// <summary>List rows light grey, the selected row black on cyan so it is obvious.</summary>
    private static readonly Scheme PickerScheme = new(new Terminal.Gui.Drawing.Attribute(Color.Gray, Color.Black))
    {
        Focus = new Terminal.Gui.Drawing.Attribute(Color.Black, Color.BrightCyan),
        Active = new Terminal.Gui.Drawing.Attribute(Color.Black, Color.BrightCyan),
        Highlight = new Terminal.Gui.Drawing.Attribute(Color.Black, Color.BrightCyan),
    };

    /// <summary>The "> " prompt stands out from what is typed after it.</summary>
    private static readonly Scheme PromptScheme = new(new Terminal.Gui.Drawing.Attribute(Color.BrightCyan, Color.Black));

    private Window BuildWindow(IApplication app)
    {
        Window window = _window = new()
        {
            Title = BuildTitle(),
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.None,

            // ⚠ Must be focusable. In Terminal.Gui v2 a view that cannot take focus cannot
            // pass it to a subview either, so with this false the input field never had focus
            // and typed keys went nowhere; only the app-level F-keys worked (reproduced in
            // isolation 2026-09-26). The other children stay unfocusable, so focus lands on
            // the input field.
            CanFocus = true,
        };

        _headerLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 4,
            Text = string.Join('\n', BuildHeaderLines()),
            CanFocus = false,
        };

        _transcriptView = new ColorTextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(ComposerRows),
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = true,

            // Keyboard focus stays on the input field below (Claude-Code-style: you are
            // always composing); the transcript still scrolls by mouse wheel (Terminal.Gui
            // routes wheel events by cursor position, not focus) and by PageUp/PageDown/
            // Ctrl+Home/Ctrl+End, which OnGlobalKeyDown forwards here regardless of focus.
            CanFocus = false,
        };
        _transcriptView.SetScheme(TextScheme);
        _transcriptView.ViewportChanged += (_, _) => OnTranscriptViewportChanged();
        _transcriptView.MouseEvent += (_, mouse) => OnTranscriptMouse(mouse);

        // The composer: a fixed block anchored to the bottom, Claude-Code-style, so typing
        // never shares a row with the log or the footer (it used to: the input sat on the
        // footer's row and the completion line popped up over the last log line).
        //
        //   ...log...
        //   ─────────────   rule
        //   > command|      input
        //   ─────────────   rule
        //   completions with their usage, or a tip (rows always reserved)
        //   F1 details ...  footer
        Label topRule = _statusRule = new()
        {
            X = 0,
            Y = Pos.AnchorEnd(4 + SuggestionRows),
            Width = Dim.Fill(),
            Height = 1,
            Text = Rule,
            CanFocus = false,
        };
        topRule.SetScheme(RuleScheme);

        Label prompt = _promptLabel = new()
        {
            X = 0,
            Y = Pos.AnchorEnd(3 + SuggestionRows),
            Width = 2,
            Height = 1,
            Text = "> ",
            CanFocus = false,
        };
        prompt.SetScheme(PromptScheme);

        _inputField = new TextField
        {
            X = Pos.Right(prompt),
            Y = Pos.AnchorEnd(3 + SuggestionRows),
            Width = Dim.Fill(),
            Height = 1,
            Text = string.Empty,
        };
        _inputField.SetScheme(TextScheme);
        _inputField.KeyDown += OnInputFieldKeyDown;

        Label bottomRule = new()
        {
            X = 0,
            Y = Pos.AnchorEnd(2 + SuggestionRows),
            Width = Dim.Fill(),
            Height = 1,
            Text = Rule,
            CanFocus = false,
        };
        bottomRule.SetScheme(RuleScheme);

        _dropdownLabel = new ColorTextView
        {
            X = 2,
            Y = Pos.AnchorEnd(1 + SuggestionRows),
            Width = Dim.Fill(),
            Height = SuggestionRows,
            ReadOnly = true,
            WordWrap = false,
            CanFocus = false,
        };
        _dropdownLabel.SetScheme(RuleScheme);

        _footerLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Text = BuildFooter(),
            CanFocus = false,
        };
        _footerLabel.SetScheme(RuleScheme);

        // The old four-line sticky header is gone: F1 has everything it showed, and the one
        // live line worth keeping (who/where/health) rides the composer's top rule.
        window.Add(_transcriptView, topRule, prompt, _inputField, bottomRule, _dropdownLabel, _footerLabel);

        app.Keyboard.KeyDown += (_, key) => OnGlobalKeyDown(app, key);

        RefreshTranscriptView();
        UpdateDropdown();

        return window;
    }

    /// <summary>
    /// Every key that reaches the command line: delegated straight to
    /// <see cref="DashboardInputLine"/> (the single source of truth for the buffer/caret/
    /// history/completion, shared with the pure test suite) and mirrored into the TextField
    /// afterward — the TextField's OWN editing never runs, since every key handled here sets
    /// <c>key.Handled = true</c>.
    /// </summary>
    private void OnInputFieldKeyDown(object? sender, Key key)
    {
        if (_searchMode)
        {
            OnSearchKeyDown(key);
            return;
        }

        if (key == Key.Esc && _searchFilter.Length > 0 && _input.Buffer.Length == 0)
        {
            // An applied filter is the first thing Esc undoes, never a surprise quit while
            // the transcript is showing a filtered view.
            key.Handled = true;
            SetSearchFilter(string.Empty);
            return;
        }

        if (key == Key.Enter)
        {
            key.Handled = true;
            SubmitInput();
        }
        else if (key == Key.Esc)
        {
            key.Handled = true;

            // Esc clears the line and nothing more. It used to quit on an empty line, which
            // ended the co-op session for anyone pressing it twice; Ctrl+Q quits now.
            _input.ClearLine();
        }
        else if (key == Key.Backspace)
        {
            key.Handled = true;
            _input.Backspace();
        }
        else if (key == Key.Delete)
        {
            key.Handled = true;
            _input.Delete();
        }
        else if (key == Key.CursorLeft)
        {
            key.Handled = true;
            _input.MoveLeft();
        }
        else if (key == Key.CursorRight)
        {
            key.Handled = true;
            _input.MoveRight();
        }
        else if (key == Key.Home)
        {
            key.Handled = true;
            _input.MoveHome();
        }
        else if (key == Key.End)
        {
            key.Handled = true;
            _input.MoveEnd();

            // "End ... jumps back and resumes follow": moving the caret to the end of what
            // you are typing and resuming the transcript's own follow-the-bottom are two
            // unrelated things happening to land on the same key by convention - do both.
            ScrollTranscriptToBottom();
        }
        else if (key == Key.CursorUp)
        {
            key.Handled = true;
            _input.HistoryUp();
        }
        else if (key == Key.CursorDown)
        {
            key.Handled = true;
            _input.HistoryDown();
        }
        else if (key == Key.Tab)
        {
            key.Handled = true;
            _input.Complete(reverse: false);
        }
        else if (key == Key.Tab.WithShift)
        {
            key.Handled = true;
            _input.Complete(reverse: true);
        }
        else if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value is > 0 and < 0x110000)
        {
            // Typing means you are about to act on what is newest: bring the log back to the
            // bottom if it had been scrolled up.
            if (!_transcript.IsFollowing)
            {
                _transcript.SetFollowing(true);
                ScrollTranscriptToBottom();
            }

            // An ordinary typed character. Co-op verbs and their arguments are ASCII, so the
            // Rune -> char narrowing below is safe for everything this console actually
            // accepts; anything outside the BMP (extremely unlikely input here) is dropped
            // rather than risking a corrupt surrogate half in the buffer.
            Rune rune = key.AsRune;
            if (rune.Value <= char.MaxValue)
            {
                key.Handled = true;
                _input.TypeChar((char)rune.Value);
            }
        }

        SyncInputField();
    }

    private void SyncInputField()
    {
        if (_inputField is not { } field)
        {
            return;
        }

        field.Text = _input.Buffer;
        field.InsertionPoint = _input.Caret;
        UpdateDropdown();
    }

    /// <summary>The completion candidates (top 6 + a count of the rest) or the single usage
    /// hint, whichever <see cref="DashboardInputLine"/> currently has — one row above the
    /// prompt, joined rather than drawn as a multi-row popup, so this layout never has to
    /// resize around how many candidates matched.</summary>
    private void UpdateDropdown()
    {
        // A drawing bug in this window must never end the co-op session with it (live
        // 2026-09-26: a TextView index error in here took the host's agent down).
        try
        {
            UpdateDropdownCore();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            LogOnly($"dashboard: UpdateDropdown failed - {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void UpdateDropdownCore()
    {
        if (_dropdownLabel is not { } view)
        {
            return;
        }

        List<List<Cell>> rows = [];
        if (!_showInputLine)
        {
            view.LoadRows(rows);
            return;
        }

        string buffer = _input.Buffer;
        IReadOnlyList<string> candidates = _input.Candidates;
        string hint = _input.Hint;

        if (_flashText.Length > 0 && Environment.TickCount64 < _flashUntilMs)
        {
            rows.Add(Cell.ToCellList(_flashText, _flashColour));
        }
        else if (_searchMode)
        {
            rows.Add(Cell.ToCellList("searching the log - type to filter, Enter keeps the filter, Esc cancels", ToneHelpful));
        }
        else if (buffer.Length == 0)
        {
            rows.Add(Cell.ToCellList("type a command - Tab completes, Up/Down recalls, `help` lists them all", ToneDiagnostic));
            rows.Add(Cell.ToCellList("Ctrl+F searches the log   Ctrl+C copies what is shown   F2 drops one of your items   F3 picks one up", ToneDiagnostic));
        }
        else if (candidates.Count > 1)
        {
            // One row per candidate, the command in cyan and what it takes after it, so the
            // list says what each one does rather than being a bare row of names.
            int shown = Math.Min(candidates.Count, SuggestionRows - (candidates.Count > SuggestionRows ? 1 : 0));
            for (int i = 0; i < shown; i++)
            {
                string verb = candidates[i];
                string usage = UsageFor(verb);
                List<Cell> row = Cell.ToCellList(verb.PadRight(16), ToneHelpful);
                row.AddRange(Cell.ToCellList(usage, TonePlain));
                rows.Add(row);
            }

            if (candidates.Count > shown)
            {
                rows.Add(Cell.ToCellList($"... {candidates.Count - shown} more - keep typing, or Tab to cycle", ToneDiagnostic));
            }
        }
        else if (hint.Length > 0)
        {
            rows.Add(Cell.ToCellList(hint, ToneHelpful));
        }
        else if (!buffer.Contains(' '))
        {
            rows.Add(Cell.ToCellList($"no co-op command starts with '{buffer}' - `help` lists them", ToneWarning));
        }

        view.LoadRows(rows);
    }

    private static string UsageFor(string verb)
    {
        foreach ((string v, string usage) in DashboardInputLine.Catalog)
        {
            if (v.Equals(verb, StringComparison.OrdinalIgnoreCase))
            {
                return usage.StartsWith(v, StringComparison.OrdinalIgnoreCase) ? usage[v.Length..].TrimStart() : usage;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Enter: handles a local <c>clear</c>/<c>cls</c> (wipes THIS window's own transcript —
    /// the dispatcher's own <c>clear</c> verb only ever clears the in-game overlay, so both
    /// aliases do both here: local wipe, then still dispatched normally so the overlay clears
    /// too), echoes the line, and runs it through the SAME dispatcher the ~ console feeds —
    /// <see cref="CoopCommandDispatcher.RunTypedLine"/> — so the reply comes back through
    /// <see cref="Note"/> exactly as any other console reply does. Snaps the view back to the
    /// bottom even if it had been scrolled up. Blank lines submit nothing.
    /// </summary>
    private void SubmitInput()
    {
        string line = _input.Submit();
        if (line.Length == 0)
        {
            return;
        }

        string firstToken = line.Split(' ', 2)[0].TrimStart('/').ToLowerInvariant();
        bool clearing = firstToken is "clear" or "cls";
        if (clearing)
        {
            // A clear leaves an empty log, not a log whose only line is "> clear".
            ClearTranscriptLocally();
            Flash("✓ log cleared (the --log file keeps everything; Ctrl+O opens it)", ToneGood);
        }
        else
        {
            _transcript.Append($"> {line}");
        }

        _transcript.SetFollowing(true);
        RefreshTranscriptView();
        ScrollTranscriptToBottom();

        if (dispatcher is null)
        {
            _transcript.Append("(no command dispatcher wired to this dashboard)");
            RefreshTranscriptView();
            return;
        }

        dispatcher.PostTypedLine(line);
    }

    /// <summary>Ctrl+L, and the local half of typing <c>clear</c>/<c>cls</c> — wipes this
    /// window's own transcript and resumes following the bottom.</summary>
    private void ClearTranscriptLocally()
    {
        _transcript.Clear();
        RefreshTranscriptView();
    }

    /// <summary>
    /// Global key handling — reaches this regardless of which view has focus, so PageUp/
    /// PageDown/Ctrl+Home/Ctrl+End keep working while the input field (the normal case) holds
    /// focus. Plain Home/End stay local to the input field (see <see cref="OnInputFieldKeyDown"/>)
    /// since they double as ordinary caret movement there.
    /// </summary>
    private void OnGlobalKeyDown(IApplication app, Key key)
    {
        if (key == Key.Q.WithCtrl)
        {
            key.Handled = true;
            RequestQuit(app);
        }
        else if (key == Key.C.WithCtrl)
        {
            // Copy, never quit: Ctrl+C used to end the agent — and with it the co-op session —
            // for anyone reaching for the usual copy shortcut.
            key.Handled = true;
            CopyVisibleLog(app);
        }
        else if (key == Key.O.WithCtrl)
        {
            key.Handled = true;
            OpenFile(logFile, "the agent log");
        }
        else if (key == Key.F5)
        {
            key.Handled = true;
            _showDebug = !_showDebug;
            RefreshTranscriptView();
            RefreshFooter();
        }
        else if (key == Key.L.WithCtrl)
        {
            key.Handled = true;
            ClearTranscriptLocally();
        }
        else if (key == Key.PageUp)
        {
            key.Handled = true;
            ScrollTranscript(-TranscriptPage());
        }
        else if (key == Key.PageDown)
        {
            key.Handled = true;
            ScrollTranscript(TranscriptPage());
        }
        else if (key == Key.End.WithCtrl)
        {
            key.Handled = true;
            ScrollTranscriptToBottom();
        }
        else if (key == Key.Home.WithCtrl)
        {
            key.Handled = true;
            ScrollTranscriptToTop();
        }
        else if (_openDialog is { } open && (key == Key.F1 || key == Key.F2 || key == Key.F3 || key == Key.Esc))
        {
            // A dialog is up: these keys reach this app-level handler too, and used to open a
            // SECOND dialog on top (live: "F1 twice doesn't always close"). Close the open one.
            key.Handled = true;
            open.RequestStop();
        }
        else if (key == Key.F1)
        {
            key.Handled = true;
            ShowDetailsDialog(app);
        }
        else if (key == Key.F.WithCtrl)
        {
            key.Handled = true;
            BeginSearch();
        }
        else if (key == Key.F2)
        {
            key.Handled = true;
            ShowDropPicker(app);
        }
        else if (key == Key.F3)
        {
            key.Handled = true;
            ShowPickupPicker(app);
        }
    }

    private int TranscriptPage() =>
        _transcriptView is { } view ? Math.Max(view.Viewport.Height - 1, 1) : 1;

    private void ScrollTranscript(int rows)
    {
        if (_transcriptView is not { } view)
        {
            return;
        }

        int maxTop = Math.Max(view.Lines - view.Viewport.Height, 0);
        int newTop = Math.Clamp(view.Viewport.Y + rows, 0, maxTop);
        view.Viewport = view.Viewport with { Y = newTop };
    }

    private void ScrollTranscriptToBottom()
    {
        _transcriptView?.MoveEnd();
    }

    private void ScrollTranscriptToTop()
    {
        if (_transcriptView is not { } view)
        {
            return;
        }

        view.Viewport = view.Viewport with { Y = 0 };
    }

    /// <summary>Fires on every scroll of the transcript view, whichever caused it (mouse
    /// wheel, PageUp/PageDown, End, or this class's own programmatic restore after appending
    /// while not following) — the single place <see cref="TranscriptBuffer.SetFollowing"/> is
    /// called, so the follow/"N new" state can never drift from what is actually on screen.</summary>
    private void OnTranscriptViewportChanged()
    {
        // Our own reload fires this too (the empty load that resets the cursor reads as "at
        // the bottom"), which switched following back on and yanked a reader who had
        // scrolled up down to the end on every refresh (live 2026-09-26).
        if (_transcriptView is not { } view || _reloadingTranscript)
        {
            return;
        }

        bool atBottom = view.Viewport.Y + view.Viewport.Height >= view.Lines;
        _transcript.SetFollowing(atBottom);
        RefreshFooter();
    }

    /// <summary>
    /// Resyncs the transcript TextView's text from <see cref="_transcript"/>. A full resync
    /// rather than an incremental append: the transcript is capped at 2000 short lines and
    /// this runs on discrete events (a handful of times a second at most, never a per-frame
    /// path), so the simplicity of "the view always exactly mirrors the buffer" is worth more
    /// here than the small saved cost of an incremental edit — and it sidesteps a real
    /// correctness trap incremental editing would not: TextView's own cursor tracks wherever
    /// the user last scrolled to (PageUp moves it, not just the viewport), so inserting new
    /// text "at the cursor" while scrolled up would corrupt old lines instead of appending.
    ///
    /// Setting <c>Text</c> resets the view to the top, so this saves/restores the scroll
    /// position explicitly: follows the bottom when the transcript was already being followed,
    /// otherwise restores the exact row it was showing (clamped, since content may have grown
    /// or — <c>Clear()</c> — shrunk to nothing).
    /// </summary>
    private void RefreshTranscriptView()
    {
        // A drawing bug in this window must never end the co-op session with it (live
        // 2026-09-26: a TextView index error in here took the host's agent down).
        try
        {
            RefreshTranscriptViewCore();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            LogOnly($"dashboard: RefreshTranscriptView failed - {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RefreshTranscriptViewCore()
    {
        if (_transcriptView is not { } view)
        {
            return;
        }

        _renderedTranscriptVersion = _transcript.Version;
        bool wasFollowing = _transcript.IsFollowing;
        int savedTop = view.Viewport.Y;

        IReadOnlyList<string> lines = _transcript.Lines;
        if (_searchFilter.Length > 0)
        {
            lines = lines.Where(l => l.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        List<List<Cell>> rows = new(lines.Count);
        int hidden = 0;
        string firstVisible = string.Empty;
        string lastVisible = string.Empty;
        foreach (string line in lines)
        {
            Tone tone = TranscriptTone.Classify(line);
            if (tone == Tone.Diagnostic && !_showDebug)
            {
                hidden++;
                continue;
            }

            Terminal.Gui.Drawing.Attribute colour = AttributeFor(tone);

            // An indented row continues or lists under the line above it: colour it, but one
            // marker per block is enough (the `cheats` table had an "i" on every row).
            string marker = line.StartsWith("  ", StringComparison.Ordinal) ? "  " : MarkerFor(tone);
            if (firstVisible.Length == 0)
            {
                firstVisible = line;
            }

            lastVisible = line;
            List<Cell> row = Cell.ToCellList(marker, colour);
            row.AddRange(Cell.ToCellList(line, colour));
            rows.Add(row);
        }

        _hiddenDebugLines = hidden;

        // Nothing visible changed (only hidden debug lines arrived — perf alone is one a
        // second): leave the view alone, so a selection or a scrolled-up reading position is
        // not disturbed by a redraw that would show the same thing.
        string signature = $"{rows.Count}|{_showDebug}|{_searchFilter}|{firstVisible}|{lastVisible}";
        if (rows.Count == _renderedRows && signature == _renderedSignature)
        {
            return;
        }

        _renderedRows = rows.Count;
        _renderedSignature = signature;

        _reloadingTranscript = true;
        try
        {
            view.LoadRows(rows);
        }
        finally
        {
            _reloadingTranscript = false;
        }

        if (wasFollowing)
        {
            view.MoveEnd();
        }
        else
        {
            int maxTop = Math.Max(view.Lines - view.Viewport.Height, 0);
            view.Viewport = view.Viewport with { Y = Math.Min(savedTop, maxTop) };
        }

        RefreshFooter();
    }

    private void RefreshFooter()
    {
        if (_footerLabel is { } footer)
        {
            footer.Text = BuildFooter();
        }
    }

    /// <summary>
    /// F1: the old long readout (NETWORK/GAME MEMORY/THIS PLAYER/OTHER PLAYERS/SHARED WORLD)
    /// that the sticky header no longer has room for, in a modal dialog over the transcript.
    /// <c>IApplication.Run</c> is reentrant by design for exactly this — a nested runnable
    /// session pushed while the outer one is pumping — so this blocks (from inside the SAME
    /// key-handler call that is itself running on the UI thread) until the dialog's own
    /// F1/Esc handler requests it to stop, then returns control to the window underneath.
    /// </summary>
    /// <summary>Ctrl+F: the input line becomes a live transcript filter (prompt "/ ").</summary>
    /// <summary>
    /// One details line in colour: a heading ("...:" on its own) in white, a "label   value"
    /// row with the label in cyan and the value coloured by what it says (a failing read in
    /// red, a warning in yellow), anything else by the same rules as the log.
    /// </summary>
    private static List<Cell> DetailsRow(string line)
    {
        if (line.Length > 0 && line.TrimEnd().EndsWith(':') && !line.StartsWith(' '))
        {
            return Cell.ToCellList(line, ToneCommand);
        }

        System.Text.RegularExpressions.Match m = DetailsLabel.Match(line);
        if (m.Success)
        {
            string value = m.Groups[2].Value;
            Tone tone = TranscriptTone.Classify(value);
            List<Cell> row = Cell.ToCellList(m.Groups[1].Value, ToneHelpful);
            row.AddRange(Cell.ToCellList(value, tone is Tone.Bad or Tone.Warning or Tone.Good ? AttributeFor(tone) : TonePlain));
            return row;
        }

        return Cell.ToCellList(line, AttributeFor(TranscriptTone.Classify(line)));
    }

    private static readonly System.Text.RegularExpressions.Regex DetailsLabel = new(
        @"^(\s*[A-Za-z][\w /()-]{0,24}?\s{2,})(\S.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex LogFileMention = new(
        @"(?:[A-Za-z]:\\[^\s""']+?\.log|kotorcoop-[a-z]+\.log|agent\d*\.log)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A click on a log line that names a log file opens that file (the log lines
    /// say "see kotorcoop-agent.log for why" — now that is one click, not a hunt).</summary>
    private void OnTranscriptMouse(Terminal.Gui.Input.Mouse mouse)
    {
        if (!mouse.IsSingleClicked || mouse.Position is not { } at || _transcriptView is not { } view)
        {
            return;
        }

        int row = view.Viewport.Y + at.Y;
        if (row < 0 || row >= view.Lines)
        {
            return;
        }

        string text = Cell.ToString(view.GetLine(row));
        System.Text.RegularExpressions.Match m = LogFileMention.Match(text);
        if (!m.Success)
        {
            return;
        }

        mouse.Handled = true;
        OpenFile(ResolveLogMention(m.Value), m.Value);
    }

    /// <summary>A mentioned log name to a real path: a full path as-is; the agent's own log
    /// names (whatever --log is called here) to this agent's log; the plugin log in the game
    /// folder.</summary>
    private string? ResolveLogMention(string mention)
    {
        if (Path.IsPathRooted(mention))
        {
            return mention;
        }

        if (mention.Contains("plugin", StringComparison.OrdinalIgnoreCase)
            && dispatcher?.GameDirectoryPath is { Length: > 0 } game)
        {
            return Path.Combine(game, "kotorcoop-plugin.log");
        }

        return logFile;
    }

    /// <summary>Opens a file with its default program (Notepad for a .log, typically).</summary>
    private void OpenFile(string? path, string what)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Flash($"✗ {what} is not on disk here{(string.IsNullOrEmpty(path) ? string.Empty : $" ({path})")}", ToneBad);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Flash($"✓ opened {Path.GetFileName(path)}", ToneGood);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Flash($"✗ could not open {path}: {ex.Message}", ToneBad);
        }
    }

    /// <summary>The composer's top rule: the one live status line (who, where, health, link).</summary>
    private string BuildStatusRule()
    {
        CoopAgent? agent = manager.Agent;
        string status;
        if (agent is null)
        {
            status = $"── not connected ({manager.Phase.ToString().ToLowerInvariant()}) - type connect or host ";
        }
        else
        {
            PlayerState local = agent.LastLocalState;
            SessionStats stats = agent.Session.GetStats();
            string place = local.Module.Length == 0
                ? "loading"
                : dispatcher?.ModuleName(local.Module) is { Length: > 0 } named && named != local.Module
                    ? named
                    : local.Module;
            int partners = agent.LastPeers.Count;
            status = $"── {BuildTitle()["KOTOR co-op - ".Length..]} · {place} · hp {DescribeHealth(local.Health, local.MaxHealth)}"
                + $" · {partners} partner{(partners == 1 ? string.Empty : "s")} · {stats.RoundTripMs:F0} ms ";
        }

        return status + Rule;
    }

    /// <summary>Ctrl+C: the log as it is shown (search filter and debug toggle applied), with
    /// its level markers, onto the clipboard.</summary>
    private void CopyVisibleLog(IApplication app)
    {
        List<string> lines = [];
        foreach (string line in _transcript.Lines)
        {
            Tone tone = TranscriptTone.Classify(line);
            if ((tone == Tone.Diagnostic && !_showDebug)
                || (_searchFilter.Length > 0 && !line.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            lines.Add(MarkerFor(tone) + line);
        }

        bool copied = app.Clipboard?.TrySetClipboardData(string.Join(Environment.NewLine, lines)) ?? false;

        // A passing notice under the input, not a log line: the copy is feedback about the
        // window, not an event in the session.
        Flash(copied
            ? $"✓ copied {lines.Count} line(s) to the clipboard{(_searchFilter.Length > 0 ? $" (filter '{_searchFilter}')" : string.Empty)}"
            : "✗ could not reach the clipboard - the full log is in the --log file",
            copied ? ToneGood : ToneBad);
    }

    private static readonly System.Text.RegularExpressions.Regex ModuleToken = new(
        @"\b(?:[a-z]{3,4}_?m\d{2}[a-z]{0,2}|stunt_\d{2})\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>"danm14aa" -> "danm14aa (Dantooine - Jedi Enclave Courtyard)" wherever a
    /// module code appears in a line, so the log says where things happened in words.</summary>
    private string WithPlaceNames(string line)
    {
        if (dispatcher is not { } commands || line.Length == 0)
        {
            return line;
        }

        HashSet<string>? done = null;
        return ModuleToken.Replace(line, m =>
        {
            done ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string name = commands.ModuleName(m.Value);
            return done.Add(m.Value) && name.Length > 0 && !name.Equals(m.Value, StringComparison.OrdinalIgnoreCase)
                && !line.Contains(name, StringComparison.OrdinalIgnoreCase)
                ? $"{m.Value} ({name})"
                : m.Value;
        });
    }

    /// <summary>The terminal window's title: who this agent is for, so two agent windows on
    /// one machine can be told apart at a glance (both used to read just "pwsh.exe").</summary>
    private string BuildTitle()
    {
        string name = manager.CurrentSettings?.PlayerName is { Length: > 0 } n ? n : "co-op";
        string role = manager.Agent is { } agent
            ? agent.Session.IsEnemyAuthority ? "host" : "joiner"
            : "not connected";
        return $"KOTOR co-op - {name} ({role})";
    }

    private void BeginSearch()
    {
        if (!_searchMode)
        {
            _stashedCommand = _input.Buffer;
            _searchMode = true;
        }

        ReplaceInputLine(_searchFilter);
        if (_promptLabel is { } prompt)
        {
            prompt.Text = "/ ";
        }

        RefreshFooter();
    }

    /// <summary>Keys while searching: edit the filter live; Enter keeps it, Esc drops it.</summary>
    private void OnSearchKeyDown(Key key)
    {
        key.Handled = true;
        if (key == Key.Enter || key == Key.Esc)
        {
            if (key == Key.Esc)
            {
                SetSearchFilter(string.Empty);
            }

            _searchMode = false;
            ReplaceInputLine(_stashedCommand);
            _stashedCommand = string.Empty;
            if (_promptLabel is { } prompt)
            {
                prompt.Text = "> ";
            }

            RefreshFooter();
            return;
        }

        if (key == Key.Backspace)
        {
            _input.Backspace();
        }
        else if (key == Key.Delete)
        {
            _input.Delete();
        }
        else if (key == Key.CursorLeft)
        {
            _input.MoveLeft();
        }
        else if (key == Key.CursorRight)
        {
            _input.MoveRight();
        }
        else if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value is > 0 and <= char.MaxValue)
        {
            _input.TypeChar((char)key.AsRune.Value);
        }
        else
        {
            key.Handled = false;
            return;
        }

        SyncInputField();
        SetSearchFilter(_input.Buffer);
    }

    private void ReplaceInputLine(string text)
    {
        _input.ClearLine();
        foreach (char c in text)
        {
            _input.TypeChar(c);
        }

        SyncInputField();
    }

    private void SetSearchFilter(string filter)
    {
        _searchFilter = filter;
        _transcript.SetFollowing(true);
        RefreshTranscriptView();
    }

    private int CountMatches() =>
        _searchFilter.Length == 0
            ? _transcript.Count
            : _transcript.Lines.Count(l => l.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase));

    /// <summary>One row of <see cref="ShowItemPicker{T}"/>: what is shown, and the item behind it.</summary>
    private readonly record struct PickerRow<T>(string Label, T Item, int MaxQuantity);

    /// <summary>
    /// The shared F2/F3 picker: a search box on top (focused; typing filters the list live,
    /// Up/Down move the selection, Enter picks), the matching rows, and — when
    /// <paramref name="withQuantity"/> — a quantity box (blank = the whole stack). Only a
    /// positive accept (Enter, or the list's own Accept) acts; Esc/closing does nothing.
    /// The chosen action runs OFF the UI thread so the window never freezes on it.
    /// </summary>
    private void ShowItemPicker<T>(
        IApplication app, string title, IReadOnlyList<PickerRow<T>> rows, bool withQuantity, Action<T, int> onChoose)
    {
        List<PickerRow<T>> shown = [.. rows];

        Label searchLabel = new() { X = 0, Y = 0, Width = 8, Height = 1, Text = "Search: ", CanFocus = false };
        searchLabel.SetScheme(PromptScheme);
        TextField search = new() { X = 8, Y = 0, Width = Dim.Fill(), Height = 1, Text = string.Empty };
        search.SetScheme(TextScheme);

        ListView list = new()
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(withQuantity ? 2 : 1),
            CanFocus = true,
        };
        list.SetScheme(PickerScheme);

        void Reload()
        {
            string filter = search.Text ?? string.Empty;
            shown = rows.Where(r => filter.Length == 0 || r.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(
                shown.Count == 0 ? ["(nothing matches)"] : shown.Select(r => r.Label)));
            list.SelectedItem = 0;
        }

        Reload();
        search.TextChanged += (_, _) => Reload();

        TextField? quantity = null;
        Label? quantityLabel = null;
        if (withQuantity)
        {
            quantityLabel = new() { X = 0, Y = Pos.AnchorEnd(2), Width = 10, Height = 1, Text = "Quantity: ", CanFocus = false };
            quantityLabel.SetScheme(PromptScheme);
            quantity = new() { X = 10, Y = Pos.AnchorEnd(2), Width = 8, Height = 1, Text = string.Empty };
            quantity.SetScheme(TextScheme);
        }

        Label hint = new()
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Text = withQuantity
                ? "type to search   Up/Down choose   Tab to quantity (blank = whole stack)   Enter confirm   Esc cancel"
                : "type to search   Up/Down choose   Enter confirm   Esc cancel",
            CanFocus = false,
        };
        hint.SetScheme(RuleScheme);

        Dialog dialog = new() { Title = title };
        dialog.Width = Dim.Percent(80);
        dialog.Height = Dim.Percent(80);
        dialog.Add(searchLabel, search, list, hint);
        if (quantity is not null && quantityLabel is not null)
        {
            dialog.Add(quantityLabel, quantity);
        }

        bool accepted = false;
        void Accept()
        {
            accepted = true;
            dialog.RequestStop();
        }

        void MoveSelection(int delta)
        {
            if (shown.Count == 0)
            {
                return;
            }

            int current = list.SelectedItem ?? 0;
            list.SelectedItem = Math.Clamp(current + delta, 0, shown.Count - 1);
        }

        search.KeyDown += (_, key) =>
        {
            if (key == Key.CursorDown) { key.Handled = true; MoveSelection(1); }
            else if (key == Key.CursorUp) { key.Handled = true; MoveSelection(-1); }
            else if (key == Key.PageDown) { key.Handled = true; MoveSelection(10); }
            else if (key == Key.PageUp) { key.Handled = true; MoveSelection(-10); }
            else if (key == Key.Enter) { key.Handled = true; Accept(); }
            else if (key == Key.Tab && quantity is not null)
            {
                // Straight to the quantity box: default focus order would land on the list,
                // where typed digits went nowhere (live 2026-09-26: "2" was ignored, the
                // whole stack of 5 dropped).
                key.Handled = true;
                quantity.SetFocus();
            }
        };
        if (quantity is not null)
        {
            quantity.KeyDown += (_, key) =>
            {
                if (key == Key.Enter) { key.Handled = true; Accept(); }
                else if (key == Key.Tab || key == Key.Tab.WithShift) { key.Handled = true; search.SetFocus(); }
                else if (key == Key.CursorDown) { key.Handled = true; MoveSelection(1); }
                else if (key == Key.CursorUp) { key.Handled = true; MoveSelection(-1); }
            };
        }

        list.Accepting += (_, _) => Accept();
        dialog.KeyDown += (_, key) =>
        {
            if (key == Key.Esc || key == Key.F2 || key == Key.F3)
            {
                key.Handled = true;
                dialog.RequestStop();
            }
        };

        search.SetFocus();
        RunDialog(app, dialog);

        if (!accepted || list.SelectedItem is not int index || index < 0 || index >= shown.Count)
        {
            return;
        }

        PickerRow<T> chosen = shown[index];
        int count = 0;
        if (quantity is not null && int.TryParse((quantity.Text ?? string.Empty).Trim(), out int typed) && typed > 0)
        {
            count = Math.Min(typed, Math.Max(1, chosen.MaxQuantity));
        }

        _ = Task.Run(() =>
        {
            try
            {
                onChoose(chosen.Item, count);
            }
            catch (Exception ex)
            {
                Note($"picker: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// F2: pick one of YOUR OWN backpack items (or part of a stack) and drop it for your
    /// partner. It used to cycle three hardcoded test blueprints (a fresh lightsaber, pistol or
    /// medpac each press, never anything the player owned, hence "no identity"). Opens from
    /// <see cref="_droppableCache"/>, refreshed in the background, so it is instant.
    /// </summary>
    private void ShowDropPicker(IApplication app)
    {
        if (dispatcher is null)
        {
            _lastDropNote = "drop: no command dispatcher wired to this dashboard";
            return;
        }

        IReadOnlyList<DroppableItem> items = _droppableCache ?? dispatcher.ListDroppableItems(fresh: false);
        if (items.Count == 0)
        {
            _lastDropNote = "drop: nothing in your backpack you can drop (equipped gear and quest items stay put)";
            return;
        }

        List<PickerRow<DroppableItem>> rows = items
            .Select(i => new PickerRow<DroppableItem>($"{i.Label,-40} {i.Resref}", i, i.Identity.StackSize))
            .ToList();

        CoopCommandDispatcher commands = dispatcher;
        ShowItemPicker(app, "Drop one of your items for your partner", rows, withQuantity: true, (item, count) =>
        {
            Note($"> drop 0x{item.ObjectId:X}{(count > 0 ? $" {count}" : string.Empty)}   ({item.Label})");
            commands.Post(() => commands.DropOwn(item, count));
            _droppableCacheAtMs = 0; // refresh on the next tick: the backpack just changed
        });
    }

    /// <summary>
    /// F3: pick one of the shared items on the ground in your module, nearest first. It used
    /// to take whichever ground item happened to be first in the list, wherever it was.
    /// </summary>
    private void ShowPickupPicker(IApplication app)
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

        PlayerState local = agent.LastLocalState;
        List<GroundItem> ground = agent.Session.Items.Snapshot()
            .Where(g => local.Module.Length == 0 || g.Module.Equals(local.Module, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Position.DistanceTo(local.Position))
            .ToList();
        if (ground.Count == 0)
        {
            _lastDropNote = "pickup: nothing on the ground here";
            return;
        }

        List<PickerRow<GroundItem>> rows = ground.Select(g =>
        {
            string name = dispatcher?.ItemName(g.Resref) ?? g.Resref;
            string stack = g.Identity.IsKnown && g.Identity.StackSize > 1 ? $" x{g.Identity.StackSize}" : string.Empty;
            return new PickerRow<GroundItem>(
                $"{name + stack,-40} {g.Position.DistanceTo(local.Position),5:F1} m   {g.Resref}", g, 1);
        }).ToList();

        ShowItemPicker(app, "Pick up an item from the ground", rows, withQuantity: false, (item, _) =>
        {
            bool requested;
            lock (kotor.Sync)
            {
                requested = kotor.TryRequestPickUp(item.ItemId);
            }

            _lastDropNote = requested
                ? $"picking up {dispatcher?.ItemName(item.Resref) ?? item.Resref}"
                : $"pickup refused: 0x{item.ItemId:X} is not on this instance's floor";
        });
    }

    private void ShowDetailsDialog(IApplication app)
    {
        ColorTextView details = new()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ReadOnly = true,

            // No word wrap, and the rows load once the view has a size: loading coloured rows
            // into a wrapping view before layout left the dialog empty and its loop spinning a
            // core, deaf to F1/Esc (live 2026-09-26). Details lines are short anyway.
            WordWrap = false,
            ScrollBars = true,

            // Focusable, so the dialog owns the keyboard while open: with nothing focusable
            // in it, keys fell through to the input line behind it ("od" typed there) and a
            // second F1 did not reach the dialog.
            CanFocus = true,
        };
        details.SetScheme(TextScheme);
        List<List<Cell>> detailRows = BuildDetailsLines().Select(DetailsRow).ToList();
        details.Initialized += (_, _) => details.LoadRows(detailRows);

        Label closeHint = new()
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Text = "F1 or Esc to close   PgUp/PgDn scroll",
            CanFocus = false,
        };
        closeHint.SetScheme(RuleScheme);

        Dialog dialog = new() { Title = "Details" };
        dialog.Width = Dim.Percent(90);
        dialog.Height = Dim.Percent(90);
        dialog.Add(details, closeHint);

        dialog.KeyDown += (_, key) =>
        {
            if (key == Key.F1 || key == Key.Esc)
            {
                key.Handled = true;
                dialog.RequestStop();
            }
            else if (key == Key.PageDown || key == Key.PageUp)
            {
                key.Handled = true;
                int page = Math.Max(details.Viewport.Height - 1, 1) * (key == Key.PageDown ? 1 : -1);
                int maxTop = Math.Max(details.Lines - details.Viewport.Height, 0);
                details.Viewport = details.Viewport with { Y = Math.Clamp(details.Viewport.Y + page, 0, maxTop) };
            }
        };

        details.SetFocus();
        RunDialog(app, dialog);
    }

    /// <summary>
    /// The sticky header: role/link/peers/rtt, this player, other players, and a one-line
    /// memory/pump summary — never scrolls with the transcript. Everything the OLD readout
    /// spelled out at length is still available; press F1 for the details dialog.
    /// </summary>
    private List<string> BuildHeaderLines()
    {
        CoopAgent? agent = manager.Agent;
        List<string> lines = [];

        if (agent is null)
        {
            lines.Add($"KOTOR CO-OP — not connected — session {manager.Phase.ToString().ToLowerInvariant()} - {manager.Detail}");
            lines.Add($"attached {bridge.Description}  —  type connect/host below, or press ~ in-game");
            lines.Add(string.Empty);
            lines.Add(string.Empty);
            return lines;
        }

        SessionStats stats = agent.Session.GetStats();
        PlayerState local = agent.LastLocalState;
        IReadOnlyList<RemotePeer> peers = agent.LastPeers;

        string role = agent.Session.IsEnemyAuthority ? "HOST" : "CLIENT";
        lines.Add(
            $"KOTOR CO-OP — {role}  link {stats.Status}  peers {stats.PeerCount}  rtt {stats.RoundTripMs:F0}ms"
            + (stats.PacketsDropped > 0 ? $"  {stats.PacketsDropped} MALFORMED" : string.Empty));

        lines.Add(
            $"you: {(local.Module.Length > 0 ? local.Module : "(unknown)")}"
            + $"  hp {DescribeHealth(local.Health, local.MaxHealth)}  {DescribeState(local)}");

        lines.Add(peers.Count == 0
            ? "players: nobody else connected yet"
            : "players: " + string.Join(", ", peers.Select(peer =>
                $"{peer.Name} ({(peer.IsInModule(local.Module) ? "here" : peer.State.Module)}, "
                + $"{DescribeHealth(peer.State.Health, peer.State.MaxHealth)})")));

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

        lines.Add($"memory {memory}  pump {agent.PumpAverageMs:F1}ms avg ({agent.PumpPeakMs:F1}ms peak){compatFlag}");

        return lines;
    }

    /// <summary>F1's details dialog content — the full readout the header no longer has room
    /// for. Plain text (no per-line colouring): reliability over polish for this pass, after
    /// a hand-rolled colour-by-meaning renderer was part of what made the PREVIOUS dashboard
    /// unusable.</summary>
    private List<string> BuildDetailsLines()
    {
        // What the sticky header used to show sits at the top of the details now.
        List<string> rows = [.. BuildHeaderLines().Where(l => l.Length > 0), string.Empty];
        CoopAgent? agent = manager.Agent;

        rows.Add($"attached   {bridge.Description}");

        if (agent is null)
        {
            rows.Add(string.Empty);
            rows.Add($"session    {manager.Phase.ToString().ToLowerInvariant()} - {manager.Detail}");
            rows.Add(string.Empty);
            rows.Add("not connected. In-game: press ~ and type  connect <ip> [password] | reconnect");
            rows.Add("Or type it in the command line below - it reaches the same dispatcher.");
            return rows;
        }

        SessionStats stats = agent.Session.GetStats();
        PlayerState local = agent.LastLocalState;
        IReadOnlyList<RemotePeer> peers = agent.LastPeers;

        string role = agent.Session.IsEnemyAuthority
            ? "HOST - this game owns the enemies, doors and companions"
            : "CLIENT - this game mirrors the host's world";

        rows.Add(string.Empty);
        rows.Add("NETWORK");
        rows.Add($" role       {role}");
        rows.Add($" link       {stats.Status}");
        rows.Add($" players    {stats.PeerCount} other connected      round trip {stats.RoundTripMs:F0} ms");
        rows.Add($" traffic    {stats.PacketsSent} sent / {stats.PacketsReceived} received      "
            + (stats.PacketsDropped > 0 ? $"{stats.PacketsDropped} MALFORMED" : "none malformed"));

        CompatibilityVerdict? verdict = agent.Session.Compatibility;
        if (verdict is not null)
        {
            SessionCapabilities off = SessionCapabilities.All & ~verdict.Allowed;
            if (off != SessionCapabilities.None || verdict.Warnings.Count > 0)
            {
                rows.Add(string.Empty);
                rows.Add($"content    {verdict.Summary}");

                if (off != SessionCapabilities.None)
                {
                    rows.Add($"           OFF: {off.ToString().Replace(", ", " ")}  (a mod/save difference makes these unsafe)");
                }

                for (int i = 0; i < verdict.Warnings.Count && i < 3; i++)
                {
                    rows.Add($"           {verdict.Warnings[i]}");
                }
            }
        }

        string readStatus = agent.LastReadSucceeded ? "ok" : $"FAILING ({agent.ReadFailureCount} failures)";
        string writeStatus = agent.Bridge.CanWriteRemotes
            ? agent.LastWriteSucceeded ? "ok" : "FAILING"
            : "not supported by this profile (read-only)";

        rows.Add(string.Empty);
        rows.Add("GAME MEMORY");
        rows.Add($" reading    {readStatus}          writing    {writeStatus}");
        rows.Add($" pump cost  {agent.PumpAverageMs:F1} ms average, {agent.PumpPeakMs:F1} ms peak   (budget {agent.PumpBudgetMs:F0} ms)");

        rows.Add(string.Empty);
        rows.Add("THIS PLAYER");
        rows.Add($" area       {(local.Module.Length > 0 ? local.Module : "(unknown)")}"
            + $"      at {local.Position}  facing {local.Facing:F2}");
        rows.Add($" health     {DescribeHealth(local.Health, local.MaxHealth)}");
        rows.Add($" state      {DescribeState(local)}");

        rows.Add(string.Empty);
        rows.Add($"OTHER PLAYERS ({peers.Count})");
        for (int i = 0; i < Math.Max(peers.Count, 1); i++)
        {
            if (i < peers.Count)
            {
                RemotePeer peer = peers[i];
                bool here = peer.IsInModule(local.Module);
                string where = here ? "here with you" : $"in {peer.State.Module}";

                string state = DescribeState(peer.State);
                if (here && local.IsPaused && !peer.State.IsPaused)
                {
                    state = state.StartsWith("exploring", StringComparison.Ordinal)
                        ? "PAUSED (mirroring yours)"
                        : $"{state}, PAUSED (mirroring yours)";
                }

                rows.Add($"   {peer.Name,-12} {where,-18} {DescribeHealth(peer.State.Health, peer.State.MaxHealth),-20}" + state);
            }
            else
            {
                rows.Add("   (nobody else connected yet)");
            }
        }

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

        rows.Add(string.Empty);
        rows.Add("SHARED WORLD");
        rows.Add($" combat     {DescribeSharedCombat(local, peers)}");
        rows.Add($" enemies    {enemies.Count} tracked - {alive} alive, {dead} dead"
            + (agent.Session.IsEnemyAuthority ? "   (published by this game)" : "   (received from the host)"));

        DialogueState convo = agent.Session.GetDialogue();
        string convoWho = convo.OwnerPeerId == agent.Session.LocalPeerId
            ? "you are"
            : $"peer {convo.OwnerPeerId} is";
        rows.Add($" talking    {(convo.Active ? $"{convoWho} talking" + (convo.SpeakerName.Length > 0 ? $" to {convo.SpeakerName}" : string.Empty) : "nobody is talking")}");

        List<GroundItem> items = agent.Session.Items.Snapshot();
        string itemNames = items.Count == 0
            ? "none"
            : string.Join(", ", items.Take(3).Select(item => item.Resref)) + (items.Count > 3 ? ", ..." : string.Empty);
        rows.Add($" items      {items.Count} on the ground: {itemNames}");

        if (_lastDropNoteValue.Length > 0)
        {
            rows.Add($" last drop  {_lastDropNoteValue}");
        }


        IReadOnlyList<string> switches = CoopConfig.DeveloperSwitches;
        if (switches.Count > 0)
        {
            rows.Add(string.Empty);
            rows.Add($"coop.ini developer switches ({switches.Count}) - testing options, not player settings:");
            rows.AddRange(switches.Select(sw => "  " + sw));
        }
        return rows;
    }

    /// <summary>The key legend, with the "N new lines below" marker folded in ahead of it
    /// while the transcript is scrolled up and something new arrived — kept in the footer
    /// rather than a dedicated row so the layout never has to resize around whether the
    /// marker is showing.</summary>
    private string BuildFooter()
    {
        string legend = _showInputLine
            ? $"F1 details  F2 drop  F3 pick up  Ctrl+F find  Ctrl+C copy  Ctrl+O open log  F5 debug {(_showDebug ? "on" : $"off ({_hiddenDebugLines} hidden)")}  Ctrl+Q quit"
            : "(no command line: input is redirected)  F1 details  F2 drop  F3 pick up  Ctrl+C to stop";

        if (_searchMode)
        {
            return $"find: type to filter the transcript ({CountMatches()} matching line(s))  Enter keep filter  Esc cancel";
        }

        if (_searchFilter.Length > 0)
        {
            return $"filter '{_searchFilter}': {CountMatches()} line(s)  Ctrl+F change  Esc (on an empty line) clear   F2 drop  F3 pick up";
        }

        if (!_transcript.IsFollowing && _transcript.NewSinceScrolled > 0)
        {
            return $"↓ {_transcript.NewSinceScrolled} new line(s) below (End to jump down)   {legend}";
        }

        return legend;
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
}
