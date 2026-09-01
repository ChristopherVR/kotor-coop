using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Watches the engine's GENERIC effect dispatcher for non-damage effects landing on a peer's
/// phantom — stun, paralyze, knockdown, slow, frightened, sleep, choke, Force push/whirlwind,
/// and (effect-sync.md §10, 2026-09-02) heal/temporary-hitpoints/confused — so they can be
/// forwarded to that peer and replayed on their real character. The non-damage sibling of
/// <see cref="PhantomHitHook"/>.
///
/// The hook point is <c>CSWSEffectListHandler::OnEffectApplied 0x004D8320</c> — the SAME kind
/// of choke point <c>OnApplyDamage</c> is, one level up: every effect the game applies to
/// anything, damage included, is dispatched through here first, by reading the effect's TYPE
/// (<c>effect+0x008</c>, VERIFIED) and indexing a 110-entry table of per-type handlers.
/// <c>OnApplyDamage</c> is registered at index 38; this hook watches the DISPATCHER itself
/// rather than adding a second detour per effect type, because five of the eight allowlist
/// members share ONE table entry (type 8, <c>OnApplySetState</c>) and are told apart only by
/// an integer sub-code the effect carries, not by a distinct handler each. See
/// docs/research/effect-sync.md for the full derivation, VERIFIED-vs-INFERRED tags included.
///
/// ⚠ THE ALLOWLIST TABLE BELOW IS A COPY. NativeAOT plugin code cannot reference
/// <c>KotorCoop.Protocol</c>, so <see cref="TryMapAllowlist"/> duplicates
/// <c>PeerEffectRules.TryMapAllowlist</c> by hand — the same "one number in two files"
/// discipline every <c>SharedBlock</c> offset already lives under. A mismatch degrades to a
/// silently-unforwarded effect, never a crash: an effect type/sub-code this table refuses is
/// simply not captured.
///
/// Runs inside every effect application in the game, so — same discipline as
/// <see cref="PhantomHitHook"/> — it does the least possible work: one id compare, one type
/// read, at most one sub-code read, no engine calls, no allocation, and a catch that can never
/// unwind into the engine.
/// </summary>
internal static unsafe class PhantomEffectHook
{
    private const nint OnEffectApplied = 0x004D8320;

