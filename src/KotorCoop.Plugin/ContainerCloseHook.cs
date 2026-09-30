using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// LIVE-PASS ADDITION (2026-08-22). Detects every REAL close of a container's loot panel —
/// <c>CSWSPlaceable::CloseInventory 0x00587560</c> running to completion.
///
/// ⚠ 2026-09-24 UPDATE: the hazard below is what this hook was originally built to fix, from
/// when the durable placeable-open channel (<see cref="PlaceableController"/> /
/// <c>KotorGameBridge.CollectChangedPlaceableState</c>) was deliberately monotonic ("a placeable
/// only ever advances ... never the reverse"). That channel now carries the placeable's CURRENT
/// open/locked state honestly, both directions, Lamport-ordered — the same door-relock fix
/// 934c18e made for doors (docs/research/door-relock.md §6) — and <c>PlaceableController.Apply</c>
/// no longer consults <see cref="WasEverClosed"/> at all: <c>PlaceableDesiredStateTracker</c>
/// gives it the same "don't re-apply an unchanged tuple, but never permanently refuse a
/// genuinely new one" guarantee <c>DoorDesiredStateTracker</c> gives doors, which is what a
/// FOREVER latch like this one cannot do (it would refuse a legitimate later re-open of the same
/// container). <see cref="Register"/> is still called from both this hook and
/// <c>OdysseyPlaceables.ClearPlaceableOpenFlag</c> (harmless bookkeeping, nothing left reads it
/// for that purpose), and the detour itself is left installed rather than ripped out — it is a
/// verified, working hook with no correctness cost to leaving it running, and removing a live
/// splice is a bigger, separate change than this pass's own scope. The hazard this class
/// documents below is historical, not current.
///
/// LIVE-CONFIRMED HAZARD this hook was built to fix (2026-08-22, before the pass above): the
/// durable channel's agent-side poll (<c>KotorGameBridge.CollectChangedPlaceableState</c>)
/// sampled every placeable's <c>+0x338</c> once a second; once it had EVER seen a container
/// open, it kept re-asserting that fact onto every instance forever (by design — the channel
/// could not express "closed", only "opened"). Reproduced live: host opens a footlocker, closes
/// it through <see cref="OdysseyPlaceables.CloseInventoryReal"/> — <c>+0x338</c> genuinely
/// clears — and within about 100ms <c>PlaceableController.Apply</c> wrote it straight back to 1
/// ("placeable 0x...: marked opened on the session's authority" in the plugin log), on BOTH
/// instances, permanently, with no interaction lock held by anyone. Without this hook the
/// container-sync close feature worked for a single frame and then silently failed for the rest
/// of the session — a durable regression, not a cosmetic flicker.
///
/// The original fix: every <c>CloseInventory</c> call (never refused — closing a container must
/// never be blocked, unlike opening one) LATCHED the id as closed for the rest of this module
/// load; <see cref="PlaceableController"/> checked <see cref="WasEverClosed"/> before writing
/// <c>Odyssey.SetPlaceableOpen</c> and skipped the write once a real close had been seen. A
/// LATCH, not a timer — the durable channel's reassertion pressure was continuous (the agent
/// kept re-polling <c>+0x338</c> and re-broadcasting "open" forever once it had ever seen 1), so
/// a time-boxed suppression was tried first and live-confirmed to lose the race (see the
/// suppression table's own remarks below for the measurement).
///
/// Splice technique and prologue-verification discipline are identical to
/// <see cref="ContainerUseHook"/> (see that class's remarks for why a jmp-splice at the VERIFIED
/// SEH prologue is safe) — <c>CloseInventory</c>'s own prologue disassembles byte-for-byte
/// identical to <c>OpenInventory</c>'s (both are ordinary MSVC <c>__thiscall</c> member
/// functions using the same catch-frame idiom; <c>tools/disasm.py 0x587560 40</c>). Unlike
/// <see cref="ContainerUseHook"/> this hook never refuses: it falls through to the relocated
/// prologue unconditionally, so the stub has no branch at all.
/// </summary>
internal static unsafe class ContainerCloseHook
{
    private const nint CloseInventory = 0x00587560;

