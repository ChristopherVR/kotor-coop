using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Logs where the game crashes: faulting address, EIP, and a raw slice of the stack
/// (candidate return addresses) into the plugin log, via a vectored exception handler.
///
/// Exists because the in-process menu-load work kept killing the game ~0.5 s after the
/// world came up, WER local dumps need admin rights this session does not have, and
/// guessing at the subsystem three times in a row did not converge. Only first-chance
/// access violations are recorded, exactly once, and the handler always continues the
/// search so crash behaviour is otherwise unchanged.
/// </summary>
internal static unsafe partial class CrashProbe
{
    [LibraryImport("kernel32.dll")]
    private static partial nint AddVectoredExceptionHandler(uint first, nint handler);

    private static int _logged;

    public static void Install()
    {
        nint handler = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&OnException;
        if (AddVectoredExceptionHandler(1, handler) != 0)
        {
            PluginHost.Log("crash probe installed");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int OnException(nint pointers)
    {
        const int ExceptionContinueSearch = 0;

        try
        {
            nint record = *(nint*)pointers;
            nint context = *(nint*)(pointers + 4);

            if (record == 0 || context == 0)
            {
                return ExceptionContinueSearch;
            }

            uint code = *(uint*)record;
            if (code != 0xC0000005 || Interlocked.Exchange(ref _logged, 1) == 1)
            {
                return ExceptionContinueSearch;
            }

            nint address = *(nint*)(record + 0x0C);
            uint eip = *(uint*)(context + 0xB8);
            uint esp = *(uint*)(context + 0xC4);

            // Raw stack slice: anything in the image range 0x400000-0x8C3000 is a
            // candidate return address, which is backtrace enough for the symbol DB.
            System.Text.StringBuilder trace = new();
            for (int i = 0; i < 64; i++)
            {
                uint value = *(uint*)((nint)esp + (i * 4));
                if (value is >= 0x400000 and < 0x8C3000)
                {
                    trace.Append($" 0x{value:X}");
                }
            }

            PluginHost.Log(
                $"CRASH: access violation at address 0x{address:X}, eip 0x{eip:X}, esp 0x{esp:X}; "
                + $"image-range stack values:{trace}");
        }
        catch
        {
            // The handler must never throw into a dying process.
        }

        return ExceptionContinueSearch;
    }
}
