using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Captures the one-line "bark" an NPC throws up in a bubble — the utility droid's beep, a
/// guard telling you to move along — so it can be replayed on every other player's screen.
///
/// A bark is NOT a conversation: it never enters the dialogue system, so the whole dialogue
/// channel (token, spectate, reply log) sees nothing and the other players get silence where
/// one player got a line. It is its own tiny channel for that reason.
///
/// Hook point: <c>CClientExoAppInternal::BarkString 0x005F2C90</c>. That is where every bark
/// converges — <c>CServerExoApp::BarkString 0x005EDBB0</c> is a three-byte thunk into it, and
/// both the script routine (<c>ExecuteCommandBarkString 0x00548900</c>) and the AI action
/// (<c>AIActionBarkString 0x0057CE00</c>) arrive through that thunk — so one detour catches
/// them all regardless of what provoked the line.
///
/// The signature is <c>__thiscall(this, uint clientObjectId, CExoString* text,
/// CResRef* sound, int ignoreRange)</c> — <c>ret 0x10</c>, verified at 0x005F2CCD.
///
/// ⚠ TWO OF THE FOUR ARGUMENTS ARE POINTERS, AND THEY ARE WHY THE REPLAY USED TO KILL THE
/// PROCESS. They were captured and shipped verbatim, on the theory that a value carried
/// through untouched needs no interpretation; in another process they are wild addresses,
/// and the far side dereferences BOTH — <c>CResRef::GetResRefStr</c> reads 16 bytes off
/// arg3, then <c>SetBark</c> reads <c>[arg2]</c> as a <c>char*</c> and strlens it. The
/// branch at 0x005F2CA8 is not an early-out: 0x005F2CD0 re-pushes both pointers and barks
/// with OBJECT_INVALID, so no guard on the speaker could ever have saved it.
///
/// So they are DEREFERENCED HERE, where they are still valid, and the contents travel.
/// See docs/research/bark-replay.md.
///
/// The detour records and returns; it calls nothing and changes no engine state, so a bark
/// behaves identically on the instance that produced it.
/// </summary>
internal static unsafe class BarkHook
{
    private const nint BarkString = 0x005F2C90;

    /// <summary>The five bytes the jump overwrites: <c>mov eax, [0x7A39FC]</c>. One whole
    /// instruction, absolute and position-independent, so relocating it is safe.</summary>
    private static readonly byte[] Prologue = [0xA1, 0xFC, 0x39, 0x7A, 0x00];

    private static readonly nint ResumeAt = BarkString + 5;

    private const uint MemCommitReserve = 0x1000 | 0x2000;
    private const uint PageExecuteReadWrite = 0x40;

    private static bool _installed;

    /// <summary>How many barks are remembered between agent reads. Barks are occasional and
    /// the agent drains this several times a second, so a short ring is ample.</summary>
    public const int Capacity = 8;

    /// <summary>Bytes of bark text carried. Nothing in the binary bounds a bark's length;
    /// this is anchored to the dialogue channel's own line limit.</summary>
    public const int TextMax = 200;

    /// <summary>A CResRef is a fixed char[16] — NOT a C string, and not NUL-terminated.</summary>
    public const int ResRefBytes = 16;

    /// <summary>The SERVER object id. The engine's argument is the client id
    /// (<c>server | 0x80000000</c> — ServerToClientObjectId 0x005F2AC0 is literally an `or`),
    /// and everything else in this mod speaks server ids, so it is normalised at capture.</summary>
    private static readonly uint[] Speaker = new uint[Capacity];

    /// <summary>arg4, the ignore-range flag. A plain int at every call site.</summary>
    private static readonly uint[] Flags = new uint[Capacity];

    private static readonly byte[] Text = new byte[Capacity * TextMax];
    private static readonly int[] TextLen = new int[Capacity];
    private static readonly byte[] ResRef = new byte[Capacity * ResRefBytes];
    private static readonly uint[] Serials = new uint[Capacity];

    private static int _write;
    private static uint _sequence;

    /// <summary>Ticks once per captured bark; the agent publishes what is new since it last
    /// looked and nothing else.</summary>
    public static uint Sequence => _sequence;

    /// <summary>
    /// True while this instance is REPLAYING a peer's bark, so the detour does not capture it
    /// and hand it straight back. Same-thread and same-call-chain: it is read inside the call
    /// it brackets, exactly like <see cref="DialogueReplyHook.AllowReplay"/>.
    /// </summary>
    public static bool Replaying { get; set; }

