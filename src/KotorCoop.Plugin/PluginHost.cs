using System.Runtime.CompilerServices;
using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Runs inside swkotor.exe.
///
/// Two threads, with a strict division that matters:
///   - the WORKER thread only touches the shared block (heartbeat, diagnostics). It must
///     never call an engine function; the engine is not reentrant.
///   - the FRAME HOOK runs on the game's own thread and is the only place engine calls
///     happen: spawning phantoms and stepping them along.
///
/// Split into partials by concern: this file owns process-wide STATE, the EnsureStarted
/// entry point and logging. PluginHost.Startup.cs owns the hook-install sequence,
/// PluginHost.Controllers.cs owns construction of the controller set, PluginHost.Frame.cs
/// owns the two per-frame schedules (MenuFrame/OnFrame), and PluginHost.SessionLifecycle.cs
/// owns what happens when a co-op session connects/releases and when the world changes
/// under every controller at once.
/// </summary>
internal static unsafe partial class PluginHost
{
    private static readonly object Gate = new();
    private static bool _started;
    private static string _logPath = "kotorcoop-plugin.log";

    /// <summary>Guards every write AND every rotate against "kotorcoop-*.log": <see cref="Log"/>
    /// does a <see cref="File.AppendAllText(string,string)"/>, <see cref="RotateLogsIfNeeded"/>
    /// and <see cref="RotateLogsIfNeededMidSession"/> do a <see cref="File.Move(string,string,bool)"/>
    /// on the same paths, and interleaving an append with a rename could either lose the line
    /// (written to the path just as it is renamed out from under it) or resurrect it into the
    /// freshly-rotated file. Startup rotation takes this lock too even though nothing can be
    /// contending for it yet, so the discipline never has an exception.</summary>
    private static readonly object LogFileGate = new();

    /// <summary>Set by <see cref="RotateLogsIfNeededMidSession"/>; 0 until the worker loop's
    /// first pass. See <see cref="LogRotation.ShouldCheckPeriodically"/>.</summary>
    private static long _lastPeriodicLogCheckMs;

    /// <summary>Per-subsystem logs are this + channel + ".log" (see <see cref="Log"/>).</summary>
    private static string _channelPathPrefix = "kotorcoop-";
    private static SharedBlock* _block;
    private static SharedBlockView? _view;
    private static PhantomController? _controller;
    private static ItemController? _items;
    private static FocusKeeper? _focus;
    private static CombatController? _combat;
    private static LivenessReconcileController? _liveness;
    private static EnemyPresenceController? _enemyPresence;
    private static PartyPresenceController? _partyPresence;
    private static PartyFollowController? _partyFollow;
    private static IncomingDamageController? _incomingDamage;
    private static PeerEffectController? _peerEffects;

    private static PeerDamageController? _peerDamage;
    private static TestDriveController? _testDrive;
    private static CheatController? _cheats;
    private static DoorController? _doors;
    private static PlaceableController? _placeables;
    private static LootController? _loot;
    private static TravelController? _travel;
    private static PauseController? _pause;
    private static CombatStateController? _combatState;
    private static PauseKeyController? _pauseKey;
    private static JournalController? _journal;
    private static MapMarkerController? _mapMarkers;
    private static MapRevealController? _mapReveal;
    private static PortraitController? _portraits;
    private static BarkController? _barks;
    private static InteractionLockController? _interactionLocks;
    private static PartyRosterController? _partyRoster;
    private static ControlReturnController? _controlReturn;
    private static MenuLoadController? _menuLoad;
    private static ConnectMenuController? _connectMenu;
    private static ChatInputController? _chatInput;
    private static PasswordPromptController? _passwordPrompt;
    private static ServerBrowserController? _browser;
    private static NativeBrowserController? _nativeBrowser;
    private static DeathController? _death;
    private static SpectateController? _spectate;
    private static ChatOverlayController? _chatOverlay;
    private static ConsoleAssistController? _consoleAssist;
    private static DialogueSpectateController? _dialogueSpectate;
    private static EncounterController? _encounters;
    private static TriggerRelayController? _triggerRelay;
    private static MidModuleMovieController? _movieRelay;
    private static AgentLivenessController? _agentLiveness;
    private static Thread? _worker;
    private static volatile bool _running = true;

