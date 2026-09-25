namespace KotorCoop.Plugin;

/// <summary>
/// A plugin-side screenshot for the isolated rig (docs/research/isolated-rig.md), because
/// <c>tools\shot.ps1</c>'s <c>PrintWindow(PW_RENDERFULLCONTENT)</c> measured unreliable
/// against this game: a window fully on-screen and unoccluded (294/300 lit sample points via a
/// plain <c>CopyFromScreen</c>) captured solid BLACK via PrintWindow on the same pass
/// (2026-09-25). DWM's PrintWindow path evidently does not always have a composed surface for
/// this game's OpenGL swapchain to hand back, even though the window is genuinely visible.
///
/// This sidesteps DWM entirely: it reads the pixels straight out of the GL context that is
/// already current on this thread, from inside the hooked <c>GDI32!SwapBuffers</c> frame
/// callback (<see cref="FrameHook"/>), which runs on the game's own render thread BEFORE the
/// real swap. That is also why this needs no window visibility, composition, or occlusion state
/// at all — it works exactly the same whether the window is on top, covered, minimized-but-
/// restorable, or ghosted (<c>movewin.ps1 -Ghost</c>): the GL context does not care what the
/// desktop compositor is doing with the window's surface.
///
/// GL entry points are resolved once with <c>GetProcAddress</c> on <c>opengl32.dll</c> — they
/// are ordinary exports (core GL 1.1), no <c>wglGetProcAddress</c> extension dance needed. No
/// <c>wglMakeCurrent</c> call is made here: the render thread already has its context current at
/// this point in the frame (it just finished drawing), which is also exactly why this must be
/// called from the frame hook and not from the plugin's worker thread.
///
/// Path handling: the shared block's cheat-text slot is 31 bytes (<c>SharedBlock.
/// CheatTextLength</c> less the null) — nowhere near enough for an absolute output path. So the
/// verb takes a short NAME instead, and this always writes to a fixed, predictable location,
/// <c>&lt;gamedir&gt;\kotorcoop-shots\&lt;name&gt;.bmp</c>; the caller (<c>tools\shot.ps1</c>)
/// polls for that file and copies/converts it to whatever <c>-OutPath</c> it was actually asked
/// for.
///
/// BMP, not PNG: a 24-bit uncompressed BMP is the one image format writable with zero
/// dependencies from a NativeAOT native library (no zlib, no System.Drawing, which is itself not
/// friendly to NativeAOT for a Windows plugin whose only job is to stay tiny and dependency-free)
/// — <c>tools\shot.ps1</c> does the BMP-&gt;PNG conversion afterward, in PowerShell, where
/// System.Drawing is unremarkable.
/// </summary>
internal static unsafe class ScreenshotController
{
    /// <summary>Kept comfortably under <c>ConsoleCommand.GamepadTextMaxLength</c> (31) so a
    /// name plus the optional " front" suffix always fits the cheat-text slot.</summary>
    private const int MaxNameLength = 24;

    private const string SubDirectory = "kotorcoop-shots";

    // GL enums (core GL 1.1 - stable since forever, no need for a GL header dependency).
    private const uint GlFront = 0x0404;
    private const uint GlBack = 0x0405;
    private const uint GlPackAlignment = 0x0D05;
    private const uint GlReadBufferQuery = 0x0C02; // GL_READ_BUFFER
    private const uint GlUnsignedByte = 0x1401;
    private const uint GlViewport = 0x0BA2;
    private const uint GlBgrExt = 0x80E0; // GL_BGR / GL_BGR_EXT - matches a BMP's native byte order, no channel swap needed.
    private const uint GlNoError = 0;

    private static bool _resolveAttempted;
    private static bool _available;
    private static string _resolveError = "";

    private static delegate* unmanaged[Stdcall]<uint, void> _glReadBuffer;
    private static delegate* unmanaged[Stdcall]<uint, int, void> _glPixelStorei;
    private static delegate* unmanaged[Stdcall]<int, int, int, int, uint, uint, void*, void> _glReadPixels;
    private static delegate* unmanaged[Stdcall]<uint, int*, void> _glGetIntegerv;
    private static delegate* unmanaged[Stdcall]<uint> _glGetError;

