using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Observes THIS instance's own engine natively firing a GENERIC trigger's OnEnter script —
/// the direction docs/research/trigger-relay.md's whole channel never watched before: a
/// client's presence in a trigger already reaches the host (<see cref="TriggerRelayController"/>'s
/// watch/request pair), but a HOST's own real character walking through the identical trigger
/// was invisible to this mod entirely, because the host's own engine just fires it directly —
/// no watch, no request, nothing to relay.
///
/// The hook point is the CALL <c>CSWSTrigger::EventHandler</c>'s event-0xC (subarea
/// entered) handler makes for the GENERIC-script case only — never the transition branch,
/// never the mine/trap branch (docs/research/trigger-relay.md §2, §5's "deliberately NOT
/// reproduced" note). VERIFIED by disassembling the exact bytes with <c>tools/disasm.py</c>
/// against <c>tools/kotor1_0_3.db</c> (GOG 1.0.3, matching every other address this codebase
/// cites from that binary):
///
/// <code>
/// 0x0058F504  8B 46 04              mov eax, dword ptr [esi + 4]      ; trigger's own id
/// 0x0058F507  6A 01                 push 1
/// 0x0058F509  50                    push eax
/// 0x0058F50A  8D 8E 4C 02 00 00     lea ecx, [esi + 0x24C]
/// 0x0058F510  51                    push ecx
/// 0x0058F511  8B 0D 00 3A 7A 00     mov ecx, dword ptr [0x7A3A00]     ; CVirtualMachine*
/// 0x0058F517  E8 A4 1A 04 00        call CVirtualMachine::RunScript (0x5D0FC0)
/// </code>
///
/// This is EventHandler's OWN reproduction of <see cref="OdysseyTriggers.FireTrigger"/> — the
/// SAME RunScript call this codebase's own relay path makes, from the engine's native code
/// instead of ours. Firing THROUGH the relay (<see cref="OdysseyTriggers.FireTrigger"/>) never
/// reaches this call site — it calls <c>RunScript</c> directly at 0x5D0FC0 — so this hook only
/// ever observes a REAL entry, never a relayed one; there is no risk of the relay observing
/// its own fire and looping.
///
/// The first 5 bytes at the hook site — <c>mov eax,[esi+4]</c> (3) then <c>push 1</c> (2) —
/// are exactly one instruction-aligned JMP's worth, landing precisely on the next instruction
/// (<c>push eax</c> at 0x0058F509). <c>esi</c> is the trigger's own <c>this</c> pointer
/// throughout <c>EventHandler</c> (every other field read in the surrounding disassembly —
/// <c>[esi+0x238]</c>, <c>[esi+0x2BC]</c>, <c>[esi+0x24C]</c> — is a documented CSWSTrigger
/// field at the SAME base), and nothing between the hook site and the eventual
/// <c>jmp 0x58F663</c> a few instructions later branches on FLAGS set before it, so the CALL
/// this stub makes (which does not preserve FLAGS) is safe — VERIFIED by reading the whole
/// branch, not merely the hook site.
///
/// Entering-object id is <c>[esi+0x29C]</c> — cached UNCONDITIONALLY at the very start of the
/// event-0xC handler (0x0058F1FA-0x0058F203, before the transition/mine/generic branch is even
/// decided; see trigger-relay.md §2 step 1), so by the time execution reaches this hook site it
/// already holds the entering object for THIS specific fire.
///
/// ⚠ INFERRED, not independently confirmed a second way (same status trigger-relay.md's own §4
/// gives the write side of this field): whether <c>GetEnteringObject()</c>, as read by the
/// SCRIPT this call runs, actually reads this exact field for a TRIGGER specifically. The bytes
/// and the call shape are VERIFIED; the semantic meaning of "entering object" for the fired
/// script is carried over from the identical claim already made (and only partially settled
/// live — §11's item 4) for the relay's OWN writes to this same field. NEEDS-LIVE: confirm a
/// real, unrelayed entry by this HOST's own character is captured, filtered correctly (only
/// entries matching this instance's own local player id, allowlist-eligible triggers only —
/// see <see cref="TriggerRelayController"/>), and reaches a peer as a fire request with no
/// crash and no double-fire beyond what §3's own documented "second engine's run may be
/// suppressed or may double-run depending on script idempotence" risk already accepts.
///
/// Runs inside every generic-script trigger fire in the game — rare compared to
/// <see cref="PhantomEffectHook"/>'s per-effect-application rate, but the same discipline
/// applies: least possible work, no engine calls, no allocation, a catch that can never unwind
/// into the engine. Filtering (which id, which peer, allowlist, eligibility) is deliberately
/// NOT done here — see <see cref="TriggerRelayController"/>'s frame-driven drain, which already
/// owns every one of those primitives and runs on the main thread where calling them is safe.
/// </summary>
internal static unsafe class TriggerFireHook
{
    /// <summary>The exact call site inside <c>CSWSTrigger::EventHandler 0x0058F140</c>'s
    /// event-0xC generic-script branch — see this class's own header for the disassembly.
    /// </summary>
    private const nint GenericFireSite = 0x0058F504;

