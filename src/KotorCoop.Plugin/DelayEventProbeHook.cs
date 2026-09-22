using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// A DIAGNOSTIC, default-OFF (<c>coop.ini delay_probe=1</c>) instrument for the one question
/// docs/research/minigames.md §14-§22 could not answer in eight rounds: why does
/// <c>k_ptar_racecs0</c>'s <c>DelayCommand(7.0, AssignCommand(GetFirstPC(), ...))</c> — and
/// <c>k_ptar_bekrace</c>'s bare <c>DelayCommand(0.1, StartNewModule(...))</c> — never dispatch?
///
/// Four read-only splices, one on each gate a delayed script situation has to pass. Static
/// disassembly of the GOG v1.0.3 binary (<c>tools/disasm.py</c>) established every one of them
/// this round; the probe exists to say WHICH one actually bites at runtime rather than guessing:
///
/// <code>
/// CSWVirtualMachineCommands::ExecuteCommandDelayCommand   0x0052FE30
///   0x0052FE72  mov eax,[esi+4]                 ; implementer+0x04 = "has caller object"
///   0x0052FE7D  jne 0x52FEF6                    ; != 1  -> FREE THE SITUATION, RETURN 0. SILENT.
///   0x0052FE91  call 0x4AED70                   ; CServerExoApp::GetObjectArray
///   0x0052FE98  call 0x4D8230                   ; hash lookup: al=0 found, al=1 NOT found
///   0x0052FEA3  jne 0x52FEF6                    ; caller id unresolvable -> SILENT DROP
///   0x0052FEE7  call 0x4B08D0                   ; CServerAIMaster::AddEventDeltaTime
///
/// CServerAIMaster::UpdateState                            0x004B0B70  (the drain)
///   0x004B0D40  call 0x4ADFA0                   ; CWorldTimer::CompareWorldTimes; >0 = not due yet
///   0x004B0D99  call 0x4D8230                   ; resolve the event's own object id
///   0x004B0DA4  jne 0x4B0E20                    ; gone -> DeleteEventData. SILENT.
///   0x004B0DA6  mov ecx,[esp+0x34] / mov al,[ecx+8]   ; the object's TYPE byte
///   0x004B0DB5  jbe/jne ...                     ; type>4 -> vtbl+0x78, ==4 -> vtbl+0x28,
///                                               ; ==3 -> vtbl+0x20, anything else -> 0x4B0E20 DROP
/// </code>
///
/// The constants above were read out of the binary's own data, not assumed:
/// <c>byte[0x74666C] = 0</c> (the "found" return of the hash lookup at 0x004D8230, which returns
/// 0 on hit and 1 on miss), <c>byte[0x746633] = 4</c> and <c>byte[0x746632] = 3</c> (the AREA and
/// MODULE object types the drain special-cases), <c>dword[0x746644] = 0x7F000000</c>
/// (OBJECT_INVALID), <c>dword[0x7441B8] = 1</c> (the event type <c>DelayCommand</c> enqueues) and
/// <c>float[0x73D6FC] = 1000.0</c> (the seconds-to-milliseconds scale).
///
/// Same discipline as <see cref="RemovePartyMemberHook"/>, which this is modelled on: verify the
/// bytes before patching, keep an independent 4-byte anchor immediately before each splice, and
/// do NOTHING inside the stub except record into a preallocated ring — no allocation, no engine
/// calls, no file I/O on the game's thread mid-dispatch. <see cref="Drain"/> turns the ring into
/// log lines from the frame hook.
///
/// ⚠ Purely observational: every splice re-executes the exact bytes it relocated and jumps back.
/// Nothing here changes a value the engine reads.
/// </summary>
internal static unsafe class DelayEventProbeHook
{
    // ---- ring ----------------------------------------------------------------------------

    public const int Capacity = 512;

    /// <summary>Stop recording after this many entries, so a probe left on by accident cannot
    /// fill a disk: the interesting window is the few seconds around one dialogue.</summary>
    private const uint MaxEntries = 40000;

    /// <summary>1 = DelayCommand entry, 2 = AddEventDeltaTime, 3 = an event came due and its
    /// object resolved, 4 = an event was discarded by the drain.</summary>
    private static readonly byte[] Kind = new byte[Capacity];

    private static readonly uint[] A = new uint[Capacity];
    private static readonly uint[] B = new uint[Capacity];
    private static readonly uint[] C = new uint[Capacity];
    private static readonly uint[] D = new uint[Capacity];
    private static readonly uint[] Serials = new uint[Capacity];

