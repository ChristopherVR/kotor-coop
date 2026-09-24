using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// Captures every allowlisted <c>SetLocalBoolean</c>/<c>SetLocalNumber</c> a script runs on this
/// engine (coop.ini <c>local_var_sync</c>, docs/research/quest-sync.md §8.3).
///
/// <b>WHERE.</b> The two call sites inside
/// <c>CSWVirtualMachineCommands::ExecuteCommandSetLocalValue 0x00543130</c> that reach the lower
/// <c>CSWVarTable</c> setters: 0x005431FE (<c>call SetLocalNumber 0x0059B0D0</c>) and 0x00543232
/// (<c>call SetLocalBoolean 0x0059B040</c>). Each is the setter's ONLY caller
/// (<c>tools/xrefs.py</c>), so every script write passes exactly one of them, and at that point
/// everything a network key needs is still live: the object id the VM popped, the resolved object
/// (<c>esi</c>), the table the engine chose (<c>eax</c>), the index and the value
/// (<see cref="LocalVarHookStubBuilder"/> documents the verified frame). Hooking the setters
/// themselves would lose the object; hooking the wrapper's entry would precede the pops.
///
/// <b>HOW.</b> Each splice overwrites exactly one whole 5-byte <c>call</c> (never part of an
/// instruction — docs/research/hook-prologue audit rule) after verifying 8 anchor bytes before it
/// and the call's own 5 bytes. The stub calls <see cref="OnSetLocal"/> under <c>pushad</c>, then
/// performs the original call re-encoded for its own address, then jumps back — so the engine's
/// write always happens exactly as before, even when the callback refuses.
///
/// <b>NO RE-ENTRANCY.</b> The callback makes no engine call at all: a few memory reads (module
/// name, the object's tag), a table lookup and five stores into the shared block. The apply side
/// (<see cref="LocalVarController"/>) and the <c>localvar</c> verb write through the lower setters
/// directly, which never pass these call sites, so a peer's value is never re-captured and echoed.
///
/// Installed only when <c>local_var_sync</c> is on (default OFF until live-verified).
/// ⚠ NEEDS-LIVE: the splice runs, the frame offsets hold at runtime, a real script write is
/// captured, and nothing else in the engine calls the setters indirectly.
/// </summary>
internal static unsafe class LocalVarSyncHook
{
    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;
    private const uint MemRelease = 0x8000;

    private static SharedBlockView? _view;
    private static bool _installed;

    public static bool IsInstalled => _installed;