    /// <summary>Where the relocated prologue resumes — right after the two relocated
    /// instructions, at the original <c>push eax</c>.</summary>
    private const nint ResumeAt = 0x0058F509;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary><c>mov eax, [esi+4]</c> then <c>push 1</c> — two whole instructions, 5 bytes,
    /// exactly the size of a near JMP and instruction-aligned on both ends.</summary>
    private static ReadOnlySpan<byte> Prologue => [0x8B, 0x46, 0x04, 0x6A, 0x01];

    private const int RelocatedBytes = 5;

    /// <summary>CSWSTrigger + this = the trigger's own object id — the field
    /// <see cref="OdysseyTriggers"/> documents at the same offset.</summary>
    private const int TriggerObjId = 0x04;

    /// <summary>CSWSTrigger + this = the cached entering-object id — matches
    /// <c>OdysseyTriggers.TriggerLastEntered</c>. Written by the engine unconditionally at the
    /// very start of the event-0xC handler, before this hook site is ever reached.</summary>
    private const int TriggerLastEntered = 0x29C;

    /// <summary>How many captures are remembered between drains. A generic trigger fires
    /// rarely compared to <see cref="PhantomEffectHook"/>'s per-effect rate; this only bounds
    /// the ring, not the feature — <see cref="TriggerRelayController"/> drains every frame.
    /// </summary>
    public const int Capacity = 8;

    private static readonly uint[] _triggerId = new uint[Capacity];
    private static readonly uint[] _enteringObjectId = new uint[Capacity];

    private static int _write;
    private static uint _sequence;

    /// <summary>Monotonic count of captures ever recorded. A drain remembers the value it last
    /// saw and reads forward from there — the same pattern <see cref="PhantomEffectHook.Sequence"/>
    /// establishes.</summary>
    public static uint Sequence => _sequence;

    public static void Read(int index, out uint triggerId, out uint enteringObjectId)
    {
        int i = ((index % Capacity) + Capacity) % Capacity;
        triggerId = _triggerId[i];
        enteringObjectId = _enteringObjectId[i];
    }

    /// <summary>A module change reassigns every object id, so a remembered capture would name
    /// a different trigger (or nothing at all).</summary>
    public static void Invalidate()
    {
        Array.Clear(_triggerId);
        Array.Clear(_enteringObjectId);
        _write = 0;
        _sequence = 0;
    }

    private static bool _installed;

    /// <summary>Verify-bytes-or-refuse, like every other patch here. MAIN THREAD, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)GenericFireSite;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} (saw {site[i]:X2}, expected {Prologue[i]:X2}); "
                    + "native trigger-fire observation is off";
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

        // Entered by jmp, so esi (the trigger's own `this`) is exactly what EventHandler was
        // already using — nothing to relocate for it. pushad/popad only needs to protect the
        // caller-saved registers our own `call` clobbers; esi survives untouched either way.
        s[n++] = 0x60; // pushad

        // push dword [esi+0x29c]  (entering object -- 2nd/rightmost cdecl arg)
        s[n++] = 0xFF; s[n++] = 0xB6;
        *(int*)(s + n) = TriggerLastEntered;
        n += 4;

        // push dword [esi+4]  (trigger's own id -- 1st/leftmost cdecl arg)
        s[n++] = 0xFF; s[n++] = 0x76; s[n++] = TriggerObjId;

        nint callback = (nint)(delegate* unmanaged[Cdecl]<uint, uint, void>)&OnFire;
        s[n++] = 0xE8; // call OnFire
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x08; // add esp, 8 (cdecl cleanup)
        s[n++] = 0x61; // popad

        for (int i = 0; i < RelocatedBytes; i++)
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9; // jmp GenericFireSite+5
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(GenericFireSite, (nuint)RelocatedBytes, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9; // jmp stub
        *(int*)(site + 1) = (int)(stub - (GenericFireSite + 5));
        NativeMethods.VirtualProtect(GenericFireSite, (nuint)RelocatedBytes, old, out _);

        _installed = true;
        diagnostic = $"native trigger-fire watch at 0x{GenericFireSite:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// Runs inside the engine's own generic-trigger dispatch, for every REAL entry any real
    /// character makes into an eligible trigger. Records only — filtering (which id, whose
    /// entry, allowlist, eligibility) belongs on the main thread, where the engine calls it
    /// needs are safe to make.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFire(uint triggerId, uint enteringObjectId)
    {
        try
        {
            if (triggerId == 0 || enteringObjectId == 0)
            {
                return;
            }

            int i = _write;
            _write = (_write + 1) % Capacity;

            _triggerId[i] = triggerId;
            _enteringObjectId[i] = enteringObjectId;
            _sequence++;
        }
        catch
        {
            // Never let a managed fault unwind into the engine's trigger dispatch.
        }
    }
}
