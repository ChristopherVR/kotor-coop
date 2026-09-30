using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// Read-only observers at the two verified retail load dispatches: the selected main-menu
/// save entry and the server's native LoadGame request. They publish the exact slot/name
/// before the original function begins; this records intent, not load completion or identity.
/// </summary>
internal static unsafe class LoadProvenanceHook
{
    private const nint MainMenuLoadPCAutoSave = 0x006CA250;
    private const nint ServerLoadGame = 0x004BA640;
    private const nint MainMenuResume = 0x006CA256;
    private const nint ServerResume = 0x004BA646;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;
    private static ReadOnlySpan<byte> Prologue => [0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8];
    private static ReadOnlySpan<byte> Following => [0x6A, 0xFF];

    private static SharedBlockView? _view;
    private static bool _installed;
    private static uint _generation;
    private static string? _savesDirectory;

    /// <summary>Both observers are in. Version 76: a world with no observed load request is a
    /// new-game candidate only while this holds.</summary>
    public static bool Installed => _installed;

    /// <summary>The generation last published (0 = never). Version 76 attributes a changed
    /// value to the next world-up.</summary>
    public static uint PublishedGeneration => _generation;

    /// <summary>Whether the last published request was made with no world standing (the main
    /// menu, or after the in-session replay destroyed the server).</summary>
    public static bool LastPublishedWithoutWorld { get; private set; }

    public static bool Install(SharedBlockView view, out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;
        char* executable=stackalloc char[512];
        uint executableLength=NativeMethods.GetModuleFileName(0,executable,512);
        if(executableLength is >0 and <512)
        {
            string? game=Path.GetDirectoryName(new string(executable,0,(int)executableLength));
            if(game is not null)_savesDirectory=Path.Combine(game,"saves");
        }
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        if (!Matches(MainMenuLoadPCAutoSave) || !Matches(ServerLoadGame))
        {
            diagnostic = "native load entry bytes differ; provenance observers not installed";
            return false;
        }

        nint menuStub = BuildStub(
            MainMenuResume,
            (nint)(delegate* unmanaged[Cdecl]<uint, void>)&OnMainMenuLoad,
            server: false);
        nint serverStub = BuildStub(
            ServerResume,
            (nint)(delegate* unmanaged[Cdecl]<int, uint, void>)&OnServerLoad,
            server: true);
        if (menuStub == 0 || serverStub == 0)
        {
            diagnostic = "VirtualAlloc failed; native load observers not installed";
            return false;
        }

        if (!NativeMethods.VirtualProtect(MainMenuLoadPCAutoSave, (nuint)Prologue.Length, PageExecuteReadWrite, out uint menuOld))
        {
            diagnostic = "VirtualProtect failed at main-menu load entry";
            return false;
        }

        if (!NativeMethods.VirtualProtect(ServerLoadGame, (nuint)Prologue.Length, PageExecuteReadWrite, out uint serverOld))
        {
            NativeMethods.VirtualProtect(MainMenuLoadPCAutoSave, (nuint)Prologue.Length, menuOld, out _);
            diagnostic = "VirtualProtect failed at server load entry";
            return false;
        }

        Patch(MainMenuLoadPCAutoSave, menuStub);
        Patch(ServerLoadGame, serverStub);
        NativeMethods.VirtualProtect(MainMenuLoadPCAutoSave, (nuint)Prologue.Length, menuOld, out _);
        NativeMethods.VirtualProtect(ServerLoadGame, (nuint)Prologue.Length, serverOld, out _);
        FlushInstructionCache((nint)(-1), MainMenuLoadPCAutoSave, (nuint)Prologue.Length);
        FlushInstructionCache((nint)(-1), ServerLoadGame, (nuint)Prologue.Length);

        _installed = true;
        diagnostic = $"read-only load provenance at LoadPCAutoSave 0x{MainMenuLoadPCAutoSave:X} and server LoadGame 0x{ServerLoadGame:X}";
        return true;
    }

