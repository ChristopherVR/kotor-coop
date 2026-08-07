using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Gives the plugin a once-per-frame callback on the game's own thread by redirecting a
/// single import.
///
/// Why this site (see docs/re/frame-hook.md): swkotor.exe calls GDI32!SwapBuffers through
/// the IAT slot at 0x0073D00C, and only two pieces of code in the whole image reference it,
/// both once per frame on the initial thread. The slot is 4-byte aligned so the store is
/// atomic on x86, which means the patch is race-free with nothing suspended. And the swap
/// is the last thing in the frame, after both CClientExoApp::MainLoop and
/// CServerExoApp::MainLoop, so engine state is settled and we are never in the middle of
/// the engine iterating its own containers.
///
/// Two alternatives were tested against the live process and refuted:
///   - Wrapping the DirectInput device vtable. The game Unacquires its devices when the
///     window loses focus, so the callback goes silent exactly when a background helper
///     would need it most.
///   - An opengl32.dll proxy. The game never calls wglSwapBuffers; it swaps through GDI32.
///
/// The main loop runs at roughly 317 Hz, not the frame rate, so anything done here must be
/// allocation-free and must rate-limit against a wall clock rather than counting frames.
/// </summary>
internal static unsafe partial class FrameHook
{
    /// <summary>IAT slot holding GDI32!SwapBuffers.</summary>
    private const nint SwapBuffersSlot = 0x0073D00C;

    private static delegate* unmanaged[Stdcall]<nint, int> _original;
    private static Action? _onFrame;
    private static volatile bool _installed;
    private static long _frames;
    private static volatile bool _inCallback;

    public static long FrameCount => Interlocked.Read(ref _frames);

    public static bool IsInstalled => _installed;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string name);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string name);

    /// <summary>
    /// Installs the hook. Must be called on the main thread; DirectInput8Create is, which
    /// is why installation is driven from there.
    /// </summary>
    public static bool Install(Action onFrame, out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        nint current = *(nint*)SwapBuffersSlot;

        // Validate that the slot still holds the genuine export before touching it. If some
        // other layer (the Steam overlay, for instance) has already wrapped it, or the
        // layout has shifted, refuse rather than corrupt the import.
        nint gdi32 = GetModuleHandle("gdi32.dll");
        nint expected = gdi32 == 0 ? 0 : GetProcAddress(gdi32, "SwapBuffers");

        if (expected == 0)
        {
            diagnostic = "could not resolve gdi32!SwapBuffers";
            return false;
        }

        if (current != expected)
        {
            diagnostic =
                $"IAT slot 0x{SwapBuffersSlot:X} holds 0x{current:X} but gdi32!SwapBuffers is "
                + $"0x{expected:X}; something else already hooked it. Refusing.";
            return false;
        }

        if (!VirtualProtect(SwapBuffersSlot, (nuint)sizeof(nint), 0x04 /* PAGE_READWRITE */, out uint oldProtect))
        {
            diagnostic = $"VirtualProtect failed, error {Marshal.GetLastWin32Error()}";
            return false;
        }

        _original = (delegate* unmanaged[Stdcall]<nint, int>)current;
        _onFrame = onFrame;

        // Single aligned dword store: atomic on x86, so no thread can observe a torn value.
        *(nint*)SwapBuffersSlot = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Detour;

        VirtualProtect(SwapBuffersSlot, (nuint)sizeof(nint), oldProtect, out _);
        _installed = true;

        diagnostic = $"hooked IAT 0x{SwapBuffersSlot:X} (real SwapBuffers 0x{current:X})";
        return true;
    }

    public static void Uninstall()
    {
        if (!_installed || _original is null)
        {
            return;
        }

        if (VirtualProtect(SwapBuffersSlot, (nuint)sizeof(nint), 0x04, out uint oldProtect))
        {
            *(nint*)SwapBuffersSlot = (nint)_original;
            VirtualProtect(SwapBuffersSlot, (nuint)sizeof(nint), oldProtect, out _);
        }

        _installed = false;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Detour(nint deviceContext)
    {
        // ⚠ Everything in here is fenced or provably non-throwing, because this is a
        // reverse-P/Invoke boundary: an escaping managed exception is not caught by the
        // engine, it is a .NET fail-fast that kills the game with nothing in the log.
        Interlocked.Increment(ref _frames);

        // Reentrancy guard. The engine has a second SwapBuffers call site (the load-screen
        // pump), and our own callback must never be able to recurse into itself.
        if (!_inCallback)
        {
            _inCallback = true;

            try
            {
                _onFrame?.Invoke();
            }
            catch
            {
                // Never let a managed fault propagate into the game's frame loop. A thrown
                // exception here would unwind through native code that cannot handle it.
            }
            finally
            {
                _inCallback = false;
            }
        }

        // Uninstall() can clear this from another thread between the check and the call, so
        // it is read once. Calling through a null function pointer would fault at address 0
        // inside the engine's frame loop, which is the least diagnosable crash there is.
        delegate* unmanaged[Stdcall]<nint, int> original = _original;
        return original == null ? 1 : original(deviceContext);
    }
}