    /// <summary>Where the relocated prologue resumes: right after <c>mov esi,ecx</c>, at the
    /// original <c>mov eax,[esi+0x338]</c> — identical offset to <see cref="ContainerUseHook"/>'s
    /// own <c>ResumeAt</c>, because the two functions share the same prologue shape.</summary>
    private const nint ResumeAt = 0x00587579;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>IDENTICAL to <see cref="ContainerUseHook.Prologue"/> — VERIFIED byte-for-byte
    /// against <c>tools/disasm.py 0x587560 40</c>.</summary>
    private static ReadOnlySpan<byte> Prologue =>
    [
        0x64, 0xA1, 0x00, 0x00, 0x00, 0x00,
        0x6A, 0xFF,
        0x68, 0xBB, 0x04, 0x72, 0x00,
        0x50,
        0x64, 0x89, 0x25, 0x00, 0x00, 0x00, 0x00,
        0x53,
        0x56,
        0x8B, 0xF1,
    ];

    /// <summary>Master switch — shares <c>container_locks</c> with <see cref="ContainerUseHook"/>,
    /// set from the same call site in <c>PluginHost</c>.</summary>
    public static bool Enabled;

    private static bool _installed;

    /// <summary>Verify-bytes-or-refuse, like every patch in this plugin. MAIN THREAD ONLY, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)CloseInventory;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not watching container close";
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
        s[n++] = 0x51; // push ecx — the CSWSPlaceable being closed. Still valid post-pushad:
                        //  pushad only saves copies, it does not clobber the live register.

        nint callback = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnCloseInventory;
        s[n++] = 0xE8; // call OnCloseInventory
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x04; // add esp, 4 — cdecl, we clean up
        s[n++] = 0x61;                               // popad