    /// <summary>Room for one resref per ring slot, preallocated: kind 5 (a dialogue node's own
    /// action script) is the one record that carries a name, and the stub that fills it runs on
    /// the game's thread mid-dispatch, where an allocation is not allowed.</summary>
    private const int NameLength = 32;

    private static readonly byte[] Names = new byte[Capacity * NameLength];

    private static int _write;
    private static uint _sequence;
    private static uint _drained;
    private static bool _installed;

    /// <summary>
    /// coop.ini <c>delay_probe_events=1</c> — also record the two HIGH-VOLUME channels (every
    /// <c>AddEventDeltaTime</c> enqueue and every event the drain brings due). MEASURED on the
    /// 2026-08-29 round: ~1200 enqueues and ~1100 dispatches in a few minutes of ordinary play,
    /// two file appends each, which showed up as 500-750 ms main-thread stalls in the plugin's
    /// own cinematic watch. The three channels that actually answer the question this probe
    /// exists for — a <c>DelayCommand</c> call and its gate values, a DISCARDED event, and a
    /// dialogue node's own action script — are rare and stay on whenever the probe is armed.
    /// </summary>
    private static bool _recordEvents;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x1000 | 0x2000;

    // ---- splice sites --------------------------------------------------------------------

    /// <summary>ExecuteCommandDelayCommand's own entry — <c>ecx</c> is still the implementer.</summary>
    private const nint DelayEntry = 0x0052FE30;

    private static ReadOnlySpan<byte> DelayEntryAnchor => [0x90, 0x90, 0x90, 0x90];

    /// <summary><c>sub esp,0xC</c> / <c>push esi</c> / <c>lea eax,[esp+8]</c> — all three are
    /// position-independent and are re-executed verbatim in the stub.</summary>
    private static ReadOnlySpan<byte> DelayEntryPrologue =>
        [0x83, 0xEC, 0x0C, 0x56, 0x8D, 0x44, 0x24, 0x08];

    private const nint DelayEntryResume = 0x0052FE38;

    /// <summary>CServerAIMaster::AddEventDeltaTime's own entry.</summary>
    private const nint AddEvent = 0x004B08D0;

    private static ReadOnlySpan<byte> AddEventAnchor => [0xC0, 0xC2, 0x08, 0x00];

    /// <summary><c>mov eax, dword ptr [0x7A39FC]</c> — a fixed-global read, relocatable.</summary>
    private static ReadOnlySpan<byte> AddEventPrologue => [0xA1, 0xFC, 0x39, 0x7A, 0x00];

    private const nint AddEventResume = 0x004B08D5;

    /// <summary>The drain, immediately past the "the event's object still exists" branch.</summary>
    private const nint Due = 0x004B0DA6;

    private static ReadOnlySpan<byte> DueAnchor => [0x74, 0x00, 0x75, 0x7A];

    /// <summary><c>mov ecx,[esp+0x34]</c> / <c>mov al,[ecx+8]</c>.</summary>
    private static ReadOnlySpan<byte> DuePrologue => [0x8B, 0x4C, 0x24, 0x34, 0x8A, 0x41, 0x08];

    private const nint DueResume = 0x004B0DAD;

    /// <summary>The drain's single discard block (both "object gone" and "unhandled type" land
    /// here) — <c>push edi</c> / <c>mov edi,[esp+0x14]</c>, then DeleteEventData.</summary>
    private const nint Discard = 0x004B0E20;

    private static ReadOnlySpan<byte> DiscardAnchor => [0xF1, 0xFE, 0xFF, 0xFF];

    private static ReadOnlySpan<byte> DiscardPrologue => [0x57, 0x8B, 0x7C, 0x24, 0x14];

    private const nint DiscardResume = 0x004B0E25;

    // ---- install -------------------------------------------------------------------------

    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        _recordEvents = PluginConfig.ReadBool("delay_probe_events", false);

        nint delayCb = (nint)(delegate* unmanaged[Cdecl]<uint, void>)&OnDelayCommand;
        nint addCb = (nint)(delegate* unmanaged[Cdecl]<uint, uint, uint, uint, void>)&OnAddEvent;
        nint dueCb = (nint)(delegate* unmanaged[Cdecl]<uint, void>)&OnDue;
        nint dropCb = (nint)(delegate* unmanaged[Cdecl]<uint, void>)&OnDiscard;

        // ExecuteCommandDelayCommand: the implementer is still in ecx at the entry, so one
        // cdecl argument carries both gate values (read managed-side off the pointer).
        if (!Splice(DelayEntry, DelayEntryAnchor, DelayEntryPrologue, DelayEntryResume,
                delayCb, PushKind.Ecx, 0, out string a))
        {
            diagnostic = $"DelayCommand entry: {a}";
            return false;
        }