    /// <summary>Where the relocated prologue resumes — right after the two relocated
    /// instructions, at the original <c>test eax, eax</c>.</summary>
    private const nint ResumeAt = 0x004D8328;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary><c>mov edx, [esp+8]</c> (effect) then <c>movzx eax, word [edx+8]</c> (its
    /// type) — two whole instructions, 8 bytes, neither position-dependent. Unlike
    /// <c>OnApplyDamage</c> this function has no SEH prologue to relocate around; it starts
    /// reading its arguments immediately.</summary>
    private static ReadOnlySpan<byte> Prologue => [0x8B, 0x54, 0x24, 0x08, 0x0F, 0xB7, 0x42, 0x08];

    private const int RelocatedBytes = 8;

    /// <summary>CSWSObject + this = object id.</summary>
    private const int ObjId = 0x04;

    /// <summary>CGameEffect + this = type (ushort). Same field <c>OnEffectApplied</c> itself
    /// dispatches on.</summary>
    private const int EffectType = 0x08;

    /// <summary>CGameEffect + this = duration in SECONDS (float). Populated by the generic
    /// "add this effect" step before dispatch reaches here — INFERRED, not independently
    /// disassembled; see effect-sync.md §2.3. Sanity-clamped below rather than trusted blind.</summary>
    private const int EffectDuration = 0x0C;

    private const int EffectIntCount = 0x30;
    private const int EffectInts = 0x34;

    /// <summary>Fallback when the duration field reads zero or absurd — most of these effects
    /// are meant to be felt for a few seconds, not applied as a permanent flag flip. Chosen as
    /// a safe, visible middle ground, not a measured figure — see effect-sync.md §2.3.</summary>
    private const uint FallbackDurationMs = 6000;

    private const float MaxSaneDurationSeconds = 3600f;

    // ---- allowlist — mirrors PeerEffectRules.TryMapAllowlist (KotorCoop.Protocol) --------
    //
    // Numeric values match PeerEffectKindValues (this project) / PeerEffectKind (Protocol).

    private static bool TryMapAllowlist(ushort engineType, int subCode, out uint kind)
    {
        switch (engineType)
        {
            case 8: // OnApplySetState — the shared state-effect family
                switch (subCode)
                {
                    case 1: kind = PeerEffectKindValues.Confused; return true;    // EffectConfused (effect-sync.md §10)
                    case 3: kind = PeerEffectKindValues.Stun; return true;        // EffectDroidStun
                    case 5: kind = PeerEffectKindValues.Paralyze; return true;    // EffectParalyze (routine 148)
                    case 6: kind = PeerEffectKindValues.Sleep; return true;       // EffectSleep (routine 154)
                    case 7: kind = PeerEffectKindValues.Choke; return true;       // EffectChoke
                    case 8: kind = PeerEffectKindValues.Frightened; return true;  // EffectHorrified
                    case 10: kind = PeerEffectKindValues.Whirlwind; return true;  // EffectWhirlWind
                    default: kind = PeerEffectKindValues.None; return false;      // unnamed sub-codes
                }

            case 15: kind = PeerEffectKindValues.TemporaryHitpoints; return true; // OnApplyTemporaryHitpoints (effect-sync.md §10)
            case 20: kind = PeerEffectKindValues.Knockdown; return true;  // OnApplyKnockdown
            case 29: kind = PeerEffectKindValues.Slow; return true;        // OnApplyMovementSpeedDecrease
            case 39: kind = PeerEffectKindValues.Heal; return true;        // OnApplyHeal (effect-sync.md §10)
            case 60: kind = PeerEffectKindValues.ForcePush; return true;   // OnApplyForcePush

            default:
                kind = PeerEffectKindValues.None;
                return false;
        }
    }

    /// <summary>How many captures are remembered between agent reads. Matches
    /// <see cref="SharedBlock.PeerEffectCapacity"/>.</summary>
    public const int Capacity = SharedBlock.PeerEffectCapacity;

    private static readonly uint[] _phantomId = new uint[Capacity];
    private static readonly uint[] _kind = new uint[Capacity];
    private static readonly uint[] _durationMs = new uint[Capacity];
    private static readonly uint[] _magnitude = new uint[Capacity];
    private static readonly uint[] _serial = new uint[Capacity];

    private static int _write;
    private static uint _sequence;

    public static uint Sequence => _sequence;

    public static void Read(
        int index, out uint phantomId, out uint kind, out uint durationMs, out uint magnitude, out uint serial)
    {
        int i = ((index % Capacity) + Capacity) % Capacity;
        phantomId = _phantomId[i];
        kind = _kind[i];
        durationMs = _durationMs[i];
        magnitude = _magnitude[i];
        serial = _serial[i];
    }

    /// <summary>A module change reassigns every object id, so a remembered capture would name
    /// a different creature (or nothing at all).</summary>
    public static void Invalidate()
    {
        Array.Clear(_phantomId);
        Array.Clear(_kind);
        Array.Clear(_durationMs);
        Array.Clear(_magnitude);
        Array.Clear(_serial);
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

        byte* site = (byte*)OnEffectApplied;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not watching phantom effects";
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

        // Entered by jmp, so the caller's stack args are still in place; pushad shifts them
        // by 0x20, which is why effect and target both read at [esp+0x28] after their
        // respective pushes — see PhantomHitHook.Install for the identical reasoning (this
        // hook has the same [retaddr][target][effect][gameLoading] stack shape at entry).
        s[n++] = 0x60;                                       // pushad
        s[n++] = 0xFF; s[n++] = 0xB4; s[n++] = 0x24;         // push dword [esp+0x28] (effect)
        *(int*)(s + n) = 0x28;
        n += 4;
        s[n++] = 0xFF; s[n++] = 0xB4; s[n++] = 0x24;         // push dword [esp+0x28] (target,
        *(int*)(s + n) = 0x28;                               //  now +0x28 after the first push)
        n += 4;

        nint callback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnEffect;
        s[n++] = 0xE8;                                       // call OnEffect
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x08;         // add esp, 8 (cdecl cleanup)
        s[n++] = 0x61;                                       // popad

        for (int i = 0; i < RelocatedBytes; i++)
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9;                                       // jmp OnEffectApplied+8
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(OnEffectApplied, (nuint)RelocatedBytes, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                      // jmp stub
        *(int*)(site + 1) = (int)(stub - (OnEffectApplied + 5));
        site[5] = 0x90;                                      // pad the 3 remaining bytes
        site[6] = 0x90;
        site[7] = 0x90;
        NativeMethods.VirtualProtect(OnEffectApplied, (nuint)RelocatedBytes, old, out _);

        _installed = true;
        diagnostic = $"phantom-effect watch at 0x{OnEffectApplied:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// Runs before the engine's own effect dispatch, for EVERY effect application in the
    /// game. The id filter comes first for exactly that reason.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEffect(nint target, nint effect)
    {
        try
        {
            if (target == 0 || effect == 0)
            {
                return;
            }

            uint targetId = *(uint*)(target + ObjId);
            if (!PhantomHitHook.IsWatchedPhantom(targetId))
            {
                return;
            }

            ushort engineType = *(ushort*)(effect + EffectType);

            // integer_list[0]: the SetState sub-code for type 8, the percent for type 29
            // (MovementSpeedDecrease) — same field, different meaning per type. Bounded reads,
            // exactly like PhantomHitHook.ReadTotal: a malformed or foreign effect must not
            // over-read.
            int aux = 0;
            int count = *(int*)(effect + EffectIntCount);
            nint ints = *(nint*)(effect + EffectInts);
            if (ints != 0 && count > 0)
            {
                aux = *(int*)ints;
            }

            int subCode = engineType == 8 ? aux : 0;
            if (!TryMapAllowlist(engineType, subCode, out uint kind))
            {
                return;
            }

            // integer_list[0] carries the real magnitude for every single-int-parameter
            // constructor this hook understands: percent for Slow (29), heal amount for Heal
            // (39), hit-point amount for TemporaryHitpoints (15) — same field, same read,
            // three different meanings depending on engineType, exactly like `aux` above.
            uint magnitude = (engineType is 29 or 39 or 15) && aux > 0 ? (uint)aux : 0;

            float durationSeconds = *(float*)(effect + EffectDuration);
            uint durationMs = durationSeconds > 0f && durationSeconds < MaxSaneDurationSeconds
                ? (uint)(durationSeconds * 1000f)
                : FallbackDurationMs;

            int i = _write;
            _write = (_write + 1) % Capacity;

            _kind[i] = kind;
            _durationMs[i] = durationMs;
            _magnitude[i] = magnitude;
            _serial[i] = ++_sequence;
            _phantomId[i] = targetId; // written last — the reader trusts a nonzero id here
        }
        catch
        {
            // Never let a managed fault unwind into the engine's effect dispatch.
        }
    }
}
