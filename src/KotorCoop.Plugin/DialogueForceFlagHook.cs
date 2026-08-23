using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Answers one question <see cref="DialogueGateHook"/> cannot answer from its own hook site:
/// was the local conversation that is about to start a CRITICAL/FIXED point the vanilla engine
/// itself would force on the player, or an ordinary player-clicked chat? Item 1's whole design
/// (docs/research/dialogue-sync.md's 2026-08-23 section) rests on this bit: only a forced
/// conversation should ever pull the OTHER player in automatically, matching vanilla's own
/// forcing rather than inventing new behaviour.
///
/// ── The static finding (docs/research/dialogue-join-latency.md's "Far-join live pass",
/// re-derived here) ──
///
/// <c>CSWSObject::AIActionDialogObject 0x0057A470</c> is the AI-action dispatcher that turns a
/// queued <c>ActionStartConversation</c> into the walk/range check and, once satisfied, the
/// actual <c>BeginConversation</c>/<c>StartDialog</c> call. Its distance gate is a flat
/// 10.0-unit threshold at <c>[0x747450]</c> (verified live in the prior round), and TWO flags on
/// the action struct — the parameter at <c>[esp+0xb4]</c> at function entry, i.e. the function's
/// own first stack argument — gate the whole block:
///
/// <code>
/// 0x0057A4E5  mov edx, [ebx+0x48]        ; ebx = action struct
/// 0x0057A4F9  mov [esp+0x24], edx
/// ...
/// 0x0057A74A  mov eax, [esp+0x24]        ; RE-DERIVED here: same slot, unmoved by the
/// 0x0057A750  test eax, eax              ;   intervening calls (all thiscall/stdcall, esp
/// 0x0057A756  jne 0x57a9d7               ;   balanced) — VERIFIED by tracing the store to
///                                        ;   the read, not merely inferred from the doc.
/// </code>
///
/// Following the jump lands in the SAME dispatch the "already close enough" fast path uses —
/// VERIFIED by disasm, so the flag does not just relax the range, it skips the walk entirely.
/// The field's IDENTITY as NWScript's <c>bIgnoreStartRange</c> (the fifth
/// <c>ActionStartConversation</c> parameter) is INFERRED from position and effect, not
/// DB-named. A script-forced call (<c>ActionStartConversation(obj, "", FALSE, FALSE, TRUE)</c>,
/// the shape every ambush/cutscene conversation uses so distance cannot block it) sets this;
/// <c>HandlePlayerToServerInputMessage</c>'s dialog branch — the path a real player CLICK takes
/// — builds the "SAME action from... hardcoded, mostly-zeroed pushes" (dialogue-join-latency.md,
/// already read), which structurally cannot set it. That is the whole signal this hook taps.
///
/// ── Why this needs its OWN hook, not a read at <see cref="DialogueGateHook"/>'s existing site ──
///
/// <c>StartDialog</c>'s only durable global companion, <c>CServerExoAppInternal+0x1B910</c>
/// (<c>action_conversation</c>), is VERIFIED by this round's own disasm of its three setter call
/// sites (all <c>push 1</c> before the call) to be a re-entrancy BOOLEAN, not the action struct
/// pointer dialogue-sync.md's prose suggested — so it carries no flag to read. The struct that
/// does is a parameter local to <c>AIActionDialogObject</c>'s own stack frame and does not
/// survive into <c>StartDialog</c>. Reading it needs a tap at the site where it is still alive,
/// which is exactly what "a new minimal read at the same site" in the round's brief allows.
///
/// ── The hook itself ──
///
/// A read-only, non-refusing whole-function-prologue detour, same discipline as
/// <see cref="DialogueGateHook"/>: verify the SEH prologue bytes before patching, relocate them
/// (all three instructions are position-independent — an immediate, an absolute-addressed
/// segment read, an absolute constant — so they move verbatim), resume normally. Captures
/// (object id, forced) into a tiny fixed table so <see cref="DialogueGateHook.OnStartDialog"/>
/// can look the SAME object id up moments later. <c>this</c> (ECX at entry) is the actor whose
/// action queue is running the conversation — the same object that becomes <c>StartDialog</c>'s
/// own <c>this</c> once it fires (both a real click and a scripted force queue
/// <c>ActionStartConversation</c> on the SPEAKING object, not the listener) — INFERRED from
/// NWN-heritage engine convention, not independently proven here; a mismatch fails safe (the
/// lookup simply misses and the conversation is treated as ordinary, today's cautious default).
///
/// <b>NEEDS-LIVE (the one item this round's brief asked to name):</b> that <c>[actionStruct+0x48]</c>
/// reads TRUE for a real ambush/forced conversation and FALSE for a real player click, on the
/// actual game, and that the object-id correlation above actually lines up in play. Nothing
/// else here is new risk beyond what <see cref="DialogueGateHook"/> already ships: read-only,
/// no engine calls, fails open to "not forced" on any doubt.
/// </summary>
internal static unsafe class DialogueForceFlagHook
{
    private const nint AIActionDialogObject = 0x0057A470;

    /// <summary>Where the relocated prologue resumes — start + the 13 relocated bytes.</summary>
    private const nint ResumeAt = 0x0057A47D;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>
    /// The SEH prologue we relocate: <c>push -1</c>, <c>mov eax, fs:[0]</c>,
    /// <c>push 0x71FEE1</c> (the handler). A DIFFERENT instruction order from
    /// <see cref="DialogueGateHook"/>'s own prologue (this function's compiler output puts the
    /// <c>mov eax,fs:[0]</c> before the handler push, not after) — same total length, still
    /// entirely position-independent, still safe to relocate verbatim.
    /// </summary>
    private static ReadOnlySpan<byte> Prologue =>
        [0x6A, 0xFF, 0x64, 0xA1, 0x00, 0x00, 0x00, 0x00, 0x68, 0xE1, 0xFE, 0x71, 0x00];