        // AddEventDeltaTime(int, int deltaMs, uint objId2, uint objId1, int eventType, void*):
        // four contiguous stack slots forwarded with the SAME displacement four times, because
        // every push moves esp down by exactly the 4 bytes that separate the next argument.
        if (!Splice(AddEvent, AddEventAnchor, AddEventPrologue, AddEventResume,
                addCb, PushKind.FourStackArgs, 0x34, out string b))
        {
            diagnostic = $"AddEventDeltaTime: {b}";
            return false;
        }

        // The drain, after the resolve succeeded: [esp+0x34] holds the resolved object pointer
        // (0x20 further along once pushad has run).
        if (!Splice(Due, DueAnchor, DuePrologue, DueResume, dueCb, PushKind.OneStackArg, 0x54, out string c))
        {
            diagnostic = $"drain due: {c}";
            return false;
        }

        // The discard block: the same slot, which the hash lookup zeroes on a miss — so a 0
        // here says "the event's object no longer exists" and a non-zero says "it exists but
        // its type byte matched none of the three dispatch branches".
        if (!Splice(Discard, DiscardAnchor, DiscardPrologue, DiscardResume, dropCb,
                PushKind.OneStackArg, 0x54, out string d))
        {
            diagnostic = $"drain discard: {d}";
            return false;
        }

