using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Watches for damage landing on a peer's phantom, so the hit can be forwarded and applied
/// to that peer's real character in their own game.
///
/// The choke point is <c>CSWSEffectListHandler::OnApplyDamage 0x004DFA40</c> — the damage
/// analog of the animation hook's dispatch site: every point of weapon, grenade and force
/// damage in the game passes through it, the attacker rides along in the effect's
/// creator_id, and the per-type amounts are final by then (after hit/miss, crits and sneak
/// attack). A miss simply never arrives, which is what we want: the client's HEALTH is the
/// thing that has to be right, and misses are cosmetic. See
/// docs/research/combat-participation.md §4.
///
/// Damage is accumulated as a CUMULATIVE TOTAL per phantom rather than published as
/// events, deliberately: the far side applies the delta between what it has already taken
/// and the total, so a lost packet costs one tick of staleness instead of a permanently
/// missed hit. That is the same "level, not event" rule the enemy-health channel follows.
///
/// This runs inside every damage event in the game, so it does the least possible work:
/// compare an id against a tiny array, sum at most 15 ints, store two words. No engine
/// calls, no allocation, and a catch that can never unwind into the engine.
/// </summary>
internal static unsafe class PhantomHitHook
{
    private const nint OnApplyDamage = 0x004DFA40;

    /// <summary>Where the relocated prologue resumes (after the two moved instructions).</summary>
    private const nint ResumeAt = 0x004DFA47;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>
    /// <c>push -1</c>, <c>push 0x0071AA08</c> (the SEH handler record), <c>mov eax, fs:[0]</c>.
    /// All 13 are verified before hooking; the first 7 are relocated (neither is
    /// position-dependent — the handler push is an absolute immediate).
    /// </summary>
    private static ReadOnlySpan<byte> Prologue =>
        [0x6A, 0xFF, 0x68, 0x08, 0xAA, 0x71, 0x00, 0x64, 0xA1, 0x00, 0x00, 0x00, 0x00];

    private const int RelocatedBytes = 7;

    /// <summary>CSWSObject + this = object id.</summary>
    private const int ObjId = 0x04;

    /// <summary>CGameEffect + this = creator_id, the attacker's object id.</summary>
    private const int EffectCreator = 0x18;

    /// <summary>CGameEffect + this = the integer list's count, then its data pointer.</summary>
    private const int EffectIntCount = 0x30;
    private const int EffectInts = 0x34;

    /// <summary>Per-damage-type slots the engine reads (0..0xE).</summary>
    private const int DamageTypeSlots = 15;

    /// <summary>The last slot is the plain/universal amount, which the engine also keeps
    /// separately (0x004DFAEA) — it mirrors the typed damage rather than adding to it.</summary>
    private const int PlainSlot = 14;

    private const uint ObjectInvalid = 0x7F000000;

    /// <summary>Phantom object ids currently being watched, and the damage each has taken.
    /// Fixed slots indexed by phantom slot — the array is read on the engine's damage path,
    /// so there is nothing to allocate or lock.</summary>
    private static readonly uint[] _watchedId = new uint[SharedBlock.SlotCapacity];
    private static readonly uint[] _cumulative = new uint[SharedBlock.SlotCapacity];
    private static readonly uint[] _lastAttacker = new uint[SharedBlock.SlotCapacity];
    private static readonly uint[] _serial = new uint[SharedBlock.SlotCapacity];

    private static bool _installed;

    /// <summary>Starts (or stops, with id 0) watching the phantom in a slot. Counters are
    /// kept across a re-register of the same id so a respawned body does not replay damage
    /// the peer has already taken.</summary>
    public static void Watch(int slot, uint phantomId)
    {
        if (slot is < 0 or >= SharedBlock.SlotCapacity)
        {
            return;
        }

        if (_watchedId[slot] != phantomId)
        {
            _watchedId[slot] = phantomId;
            _cumulative[slot] = 0;
            _lastAttacker[slot] = 0;
            _serial[slot] = 0;
        }
    }

    public static void ReadSlot(int slot, out uint phantomId, out uint cumulative, out uint attacker, out uint serial)
    {
        phantomId = _watchedId[slot];
        cumulative = _cumulative[slot];
        attacker = _lastAttacker[slot];
        serial = _serial[slot];
    }

    /// <summary>The per-type amounts of the most recent hit, for the controller to log
    /// outside the hook (the hook itself must not allocate or format). Damage accounting
    /// is per damage TYPE, and which slots a given effect populates is not obvious from
    /// the disassembly alone — this is how it gets measured rather than assumed.</summary>
    private static readonly int[] _lastAmounts = new int[DamageTypeSlots];
    private static uint _lastAmountsSerial;