    public static void Read(
        int index, out uint speaker, out uint flags, out uint serial,
        Span<byte> text, out int textLength, Span<byte> resRef)
    {
        int i = ((index % Capacity) + Capacity) % Capacity;
        serial = Serials[i];
        speaker = Speaker[i];
        flags = Flags[i];

        textLength = Math.Min(TextLen[i], text.Length);
        for (int b = 0; b < textLength; b++)
        {
            text[b] = Text[(i * TextMax) + b];
        }

        for (int b = 0; b < ResRefBytes && b < resRef.Length; b++)
        {
            resRef[b] = ResRef[(i * ResRefBytes) + b];
        }
    }

    /// <summary>Forgets every captured bark. A module change invalidates the object ids the
    /// entries name, so replaying one afterwards would talk about a different creature.</summary>
    public static void Invalidate()
    {
        for (int i = 0; i < Capacity; i++)
        {
            Serials[i] = 0;
            Speaker[i] = 0;
            Flags[i] = 0;
            TextLen[i] = 0;
        }

        _write = 0;
        _sequence = 0;
    }

    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)BarkString;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not hooking barks";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 64, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;

        // Entered by JMP, so esp still points at the caller's return address and the four
        // stack arguments sit at [esp+4..0x10]. pushad shifts those by 0x20.
        s[n++] = 0x60;                                       // pushad

        // push arg4, arg3, arg2, arg1 (cdecl callback takes them left to right)
        foreach (int offset in (ReadOnlySpan<int>)[0x30, 0x2C, 0x28, 0x24])
        {
            s[n++] = 0xFF; s[n++] = 0xB4; s[n++] = 0x24;     // push dword [esp+offset]
            *(int*)(s + n) = offset;
            n += 4;
        }

        nint callback = (nint)(delegate* unmanaged[Cdecl]<uint, uint, uint, uint, void>)&OnBark;
        s[n++] = 0xE8;                                       // call OnBark
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x10;         // add esp, 0x10 (cdecl cleanup)
        s[n++] = 0x61;                                       // popad

        for (int i = 0; i < Prologue.Length; i++)             // the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9;                                       // jmp BarkString+5
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(BarkString, 5, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                      // jmp stub
        *(int*)(site + 1) = (int)(stub - (BarkString + 5));
        NativeMethods.VirtualProtect(BarkString, 5, old, out _);

        _installed = true;
        diagnostic = $"bark hook at 0x{BarkString:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// Runs inside the engine's call, before its own body. Records and returns — no engine
    /// calls, no allocation, nothing that can throw: this is the game's thread mid-dispatch.
    /// </summary>
    /// <summary>A pointer plausible enough to read: user-mode, and not a small integer that
    /// happens to be sitting in an argument slot. Cheap, and no engine call — this runs on
    /// the game's thread inside the dispatch.</summary>
    private static bool Readable(uint p) => p >= 0x00010000 && p < 0x7FFF0000;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBark(uint clientObjectId, uint textPtr, uint resRefPtr, uint flags)
    {
        try
        {
            if (Replaying || clientObjectId is 0 or Odyssey.ObjectInvalid)
            {
                return; // a peer's bark we are playing back, not one of ours to publish
            }

            if (!Readable(textPtr) || !Readable(resRefPtr))
            {
                return;
            }

            int slot = _write;
            int n = 0;

            // CExoString: char* at +0, length at +4 — and ⚠ THE LENGTH INCLUDES THE NUL
            // (CExoString::Constructor_3 0x005E5B00 does `inc eax` before storing it). Cut at
            // the first real NUL as well; three bugs in this repo have come from that field.
            nint cstr = *(nint*)textPtr;
            uint len = *(uint*)(textPtr + 4);

            if (cstr != 0 && len is > 0 and < 4096 && Readable((uint)cstr))
            {
                int cap = Math.Min((int)len - 1, TextMax);
                byte* p = (byte*)cstr;
                while (n < cap && p[n] != 0)
                {
                    Text[(slot * TextMax) + n] = p[n];
                    n++;
                }
            }

            // CResRef is a fixed char[16], so copy all 16 bytes verbatim rather than treating
            // it as a string. An EMPTY resref is normal — a TLK row with no voice-over.
            byte* r = (byte*)resRefPtr;
            for (int b = 0; b < ResRefBytes; b++)
            {
                ResRef[(slot * ResRefBytes) + b] = r[b];
            }

            Speaker[slot] = clientObjectId & 0x7FFFFFFF;   // back to a server id
            Flags[slot] = flags;
            TextLen[slot] = n;

            // Serial LAST: the agent reads a slot only once its serial says it is complete.
            Serials[slot] = ++_sequence;

            _write = (slot + 1) % Capacity;
        }
        catch
        {
            // Never let a managed fault unwind into the engine.
        }
    }
}
