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
/// </summary>
internal static unsafe class PluginHost
{
    private static readonly object Gate = new();
    private static bool _started;
    private static string _logPath = "kotorcoop-plugin.log";
    private static SharedBlock* _block;
    private static SharedBlockView? _view;
    private static PhantomController? _controller;
    private static ItemController? _items;
    private static FocusKeeper? _focus;
    private static CombatController? _combat;
    private static DoorController? _doors;
    private static PlaceableController? _placeables;
    private static LootController? _loot;
    private static TravelController? _travel;
    private static PauseController? _pause;
    private static PauseKeyController? _pauseKey;
    private static JournalController? _journal;
    private static MapMarkerController? _mapMarkers;
    private static MenuLoadController? _menuLoad;
    private static ConnectMenuController? _connectMenu;
    private static ChatInputController? _chatInput;
    private static ServerBrowserController? _browser;
    private static NativeBrowserController? _nativeBrowser;
    private static DeathController? _death;
    private static ChatOverlayController? _chatOverlay;
    private static DialogueSpectateController? _dialogueSpectate;
    private static Thread? _worker;
    private static volatile bool _running = true;

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
            string directory = NativeMethods.GetModuleDirectory("dinput8.dll");
            _logPath = Path.Combine(directory, "kotorcoop-plugin.log");

            // Before any controller is constructed: several read their settings from coop.ini
            // in their field initialisers.
            PluginConfig.UseDirectory(directory);

            Log($"=== KotorCoop plugin loaded into pid {NativeMethods.GetCurrentProcessId()} ===");

            // The in-game ~ console needs the engine's dev console, which is gated by
            // EnableCheats in swkotor.ini. Enabling it is the game's own sanctioned surface.
            EnsureCheatsEnabled(directory);

            if (!AllocateBlock())
            {
                Log("FATAL: could not allocate the shared block");
                return;
            }

            _view = new SharedBlockView((byte*)_block);
            _controller = new PhantomController(_view);
            _items = new ItemController(_view);
            _focus = new FocusKeeper(_view);
            _combat = new CombatController(_view);
            _doors = new DoorController(_view);
            _placeables = new PlaceableController(_view);
            _loot = new LootController(_view);
            _travel = new TravelController(_view);
            _menuLoad = new MenuLoadController(_view);
            _connectMenu = new ConnectMenuController(_view);
            _chatInput = new ChatInputController();
            _browser = new ServerBrowserController(_view);
            _nativeBrowser = new NativeBrowserController(_view);
            _death = new DeathController(_view);
            _chatOverlay = new ChatOverlayController(_view);
            _dialogueSpectate = new DialogueSpectateController(_view);
            _pause = new PauseController(_view);
            _pauseKey = new PauseKeyController();
            _journal = new JournalController(_view);
            _mapMarkers = new MapMarkerController(_view);

            // Install the frame hook from here because DirectInput8Create - our caller - is
            // verified to run on the game's main thread during startup. Installing from the
            // worker thread would patch an import from the wrong thread.
            if (FrameHook.Install(OnFrame, out string hookDiagnostic))
            {
                Log($"frame hook installed: {hookDiagnostic}");
            }
            else
            {
                Log($"frame hook NOT installed: {hookDiagnostic}");
            }

            // Intercept the console command interpreter so co-op verbs route to the block and
            // are swallowed (no "function <x> not found"), while real typos still get flagged.
            // Independent of the frame hook, so the co-op console works at the menu too.
            if (FuncInterpHook.Install(_view, out string interpDiagnostic))
            {
                Log($"console interp hooked: {interpDiagnostic}");
            }
            else
            {
                Log($"console interp NOT hooked: {interpDiagnostic}");
            }

            // A per-frame callback that also fires at the main menu (the SwapBuffers hook is
            // dormant there behind the menu movie), used to drive the Co-op menu button.
            if (MainLoopHook.Install(MenuFrame, out string menuHookDiagnostic))
            {
                Log($"menu frame hook installed: {menuHookDiagnostic}");
            }
            else
            {
                Log($"menu frame hook NOT installed: {menuHookDiagnostic}");
            }

            // The world-up party message crashes on a half-initialised player creature's
            // null faction; the guard routes that one case onto the handler's own
            // default-faction path. Required for the plugin-driven load (the browser's
            // click-to-join lands through it).
            if (PartyFactionGuard.Install(out string guardDiagnostic))
            {
                Log($"party faction guard: {guardDiagnostic}");
            }
            else
            {
                Log($"party faction guard NOT installed: {guardDiagnostic}");
            }

