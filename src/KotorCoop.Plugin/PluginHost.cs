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
    private static TravelController? _travel;
    private static PauseController? _pause;
    private static JournalController? _journal;
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

            Log($"=== KotorCoop plugin loaded into pid {NativeMethods.GetCurrentProcessId()} ===");

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
            _travel = new TravelController(_view);
            _pause = new PauseController(_view);
            _journal = new JournalController(_view);

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

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "KotorCoop plugin",
            };

            _worker.Start();
            Log("worker thread started");
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
        _block->Version = 7;

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
    private static void OnFrame()
    {
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

            _items?.FlushOneBody();
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

            travel.Execute();
            return;
        }

        _controller?.OnFrame();
        _items?.OnFrame();
        _combat?.OnFrame();
        _doors?.OnFrame();
        _pause?.OnFrame();
        _journal?.OnFrame();
    }

    private static void InvalidateAll(string reason)
    {
        Log($"epoch: {reason}; clearing phantom, item, combat and door state");
        _controller?.Invalidate();
        _items?.Invalidate();
        _combat?.Invalidate();
        _doors?.Invalidate();
        _travel?.Invalidate();
        _pause?.Invalidate();
        _journal?.Invalidate();
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
