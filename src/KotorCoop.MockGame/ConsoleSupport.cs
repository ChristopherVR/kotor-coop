using System.Runtime.InteropServices;

namespace KotorCoop.MockGame;

/// <summary>
/// Windows Terminal handles ANSI sequences out of the box, but legacy conhost does not
/// unless virtual terminal processing is switched on, and without it the renderer prints
/// escape codes as literal text.
/// </summary>
internal static partial class ConsoleSupport
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);

    public static void EnableAnsi()
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        nint handle = GetStdHandle(StdOutputHandle);
        if (handle == 0 || handle == -1)
        {
            return;
        }

        if (GetConsoleMode(handle, out uint mode))
        {
            SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
    }
}