        _installed = true;
        diagnostic = "delayed-event probe armed on the four delayed-event gates";
        return true;
    }

    private enum PushKind
    {
        /// <summary>Forward <c>ecx</c> (a thiscall <c>this</c>) as the one cdecl argument.</summary>
        Ecx,

        /// <summary>Forward one stack slot at the given post-<c>pushad</c> displacement.</summary>
        OneStackArg,

        /// <summary>Forward four CONTIGUOUS stack slots, highest first, all read through the
        /// same displacement — see <see cref="Install"/>'s own comment.</summary>
        FourStackArgs,

    }

    private static bool Splice(
        nint site,
        ReadOnlySpan<byte> anchor,
        ReadOnlySpan<byte> prologue,
        nint resume,
        nint callback,
        PushKind push,
        int displacement,
        out string diagnostic)
    {
        byte* anchorSite = (byte*)(site - anchor.Length);
        for (int i = 0; i < anchor.Length; i++)
        {
            if (anchorSite[i] != anchor[i])
            {
                diagnostic = $"anchor mismatch at +{i} (0x{anchorSite[i]:X2})";
                return false;
            }
        }

        byte* target = (byte*)site;
        for (int i = 0; i < prologue.Length; i++)
        {
            if (target[i] != prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} (0x{target[i]:X2})";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 96, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;
        int args;

        s[n++] = 0x60; // pushad

        switch (push)
        {
            case PushKind.Ecx:
                s[n++] = 0x51; // push ecx
                args = 1;
                break;

            case PushKind.OneStackArg:
                s[n++] = 0xFF; s[n++] = 0xB4; s[n++] = 0x24; // push dword ptr [esp+disp32]
                *(int*)(s + n) = displacement;
                n += 4;
                args = 1;
                break;

            default:
                for (int i = 0; i < 4; i++)
                {
                    s[n++] = 0xFF; s[n++] = 0xB4; s[n++] = 0x24;
                    *(int*)(s + n) = displacement;
                    n += 4;
                }

                args = 4;
                break;
        }

        s[n++] = 0xE8; // call callback
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = (byte)(args * 4); // add esp, N (cdecl cleanup)
        s[n++] = 0x61;                                           // popad

        for (int i = 0; i < prologue.Length; i++)
        {
            s[n++] = prologue[i];
        }

        s[n++] = 0xE9; // jmp resume
        *(int*)(s + n) = (int)(resume - (stub + n + 4));
        n += 4;

        if (!NativeMethods.VirtualProtect(site, (nuint)prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        target[0] = 0xE9;
        *(int*)(target + 1) = (int)(stub - (site + 5));
        for (int i = 5; i < prologue.Length; i++)
        {
            target[i] = 0x90;
        }

        NativeMethods.VirtualProtect(site, (nuint)prologue.Length, old, out _);

        diagnostic = $"stub 0x{stub:X}";
        return true;
    }

    // ---- callbacks -----------------------------------------------------------------------

    private static void Record(byte kind, uint a, uint b, uint c, uint d)
    {
        if (_sequence >= MaxEntries)
        {
            return;
        }

        int slot = _write;
        Kind[slot] = kind;
        A[slot] = a;
        B[slot] = b;
        C[slot] = c;
        D[slot] = d;
        Serials[slot] = ++_sequence; // last: the drain trusts a slot only once this is set
        _write = (slot + 1) % Capacity;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDelayCommand(uint implementer)
    {
        try
        {
            if (implementer is > 0x10000 and < 0x7FFF0000)
            {
                Record(1, *(uint*)(implementer + 4), *(uint*)(implementer + 8), 0, 0);
            }
        }
        catch
        {
            // Never let a managed fault unwind into the engine.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAddEvent(uint deltaMs, uint objId2, uint objId1, uint eventType)
    {
        try
        {
            if (_recordEvents)
            {
                Record(2, deltaMs, objId2, objId1, eventType);
            }
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDue(uint objectPointer)
    {
        try
        {
            if (!_recordEvents)
            {
                return;
            }

            uint type = 0xFFFFFFFF;
            if (objectPointer is > 0x10000 and < 0x7FFF0000)
            {
                type = *(byte*)(objectPointer + 8);
            }

            Record(3, objectPointer, type, 0, 0);
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDiscard(uint objectPointer)
    {
        try
        {
            Record(4, objectPointer, 0, 0, 0);
        }
        catch
        {
        }
    }

    /// <summary>
    /// One dialogue node's own action script, at the instant <c>CSWSDialog::RunScript</c> hands
    /// it to the VM. <paramref name="exoString"/> is the ADDRESS of the caller's local
    /// <c>CExoString</c> ({ char* CString; uint Length; }) — read to the first null, never
    /// through the length, per this project's own CExoString-length-includes-the-null rule.
    /// </summary>
    private static void RecordDialogueScript(uint exoString, uint objectId)
    {
        try
        {
            int slot = _write;
            if (_sequence >= MaxEntries)
            {
                return;
            }

            int at = slot * NameLength;
            for (int i = 0; i < NameLength; i++)
            {
                Names[at + i] = 0;
            }

            if (exoString is > 0x10000 and < 0x7FFF0000)
            {
                uint chars = *(uint*)exoString;
                if (chars is > 0x10000 and < 0x7FFF0000)
                {
                    for (int i = 0; i < NameLength - 1; i++)
                    {
                        byte b = *(byte*)(chars + (uint)i);
                        if (b == 0)
                        {
                            break;
                        }

                        Names[at + i] = b;
                    }
                }
            }

            Record(5, objectId, 0, 0, 0);
        }
        catch
        {
        }
    }

    /// <summary>Called by the single shared CSWSDialog::RunScript splice. This probe only
    /// records while its own four delayed-event sites are installed; the production action
    /// gate remains the sole owner of the dialogue splice.</summary>
    internal static void ObserveDialogueScript(uint exoString, uint objectId)
    {
        if (_installed)
        {
            RecordDialogueScript(exoString, objectId);
        }
    }

    // ---- drain ---------------------------------------------------------------------------

    /// <summary>Turns whatever the stubs recorded since the last call into log lines. Called
    /// from the frame hook, on the game's own thread, where file I/O is safe.</summary>
    public static void Drain()
    {
        if (!_installed || _sequence == _drained)
        {
            return;
        }

        uint from = _sequence - _drained > Capacity ? _sequence - Capacity : _drained;
        for (uint serial = from + 1; serial <= _sequence; serial++)
        {
            int slot = (int)((serial - 1) % Capacity);
            if (Serials[slot] != serial)
            {
                continue;
            }

            switch (Kind[slot])
            {
                case 1:
                    PluginHost.Log($"delayprobe: DelayCommand hasCaller={A[slot]} "
                        + $"self=0x{B[slot]:X}"
                        + (A[slot] == 1 ? "" : "  <-- GATE 1 FAILS: the call is a silent no-op"));
                    break;

                case 2:
                    PluginHost.Log($"delayprobe: enqueue obj=0x{C[slot]:X} obj2=0x{B[slot]:X} "
                        + $"type={D[slot]} in {A[slot]} ms");
                    break;

                case 3:
                    PluginHost.Log($"delayprobe: due obj=0x{A[slot]:X} objtype={B[slot]}");
                    break;

                case 4:
                    PluginHost.Log($"delayprobe: DISCARD resolved=0x{A[slot]:X}"
                        + (A[slot] == 0
                            ? "  <-- the event's object no longer exists"
                            : "  <-- object exists, its type byte matched no dispatch branch"));
                    break;

                case 5:
                    int at = slot * NameLength;
                    int len = 0;
                    while (len < NameLength && Names[at + len] != 0)
                    {
                        len++;
                    }

                    PluginHost.Log("delayprobe: dialogue node script '"
                        + System.Text.Encoding.ASCII.GetString(Names, at, len)
                        + $"' runs as self=0x{A[slot]:X}");
                    break;
            }
        }

        _drained = _sequence;
    }
}
