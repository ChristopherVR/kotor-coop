using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Observes the exact area OnEnter-script dispatch in <c>CSWSArea::EventHandler</c>. At
/// <c>0x0050D974</c>, the event-0xC branch passes <c>CSWSArea+0x174</c> (symbol-verified
/// <c>script_on_enter</c>) and the area's object id to <c>CVirtualMachine::RunScript</c>. The
/// observer receives the bounded resref, current module, OBJECT_SELF and whether a host roster
/// positively identifies this instance as a client. It does not alter the script call.
///
/// The callback is rare (area OnEnter only), runs on the engine thread, and must remain
/// non-blocking. No log or engine function call occurs in the native stub itself; observer work
/// is limited to the registered controller callback.
/// </summary>
internal static unsafe class KorribanAreaEnterHook
{
    private const nint CallSite = 0x0050D974;
    private const nint RunScript = 0x005D0FC0;
    private const nint Resume = 0x0050D979;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    private static ReadOnlySpan<byte> Anchor =>
        [0x6A, 0x01, 0x52, 0x81, 0xC6, 0x74, 0x01, 0x00, 0x00, 0x56];

    private static ReadOnlySpan<byte> OriginalCall => [0xE8, 0x47, 0x36, 0x0C, 0x00];

    internal delegate void ScriptObserver(
        ReadOnlySpan<byte> module,
        ReadOnlySpan<byte> scriptResref,
        uint objectSelf,
        bool isClient,
        uint sequence);

    private static SharedBlockView? _view;
    private static bool _installed;
    private static uint _dispatchSequence;

    public static event ScriptObserver? Observer;

    public static bool Install(SharedBlockView view, out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;

        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* anchorSite = (byte*)(CallSite - Anchor.Length);
        for (int i = 0; i < Anchor.Length; i++)
        {
            if (anchorSite[i] != Anchor[i])
            {
                diagnostic = $"CSWSArea event anchor mismatch at -0x{Anchor.Length - i:X}; area-script observer not installed";
                return false;
            }
        }

        byte* site = (byte*)CallSite;
        for (int i = 0; i < OriginalCall.Length; i++)
        {
            if (site[i] != OriginalCall[i])
            {
                diagnostic = $"RunScript call mismatch at +{i}; area-script observer not installed";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 96, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed; area-script observer not installed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;
        s[n++] = 0x9C; // pushfd
        s[n++] = 0x60; // pushad
        // Before the hook call, [esp] is the CExoString* and [esp+4] is OBJECT_SELF.
        // After pushfd+pushad those original arguments are at +0x24 and +0x28.
        s[n++] = 0xFF; s[n++] = 0x74; s[n++] = 0x24; s[n++] = 0x28; // push object id
        s[n++] = 0xFF; s[n++] = 0x74; s[n++] = 0x24; s[n++] = 0x28; // push CExoString* (shifted)

        nint callback = (nint)(delegate* unmanaged[Cdecl]<uint, uint, void>)&OnAreaEnterScript;
        s[n++] = 0xE8;
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;
        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x08; // cdecl cleanup
        s[n++] = 0x61; // popad
        s[n++] = 0x9D; // popfd

        s[n++] = 0xE8; // the original VM call
        *(int*)(s + n) = (int)(RunScript - (stub + n + 4));
        n += 4;
        s[n++] = 0xE9; // continue after the original call
        *(int*)(s + n) = (int)(Resume - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(CallSite, (nuint)OriginalCall.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed; area-script observer not installed";
            return false;
        }

        site[0] = 0xE9;
        *(int*)(site + 1) = (int)(stub - (CallSite + 5));
        NativeMethods.VirtualProtect(CallSite, (nuint)OriginalCall.Length, old, out _);
        FlushInstructionCache((nint)(-1), CallSite, (nuint)OriginalCall.Length);

        _installed = true;
        diagnostic = $"CSWSArea::EventHandler OnEnter dispatch observer at 0x{CallSite:X} (stub 0x{stub:X})";
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAreaEnterScript(uint exoString, uint objectId)
    {
        try
        {
            SharedBlockView? view = _view;
            ScriptObserver? observer = Observer;
            if (view is null || observer is null
                || exoString is <= 0x10000 or >= 0x7FFF0000
                || !NativeMethods.IsReadable((nint)exoString, sizeof(uint)))
            {
                return;
            }

            uint chars = *(uint*)exoString;
            if (chars is <= 0x10000 or >= 0x7FFF0000
                || !NativeMethods.IsReadable((nint)chars, 1))
            {
                return;
            }

            byte* name = stackalloc byte[32];
            int nameLength = 0;
            for (; nameLength < 31; nameLength++)
            {
                if (!NativeMethods.IsReadable((nint)(chars + (uint)nameLength), 1))
                {
                    return;
                }

                byte value = *((byte*)chars + nameLength);
                if (value == 0)
                {
                    break;
                }

                name[nameLength] = value;
            }

            if (nameLength == 31)
            {
                return;
            }

            byte* module = stackalloc byte[SharedBlock.ModuleNameLength];
            int moduleLength = Odyssey.ReadCurrentModuleName(module, SharedBlock.ModuleNameLength);
            if (moduleLength <= 0 || moduleLength >= SharedBlock.ModuleNameLength)
            {
                return;
            }

            uint sequence = unchecked(++_dispatchSequence);
            bool isClient = view.TryGetHostPartyRoster(out _, out _);
            observer(
                new ReadOnlySpan<byte>(module, moduleLength),
                new ReadOnlySpan<byte>(name, nameLength),
                objectId,
                isClient,
                sequence);
        }
        catch
        {
            // This hook observes only; a managed failure cannot prevent the engine's script call.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}