    /// <summary>The swap-hook frame count last seen from MenuFrame, and how many MainLoop
    /// ticks it has sat unchanged — the "is the swap hook dormant?" probe the seamless
    /// hold's fallback heartbeat uses.</summary>
    private static long _heldFrameSample;
    private static int _heldStallTicks;

    public static nint BlockAddress => (nint)_block;

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        try
        {
            string directory = NativeMethods.GetGameDirectory();
            _logPath = Path.Combine(directory, "kotorcoop-plugin.log");
            _channelPathPrefix = Path.Combine(directory, "kotorcoop-");

            // Before any controller is constructed: several read their settings from coop.ini
            // in their field initialisers.
            PluginConfig.UseDirectory(directory);

            // Before the first Log() call, so a session that rotates does not write its own
            // banner and then immediately move it. See LogRotation for why: AppendAllText
            // never truncates, and a live test rig's combined log reached 170 MB across 158
            // sessions before this existed.
            RotateLogsIfNeeded(directory);

            Log($"=== KotorCoop plugin loaded into pid {NativeMethods.GetCurrentProcessId()} ===");

            // So a subscriber exception PluginEvents catches on a later raise still reaches
            // the log instead of being dropped silently. Wired before a single hook or
            // controller exists, same discipline as CrashProbe just below: nothing here can
            // raise an event yet, but nothing after this point should be able to raise one
            // without a working log sink either.
            PluginEvents.LogFailure = Log;

            // FIRST, before a single hook or controller exists. A fault during startup is
            // exactly as fatal as one during play, and the probe is what turns "the game
            // vanished" into a line naming the exception.
            InstallCrashProbe();

            // The in-game ~ console needs the engine's dev console, which is gated by
            // EnableCheats in swkotor.ini. Enabling it is the game's own sanctioned surface.
            EnsureCheatsEnabled(directory);

            if (!AllocateBlock())
            {
                Log("FATAL: could not allocate the shared block");
                return;
            }

            ConstructControllers(directory);

            InstallHooks();

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "KotorCoop plugin",
            };

            _worker.Start();
            Log("worker thread started");