        for (int i = 0; i < Prologue.Length; i++) // the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9; // jmp CloseInventory+25
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        if (!NativeMethods.VirtualProtect(CloseInventory, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9; // jmp stub
        *(int*)(site + 1) = (int)(stub - (CloseInventory + 5));
        for (int i = 5; i < Prologue.Length; i++)
        {
            site[i] = 0x90; // pad so a mid-prologue resume or disassembler sees no garbage
        }

        NativeMethods.VirtualProtect(CloseInventory, (nuint)Prologue.Length, old, out _);

        _installed = true;
        diagnostic = $"container-close watch at 0x{CloseInventory:X} (stub 0x{stub:X})";
        return true;
    }

    // ---- the latch: ids THIS instance has EVER watched close for real, this module ------
    //
    // ⚠ LIVE-CORRECTED FROM A TIME-BOXED VERSION. The first cut of this suppressed the durable
    // channel's reassertion for a fixed few seconds after a close, on the theory that would
    // outlast the agent's ~1s poll and PlaceableController's own 40-frame pass. Live-measured:
    // it does not — the durable channel's reassertion PRESSURE IS CONTINUOUS (the agent keeps
    // re-polling +0x338 and re-broadcasting "open" forever once it has ever seen 1, and
    // PlaceableController keeps re-checking every 40-frame pass forever too), so ANY finite
    // window eventually loses the race — reproduced live, the container reopened at +3-4s
    // against a 4000ms window. A time-boxed defence cannot fix a permanent source of pressure.
    //
    // The correct fix is a LATCH, not a timer: once THIS instance has seen id close for real,
    // treat it as closed for the rest of THIS module load, full stop — mirroring the shape of
    // the durable channel's own "write 1, never cleared" persistence, but in the opposite
    // direction, so the two channels stop fighting. This is safe for a LEGITIMATE later re-open
    // too: PlaceableController's own gate is `!Odyssey.IsPlaceableOpen(placeable)` — the moment
    // a real open (local or mirrored) sets +0x338 back to 1 through the ordinary live path, that
    // gate alone already skips the write; the latch here only ever matters while the field
    // genuinely reads 0, where its only job is stopping the STALE durable bit from re-lying
    // about it.
    private const int Capacity = 64; // generous: this pass's own live sweep found 64
                                      //  placeables TOTAL in Endar Spire's first module.
    private static readonly uint[] _closedIds = new uint[Capacity];
    private static int _count;
    private static uint _latestCloseId;
    private static uint _latestCloseSerial;
    private static uint _latestCloseAttemptSerial;

    public static uint LatestLocalClose(out uint serial, out uint attemptSerial)
    {
        serial = _latestCloseSerial;
        attemptSerial = _latestCloseAttemptSerial;
        return _latestCloseId;
    }

    /// <summary>CSWSPlaceable + this = the object's own id — same embedded layout every other
    /// object class in this plugin already relies on.</summary>
    private const int ObjectIdOffset = 0x04;

    /// <summary>
    /// Runs at every <c>CloseInventory</c> call — real players closing their own panel, this
    /// pass's own <c>closeplaceable</c> staging verb, anything else that ever calls it. No
    /// refusal, no engine calls, no allocation.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCloseInventory(nint placeable)
    {
        try
        {
            if (!Enabled || placeable is <= 0x10000 or >= 0x7FFF_0000)
            {
                return;
            }

            uint id = *(uint*)(placeable + ObjectIdOffset);
            PlaceableLocalChanges.Record(id, false);
            _latestCloseId = id;
            ContainerUseHook.LatestAttempt(out _latestCloseAttemptSerial);
            if (++_latestCloseSerial == 0) ++_latestCloseSerial;
            Register(id);
        }
        catch
        {
            // Never let a managed fault unwind into the engine's CloseInventory call.
        }
    }

    /// <summary>
    /// Latches <paramref name="id"/> as closed for the rest of this module load. Called from two
    /// places: the hook callback above (a REAL close, any caller) and
    /// <see cref="OdysseyPlaceables.ClearPlaceableOpenFlag"/> (this instance's own MIRROR of a
    /// peer's close — <see cref="InteractionLockController.ReconcileContainerAnimations"/> never
    /// calls the real <c>CloseInventory</c>, so the hook above would never see that edge on its
    /// own; the durable channel's local agent-side poll runs independently on every instance and
    /// needs the same protection on the mirror side too, live-confirmed the same way as the real
    /// close). Idempotent — re-registering an already-latched id is a cheap no-op scan, not a
    /// growing duplicate.
    /// </summary>
    public static void Register(uint id)
    {
        if (id == 0)
        {
            return;
        }

        for (int i = 0; i < _count; i++)
        {
            if (_closedIds[i] == id)
            {
                return;
            }
        }

        if (_count < Capacity)
        {
            _closedIds[_count++] = id;
        }

        // Capacity exceeded: this module has more distinct closed containers than the (already
        // generous) table holds. Graceful degradation, not a crash — the newest closes simply
        // stop being protected, same "log a few, not forever" discipline
        // PlaceableController.LogFew already uses elsewhere in this file's own neighbourhood.
    }

    /// <summary>Whether <paramref name="id"/> has EVER been seen closing for real in this module
    /// load. No longer consulted by <see cref="PlaceableController"/> (2026-09-24 — see the
    /// class remarks above): kept for any other caller that genuinely wants a permanent
    /// per-module "was this ever really closed" answer, since <see cref="Register"/> keeps
    /// maintaining it regardless.</summary>
    public static bool WasEverClosed(uint id)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_closedIds[i] == id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A module change reassigns every object id; a stale latch would name nothing
    /// there and could — by sheer id-number coincidence — suppress an unrelated placeable in the
    /// new module.</summary>
    public static void Invalidate()
    {
        _count = 0;
    }
}