    /// <summary>Handles a `screenshot ...` console line. MAIN/RENDER THREAD ONLY (the cheat
    /// drain, itself inside the hooked SwapBuffers callback) - see the type header for why.
    /// Returns the reply line, which also says whether the file was actually written.</summary>
    public static string HandleConsole(string arguments)
    {
        string[] parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0 || parts[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return Status();
        }

        string name = parts[0];
        bool front = parts.Length > 1 && parts[1].Equals("front", StringComparison.OrdinalIgnoreCase);
        if (parts.Length > 2 || (parts.Length == 2 && !front))
        {
            return $"screenshot: unknown argument '{parts[^1]}' - usage: screenshot <name> [front] | screenshot status";
        }

        if (!IsSafeName(name))
        {
            return $"screenshot: '{name}' is not a safe file name (letters, digits, _, - only; "
                + $"max {MaxNameLength} characters, no path separators)";
        }

        return Capture(name, front);
    }

    private static string Status()
    {
        bool resolved = EnsureResolved(out string resolveError);
        string dims = $"{Odyssey.ScreenWidth}x{Odyssey.ScreenHeight}";
        string directory = System.IO.Path.Combine(NativeMethods.GetGameDirectory(), SubDirectory);
        string line = $"screenshot: opengl32 functions {(resolved ? "resolved" : $"NOT resolved ({resolveError})")}, "
            + $"screen {dims}, output directory '{directory}'";
        PluginHost.Log(line);
        return line;
    }

    private static bool IsSafeName(string name)
    {
        if (name.Length == 0 || name.Length > MaxNameLength)
        {
            return false;
        }

        foreach (char c in name)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c is '_' or '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static string Capture(string name, bool front)
    {
        if (!EnsureResolved(out string resolveError))
        {
            return $"screenshot: FAILED - {resolveError}";
        }

        int width = Odyssey.ScreenWidth;
        int height = Odyssey.ScreenHeight;

        if (width <= 0 || height <= 0)
        {
            // Fallback for the rare frame where the engine's own SCREEN_WIDTH/SCREEN_HEIGHT
            // globals are not populated yet (very first frames, or a main-menu edge case): ask
            // GL directly what it thinks the current viewport is.
            int* viewport = stackalloc int[4];
            _glGetIntegerv(GlViewport, viewport);
            width = viewport[2];
            height = viewport[3];
        }

        if (width <= 0 || height <= 0)
        {
            return "screenshot: FAILED - could not determine the window size "
                + "(Odyssey.ScreenWidth/Height and GL_VIEWPORT both empty)";
        }

        int rowBytes = width * 3;
        byte[] pixels = new byte[rowBytes * height];

        // Tightly packed rows (no row padding from GL's own side) - this controller does the
        // BMP row padding itself in WriteBmp, since GL's row alignment and a BMP's 4-byte row
        // alignment are unrelated requirements.
        // GL error flags stick until read, and the engine never reads them: a flag its own
        // rendering left set was reported as OUR failure (live 2026-09-25, 0x0502 on some
        // frames). Drain them first, so the check below sees only this read's outcome.
        for (int i = 0; i < 16 && _glGetError() != GlNoError; i++)
        {
        }

        // Save and restore what this changes: the engine may read pixels itself.
        int savedReadBuffer = 0;
        int savedPackAlignment = 4;
        _glGetIntegerv(GlReadBufferQuery, &savedReadBuffer);
        _glGetIntegerv(GlPackAlignment, &savedPackAlignment);

        _glPixelStorei(GlPackAlignment, 1);
        _glReadBuffer(front ? GlFront : GlBack);

        fixed (byte* p = pixels)
        {
            _glReadPixels(0, 0, width, height, GlBgrExt, GlUnsignedByte, p);
        }

        uint glError = _glGetError();
        if (savedReadBuffer != 0)
        {
            _glReadBuffer((uint)savedReadBuffer);
        }

        _glPixelStorei(GlPackAlignment, savedPackAlignment);
        if (glError != GlNoError)
        {
            return $"screenshot: FAILED - glReadPixels reported GL error 0x{glError:X4} "
                + $"(buffer={(front ? "front" : "back")}, {width}x{height})";
        }

        string directory = System.IO.Path.Combine(NativeMethods.GetGameDirectory(), SubDirectory);
        string path = System.IO.Path.Combine(directory, $"{name}.bmp");

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            WriteBmp(path, pixels, width, height);
        }
        catch (Exception ex)
        {
            return $"screenshot: FAILED - could not write '{path}': {ex.Message}";
        }

        string reply = $"screenshot: wrote '{path}' ({width}x{height}, {(front ? "front" : "back")} buffer)";
        PluginHost.Log(reply);
        return reply;
    }