            // One-shot animations (draws, swings, flourishes, item use) are dispatched and
            // finished between polls, so they are captured by hooking the engine's own
            // dispatch rather than sampled.
            if (AnimationHook.Install(out string animationDiagnostic))
            {
                Log($"animations: {animationDiagnostic}");
            }
            else
            {
                Log($"animation hook NOT installed: {animationDiagnostic}");
            }

            // Which dialogue reply the player picks is dispatched and gone between frames,
            // so it is captured by hooking the engine's own reply entry rather than sampled.
            if (DialogueReplyHook.Install(out string replyDiagnostic))
            {
                Log($"dialogue: {replyDiagnostic}");
            }
            else
            {
                Log($"dialogue reply hook NOT installed: {replyDiagnostic}");
            }

            // The engine reverts any pause that lacks a valid "PausedBy" record within one
            // tick, which in a loopback session is every pause there is — space bar, HUD
            // button and mirror alike. See PauseSanityPatch.
            if (PauseSanityPatch.Install(out string pausePatchDiagnostic))
            {
                Log($"pause: {pausePatchDiagnostic}");
            }
            else
            {
                Log($"pause sanity patch NOT installed: {pausePatchDiagnostic}");
            }

            // Co-op death handling: suppress the control auto-swap and the game-over screen.
            // Both patches stay inert until the agent turns them on, so a plugin with no
            // session behaves exactly like stock KOTOR.
            if (DeathController.Install(out string deathDiagnostic))
            {
                Log($"death handling: {deathDiagnostic}");
            }
            else
            {
                Log($"death handling NOT installed: {deathDiagnostic}");
            }

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "KotorCoop plugin",
            };

            _worker.Start();
            Log("worker thread started");
            CrashProbe.Install();

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
        _block->Version = 18;

        // A guard dword after the version. Magic plus version alone was not selective
        // enough: a scan once matched something else at a lower address and the agent
        // silently anchored on it. 16 bytes makes a coincidental match implausible.
        *(uint*)((byte*)_block + 0x0C) = SharedBlock.GuardValue;

        WriteModuleName("kotor");

        Log($"shared block at 0x{memory:X} ({SharedBlock.TotalSize} bytes)");
        return true;
    }

    /// <summary>World identity last frame; 0 = no stable world was seen.</summary>
    private static ulong _epoch;

    /// <summary>True while the pre-store flush window is open (trans_pending set).</summary>
    private static bool _transitionPending;

    /// <summary>
    /// Called once per frame on the GAME'S thread by <see cref="FrameHook"/>.
    /// Allocation-free and exception-swallowing by construction.
    ///
    /// The epoch gate runs before anything else, because a module transition destroys the
    /// entire object array and resets the id counters: every cached creature pointer is
    /// freed memory and every remembered object id names a stranger. Both live bugs this
    /// fixes were of exactly that shape — CombatController suppressing kills on recycled
    /// ids, and PhantomController calling through a vtable read from a freed object.
    /// </summary>
    /// <summary>
    /// Per-frame callback from <see cref="MainLoopHook"/>, which — unlike the SwapBuffers
    /// <see cref="FrameHook"/> — also fires at the main menu (behind the menu movie the swap hook
    /// is dormant). Used only to keep the Co-op menu button un-hidden and bound.
    /// </summary>
    private static void MenuFrame()
    {
        // The native browser first: its IsOpen gate tells the others the main-menu object
        // is currently freed (a shown child panel reclaims the menu until close rebuilds it).
        _nativeBrowser?.OnFrame();
        _connectMenu?.OnFrame();
        _browser?.OnFrame();

        // The menu-load replay lives HERE, not on the SwapBuffers hook: at the main menu the
        // back-buffer swap is movie-gated and never fires (measured live: the frame counter
        // stays 0 at a settled menu), so a load request serviced from OnFrame sat forever.
        // MainLoop fires at the menu and in-world alike, on the same game thread.
        _menuLoad?.OnFrame();
    }

    private static void OnFrame()
    {
        // Before the epoch gate, deliberately: these run at the main menu too, which is exactly
        // where the gate returns early (no server) — the Enter chat prompt and the chat
        // overlay. (The Co-op button, browsers and menu-load run on MainLoopHook — the swap
        // hook is dormant at the menu; the ~ console routes through FuncInterpHook.)
        _chatInput?.OnFrame();
        _chatOverlay?.OnFrame();

        // Before the epoch gate, and before anything else touches the world: during a module
        // transition there is a frame in which the server's module is destroyed but the
        // client still holds one, and the engine's own map init dereferences the server
        // module every frame without a null check. Closing the client's world here is what
        // the engine's dump-player handler would do a frame later — see
        // docs/research/travel-map-init-crash.md.
        if (Odyssey.CloseClientWorldIfServerModuleGone())
        {
            Log("transition: closed the client world ahead of the map init");
        }

        if (!Odyssey.TryGetWorldEpoch(out ulong epoch))
        {
            // No world, or a transition mid-flight. Drop everything now, so nothing runs
            // against freed objects on the first frame of the new module.
            if (_epoch != 0)
            {
                _epoch = 0;
                _transitionPending = false;
                InvalidateAll("world unloading");
            }

            return;
        }

        if (epoch != _epoch)
        {
            if (_epoch != 0)
            {
                InvalidateAll("module changed");
            }

            _epoch = epoch;
            _transitionPending = false;
        }

        // Focus first: the others do nothing useful in a paused world.
        _focus?.OnFrame();

        if (Odyssey.IsAreaTransitionPending())
        {
            // Pre-store flush: StoreCurrentModule serialises every non-party creature and
            // item on the way out, so anything the plugin spawned must be gone before the
            // area is stored or it becomes a permanent resident of the saved module. One
            // destroy per frame; the window is ~150 frames and there are at most 16 bodies.
            if (!_transitionPending)
            {
                _transitionPending = true;
                Log("area transition pending: flushing spawned objects before the module is stored");
            }

            if (_controller is { } phantoms && phantoms.FlushOneBody())
            {
                return;
            }

            if (_items is { } items && items.FlushOneBody())
            {
                return;
            }

            _mapMarkers?.FlushOneBody();
            return;
        }

        if (_transitionPending)
        {
            // The pending flag cleared without the module changing (an aborted or
            // cancelled transition). The flush destroyed real bodies, so reset state and
            // let the controllers rebuild them.
            _transitionPending = false;
            InvalidateAll("transition flushed but not taken");
        }

        if (_travel is { HasPendingRequest: true } travel)
        {
            // A session-commanded module move. Same rule as a door transition: everything
            // this plugin spawned must be gone before the outgoing area is stored, or it
            // becomes a permanent resident of the saved module. One destroy per frame
            // until clean, then the warp itself.
            if (_controller is { } phantoms && phantoms.FlushOneBody())
            {
                return;
            }

            if (_items is { } items && items.FlushOneBody())
            {
                return;
            }

            if (_mapMarkers is { } markers && markers.FlushOneBody())
            {
                return;
            }

            travel.Execute();
            return;
        }

        // Publish whatever one-shot animation the hook caught since the last frame, so the
        // agent can ship it with this tick's state.
        if (_view is { } view && AnimationHook.Sequence != view.LocalOneShotSerial)
        {
            view.LocalOneShotAnim = AnimationHook.LastAnimation;
            view.LocalOneShotSerial = AnimationHook.Sequence;
        }

        // Death first among the world controllers: a downed player must be published (and a
        // pending revive applied) before the phantom/combat passes read the same creature.
        _death?.OnFrame();

        _controller?.OnFrame();
        _items?.OnFrame();
        _combat?.OnFrame();
        _doors?.OnFrame();
        _placeables?.OnFrame();
        _loot?.OnFrame();
        _pause?.OnFrame();
        _pauseKey?.OnFrame();
        _journal?.OnFrame();
        _mapMarkers?.OnFrame();

        // Read-only: publishes the owner's current conversation line for the other player to
        // spectate. Runs in-world only (a conversation needs a live server + GUI).
        _dialogueSpectate?.OnFrame();
    }

    private static void InvalidateAll(string reason)
    {
        Log($"epoch: {reason}; clearing phantom, item, combat and door state");
        _controller?.Invalidate();
        _items?.Invalidate();
        _combat?.Invalidate();
        _doors?.Invalidate();
        _placeables?.Invalidate();
        _loot?.Invalidate();
        _travel?.Invalidate();
        _pause?.Invalidate();
        _journal?.Invalidate();
        _mapMarkers?.Invalidate();
        _connectMenu?.Invalidate();
        _death?.Invalidate();
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
                    long frames = FrameHook.FrameCount;
                    if (FrameHook.IsInstalled && frames == lastFrames)
                    {
                        if (++quietTicks == 120)
                        {
                            Log("WARNING: frame hook installed but has not fired for ~2s");
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

    public static void Log(string message)
    {
        try
        {
            DateTime now = DateTime.Now;
            File.AppendAllText(
                _logPath,
                $"[{now:HH:mm:ss.fff}] {message}{Environment.NewLine}",
                Encoding.ASCII);
        }
        catch
        {
            // Logging must never throw into the game's call stack.
        }
    }
}
