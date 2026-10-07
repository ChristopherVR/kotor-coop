using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// The dinput8.dll surface the game expects.
///
/// Each export is re-implemented here and forwarded to the NEXT dinput8 in line, so the game
/// gets identical behaviour and we get a place to run. DirectInput8Create is the useful
/// one: the game calls it during startup, which is the moment the plugin starts.
///
/// "Next in line" is normally the real system DLL, but this proxy is chainable so another
/// mod that also wants to be dinput8.dll can coexist with it. The forward target is resolved
/// once, in this order (see <see cref="ResolveForwardTarget"/>):
///
///   1. <c>dinput8_next = &lt;file&gt;</c> in coop.ini — an explicit override;
///   2. the entry after this DLL in <c>dinput8.chain</c> beside swkotor.exe, the list file
///      the KotorCoop.Loader wrapper reads (so one file describes the whole chain);
///   3. the real dinput8.dll in the system directory.
///
/// Calling convention is stdcall because this is x86 and that is what the Win32 ABI uses.
/// Getting it wrong would corrupt the stack on the very first call.
/// </summary>
internal static unsafe class Exports
{
    /// <summary>coop.ini key naming the DLL this proxy forwards to instead of the system one.</summary>
    private const string NextKey = "dinput8_next";

    /// <summary>The chain list file, shared with src/KotorCoop.Loader — same name, same format.</summary>
    private const string ChainFileName = "dinput8.chain";

    private static nint _realModule;
    private static readonly object Gate = new();

    private static delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, int> _directInput8Create;
    private static delegate* unmanaged[Stdcall]<int> _dllCanUnloadNow;
    private static delegate* unmanaged[Stdcall]<nint, nint, nint, int> _dllGetClassObject;
    private static delegate* unmanaged[Stdcall]<int> _dllRegisterServer;
    private static delegate* unmanaged[Stdcall]<int> _dllUnregisterServer;
    private static delegate* unmanaged[Stdcall]<nint> _getdfDIJoystick;

    private const int ErrorFail = unchecked((int)0x80004005); // E_FAIL

    /// <summary>
    /// ⚠ Called from every export, and an export IS an [UnmanagedCallersOnly] entry point:
    /// a managed exception escaping one of these does not unwind into the game, it kills the
    /// process outright with a .NET fail-fast (see CrashProbe). Path.Combine, LoadLibrary and
    /// the lock can all fail on a broken install, so the whole body is fenced. A failure here
    /// leaves the forwarding pointers null, which every export already handles.
    /// </summary>
    private static void EnsureRealModule()
    {
        try
        {
            EnsureRealModuleCore();
        }
        catch (Exception ex)
        {
            PluginHost.Log($"FATAL: could not prepare the dinput8 forward: {ex.Message}");
        }
    }

    private static void EnsureRealModuleCore()
    {
        lock (Gate)
        {
            if (_realModule != 0)
            {
                return;
            }

            _realModule = ResolveForwardTarget();
            if (_realModule == 0)
            {
                return;
            }

            _directInput8Create = (delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, int>)
                NativeMethods.GetProcAddress(_realModule, "DirectInput8Create");
            _dllCanUnloadNow = (delegate* unmanaged[Stdcall]<int>)
                NativeMethods.GetProcAddress(_realModule, "DllCanUnloadNow");
            _dllGetClassObject = (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)
                NativeMethods.GetProcAddress(_realModule, "DllGetClassObject");
            _dllRegisterServer = (delegate* unmanaged[Stdcall]<int>)
                NativeMethods.GetProcAddress(_realModule, "DllRegisterServer");
            _dllUnregisterServer = (delegate* unmanaged[Stdcall]<int>)
                NativeMethods.GetProcAddress(_realModule, "DllUnregisterServer");
            _getdfDIJoystick = (delegate* unmanaged[Stdcall]<nint>)
                NativeMethods.GetProcAddress(_realModule, "GetdfDIJoystick");
        }
    }

    /// <summary>
    /// Loads and returns the module every export forwards to, or 0 when nothing usable could
    /// be loaded. Every candidate is loaded by ABSOLUTE path: loading "dinput8.dll" by name
    /// would find whatever sits beside the game (this proxy, or the loader) and recurse.
    /// </summary>
    private static nint ResolveForwardTarget()
    {
        string gameDirectory = NativeMethods.GetGameDirectory();

        // EnsureStarted sets the same directory again later; doing it here too lets the
        // override key be read before a single controller exists.
        PluginConfig.UseDirectory(gameDirectory);

        // This DLL's own file — it may not be called dinput8.dll (chain mode), and it is the
        // one thing a candidate must never resolve to.
        string? self = NativeMethods.GetModulePathContaining(
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, int>)&DirectInput8Create);

        // Whatever owns the dinput8.dll name in the game directory is either this proxy or the
        // loader that loaded it; forwarding to it would loop the chain back onto itself.
        string gameDirectInput = Path.Combine(gameDirectory, "dinput8.dll");

