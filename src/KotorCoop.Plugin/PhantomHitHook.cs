using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Watches the engine's single damage choke point, for two things at once: damage landing
/// on a peer's PHANTOM (forwarded to that peer's real character), and damage the local
/// player DEALS to anything (forwarded to whoever owns that creature's fight).
///
/// The choke point is <c>CSWSEffectListHandler::OnApplyDamage 0x004DFA40</c> — the damage
/// analog of the animation hook's dispatch site: every point of weapon, grenade and force
/// damage in the game passes through it, the attacker rides along in the effect's
/// creator_id, and the per-type amounts are final by then (after hit/miss, crits and sneak
/// attack). A miss simply never arrives, which is what we want: the client's HEALTH is the
/// thing that has to be right, and misses are cosmetic. See
/// docs/research/combat-participation.md §4.
///
/// Both are accumulated as CUMULATIVE TOTALS rather than published as events: the far side
/// applies the delta between the total and what it has already applied, so a lost packet
/// costs one tick of staleness instead of a permanently missed hit, a duplicate costs
/// nothing, and order does not matter.
///
/// The damage-dealt half is what replaced reporting enemy HEALTH back to the fight's owner.
/// Health had to be sampled, and the owner's mirror wrote its own value back over our copy
/// between samples, so most of a lone player's fight was erased before anyone saw it
/// (measured: 15 hp to 11 hp in ninety seconds, then nothing). A sum of damage dealt is
/// immune to that because it is never read back out of the game — and it can say WHO dealt
/// what, which health cannot express at all.
///
/// This runs inside every damage event in the game, so it does the least possible work:
/// compare an id against two tiny arrays, read a handful of ints, add. No engine calls, no
/// allocation, and a catch that can never unwind into the engine.
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

    /// <summary>The SERVER CSWSCreature vtable — the same constant CreatureFinder scans for.
    /// Doors, placeables and mines all take damage through this hook too, and forwarding a
    /// door's id to the owner would have it apply the damage to its own copy of the door.
    /// Creatures only, for now.</summary>
    private const nint ServerCreatureVtable = 0x007470C8;

    /// <summary>CSWSObject + this = current hit points (int16; negative on an overkill).</summary>
    private const int ObjCurrentHitPoints = 0x0DC;

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

    /// <summary>Damage the local side has dealt, per target object id.</summary>
    private static readonly DamageDealtTable _dealt = new(SharedBlock.DamageLedgerCapacity);

    /// <summary>
    /// Object ids whose damage counts as OURS: the local player and its party.
    ///
    /// Refreshed from the frame thread, never resolved in here. A client holds solo mode so
    /// only the PC is actually swinging today, but a companion's hit has to count the day
    /// that changes, and the alternative test — "anything that is not one of the mirrored
    /// enemies" — silently credits us with enemy-on-enemy damage the moment the 32-slot
    /// census sheds a creature.
    /// </summary>
    private static readonly uint[] _ourAttacker = new uint[16];
    private static int _ourAttackerCount;

    /// <summary>
    /// ⚠ Set while the plugin is applying a damage effect OF ITS OWN, so the dealt ledger
    /// does not count it.
    ///
    /// Without this the channel feeds itself: the mirroring side finishes a mirrored enemy
    /// with <c>KillCreature</c> (a 9999-point engine damage effect) and the ledger reports
    /// 9999 fresh damage to the owner for a creature the owner has already killed; the
    /// owner's own application of a peer's damage is likewise seen by its own hook. Set
    /// inside Odyssey.ApplyDamageEffect so no call site can forget it.
    ///
    /// The PHANTOM half is deliberately left visible: the test hook stages a real hit on a
    /// phantom through exactly that call, and suppressing it would blind the tool that
    /// verifies this whole path.
    /// </summary>
    public static bool SuppressDealt { get; set; }

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

    /// <summary>
    /// The same "is this id one of our watched phantoms" test <see cref="OnDamage"/> uses,
    /// exposed so a sibling hook on a different engine choke point (<c>PhantomEffectHook</c>,
    /// for non-damage effects — docs/research/effect-sync.md) can reuse the one phantom-target
    /// test rather than keeping a second copy of the watch list in sync with this one.
    /// </summary>
    public static bool IsWatchedPhantom(uint objectId)
    {
        if (objectId == 0)
        {
            return false;
        }

        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            if (_watchedId[i] != 0 && _watchedId[i] == objectId)
            {
                return true;
            }
        }

        return false;
    }

    public static void ReadSlot(int slot, out uint phantomId, out uint cumulative, out uint attacker, out uint serial)
    {
        if (slot is < 0 or >= SharedBlock.SlotCapacity)
        {
            // Bounded like Watch is. An IndexOutOfRangeException raised on the frame hook
            // does not surface as a bug report; it kills the game (see CrashProbe).
            phantomId = 0;
            cumulative = 0;
            attacker = 0;
            serial = 0;
            return;
        }

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

    public static int ReadAmount(int index) =>
        index is >= 0 and < DamageTypeSlots ? _lastAmounts[index] : 0;

    // ---- damage this side has DEALT ----------------------------------------------

    /// <summary>Tells the hook whose damage counts as ours. Frame thread; the array is only
    /// ever grown-then-counted, so the hook can never read a half-written set.</summary>
    public static void WatchAttackers(ReadOnlySpan<uint> objectIds)
    {
        int count = 0;
        for (int i = 0; i < objectIds.Length && count < _ourAttacker.Length; i++)
        {
            if (objectIds[i] is not (0 or ObjectInvalid))
            {
                _ourAttacker[count++] = objectIds[i];
            }
        }

        // Widen before narrowing: an id written above the old count is invisible until the
        // count moves, and an id below it was already valid.
        for (int i = count; i < _ourAttacker.Length; i++)
        {
            _ourAttacker[i] = 0;
        }

        _ourAttackerCount = count;
    }

    public static DamageDealtTable Dealt => _dealt;

    /// <summary>Drops every watch and its totals — a module change frees the bodies and the
    /// peer's own health is re-published from scratch. The dealt ledger goes with it: object
    /// ids are assigned per module load, so a total carried across would be credited against
    /// a completely different creature.</summary>
    public static void Invalidate()
    {
        Array.Clear(_watchedId);
        Array.Clear(_cumulative);
        Array.Clear(_lastAttacker);
        Array.Clear(_serial);
        Array.Clear(_ourAttacker);
        _ourAttackerCount = 0;
        _dealt.Clear();
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

            bool ours = slot < 0 && !SuppressDealt && IsOursToCredit(target, targetId, effect);

            if (slot < 0 && !ours)
            {
                return;
            }

            int total = ReadTotal(effect, breakdown: slot >= 0);
            if (total <= 0)
            {
                return;
            }

            if (slot < 0)
            {
                // The ledger travels to the enemy OWNER, so it must be keyed in the
                // owner's id space: a hit on a materialised stand-in is recorded under
                // the owner id the body stands in for, not its local spawn id.
                uint ownerId = MaterialisedEnemies.ToOwner(targetId);
                uint creditId = ownerId != 0 ? ownerId : targetId;

                // ROUND 50: the decisive-control bypass — see
                // EnemyPresenceController.LocalCombatBypassId's own doc comment. This hook is
                // otherwise pure observation (it never suppresses or replaces the engine's own
                // damage application — the stub always resumes into it), but a credited debt
                // here is itself a write into the peer-damage ledger, so the bypassed id must
                // not accrue one either.
                if (creditId == EnemyPresenceController.LocalCombatBypassId && creditId != 0)
                {
                    return;
                }

                _dealt.Add(creditId, (uint)total);
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

    /// <summary>
    /// Whether this hit is one of OURS landing on someone else's creature — the thing the
    /// fight's owner needs to hear about.
    /// </summary>
    private static bool IsOursToCredit(nint target, uint targetId, nint effect)
    {
        if (_ourAttackerCount == 0)
        {
            return false;
        }

        // Creatures only. Doors, placeables and mines come through here as well, and a
        // door's id forwarded to the owner would have it bash its own copy of that door.
        if (*(nint*)target != ServerCreatureVtable)
        {
            return false;
        }

        // ⚠ Already a corpse. The engine's own dead check is a few instructions past this
        // hook, so splash landing on a body it has already killed still arrives here — and
        // crediting it would report damage nobody took.
        if (*(short*)(target + ObjCurrentHitPoints) <= 0)
        {
            return false;
        }

        uint attacker = *(uint*)(effect + EffectCreator);
        if (attacker is 0 or ObjectInvalid)
        {
            return false;
        }

        bool fromUs = false;
        for (int i = 0; i < _ourAttackerCount; i++)
        {
            uint id = _ourAttacker[i];

            // Damage BETWEEN our own bodies is not a contribution to anyone's fight — and
            // the incoming-phantom-damage channel lands on the player through this very
            // path, so counting it would loop it straight back out again.
            if (id == targetId)
            {
                return false;
            }

            fromUs |= id == attacker;
        }

        return fromUs;
    }

    /// <summary>
    /// The damage this effect will actually take off, read out of the effect's integer list.
    ///
    /// ⚠ Slot 14 is not "one more damage type" — it IS the number the engine applies.
    /// Verified in the instruction stream: <c>OnApplyDamage</c> copies slot 14 aside at
    /// 0x004DFAEA, exits outright at 0x004DFB0A when it is zero, runs the target's
    /// resistance/immunity/reduction virtuals over that one value, and passes the result to
    /// <c>CSWSCreature::DoDamage</c> at 0x004DFD9B. The other fourteen slots are the
    /// breakdown, copied into the target's per-type damage tally at 0x004DFD10 and nothing
    /// else — which is why summing all fifteen once reported double.
    ///
    /// The typed sum stays as the fallback for an effect that leaves slot 14 unpopulated
    /// (slots read -1, not 0, when unset), which preserves the behaviour that was measured
    /// live on the phantom channel.
    ///
    /// ⚠ AND NOTE WHAT THIS NUMBER IS NOT: it is the amount BEFORE the target's resistances,
    /// which is exactly why it is safe to forward. See docs/research/peer-damage-ledger.md.
    /// </summary>
    private static int ReadTotal(nint effect, bool breakdown)
    {
        int count = *(int*)(effect + EffectIntCount);
        nint ints = *(nint*)(effect + EffectInts);
        if (ints == 0 || count <= 0)
        {
            return 0;
        }

        // Bounded by the list's own count as well as the engine's 15 damage types: a
        // foreign or malformed effect must not make this over-read.
        int slots = Math.Min(count, DamageTypeSlots);
        int typed = 0;
        int plain = 0;

        for (int i = 0; i < slots; i++)
        {
            int amount = *(int*)(ints + (i * 4));

            if (breakdown)
            {
                _lastAmounts[i] = amount;
            }

            // An unpopulated slot reads -1, not 0 (measured live).
            if (amount <= 0)
            {
                continue;
            }

            if (i == PlainSlot)
            {
                plain = amount;
            }
            else
            {
                typed += amount;
            }
        }

        if (breakdown)
        {
            for (int i = slots; i < DamageTypeSlots; i++)
            {
                _lastAmounts[i] = 0;
            }

            _lastAmountsSerial++;
        }

        return plain > 0 ? plain : typed;
    }
}