            // Last, once everything the agent looks for exists: start the agent for this
            // instance so installing the plugin is the whole installation. Opt-out in
            // coop.ini; a missing agent executable is simply skipped.
            AgentLauncher.EnsureRunning(directory);
        }
        catch (Exception ex)
        {
            // Never let plugin failure take the game down: the export must still forward.
            Log($"FATAL during startup: {ex}");
        }
    }

    private static bool AllocateBlock()
    {
        // VirtualAlloc rather than a managed allocation: the block must sit at a fixed
        // address in a MEM_PRIVATE committed region for the whole session, because the
        // external agent locates it by scanning that memory for the signature.
        nint memory = NativeMethods.VirtualAlloc(
            0,
            SharedBlock.TotalSize,
            NativeMethods.MemCommit | NativeMethods.MemReserve,
            NativeMethods.PageReadWrite);

        if (memory == 0)
        {
            return false;
        }

        _block = (SharedBlock*)memory;
        Unsafe.InitBlockUnaligned(_block, 0, SharedBlock.TotalSize);

        _block->Magic = SharedBlock.MagicValue;
        // ⚠ ONE NUMBER IN TWO FILES. KotorGameBridge.PluginSignature embeds this byte in the
        // pattern the agent scans for, so bumping only one end reads as "plugin shared block
        // not found" — which looks like a broken install rather than a version skew.
        // 37 = the load-screen texture in the browser details array (SharedBlock, version 37).
        // 38 = the engine-resolved player pointer.
        // 39 = per-instance item identity in the item slot's tail (SharedBlock, version 39).
        // 41 = the four `setitem` identity fields at the cheat region's tail (version 41).
        // 42 = the transition-cinematic channel, and the block grown to 0x3A00 to hold it
        //      (SharedBlock, version 42) — so both players can see the same movie.
        // 43 = the agent-written liveness heartbeat (SharedBlock.OffsetAgentHeartbeat).
        // 44 = the peer-effect channel (stun/paralyze/knockdown/slow/frightened/sleep/choke/
        //      Force push/whirlwind forwarded to a peer's phantom), block grown to 0x3C00 with
        //      its own ring at 0x3A00 — see docs/research/effect-sync.md.
        // 45 = the creature-liveness reconciliation channel (every creature the enemy owner's
        //      engine knows about, not just the 32-slot combat census), block grown to 0x4000
        //      with its own 96-entry array at 0x3C00 — see docs/research/creature-liveness.md.
        // ItemHarnessTests.TheBlockVersionIsTheSameNumberInBothHalves now fails the build on
        // a half-bump; it did not exist for 41.
        // 46 = the generic trigger relay region at 0x3B88 — see docs/research/trigger-relay.md.
        // 47 = the transition-announce channel + client-door-refusal arm at 0x38E4..0x3928 —
        //      see docs/research/transition-smoothness.md.
        // 48 = the mid-module movie relay region at 0x392C..0x396C (the DETECTION half
        //      docs/research/trigger-relay.md §6 shipped without a hook for) — see
        //      docs/research/cinematics.md's 2026-08-22 section.
        // 49 = the world-load edge at 0x396C (roadmap item 14 phase 2's save-vs-load
        //      detection primitive) — see docs/research/session-save.md's Phase 2 section.
        // 50 = container-lock target ids at 0x3F08..0x3F1C, InteractionKind.Container's own
        //      object-id key parallel to the existing tag-keyed interaction lock region — see
        //      docs/research/container-sync.md.
        // 51 = the local/remote dialogue-forced bit at 0x3F1C..0x3F24 — whether this
        //      conversation is one the engine itself forces regardless of range, distinct
        //      from a player's own click — see docs/research/dialogue-sync.md.
        // 52 = the mid-session-load-request flag at 0x3F24 (the `loadgame` verb) — tells
        //      MenuLoadController to replay CSWGuiSaveLoad::LoadGame's bFromMainMenu==0
        //      branch (DestroyServer + UnloadModule before CreateServer, guiManager from
        //      [clientInternal+0x274]) instead of acking-and-mooting when a server already
        //      exists — see docs/research/session-save.md's dated entry.
        // 53 = party-roster body identity at 0x3F28..0x3F70 (two nine-slot object-id arrays,
        //      local and host) — lets a peer recognise its own already-placed, GIT-identical
        //      copy of a just-recruited companion and reuse it instead of spawning a duplicate
        //      body next to it. See docs/research/dialogue-consequences.md's 2026-08-24 "stale
        //      mirrored body" entry. NEEDS-LIVE.
        _block->Version = 53;

        // A guard dword after the version. Magic plus version alone was not selective
        // enough: a scan once matched something else at a lower address and the agent
        // silently anchored on it. 16 bytes makes a coincidental match implausible.
        *(uint*)((byte*)_block + 0x0C) = SharedBlock.GuardValue;

        // ⚠ The dialogue nodes start at -1, not at the zero the block was just filled with.
        // Zero is a VALID entry index — the first line of a conversation — so a block that has
        // never held one would otherwise be indistinguishable from one sitting at the top of a
        // dialogue. Caught by the node probe on the first live run: an instance that had never
        // talked to anybody reported node 0 with an empty partner tag.
        *(int*)((byte*)_block + SharedBlock.OffsetLocalDialogueNode) = -1;
        *(int*)((byte*)_block + SharedBlock.OffsetRemoteDialogueNode) = -1;

        WriteModuleName("kotor");

        Log($"shared block at 0x{memory:X} ({SharedBlock.TotalSize} bytes)");
        return true;
    }

    private static void WriteModuleName(string value)
    {
        byte* field = (byte*)_block + 0x20;
        Unsafe.InitBlockUnaligned(field, 0, SharedBlock.ModuleNameLength);

        int count = Math.Min(value.Length, SharedBlock.ModuleNameLength - 1);
        for (int i = 0; i < count; i++)
        {
            char c = value[i];
            field[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }
    }

    /// <summary>
    /// Ensures <c>[Game Options] EnableCheats=1</c> in swkotor.ini so the grave/tilde key
    /// opens the engine's developer console (which the co-op ~ overlay reads). Idempotent and
    /// conservative: it touches only that one key, and does nothing if the ini is absent
    /// (the game recreates it with cheats off, but never silently — the log says so).
    /// </summary>
    private static void EnsureCheatsEnabled(string directory)
    {
        try
        {
            string path = Path.Combine(directory, "swkotor.ini");
            if (!File.Exists(path))
            {
                Log("swkotor.ini not found; set [Game Options] EnableCheats=1 to use the ~ console");
                return;
            }

            List<string> lines = [.. File.ReadAllLines(path)];
            int section = -1;
            int keyLine = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    if (trimmed.Equals("[Game Options]", StringComparison.OrdinalIgnoreCase))
                    {
                        section = i;
                    }
                    else if (section >= 0 && keyLine < 0)
                    {
                        break; // left the section without finding the key
                    }
                }
                else if (section >= 0 && trimmed.StartsWith("EnableCheats", StringComparison.OrdinalIgnoreCase))
                {
                    keyLine = i;
                    break;
                }
            }

            if (keyLine >= 0)
            {
                if (lines[keyLine].Trim().EndsWith('1'))
                {
                    return; // already enabled
                }

                lines[keyLine] = "EnableCheats=1";
            }
            else if (section >= 0)
            {
                lines.Insert(section + 1, "EnableCheats=1");
            }
            else
            {
                lines.Add("[Game Options]");
                lines.Add("EnableCheats=1");
            }

            File.WriteAllLines(path, lines);
            Log("enabled the developer console in swkotor.ini ([Game Options] EnableCheats=1)");
        }
        catch (Exception ex)
        {
            Log($"could not enable cheats in swkotor.ini: {ex.Message}");
        }
    }

    private static void WorkerLoop()
    {
        uint lastRemoteCount = 0;
        uint lastPhantomCount = 0;
        long lastFrames = 0;
        int quietTicks = 0;

        while (_running)
        {
            try
            {
                _block->Heartbeat++;

                // The crash probe's handler is machine code and cannot format a log line
                // itself (see CrashProbe: the managed version of it WAS the crash). It leaves
                // a record behind; this is where that record becomes words.
                CrashProbe.DrainReports();

                // Throttled internally to ~once per LogRotation.PeriodicCheckIntervalMs; a
                // no-op call on every other pass through this ~16ms loop.
                RotateLogsIfNeededMidSession();

                if (_view is { } view)
                {
                    uint remotes = view.RemoteCount;
                    if (remotes != lastRemoteCount)
                    {
                        lastRemoteCount = remotes;
                        Log($"agent reports {remotes} remote peer(s)");
                    }

                    uint phantoms = view.PhantomCount;
                    if (phantoms != lastPhantomCount)
                    {
                        lastPhantomCount = phantoms;
                        Log($"{phantoms} phantom(s) alive");
                    }

                    // Report a stalled frame hook once rather than every tick. If the hook
                    // is installed but never fires, everything else silently does nothing,
                    // and that is worth saying out loud.
                    //
                    // This is the ONE thing still moving during a cinematic: a movie blocks
                    // the main thread inside WaitForMovie, so the swap never happens and this
                    // warning is the expected — not anomalous — reading for its whole
                    // duration. The line says so, because a reader who does not know that
                    // will spend the length of a movie chasing a hook that is fine.
                    // See docs/research/cinematics.md and CinematicWatch.
                    long frames = FrameHook.FrameCount;
                    if (FrameHook.IsInstalled && frames == lastFrames)
                    {
                        if (++quietTicks == 120)
                        {
                            Log("WARNING: frame hook installed but has not fired for ~2s "
                                + "(normal while a movie or a module load holds the main thread)");
                        }
                    }
                    else
                    {
                        quietTicks = 0;
                        lastFrames = frames;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"worker error: {ex.Message}");
            }

            Thread.Sleep(16);
        }

        Log("worker stopping");
    }

    /// <summary>
    /// The subsystems that get their own log file.
    ///
    /// One 15 MB file with every subsystem interleaved is unreadable when you are chasing
    /// one of them, which is the whole complaint. Rather than retro-fit a channel argument
    /// onto ~200 call sites, this uses the prefix convention the messages ALREADY follow
    /// ("dialogue: ...", "pause: ...", "combat: ..."), so a line files itself.
    ///
    /// The list is explicit on purpose: a free-form prefix would spawn a new file for every
    /// typo and every message that merely happens to contain a colon.
    /// </summary>
    private static readonly string[] LogChannels =
    [
        "dialogue", "pause", "combat", "bark", "phantom", "item", "loot", "door",
        "journal", "minimap", "death", "transition", "epoch", "party", "enemy",
        "chat", "travel", "portrait", "encounter", "hook", "agent", "session",
        "cinematic",
    ];

    /// <summary>Prefixes that name the same subsystem as one of the channels above.</summary>
    private static string CanonicalChannel(string prefix) => prefix switch
    {
        "death handling" => "death",
        "phantoms" => "phantom",
        "barks" => "bark",
        "items" => "item",
        "doors" => "door",
        "frame hook" or "menu frame hook" or "console interp" => "hook",
        _ => prefix,
    };

    /// <summary>
    /// Files a line by its own prefix. Returns "general" for anything unprefixed — startup
    /// banners, fatals, and the handful of messages that never adopted the convention.
    /// </summary>
    private static string ChannelFor(string message)
    {
        int colon = message.IndexOf(':');

        // A timestamp or an address would otherwise look like a prefix; a real one is a
        // short lowercase word or two, and never contains a digit.
        if (colon <= 0 || colon > 20)
        {
            return "general";
        }

        string prefix = CanonicalChannel(message[..colon].ToLowerInvariant());

        return Array.IndexOf(LogChannels, prefix) >= 0 ? prefix : "general";
    }

    public static void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";

            // Same lock RotateLogsIfNeeded/RotateLogsIfNeededMidSession take around their
            // File.Move: without it, a rotate racing this append could lose the line or
            // write it into the file the rotate is in the middle of renaming away. See
            // LogFileGate's doc comment.
            lock (LogFileGate)
            {
                // The combined file stays the timeline: it is what you read when the
                // question is "what happened around 14:48", and what verify.ps1 and every
                // existing note grep. The split files are for reading ONE subsystem end to
                // end.
                File.AppendAllText(_logPath, line, Encoding.ASCII);
                File.AppendAllText(_channelPathPrefix + ChannelFor(message) + ".log", line, Encoding.ASCII);
            }
        }
        catch
        {
            // Logging must never throw into the game's call stack.
        }
    }

    /// <summary>
    /// Rotates every "kotorcoop-*.log" this process writes (the combined file and every
    /// channel split) that has grown past coop.ini's <c>log_max_mb</c> (default
    /// <see cref="LogRotation.DefaultCapMb"/>): the existing file becomes "*.1.log",
    /// overwriting whatever was there, so exactly one prior session's tail survives and the
    /// fresh session starts a clean file. A file at or under the cap is left alone —
    /// sessions keep concatenating into it until it next crosses the line.
    ///
    /// Called once, here, before <see cref="Log"/> is ever called — nothing else can be
    /// contending for <see cref="LogFileGate"/> yet, this just establishes the discipline the
    /// mid-session sweep (<see cref="RotateLogsIfNeededMidSession"/>) relies on for the rest
    /// of the session.
    /// </summary>
    private static void RotateLogsIfNeeded(string directory)
    {
        try
        {
            lock (LogFileGate)
            {
                RotateLogFilesOverCap(directory);
            }
        }
        catch
        {
            // Best-effort, same as Log() itself: a rotation failure must not stop the
            // plugin from starting, it just means this session's log grows a bit more.
        }
    }

    /// <summary>
    /// The periodic sibling of <see cref="RotateLogsIfNeeded"/>, called from
    /// <see cref="WorkerLoop"/> roughly every <see cref="LogRotation.PeriodicCheckIntervalMs"/>
    /// (see <see cref="LogRotation.ShouldCheckPeriodically"/>) so a session that runs long
    /// enough to cross <c>log_max_mb</c> on its own is capped before the NEXT process start
    /// rather than only then — a live test rig's combined log once reached 170 MB across 158
    /// sessions specifically because rotation only ran at startup.
    ///
    /// Safe to run mid-session, unlike a naive move: it takes the exact same
    /// <see cref="LogFileGate"/> lock <see cref="Log"/> takes around its
    /// <see cref="File.AppendAllText(string,string)"/> calls, so a rotate here can never
    /// interleave with an in-flight write to the same path. A tool tailing the file externally
    /// (tools/verify.ps1) can still observe the rename as a gap, but that risk is accepted in
    /// exchange for a session that no longer grows unbounded between restarts.
    /// </summary>
    private static void RotateLogsIfNeededMidSession()
    {
        long now = Environment.TickCount64;
        if (!LogRotation.ShouldCheckPeriodically(_lastPeriodicLogCheckMs, now, LogRotation.PeriodicCheckIntervalMs))
        {
            return;
        }

        _lastPeriodicLogCheckMs = now;

        try
        {
            string? directory = Path.GetDirectoryName(_logPath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            lock (LogFileGate)
            {
                RotateLogFilesOverCap(directory);
            }
        }
        catch
        {
            // Best-effort, same as Log() itself.
        }
    }

    /// <summary>
    /// The actual sweep shared by <see cref="RotateLogsIfNeeded"/> (once, at startup) and
    /// <see cref="RotateLogsIfNeededMidSession"/> (periodically, during play): every
    /// "kotorcoop-*.log" over cap becomes its "*.1.log" sibling, overwriting whatever was
    /// there, so exactly one prior generation's tail survives. Callers must already hold
    /// <see cref="LogFileGate"/>.
    ///
    /// Skips "kotorcoop-agent.log" on purpose — that file belongs to the agent process
    /// AgentLauncher spawns, and it rotates it itself right before opening it (see
    /// AgentLauncher.RotateAgentLogIfNeeded), not as part of this directory-wide sweep. It
    /// cannot be rotated mid-session by either side: see the comment on
    /// AgentLauncher.Launch's StartupInfo.StdOutput assignment.
    /// </summary>
    private static void RotateLogFilesOverCap(string directory)
    {
        long cap = LogRotation.CapBytes(PluginConfig.ReadInt("log_max_mb", LogRotation.DefaultCapMb));

        foreach (string path in Directory.EnumerateFiles(directory, "kotorcoop-*.log"))
        {
            string name = Path.GetFileName(path);
            if (name.EndsWith(".1.log", StringComparison.OrdinalIgnoreCase)
                || name.Equals("kotorcoop-agent.log", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FileInfo info = new(path);
            if (!info.Exists || !LogRotation.ShouldRotate(info.Length, cap))
            {
                continue;
            }

            File.Move(path, LogRotation.RotatedPath(path), overwrite: true);
        }
    }
}