    public static uint AmountsSerial => _lastAmountsSerial;

    public static int ReadAmount(int index) => _lastAmounts[index];

    /// <summary>Drops every watch and its totals — a module change frees the bodies and the
    /// peer's own health is re-published from scratch.</summary>
    public static void Invalidate()
    {
        Array.Clear(_watchedId);
        Array.Clear(_cumulative);
        Array.Clear(_lastAttacker);
        Array.Clear(_serial);
    }

    /// <summary>Verify-bytes-or-refuse, like every other patch here. MAIN THREAD, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)OnApplyDamage;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not watching phantom hits";
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
        // by 0x20, which is why target is read at [esp+0x24] and the effect at [esp+0x28].
        s[n++] = 0x60;                                       // pushad
        s[n++] = 0xFF; s[n++] = 0xB4; s[n++] = 0x24;         // push dword [esp+0x28] (effect)
        *(int*)(s + n) = 0x28;
        n += 4;
        s[n++] = 0xFF; s[n++] = 0xB4; s[n++] = 0x24;         // push dword [esp+0x28] (target,
        *(int*)(s + n) = 0x28;                               //  now +0x28 after the first push)
        n += 4;

        nint callback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnDamage;
        s[n++] = 0xE8;                                       // call OnDamage
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x08;         // add esp, 8 (cdecl cleanup)
        s[n++] = 0x61;                                       // popad

        for (int i = 0; i < RelocatedBytes; i++)
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9;                                       // jmp OnApplyDamage+7
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(OnApplyDamage, (nuint)RelocatedBytes, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                      // jmp stub
        *(int*)(site + 1) = (int)(stub - (OnApplyDamage + 5));
        site[5] = 0x90;                                      // pad the 2 remaining bytes
        site[6] = 0x90;
        NativeMethods.VirtualProtect(OnApplyDamage, (nuint)RelocatedBytes, old, out _);

        _installed = true;
        diagnostic = $"phantom-hit watch at 0x{OnApplyDamage:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// Runs before the engine's own damage application, for EVERY damage event in the
    /// game. The id filter comes first for exactly that reason.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDamage(nint target, nint effect)
    {
        try
        {
            if (target == 0 || effect == 0)
            {
                return;
            }

            uint targetId = *(uint*)(target + ObjId);

            int slot = -1;
            for (int i = 0; i < SharedBlock.SlotCapacity; i++)
            {
                if (_watchedId[i] != 0 && _watchedId[i] == targetId)
                {
                    slot = i;
                    break;
                }
            }

            if (slot < 0)
            {
                return;
            }

            int count = *(int*)(effect + EffectIntCount);
            nint ints = *(nint*)(effect + EffectInts);
            if (ints == 0 || count <= 0)
            {
                return;
            }

            // Bounded by the list's own count as well as the engine's 15 damage types: a
            // foreign or malformed effect must not make this over-read.
            int slots = Math.Min(count, DamageTypeSlots);
            int total = 0;
            int plain = 0;

            for (int i = 0; i < slots; i++)
            {
                int amount = *(int*)(ints + (i * 4));
                _lastAmounts[i] = amount;

                // An unpopulated slot reads -1, not 0 (measured live).
                if (amount <= 0)
                {
                    continue;
                }

                // ⚠ Slot 14 is the PLAIN/universal view of the same damage, not an extra
                // component: a 7-point universal hit measures as [3]=7 AND [14]=7, so
                // summing all fifteen reported 14 and the peer took double. Sum the typed
                // slots and keep the plain slot only as the fallback for an effect that
                // populates nothing else.
                if (i == PlainSlot)
                {
                    plain = amount;
                }
                else
                {
                    total += amount;
                }
            }

            for (int i = slots; i < DamageTypeSlots; i++)
            {
                _lastAmounts[i] = 0;
            }

            _lastAmountsSerial++;

            if (total == 0)
            {
                total = plain;
            }

            if (total <= 0)
            {
                return;
            }

            uint attacker = *(uint*)(effect + EffectCreator);

            _cumulative[slot] += (uint)total;
            _lastAttacker[slot] = attacker is 0 or ObjectInvalid ? _lastAttacker[slot] : attacker;
            _serial[slot]++;
        }
        catch
        {
            // Never let a managed fault unwind into the engine's damage application.
        }
    }
}
