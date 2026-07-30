using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// The dinput8.dll surface the game expects.
///
/// Each export is re-implemented here and forwarded to the real system DLL, so the game
/// gets identical behaviour and we get a place to run. DirectInput8Create is the useful
/// one: the game calls it during startup, which is the moment the plugin starts.
///
/// Calling convention is stdcall because this is x86 and that is what the Win32 ABI uses.
/// Getting it wrong would corrupt the stack on the very first call.
/// </summary>
internal static unsafe class Exports
{
    private static nint _realModule;
    private static readonly object Gate = new();

    private static delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, int> _directInput8Create;
    private static delegate* unmanaged[Stdcall]<int> _dllCanUnloadNow;
    private static delegate* unmanaged[Stdcall]<nint, nint, nint, int> _dllGetClassObject;
    private static delegate* unmanaged[Stdcall]<int> _dllRegisterServer;
    private static delegate* unmanaged[Stdcall]<int> _dllUnregisterServer;
    private static delegate* unmanaged[Stdcall]<nint> _getdfDIJoystick;

    private const int ErrorFail = unchecked((int)0x80004005); // E_FAIL

    private static void EnsureRealModule()
    {
        lock (Gate)
        {
            if (_realModule != 0)
            {
                return;
            }

            // Load by absolute system path. Loading "dinput8.dll" by name would find
            // this proxy again, because the game directory is searched first, and the
            // process would recurse into itself.
            string path = Path.Combine(NativeMethods.GetSystemDirectoryPath(), "dinput8.dll");
            _realModule = NativeMethods.LoadLibrary(path);

            if (_realModule == 0)
            {
                PluginHost.Log($"FATAL: could not load the real dinput8 from {path}");
                return;
            }

            PluginHost.Log($"forwarding to {path} (module 0x{_realModule:X})");

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

    [UnmanagedCallersOnly(EntryPoint = "DirectInput8Create", CallConvs = [typeof(CallConvStdcall)])]
    public static int DirectInput8Create(nint instance, uint version, nint riid, nint output, nint outer)
    {
        // This is the hook point: the game calls it once during startup.
        EnsureRealModule();
        PluginHost.EnsureStarted();

        return _directInput8Create is null
            ? ErrorFail
            : _directInput8Create(instance, version, riid, output, outer);
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