    /// <summary>Verify both sites, build both stubs, then patch both — or patch nothing.
    /// MAIN THREAD ONLY, once.</summary>
    public static bool Install(SharedBlockView view, out string diagnostic)
    {
        _view = view;
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        if (!Verify(LocalVarHookStubBuilder.NumberCallSite, LocalVarHookStubBuilder.NumberAnchor,
                LocalVarHookStubBuilder.NumberCall, LocalVarHookStubBuilder.SetLocalNumber, out diagnostic)
            || !Verify(LocalVarHookStubBuilder.BooleanCallSite, LocalVarHookStubBuilder.BooleanAnchor,
                LocalVarHookStubBuilder.BooleanCall, LocalVarHookStubBuilder.SetLocalBoolean, out diagnostic))
        {
            return false;
        }

        nint stubs = NativeMethods.VirtualAlloc(0, (nuint)(2 * LocalVarHookStubBuilder.StubLength), MemCommitReserve, PageExecuteReadWrite);
        if (stubs == 0)
        {
            diagnostic = "VirtualAlloc failed; local variables stay per-instance";
            return false;
        }

        nint numberStub = stubs;
        nint booleanStub = stubs + LocalVarHookStubBuilder.StubLength;
        uint callback = unchecked((uint)(nint)(delegate* unmanaged[Cdecl]<uint, nint, nint, uint, int, int, void>)&OnSetLocal);

        if (LocalVarHookStubBuilder.WriteStub(
                new Span<byte>((void*)numberStub, LocalVarHookStubBuilder.StubLength), LocalVarKind.Number,
                callback, unchecked((uint)numberStub), LocalVarHookStubBuilder.SetLocalNumber,
                LocalVarHookStubBuilder.NumberCallSite) == 0
            || LocalVarHookStubBuilder.WriteStub(
                new Span<byte>((void*)booleanStub, LocalVarHookStubBuilder.StubLength), LocalVarKind.Boolean,
                callback, unchecked((uint)booleanStub), LocalVarHookStubBuilder.SetLocalBoolean,
                LocalVarHookStubBuilder.BooleanCallSite) == 0)
        {
            _ = NativeMethods.VirtualFree(stubs, 0, MemRelease);
            diagnostic = "could not build the capture stubs";
            return false;
        }

        Span<byte> numberJump = stackalloc byte[LocalVarHookStubBuilder.SiteLength];
        Span<byte> booleanJump = stackalloc byte[LocalVarHookStubBuilder.SiteLength];
        if (!LocalVarHookStubBuilder.WriteSiteJump(numberJump, unchecked((uint)numberStub), LocalVarHookStubBuilder.NumberCallSite)
            || !LocalVarHookStubBuilder.WriteSiteJump(booleanJump, unchecked((uint)booleanStub), LocalVarHookStubBuilder.BooleanCallSite))
        {
            _ = NativeMethods.VirtualFree(stubs, 0, MemRelease);
            diagnostic = "could not build the site jumps";
            return false;
        }

        NativeMethods.FlushInstructionCache((nint)(-1), stubs, (nuint)(2 * LocalVarHookStubBuilder.StubLength));

        // Both sites sit inside one function well within a page; one protection change covers
        // them, and both jumps are written only after everything above succeeded.
        nint first = (nint)LocalVarHookStubBuilder.NumberCallSite;
        nuint span = (nuint)(LocalVarHookStubBuilder.BooleanCallSite + LocalVarHookStubBuilder.SiteLength - LocalVarHookStubBuilder.NumberCallSite);
        if (!NativeMethods.VirtualProtect(first, span, PageExecuteReadWrite, out uint oldProtect))
        {
            _ = NativeMethods.VirtualFree(stubs, 0, MemRelease);
            diagnostic = "VirtualProtect failed; local variables stay per-instance";
            return false;
        }

        numberJump.CopyTo(new Span<byte>((void*)LocalVarHookStubBuilder.NumberCallSite, LocalVarHookStubBuilder.SiteLength));
        booleanJump.CopyTo(new Span<byte>((void*)LocalVarHookStubBuilder.BooleanCallSite, LocalVarHookStubBuilder.SiteLength));
        NativeMethods.FlushInstructionCache((nint)(-1), first, span);
        _ = NativeMethods.VirtualProtect(first, span, oldProtect, out _);

        _installed = true;
        diagnostic = $"SetLocalNumber/SetLocalBoolean call sites 0x{LocalVarHookStubBuilder.NumberCallSite:X}/"
            + $"0x{LocalVarHookStubBuilder.BooleanCallSite:X} (stubs 0x{numberStub:X}/0x{booleanStub:X}); "
            + $"{LocalVarWatchTable.AllowlistedCount} allowlisted slot(s)";
        return true;
    }