    /// <summary>
    /// Resolves the five GL entry points this controller needs, once, from opengl32.dll's own
    /// export table (they are core GL 1.1, always exported directly - no
    /// <c>wglGetProcAddress</c>/extension-string dance needed, unlike a GL 1.2+ or extension
    /// function). Cached after the first call either way, so a game that genuinely has no GL
    /// context (should never happen - this game IS OpenGL) does not retry the lookup every
    /// screenshot.
    /// </summary>
    private static bool EnsureResolved(out string error)
    {
        if (_resolveAttempted)
        {
            error = _resolveError;
            return _available;
        }

        _resolveAttempted = true;

        nint gl = NativeMethods.GetModuleHandle("opengl32.dll");
        if (gl == 0)
        {
            _resolveError = "opengl32.dll is not loaded in this process";
            error = _resolveError;
            return false;
        }

        nint pReadBuffer = NativeMethods.GetProcAddress(gl, "glReadBuffer");
        nint pPixelStorei = NativeMethods.GetProcAddress(gl, "glPixelStorei");
        nint pReadPixels = NativeMethods.GetProcAddress(gl, "glReadPixels");
        nint pGetIntegerv = NativeMethods.GetProcAddress(gl, "glGetIntegerv");
        nint pGetError = NativeMethods.GetProcAddress(gl, "glGetError");

        if (pReadBuffer == 0 || pPixelStorei == 0 || pReadPixels == 0 || pGetIntegerv == 0 || pGetError == 0)
        {
            _resolveError = "GetProcAddress on opengl32.dll failed for one or more of "
                + "glReadBuffer/glPixelStorei/glReadPixels/glGetIntegerv/glGetError";
            error = _resolveError;
            return false;
        }

        _glReadBuffer = (delegate* unmanaged[Stdcall]<uint, void>)pReadBuffer;
        _glPixelStorei = (delegate* unmanaged[Stdcall]<uint, int, void>)pPixelStorei;
        _glReadPixels = (delegate* unmanaged[Stdcall]<int, int, int, int, uint, uint, void*, void>)pReadPixels;
        _glGetIntegerv = (delegate* unmanaged[Stdcall]<uint, int*, void>)pGetIntegerv;
        _glGetError = (delegate* unmanaged[Stdcall]<uint>)pGetError;

        _available = true;
        error = "";
        return true;
    }

    /// <summary>
    /// Writes a 24-bit uncompressed BMP. Deliberately NOT flipped: <c>glReadPixels</c> returns
    /// rows starting at GL window-coordinate y=0 (the BOTTOM row first, OpenGL's origin is
    /// lower-left), and a BMP with a POSITIVE <c>biHeight</c> is defined as bottom-up too (its
    /// first pixel row is the bottom of the image) - the two conventions already agree, so the
    /// buffer copies straight across. ⚠ NEEDS-LIVE: confirm the written image is right-side up
    /// against the real game (nothing in this pass could run swkotor.exe - the rig was locked);
    /// if it turns out upside down, the fix is either a negative <c>biHeight</c> (top-down BMP)
    /// or reversing the row-copy order below, NOT both.
    /// </summary>
    private static void WriteBmp(string path, byte[] pixels, int width, int height)
    {
        int rowBytes = width * 3;
        int paddedRowBytes = (rowBytes + 3) & ~3;
        int pad = paddedRowBytes - rowBytes;
        int imageSize = paddedRowBytes * height;
        int fileSize = 14 + 40 + imageSize;

        using System.IO.FileStream stream = new(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        using System.IO.BinaryWriter writer = new(stream);

        // BITMAPFILEHEADER (14 bytes).
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);
        writer.Write(0u); // reserved1/reserved2
        writer.Write(54u); // bfOffBits: 14 (file header) + 40 (info header)

        // BITMAPINFOHEADER (40 bytes).
        writer.Write(40u); // biSize
        writer.Write(width);
        writer.Write(height); // positive => bottom-up; see the doc comment above.
        writer.Write((ushort)1); // biPlanes
        writer.Write((ushort)24); // biBitCount
        writer.Write(0u); // biCompression = BI_RGB
        writer.Write((uint)imageSize);
        writer.Write(0); // biXPelsPerMeter
        writer.Write(0); // biYPelsPerMeter
        writer.Write(0u); // biClrUsed
        writer.Write(0u); // biClrImportant

        byte[]? padBytes = pad > 0 ? new byte[pad] : null;
        for (int row = 0; row < height; row++)
        {
            writer.Write(pixels, row * rowBytes, rowBytes);
            if (padBytes is not null)
            {
                writer.Write(padBytes);
            }
        }
    }
}