        foreach ((string label, string path) in Candidates(gameDirectory, self))
        {
            if (self is not null && string.Equals(path, self, StringComparison.OrdinalIgnoreCase))
            {
                PluginHost.Log($"dinput8 forward: skipping {label} {path} (this DLL)");
                continue;
            }

            if (string.Equals(path, gameDirectInput, StringComparison.OrdinalIgnoreCase))
            {
                PluginHost.Log($"dinput8 forward: skipping {label} {path} (the game-directory dinput8.dll is the head of the chain, not a target)");
                continue;
            }

            if (!File.Exists(path))
            {
                PluginHost.Log($"dinput8 forward: {label} {path} does not exist, trying the next candidate");
                continue;
            }

            nint module = NativeMethods.LoadLibrary(path);
            if (module == 0)
            {
                PluginHost.Log($"dinput8 forward: {label} {path} failed to load, trying the next candidate");
                continue;
            }

            if (NativeMethods.GetProcAddress(module, "DirectInput8Create") == 0)
            {
                PluginHost.Log($"dinput8 forward: {label} {path} loaded but exports no DirectInput8Create, trying the next candidate");
                continue;
            }

            PluginHost.Log($"forwarding to {label} {path} (module 0x{module:X})");
            return module;
        }

        PluginHost.Log("FATAL: no dinput8 candidate could be loaded; every export will fail");
        return 0;
    }

    /// <summary>The forward candidates in priority order: explicit key, chain file, system.</summary>
    private static IEnumerable<(string Label, string Path)> Candidates(string gameDirectory, string? self)
    {
        string? next = PluginConfig.Read(NextKey);
        if (!string.IsNullOrWhiteSpace(next))
        {
            yield return ($"coop.ini {NextKey}", Path.GetFullPath(Path.Combine(gameDirectory, next)));
        }

        foreach (string entry in ChainEntriesAfterSelf(gameDirectory, self))
        {
            yield return ($"{ChainFileName} entry", entry);
        }

        yield return ("system", Path.Combine(NativeMethods.GetSystemDirectoryPath(), "dinput8.dll"));
    }

    /// <summary>
    /// The entries listed after this DLL in dinput8.chain, as full paths. Same format the
    /// loader reads: one path per line, relative to the game directory, '#'/';' comments.
    /// An entry that does not name this DLL yields nothing — a chain file we are not part
    /// of is somebody else's business.
    /// </summary>
    private static IEnumerable<string> ChainEntriesAfterSelf(string gameDirectory, string? self)
    {
        string chainPath = Path.Combine(gameDirectory, ChainFileName);
        if (self is null || !File.Exists(chainPath))
        {
            yield break;
        }

        bool seenSelf = false;
        foreach (string raw in File.ReadAllLines(chainPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            string full = Path.GetFullPath(Path.Combine(gameDirectory, line));
            if (!seenSelf)
            {
                seenSelf = string.Equals(full, self, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            yield return full;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "DirectInput8Create", CallConvs = [typeof(CallConvStdcall)])]
    public static int DirectInput8Create(nint instance, uint version, nint riid, nint output, nint outer)
    {
        // This is the hook point: the game calls it once during startup.
        EnsureRealModule();

        // EnsureStarted has its own catch, but it is the one call here that runs arbitrary
        // plugin code, so it is fenced again at the boundary rather than trusted.
        try
        {
            PluginHost.EnsureStarted();
        }
        catch (Exception ex)
        {
            PluginHost.Log($"FATAL during startup (at the export boundary): {ex}");
        }

        if (_directInput8Create is null)
        {
            return ErrorFail;
        }

        int result = _directInput8Create(instance, version, riid, output, outer);

        // ALWAYS wrapped, independent of coop.ini gamepad: the wrap also carries the gameplay
        // keyboard lock for held players (MinigameLockController / SoloSegmentSpectateController
        // via DiscardGameplayQueuedInput and the GameplayLocked masking), the rig's `input key`
        // injection, and input isolation (docs/research/isolated-rig.md). Only the gamepad's own
        // XInput reading is gated on `gamepad` (GamepadController). A vptr swap on the returned
        // object only, fenced like EnsureStarted above.
        if (result >= 0)
        {
            try
            {
                DirectInputKeyboardHook.WrapDirectInput(output);
                PluginHost.Log($"gamepad: {DirectInputKeyboardHook.LastDiagnostic ?? "IDirectInput8 not wrapped"}");
            }
            catch (Exception ex)
            {
                PluginHost.Log($"gamepad: IDirectInput8 wrap threw: {ex.Message}");
            }
        }

        return result;
    }

    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow", CallConvs = [typeof(CallConvStdcall)])]
    public static int DllCanUnloadNow()
    {
        EnsureRealModule();
        return _dllCanUnloadNow is null ? ErrorFail : _dllCanUnloadNow();
    }

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject", CallConvs = [typeof(CallConvStdcall)])]
    public static int DllGetClassObject(nint classId, nint interfaceId, nint output)
    {
        EnsureRealModule();
        return _dllGetClassObject is null ? ErrorFail : _dllGetClassObject(classId, interfaceId, output);
    }

    [UnmanagedCallersOnly(EntryPoint = "DllRegisterServer", CallConvs = [typeof(CallConvStdcall)])]
    public static int DllRegisterServer()
    {
        EnsureRealModule();
        return _dllRegisterServer is null ? ErrorFail : _dllRegisterServer();
    }

    [UnmanagedCallersOnly(EntryPoint = "DllUnregisterServer", CallConvs = [typeof(CallConvStdcall)])]
    public static int DllUnregisterServer()
    {
        EnsureRealModule();
        return _dllUnregisterServer is null ? ErrorFail : _dllUnregisterServer();
    }

    [UnmanagedCallersOnly(EntryPoint = "GetdfDIJoystick", CallConvs = [typeof(CallConvStdcall)])]
    public static nint GetdfDIJoystick()
    {
        EnsureRealModule();
        return _getdfDIJoystick is null ? 0 : _getdfDIJoystick();
    }
}
