using System.Runtime.InteropServices;
using System.Text;

namespace KotorCoop.Memory;

/// <summary>
/// Whether this process is actually running under Wine/Proton rather than real Windows — the
/// one thing that matters for a Linux player, since everything downstream (registry, drive
/// letters, dinput8.dll loading) already works identically once that is true.
///
/// Detected the only way that is actually reliable: real Windows and Wine both ship an
/// ntdll.dll, so its mere presence proves nothing, but ONLY Wine's ntdll additionally exports
/// <c>wine_get_version</c> — a plain C string naming the build. Asking for that export and
/// seeing whether it resolves at all IS the test.
///
/// Lives here, in KotorCoop.Memory, rather than in KotorCoop.Agent or KotorCoop.Plugin,
/// because it needs to be usable from BOTH: the agent (x64, an ordinary assembly reference —
/// KotorCoop.Agent -&gt; KotorCoop.Session -&gt; KotorCoop.Memory) and the plugin (x86
/// NativeAOT, which carries no assembly references at all and only ever shares source files —
/// see KotorCoop.Plugin.csproj's <c>&lt;Compile Include="..\KotorCoop.Memory\WineDetection.cs"&gt;</c>,
/// the same technique it already uses for the Protocol files it shares). No reflection and no
/// <c>Marshal.GetDelegateForFunctionPointer&lt;T&gt;</c> either: both need the generic
/// instantiation machinery NativeAOT does without, so the exported symbol is read as a raw
/// address and invoked through an unmanaged function pointer instead, exactly as
/// <c>wine_get_version</c>'s own C signature is laid out (<c>const char *wine_get_version(void)</c>,
/// cdecl) — the plugin's own NativeMethods.cs is not reused here for the same reason
/// LogRotation.cs does not reuse it: whichever project compiles this file must not need
/// anything besides what this one file declares.
///
/// Never throws: every step is wrapped, and any failure along the way (module not found, no
/// such export, a null pointer) is simply "not Wine" rather than an exception a caller has to
/// handle. Computed once and cached — the two module lookups never change for the life of the
/// process.
///
/// ⚠ NEEDS-LIVE. There is no Linux/Wine box in this environment, so nothing here has run
/// under real Wine; the reasoning above matches Wine's own documented export, and the plugin
/// build (NativeAOT x86) compiling this file clean is the only proof available today that the
/// unmanaged-function-pointer approach is AOT-safe.
/// </summary>
public static partial class WineDetection
{
    private static readonly bool _isWine;
    private static readonly string? _version;

    static WineDetection()
    {
        (_isWine, _version) = Detect();
    }

    /// <summary>True when <c>ntdll.dll</c> exports <c>wine_get_version</c> — i.e. this process
    /// is running under Wine or a Proton build of it, not real Windows.</summary>
    public static bool IsWine => _isWine;

    /// <summary>Wine's own version string (e.g. "9.0", "GE-Proton9-20"), or null when
    /// <see cref="IsWine"/> is false or the string could not be read.</summary>
    public static string? Version => _version;

    /// <summary>
    /// Wine's description of the host it is running on top of ("Linux 6.5.0-...", etc), via
    /// the optional <c>wine_get_host_version</c> export. Not cached alongside
    /// <see cref="IsWine"/>/<see cref="Version"/> above: it is cheap enough to read on demand,
    /// and nothing here needs it every time <see cref="IsWine"/> is checked.
    /// </summary>
    public static string? HostDescription
    {
        get
        {
            if (!_isWine)
            {
                return null;
            }

            try
            {
                nint ntdll = GetModuleHandle("ntdll.dll");
                if (ntdll == 0)
                {
                    return null;
                }

                nint export = GetProcAddress(ntdll, "wine_get_host_version");
                if (export == 0)
                {
                    return null;
                }

                unsafe
                {
                    var hostVersion = (delegate* unmanaged[Cdecl]<byte**, byte**, void>)export;
                    byte* sysname = null;
                    byte* release = null;
                    hostVersion(&sysname, &release);

                    string? sys = ReadAnsiString(sysname);
                    string? rel = ReadAnsiString(release);
                    if (sys is null && rel is null)
                    {
                        return null;
                    }

                    return sys is null ? rel : rel is null ? sys : $"{sys} {rel}";
                }
            }
            catch
            {
                return null;
            }
        }
    }

    private static (bool IsWine, string? Version) Detect()
    {
        try
        {
            nint ntdll = GetModuleHandle("ntdll.dll");
            if (ntdll == 0)
            {
                return (false, null);
            }

            nint export = GetProcAddress(ntdll, "wine_get_version");
            if (export == 0)
            {
                return (false, null);
            }

            unsafe
            {
                var getVersion = (delegate* unmanaged[Cdecl]<byte*>)export;
                return (true, ReadAnsiString(getVersion()));
            }
        }
        catch
        {
            return (false, null);
        }
    }

    private static unsafe string? ReadAnsiString(byte* text)
    {
        if (text is null)
        {
            return null;
        }

        int length = 0;
        while (text[length] != 0)
        {
            length++;
        }

        return Encoding.ASCII.GetString(text, length);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string name);
}