    private static bool Matches(nint address)
    {
        byte* site = (byte*)address;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i]) return false;
        }
        for (int i = 0; i < Following.Length; i++)
        {
            if (site[Prologue.Length + i] != Following[i]) return false;
        }
        return true;
    }

    private static nint BuildStub(nint resume, nint callback, bool server)
    {
        nint stub = NativeMethods.VirtualAlloc(0, 64, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0) return 0;

        int written = LoadProvenanceStubBuilder.Write(
            new Span<byte>((void*)stub, server ? LoadProvenanceStubBuilder.ServerLength : LoadProvenanceStubBuilder.MainMenuLength),
            (uint)stub,
            (uint)callback,
            (uint)resume,
            server);
        if (written != (server ? LoadProvenanceStubBuilder.ServerLength : LoadProvenanceStubBuilder.MainMenuLength))
        {
            return 0;
        }
        FlushInstructionCache((nint)(-1), stub, (nuint)written);
        return stub;
    }

    private static void Patch(nint address, nint stub)
    {
        byte* site = (byte*)address;
        site[0] = 0xE9;
        *(int*)(site + 1) = (int)(stub - (address + 5));
        site[5] = 0x90;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMainMenuLoad(uint entry)
    {
        try
        {
            if (entry is <= 0x10000 or >= 0x7FFF0000
                || !NativeMethods.IsReadable((nint)entry + LoadProvenanceCodec.MainMenuEntryFlagsOffset, sizeof(uint)))
            {
                Publish(LoadProvenance.NoSaveSlot, LoadProvenanceSource.MainMenuUnsupportedEntry, LoadProvenanceStatus.InvalidEntry, []);
                return;
            }

            uint flags = *(uint*)((byte*)entry + LoadProvenanceCodec.MainMenuEntryFlagsOffset);
            LoadProvenanceSource source = LoadProvenanceCodec.ClassifyMainMenuEntry(flags);
            if (source == LoadProvenanceSource.MainMenuSaveEntry)
            {
                if (!NativeMethods.IsReadable((nint)entry + SharedBlock.LoadEntrySaveNumberOffset, sizeof(int))
                    || !NativeMethods.IsReadable((nint)entry + SharedBlock.LoadEntrySaveNameOffset, 8))
                {
                    Publish(LoadProvenance.NoSaveSlot, source, LoadProvenanceStatus.InvalidEntry, []);
                    return;
                }

                int slot = *(int*)((byte*)entry + SharedBlock.LoadEntrySaveNumberOffset);
                CaptureName(slot, source, (nint)((byte*)entry + SharedBlock.LoadEntrySaveNameOffset));
                return;
            }

            if (source == LoadProvenanceSource.MainMenuDirectDirectory)
            {
                if (!NativeMethods.IsReadable((nint)entry + LoadProvenanceCodec.MainMenuDirectDirectoryOffset, 8))
                {
                    Publish(LoadProvenance.NoSaveSlot, source, LoadProvenanceStatus.InvalidEntry, []);
                    return;
                }

                CaptureName(
                    LoadProvenance.NoSaveSlot,
                    source,
                    (nint)((byte*)entry + LoadProvenanceCodec.MainMenuDirectDirectoryOffset));
                return;
            }

            Publish(LoadProvenance.NoSaveSlot, source, LoadProvenanceStatus.UnsupportedEntry, []);
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnServerLoad(int slot, uint name)
    {
        try
        {
            CaptureName(slot, LoadProvenanceSource.ServerLoadRequest, (nint)name);
        }
        catch { }
    }

    private static void CaptureName(int slot, LoadProvenanceSource source, nint exoString)
    {
        if (exoString <= 0x10000 || exoString >= 0x7FFF0000
            || !NativeMethods.IsReadable(exoString, 8))
        {
            Publish(slot, source, NameStatus(source, LoadProvenanceStatus.InvalidSaveName), []);
            return;
        }

        byte* chars = (byte*)*(uint*)exoString;
        int length = *(int*)(exoString + 4);
        if ((nint)chars <= 0x10000 || (nint)chars >= 0x7FFF0000 || length <= 0)
        {
            Publish(slot, source, NameStatus(source, LoadProvenanceStatus.InvalidSaveName), []);
            return;
        }
        // CExoString's stored length includes its trailing NUL in this build (constructor
        // 0x5E5B00). Keep the mailbox name content only, but validate and account for that
        // terminator before bounding the copy.
        if (length > SharedBlock.LoadProvenanceNameCapacity + 1)
        {
            Publish(slot, source, NameStatus(source, LoadProvenanceStatus.SaveNameTooLong), []);
            return;
        }
        if (!NativeMethods.IsReadable((nint)chars, (nuint)length))
        {
            Publish(slot, source, NameStatus(source, LoadProvenanceStatus.InvalidSaveName), []);
            return;
        }

        ReadOnlySpan<byte> stored = new(chars, length);
        if (!LoadProvenanceCodec.TryStripCExoTerminator(stored, out ReadOnlySpan<byte> name))
        {
            Publish(slot, source, NameStatus(source, LoadProvenanceStatus.InvalidSaveName), []);
            return;
        }

        Publish(slot, source,
            source == LoadProvenanceSource.MainMenuDirectDirectory
                ? LoadProvenanceStatus.CapturedDirectory
                : LoadProvenanceStatus.Captured,
            name);
    }

    private static LoadProvenanceStatus NameStatus(LoadProvenanceSource source, LoadProvenanceStatus saveStatus) =>
        source == LoadProvenanceSource.MainMenuDirectDirectory
            ? saveStatus == LoadProvenanceStatus.SaveNameTooLong
                ? LoadProvenanceStatus.DirectoryNameTooLong
                : LoadProvenanceStatus.InvalidDirectoryName
            : saveStatus;

    private static void Publish(int slot, LoadProvenanceSource source, LoadProvenanceStatus status, ReadOnlySpan<byte> name)
    {
        SharedBlockView? view = _view;
        if (view is null) return;

        uint even = LoadProvenanceGeneration.Next(_generation, out uint odd);
        LastPublishedWithoutWorld = !PluginHost.WorldStanding;
        _generation = even;

        view.BeginLoadProvenanceWrite(odd);
        view.WriteLoadProvenancePayload(slot, source, status, view.WorldLoadSerial, name);
        uint serial=view.WorldLoadSerial,epoch=view.LivenessNativeEpoch;
        long requested=Environment.TickCount64;
        LocalSaveReadProof? proof=null;
        try
        {
            if(_installed && _savesDirectory is not null
                && status is LoadProvenanceStatus.Captured or LoadProvenanceStatus.CapturedDirectory
                && LocalSaveReadProofReader.TryResolve(_savesDirectory,slot,source,System.Text.Encoding.Latin1.GetString(name),out string directory))
                LocalSaveReadProofReader.TryCapture(directory,out proof);
        } catch { }
        // Failure is published too, so a later unrelated request revokes an older proof.
        if(serial!=view.WorldLoadSerial || epoch!=view.LivenessNativeEpoch)proof=null;
        view.PublishNativePartyLoadProof(even,serial,epoch,requested,proof);
        view.PublishLoadProvenanceGeneration(even);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}