    private static bool _installed;

    /// <summary>Verify-bytes-or-refuse, like every patch in this plugin. MAIN THREAD, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)AIActionDialogObject;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not tapping the forced-conversation flag";
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

        s[n++] = 0x60; // pushad

        // eax = the action struct pointer — the function's own first stack arg, at [esp+4]
        // before pushad, i.e. [esp+0x24] (0x20 of pushad + 4) after it.
        s[n++] = 0x8B; s[n++] = 0x44; s[n++] = 0x24; s[n++] = 0x24; // mov eax, [esp+0x24]
        s[n++] = 0x50;                                             // push eax (actionStruct: 2nd/rightmost cdecl arg)

        // eax = the saved ECX (this) from the pushad block. Layout after pushad, pushed in
        // order EAX,ECX,EDX,EBX,ESP,EBP,ESI,EDI so ECX sits at [esp+0x18]; the extra "push eax"
        // above shifted everything 4 further, so +0x1C here.
        s[n++] = 0x8B; s[n++] = 0x44; s[n++] = 0x24; s[n++] = 0x1C; // mov eax, [esp+0x1C]
        s[n++] = 0x50;                                             // push eax (this: 1st/leftmost cdecl arg)

        nint callback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnAIActionDialogObject;
        s[n++] = 0xE8; // call OnAIActionDialogObject
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x08; // add esp, 8 — cdecl, we clean up
        s[n++] = 0x61;                               // popad — this (ecx) and every other
                                                       //  register comes back exactly as the
                                                       //  real function expects to find it

        for (int i = 0; i < Prologue.Length; i++) // the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9; // jmp AIActionDialogObject+13
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        if (!NativeMethods.VirtualProtect(AIActionDialogObject, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9; // jmp stub
        *(int*)(site + 1) = (int)(stub - (AIActionDialogObject + 5));
        for (int i = 5; i < Prologue.Length; i++)
        {
            site[i] = 0x90; // pad so a mid-prologue resume or disassembler sees no garbage
        }

        NativeMethods.VirtualProtect(AIActionDialogObject, (nuint)Prologue.Length, old, out _);

        _installed = true;
        diagnostic = $"forced-conversation tap at 0x{AIActionDialogObject:X} (stub 0x{stub:X})";
        return true;
    }

    // ---- the table --------------------------------------------------------------------
    //
    // Keyed by CSWSObject id (CSWSObject+4, VERIFIED offset — docs/research/dialogue-sync.md's
    // UpdateDialog gate reads the identical field). Small and fixed: this engine never runs
    // more than a handful of AI dialogue actions in flight, and a full table just evicts the
    // oldest entry round-robin rather than growing. Single-threaded by construction — both this
    // hook and DialogueGateHook's own run on the engine's own thread inside the same per-tick
    // dispatch, the same discipline every other native hook in this plugin already relies on.

    private const int TableCapacity = 8;

    /// <summary>How long a recorded (object, forced) pair stays valid. Generous relative to the
    /// one-tick gap this is really bridging (AIActionDialogObject's own dispatch calls through
    /// to StartDialog inside the SAME evaluation once its range/bypass check passes) — this only
    /// exists to bound a stale entry from an action that never completed, not because the real
    /// gap is ever this long.</summary>
    private const int TtlMs = 3000;

    private static readonly int[] _objectIds = new int[TableCapacity];
    private static readonly bool[] _forced = new bool[TableCapacity];

    /// <summary>0 = empty slot. <c>Environment.TickCount</c> can legitimately be 0 once every
    /// ~49 days of uptime; OR-ing in 1 keeps that value out of the sentinel's way, the same
    /// trick this codebase uses everywhere a tick count doubles as a presence flag.</summary>
    private static readonly int[] _tick = new int[TableCapacity];

    private static int _nextSlot;

    private static void Record(int objectId, bool forced)
    {
        int now = Environment.TickCount | 1;

        for (int i = 0; i < TableCapacity; i++)
        {
            if (_tick[i] != 0 && _objectIds[i] == objectId)
            {
                _forced[i] = forced;
                _tick[i] = now;
                return;
            }
        }

        int slot = _nextSlot;
        _nextSlot = (_nextSlot + 1) % TableCapacity;
        _objectIds[slot] = objectId;
        _forced[slot] = forced;
        _tick[slot] = now;
    }

    /// <summary>
    /// Was the most recent <c>AIActionDialogObject</c> dispatch for this object id forced (its
    /// action struct's <c>+0x48</c> nonzero), within the last <see cref="TtlMs"/>? Read-only —
    /// does not consume the entry, since <see cref="DialogueGateHook"/> may reasonably ask
    /// again for the same conversation on a later frame (a refused/retried open, for instance).
    /// </summary>
    public static bool WasForced(int objectId)
    {
        int now = Environment.TickCount;

        for (int i = 0; i < TableCapacity; i++)
        {
            if (_tick[i] != 0 && _objectIds[i] == objectId && unchecked(now - _tick[i]) is >= 0 and < TtlMs)
            {
                return _forced[i];
            }
        }

        return false;
    }

    private const int ObjectId = 0x04;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAIActionDialogObject(nint thisObj, nint actionStruct)
    {
        try
        {
            if (thisObj is <= 0x10000 or >= 0x7FFF_0000 || actionStruct is <= 0x10000 or >= 0x7FFF_0000)
            {
                return;
            }

            int objectId = *(int*)(thisObj + ObjectId);
            bool forced = *(int*)(actionStruct + 0x48) != 0;
            Record(objectId, forced);
        }
        catch
        {
            // Never let a managed fault unwind into the engine.
        }
    }
}