    private static bool Verify(uint site, ReadOnlySpan<byte> anchor, ReadOnlySpan<byte> call, uint setter, out string diagnostic)
    {
        ReadOnlySpan<byte> liveAnchor = new((void*)(site - (uint)anchor.Length), anchor.Length);
        if (!liveAnchor.SequenceEqual(anchor))
        {
            diagnostic = $"anchor mismatch before 0x{site:X}; not hooking local variables";
            return false;
        }

        ReadOnlySpan<byte> liveCall = new((void*)site, call.Length);
        if (!liveCall.SequenceEqual(call) || LocalVarHookStubBuilder.CallTarget(liveCall, site) != setter)
        {
            diagnostic = $"call mismatch at 0x{site:X} (expected call 0x{setter:X}); not hooking local variables";
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    /// <summary>
    /// Runs on the game thread between the VM's stack pops and the engine's own table write.
    /// Records and returns: no engine call, no allocation, nothing that can throw past the
    /// catch. The engine write proceeds after this regardless of what it decides.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSetLocal(uint kindValue, nint obj, nint table, uint objectId, int slot, int value)
    {
        try
        {
            SharedBlockView? view = _view;
            if (view is null || kindValue > 1)
            {
                return;
            }

            LocalVarKind kind = (LocalVarKind)kindValue;

            // Cheap gate first: the generic AI writes its own flags (boolean slot 20 and up)
            // constantly; only a watched (kind, slot) reads anything further.
            if (!LocalVarWatchTable.IsInteresting(kind, slot))
            {
                return;
            }

            // The engine chose the object's OWN table (not an area/module one), and the object
            // carries the id the VM popped, in the deterministic GIT range.
            if (obj == 0 || table != obj + Odyssey.ObjLocalVarTable
                || !LocalVarSyncRule.IsGitObjectId(objectId) || *(uint*)(obj + 0x04) != objectId)
            {
                view.CountLocalVarRefused();
                return;
            }

            byte* module = stackalloc byte[32];
            int moduleLength = Odyssey.ReadCurrentModuleName(module, 32);
            byte* tag = stackalloc byte[LocalVarWatchTable.TagCapacity];
            Odyssey.ReadObjectTag(obj, tag, LocalVarWatchTable.TagCapacity);
            int tagLength = 0;
            while (tagLength < LocalVarWatchTable.TagCapacity && tag[tagLength] != 0)
            {
                tagLength++;
            }

            if (moduleLength <= 0 || tagLength == 0)
            {
                view.CountLocalVarRefused();
                return;
            }

            uint moduleHash = LocalVarSyncRule.HashModuleAscii(new ReadOnlySpan<byte>(module, moduleLength));
            int watch = LocalVarWatchTable.Find(moduleHash, tag, tagLength, kind, slot);
            uint generation = view.LocalVarGeneration;
            if (watch < 0 || generation == 0)
            {
                view.CountLocalVarRefused();
                return;
            }

            sbyte normalised = LocalVarSyncRule.NormaliseValue(kind, value);
            uint cursor = view.PublishLocalVarCapture(
                moduleHash, LocalVarSyncRule.PackEntry((ushort)objectId, kind, (byte)slot, normalised), generation);
            LocalVarWatchTable.NoteCapture(watch, cursor);
        }
        catch
        {
            // Never let a managed fault unwind into the engine.
        }
    }

    /// <summary>
    /// The <c>localvar set</c> verb's equivalent of a script write: publishes a capture for a
    /// watched slot exactly as <see cref="OnSetLocal"/> would, so the live test can drive the
    /// whole pipe without waiting for a story script. Returns false when nothing is watched
    /// for this object/slot (or no world generation exists yet). MAIN THREAD ONLY.
    /// </summary>
    public static bool PublishConsoleWrite(nint obj, uint objectId, LocalVarKind kind, byte slot, sbyte value, out int watch)
    {
        watch = -1;
        SharedBlockView? view = _view;
        if (view is null || !LocalVarSyncRule.IsGitObjectId(objectId))
        {
            return false;
        }

        byte* module = stackalloc byte[32];
        int moduleLength = Odyssey.ReadCurrentModuleName(module, 32);
        byte* tag = stackalloc byte[LocalVarWatchTable.TagCapacity];
        Odyssey.ReadObjectTag(obj, tag, LocalVarWatchTable.TagCapacity);
        int tagLength = 0;
        while (tagLength < LocalVarWatchTable.TagCapacity && tag[tagLength] != 0)
        {
            tagLength++;
        }

        if (moduleLength <= 0 || tagLength == 0 || view.LocalVarGeneration == 0)
        {
            return false;
        }

        uint moduleHash = LocalVarSyncRule.HashModuleAscii(new ReadOnlySpan<byte>(module, moduleLength));
        watch = LocalVarWatchTable.Find(moduleHash, tag, tagLength, kind, slot);
        if (watch < 0)
        {
            return false;
        }

        uint cursor = view.PublishLocalVarCapture(
            moduleHash, LocalVarSyncRule.PackEntry((ushort)objectId, kind, slot, value), view.LocalVarGeneration);
        LocalVarWatchTable.NoteCapture(watch, cursor);
        return true;
    }
}
