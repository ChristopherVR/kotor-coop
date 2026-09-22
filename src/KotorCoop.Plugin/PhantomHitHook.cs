using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Watches the engine's single damage choke point, for THREE things at once: damage landing
/// on a peer's PHANTOM (forwarded to that peer's real character), damage the local player
/// DEALS to anything (forwarded to whoever owns that creature's fight), and damage landing on
/// a companion <c>companion_local_combat</c> has RELEASED to fight here (forwarded to the
/// host, whose authoritative body never otherwise sees it — see
/// docs/research/companion-state-sync.md §13 and <see cref="WatchCompanion"/>).
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
/// compare ids, read effect amounts, and add. A target that is a retained Lehon roster body
/// takes one bounded live-proof read before its damage can be forwarded; all other targets
/// keep the original fixed-table path. No allocation, and a catch that can never unwind into
/// the engine.
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

    /// <summary>The largest single hit this bookkeeping will believe. KOTOR's biggest real
    /// numbers (a critical two-weapon sneak attack, a thermal detonator, Force Storm) stay in
    /// two digits or low three; our own KillCreature applies 9999 but under SuppressDealt.
    /// Anything past this is a misread effect, never a hit — see OnDamage's gate.</summary>
    internal const int MaxPlausibleHit = 2000;

    /// <summary>How many implausible effects get their int list dumped to the log. The
    /// first few are the diagnosis; after that the counter alone says it is still happening.</summary>
    private const int ImplausibleDumpLimit = 5;
    private static int _implausibleDumped;

    /// <summary>Implausible hits refused so far this process, published for the dashboard-
    /// style checks (a non-zero value on a peer means an effect this hook does not
    /// understand is landing on someone's creature).</summary>
    public static uint ImplausibleRefused { get; private set; }

    /// <summary>Phantom object ids currently being watched, and the damage each has taken.
    /// Fixed slots indexed by phantom slot — the array is read on the engine's damage path,
    /// so there is nothing to allocate or lock.</summary>
    private static readonly uint[] _watchedId = new uint[SharedBlock.SlotCapacity];
    private static readonly uint[] _cumulative = new uint[SharedBlock.SlotCapacity];
    private static readonly uint[] _lastAttacker = new uint[SharedBlock.SlotCapacity];
    private static readonly uint[] _serial = new uint[SharedBlock.SlotCapacity];

    /// <summary>Per-slot per-attacker breakdown of the same hits <see cref="_cumulative"/>
    /// sums — see <see cref="PhantomAttackerTable"/>'s own doc comment for the eviction rule.
    /// One instance per phantom slot, constructed once; every method after that touches only
    /// its own fixed arrays, so this stays allocation-free on the damage hook's hot path.</summary>
    private static readonly PhantomAttackerTable[] _attackerTables = CreateAttackerTables();

    private static PhantomAttackerTable[] CreateAttackerTables()
    {
        var tables = new PhantomAttackerTable[SharedBlock.SlotCapacity];
        for (int i = 0; i < tables.Length; i++)
        {
            tables[i] = new PhantomAttackerTable();
        }

        return tables;
    }

    /// <summary>Damage the local side has dealt, per target object id.</summary>
    private static readonly DamageDealtTable _dealt = new(SharedBlock.DamageLedgerCapacity);

    /// <summary>
    /// Companion presence slots currently RELEASED to their own engine AI
    /// (<c>companion_local_combat</c>), watched for damage TAKEN — the reverse of the dealt
    /// table above. Indexed by presence slot, exactly like the phantom watch arrays, and only
    /// ever nonzero while <see cref="PartyPresenceController"/> has that slot released: the
    /// blanket "host owns companion health" mirror write goes through
    /// <c>Odyssey.SetCurrentHitPoints</c> directly and never touches this hook, so there is
    /// nothing here to suppress the way <see cref="SuppressDealt"/> guards the dealt side.
    /// </summary>
    private static readonly uint[] _companionId = new uint[SharedBlock.CompanionDamageSlotCapacity];

    /// <summary>The NPC roster slot (plus one; 0 = unused) each watched companion id stands
    /// for — the identity that survives a module transition, carried alongside the id so the
    /// publisher never has to ask <see cref="PartyPresenceController"/> for it again.</summary>
    private static readonly uint[] _companionRosterSlotPlusOne = new uint[SharedBlock.CompanionDamageSlotCapacity];

    /// <summary>
    /// Damage taken per NPC ROSTER slot (indexed by roster slot plus one), not per presence
    /// slot — and kept across a reclaim. The 2026-09-02 F-companion pass found why: a
    /// companion is reclaimed and re-released every time its local fight's 25 s slot turns
    /// over, and a total that restarted at 0 on each release met a host ledger that only ever
    /// banks the RISE within one generation (<c>CompanionDamageLedger</c> rule 2): the second
    /// release's first 15 points were read as an 8-point rise over the first release's 7, and
    /// a restart that stayed below the old total would have been read as nothing at all. The
    /// generation is bumped only at a module edge (<c>CompanionDamageController.Invalidate</c>,
    /// next to <see cref="Invalidate"/> here), so within a module this total has to be what the
    /// ledger assumes it is: monotonic per roster slot for as long as the bodies live.
    /// </summary>
    private static readonly uint[] _companionCumulative = new uint[SharedBlock.PartyRosterSlots + 1];
    private static readonly uint[] _companionSerial = new uint[SharedBlock.PartyRosterSlots + 1];

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

    // ---- enemy DAMAGE scaling (coop.ini enemy_damage, docs/research/player-count-scaling.md)

    /// <summary>The percent a hostile hit on a player body is rewritten to, 100 = untouched.
    /// Set every pass by <see cref="EnemyScalingController"/> from the agent's resolved
    /// value; never read from coop.ini here.</summary>
    private static int _damagePercent = EnemyScalingRule.Vanilla;

    /// <summary>Player bodies whose incoming hostile hits are scaled: the local PC and the
    /// local party roster (<see cref="WatchDamageScaleTargets"/>). Phantoms and released
    /// companions are already in <see cref="_watchedId"/>/<see cref="_companionId"/> and are
    /// scaled through those. Fixed array, widen-then-count like <see cref="_ourAttacker"/>.</summary>
    private static readonly uint[] _scaleTarget = new uint[16];
    private static int _scaleTargetCount;

    /// <summary>Hits this process rewrote, for the status line and the live checklist.</summary>
    public static uint DamageScaledHits { get; private set; }

    /// <summary>How many damage rewrites are logged (the rest are only counted).</summary>
    private const uint DamageScaleLogBudget = 12;

    /// <summary>MAIN THREAD. A percent outside the agent's range is refused as vanilla by
    /// <see cref="EnemyScalingRule"/> upstream; this only stores it.</summary>
    public static void SetDamagePercent(int percent) => _damagePercent = percent;

    /// <summary>MAIN THREAD. Same widen-before-narrow discipline as <see cref="WatchAttackers"/>.</summary>
    public static void WatchDamageScaleTargets(ReadOnlySpan<uint> objectIds)
    {
        int count = 0;
        for (int i = 0; i < objectIds.Length && count < _scaleTarget.Length; i++)
        {
            if (objectIds[i] is not (0 or ObjectInvalid))
            {
                _scaleTarget[count++] = objectIds[i];
            }
        }

        for (int i = count; i < _scaleTarget.Length; i++)
        {
            _scaleTarget[i] = 0;
        }

        _scaleTargetCount = count;
    }

    private static bool IsDamageScaleTarget(uint objectId)
    {
        for (int i = 0; i < _scaleTargetCount; i++)
        {
            if (_scaleTarget[i] == objectId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWatchedCompanion(uint objectId)
    {
        for (int i = 0; i < SharedBlock.CompanionDamageSlotCapacity; i++)
        {
            if (_companionId[i] != 0 && _companionId[i] == objectId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rewrites a hostile hit's per-type amounts IN PLACE before the engine reads them —
    /// slot 14 (the number the engine applies, see <see cref="ReadTotal"/>) and the typed
    /// breakdown alike, so the tally the engine keeps per type matches what it applied. Runs
    /// only when the target is a player body and the attacker is NOT one (not ours, not a
    /// phantom, not a released companion), so friendly fire, a peer's stand-in and the mod's
    /// own forwarded applies (<see cref="SuppressDealt"/>, checked by the caller) stay
    /// untouched. A creator of 0/invalid (a mine, a trap, a scripted hit) is left alone too:
    /// nothing says it was an enemy. The bookkeeping below then reads the SCALED numbers, so
    /// a phantom's forwarded total already carries the multiplier and the owner applies it
    /// once, unscaled (its own apply runs under SuppressDealt). NEEDS-LIVE: this is the only
    /// place the mod writes into an effect's integer list.
    /// </summary>
    private static void ScaleHostileDamage(nint effect)
    {
        uint attacker = *(uint*)(effect + EffectCreator);
        if (attacker is 0 or ObjectInvalid
            || IsDamageScaleTarget(attacker) || IsWatchedPhantom(attacker) || IsWatchedCompanion(attacker))
        {
            return;
        }

        int count = *(int*)(effect + EffectIntCount);
        nint ints = *(nint*)(effect + EffectInts);
        if (ints == 0 || count <= 0)
        {
            return;
        }

        int slots = Math.Min(count, DamageTypeSlots);
        bool touched = false;
        int firstBefore = 0;
        int firstAfter = 0;
        for (int i = 0; i < slots; i++)
        {
            int* amount = (int*)(ints + (i * 4));
            if (*amount > 0 && *amount <= MaxPlausibleHit)
            {
                int before = *amount;
                *amount = EnemyScalingRule.ScaleDamage(*amount, _damagePercent);
                if (!touched)
                {
                    firstBefore = before;
                    firstAfter = *amount;
                }

                touched = true;
            }
        }

        if (touched)
        {
            DamageScaledHits++;

            // The first few rewrites are logged (2026-09-19) so a live pass can SEE enemy_damage
            // act: before this the counter was the only trace and nothing read it.
            if (DamageScaledHits <= DamageScaleLogBudget)
            {
                PluginHost.Log($"scaling: enemy damage {firstBefore} -> {firstAfter} ({_damagePercent}%) on a hit to a player body (rewrite #{DamageScaledHits})");
            }
        }
    }

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

    // ---- ROUND 52: ground-truth diagnostic — every damage event landing on the LOCAL PC,
    // named by attacker id, so no future round has to trust the "is fight action" proxy
    // round 51 found does not detect success even when a real hit lands (0/6000 sampled
    // frames across three trials where the player's own HP demonstrably fell). Logging
    // only: never suppresses or redirects the engine's own damage application below. See
    // docs/research/shared-state-audit.md §2, round 52.

    /// <summary>Refreshed every frame from PluginHost's own player-pointer read (never
    /// resolved inside this hook — see the "no engine calls" rule in the class doc above).
    /// 0 when no world is up.</summary>
    private static uint _localPlayerId;

    private const int LocalHitLogSlots = 8;
    private static readonly uint[] _localHitLoggedAttacker = new uint[LocalHitLogSlots];
    private static readonly long[] _localHitLoggedAtMs = new long[LocalHitLogSlots];

    /// <summary>Tells the hook which object id is the local PC, so <see cref="OnDamage"/>
    /// can recognise a hit landing on it without an engine call. MAIN THREAD, once a frame.</summary>
    public static void WatchLocalPlayer(uint playerId) => _localPlayerId = playerId;

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
            _attackerTables[slot].Clear();
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

    /// <summary>The per-attacker breakdown captured for a watched phantom slot, for the
    /// controller to publish alongside <see cref="ReadSlot"/>'s legacy scalar total. Writes
    /// into <paramref name="ids"/>/<paramref name="totals"/> (each expected at least
    /// <see cref="PhantomAttackerTable.Capacity"/> long) and returns how many entries were
    /// valid.</summary>
    public static int ReadAttackers(int slot, Span<uint> ids, Span<uint> totals)
    {
        if (slot is < 0 or >= SharedBlock.SlotCapacity)
        {
            return 0;
        }

        PhantomAttackerTable table = _attackerTables[slot];
        int count = 0;

        for (int i = 0; i < PhantomAttackerTable.Capacity && count < ids.Length && count < totals.Length; i++)
        {
            uint id = table.IdAt(i);
            if (id == 0)
            {
                continue;
            }

            ids[count] = id;
            totals[count] = table.TotalAt(i);
            count++;
        }

        return count;
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

    // ---- companion damage TAKEN ---------------------------------------------------

    /// <summary>
    /// Starts (or stops, with id 0) watching a released companion's damage taken. Called every
    /// pass from <see cref="PartyPresenceController"/> — only ever with a nonzero id while that
    /// presence slot is actually released to its own engine AI. The watch (which id counts,
    /// and under which roster slot) is per presence slot; the TOTAL is per roster slot and is
    /// never reset here — a reclaim only stops counting, and the same companion released
    /// again (this slot or another) carries on from where it was, see
    /// <see cref="_companionCumulative"/>. Only <see cref="Invalidate"/> zeroes the totals.
    /// </summary>
    public static void WatchCompanion(int slot, uint objectId, uint rosterSlotPlusOne)
    {
        if (slot is < 0 or >= SharedBlock.CompanionDamageSlotCapacity)
        {
            return;
        }

        if (objectId != 0 && rosterSlotPlusOne is 0 or > SharedBlock.PartyRosterSlots)
        {
            // Not a companion this hook can bank against: nothing survives a transition for
            // it, so nothing is reported for it either.
            objectId = 0;
        }

        _companionId[slot] = objectId;
        _companionRosterSlotPlusOne[slot] = objectId == 0 ? 0 : rosterSlotPlusOne;
    }

    /// <summary>Stops companion-damage attribution for a body at the instant it is transferred
    /// to the enemy channel. The party controller will clear its per-slot caches later in the
    /// same frame; this closes the interval in which a hit could be counted by both channels.</summary>
    public static void ClearCompanionWatch(uint objectId)
    {
        if (objectId == 0)
        {
            return;
        }

        for (int slot = 0; slot < _companionId.Length; slot++)
        {
            if (_companionId[slot] == objectId)
            {
                _companionId[slot] = 0;
                _companionRosterSlotPlusOne[slot] = 0;
            }
        }
    }

    public static void ReadCompanionSlot(
        int slot, out uint rosterSlotPlusOne, out uint cumulative, out uint serial) =>
        ReadCompanionSlot(slot, out rosterSlotPlusOne, out cumulative, out serial, out _);

    /// <summary>The form <see cref="CompanionDamageController"/>'s heal sampling needs: also
    /// which body the watch names, so a hit-point sample can be tied to the same object the
    /// damage serial belongs to (<see cref="CompanionHealSampleRule"/>).</summary>
    public static void ReadCompanionSlot(
        int slot, out uint rosterSlotPlusOne, out uint cumulative, out uint serial, out uint objectId)
    {
        if (slot is < 0 or >= SharedBlock.CompanionDamageSlotCapacity || _companionId[slot] == 0)
        {
            rosterSlotPlusOne = 0;
            cumulative = 0;
            serial = 0;
            objectId = 0;
            return;
        }

        objectId = _companionId[slot];
        rosterSlotPlusOne = _companionRosterSlotPlusOne[slot];
        cumulative = _companionCumulative[rosterSlotPlusOne];
        serial = _companionSerial[rosterSlotPlusOne];
    }

    /// <summary>Drops every watch and its totals — a module change frees the bodies and the
    /// peer's own health is re-published from scratch. The dealt ledger goes with it: object
    /// ids are assigned per module load, so a total carried across would be credited against
    /// a completely different creature. The companion-damage watch goes with it too, for the
    /// same reason.</summary>
    public static void Invalidate()
    {
        Array.Clear(_watchedId);
        Array.Clear(_cumulative);
        Array.Clear(_lastAttacker);
        Array.Clear(_serial);
        Array.Clear(_ourAttacker);
        _ourAttackerCount = 0;
        Array.Clear(_scaleTarget);
        _scaleTargetCount = 0;
        _dealt.Clear();
        Array.Clear(_companionId);
        Array.Clear(_companionRosterSlotPlusOne);
        Array.Clear(_companionCumulative);
        Array.Clear(_companionSerial);

        for (int i = 0; i < _attackerTables.Length; i++)
        {
            _attackerTables[i].Clear();
        }
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

            // ROUND 52 GROUND-TRUTH DIAGNOSTIC. Independent of the phantom/dealt bookkeeping
            // below — this is the direct answer to "did a hit land on the actual local PC",
            // read off the engine's own damage choke point rather than the action-queue head
            // type round 9-51 relied on.
            if (_localPlayerId != 0 && targetId == _localPlayerId)
            {
                LogLocalHitGroundTruth(effect);
            }

            int slot = -1;
            for (int i = 0; i < SharedBlock.SlotCapacity; i++)
            {
                if (_watchedId[i] != 0 && _watchedId[i] == targetId)
                {
                    slot = i;
                    break;
                }
            }

            int companionSlot = -1;
            if (slot < 0)
            {
                for (int i = 0; i < SharedBlock.CompanionDamageSlotCapacity; i++)
                {
                    if (_companionId[i] != 0 && _companionId[i] == targetId)
                    {
                        companionSlot = i;
                        break;
                    }
                }
            }

            // enemy_damage: rewrite a hostile hit on a player body BEFORE anything below reads
            // the amounts, so the engine's apply and every ledger see the same number.
            if (_damagePercent != EnemyScalingRule.Vanilla && !SuppressDealt
                && (slot >= 0 || companionSlot >= 0 || IsDamageScaleTarget(targetId)))
            {
                ScaleHostileDamage(effect);
            }

            bool ours = slot < 0 && companionSlot < 0 && !SuppressDealt
                && IsOursToCredit(target, targetId, effect);

            if (slot < 0 && companionSlot < 0 && !ours)
            {
                return;
            }

            int total = ReadTotal(effect, breakdown: slot >= 0);
            if (total <= 0)
            {
                return;
            }

            // ⚠ PLAUSIBILITY GATE (2026-09-02). Live on tar_m10aa a client's ledger row for
            // tar10_vulkarpt02 (0x46) jumped from 3 to 1,634,994,435 (0x61747103) between two
            // agent ticks; the host's rise-only merge banked the whole rise, its per-pass clamp
            // turned it into "applied 200" nine times in a second, and the Vulkar died of
            // nothing anyone had done. A number that size is not a hit in this game — no weapon,
            // grenade or Force power reaches four digits — so whatever effect carried it
            // (its int list is dumped below, bounded, for the next occurrence) must NOT be
            // credited to any of the three cumulative totals: an enemy ledger row becomes a
            // guaranteed kill on the owner, a companion row wipes the host's companion, a
            // phantom row wipes the peer's PC. The engine's own apply still runs (the stub
            // always resumes into it); only OUR bookkeeping refuses it.
            if (total > MaxPlausibleHit)
            {
                NoteImplausibleHit(target, targetId, effect, total);
                return;
            }

            if (companionSlot >= 0)
            {
                uint rosterSlotPlusOne = _companionRosterSlotPlusOne[companionSlot];
                _companionCumulative[rosterSlotPlusOne] += (uint)total;
                _companionSerial[rosterSlotPlusOne]++;
                return;
            }

            if (slot < 0)
            {
                // The ledger travels to the enemy OWNER, so it must be keyed in the
                // owner's id space: a hit on a materialised stand-in is recorded under
                // the owner id the body stands in for, not its local spawn id.
                bool retainedLocal = MaterialisedEnemies.IsRetainedLocal(targetId)
                    || MaterialisedEnemies.IsQuarantinedRetainedLocal(targetId);
                uint ownerId;
                if (retainedLocal)
                {
                    if (!MaterialisedEnemies.TryGetCurrentRetainedOwner(targetId, out ownerId))
                    {
                        return; // stale/unfinished retained proof must never become a raw host id
                    }
                }
                else
                {
                    ownerId = MaterialisedEnemies.ToOwner(targetId);
                }

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

            if (attacker is not (0 or ObjectInvalid))
            {
                _attackerTables[slot].Add(attacker, (uint)total);
            }
        }
        catch
        {
            // Never let a managed fault unwind into the engine's damage application.
        }
    }

    /// <summary>
    /// Counts an implausible hit and, for the first few, dumps everything about the effect
    /// that carried it: the effect's vtable and its first header dwords (type/subtype live in
    /// there), the creator, the int list's count and every int the engine would read. This is
    /// the evidence the 2026-09-02 occurrence did not leave behind — the client log had rotated
    /// and nothing named the hit, only the host's ledger row showed the number. Bounded so a
    /// repeating source costs five lines and a counter, not a log per frame.
    /// </summary>
    private static void NoteImplausibleHit(nint target, uint targetId, nint effect, int total)
    {
        ImplausibleRefused++;

        if (_implausibleDumped >= ImplausibleDumpLimit)
        {
            return;
        }

        _implausibleDumped++;

        int count = *(int*)(effect + EffectIntCount);
        nint ints = *(nint*)(effect + EffectInts);
        int shown = Math.Min(Math.Max(count, 0), 24);

        System.Text.StringBuilder sb = new(320);
        sb.Append("combat: REFUSED an implausible hit of ").Append(total)
          .Append(" on 0x").Append(targetId.ToString("X"))
          .Append(" (hp ").Append(*(short*)(target + ObjCurrentHitPoints))
          .Append(") from creator 0x").Append((*(uint*)(effect + EffectCreator)).ToString("X"))
          .Append(": effect 0x").Append(((nint)effect).ToString("X"))
          .Append(" vtable 0x").Append((*(nint*)effect).ToString("X"))
          .Append(" hdr");

        for (int i = 4; i < 0x30; i += 4)
        {
            sb.Append(' ').Append((*(uint*)(effect + i)).ToString("X"));
        }

        sb.Append(" ints[").Append(count).Append("]@0x").Append(ints.ToString("X")).Append(" =");

        for (int i = 0; i < shown && ints != 0; i++)
        {
            sb.Append(' ').Append(*(int*)(ints + (i * 4)));
        }

        PluginHost.Log(sb.ToString());
    }

    /// <summary>
    /// ROUND 52. Logs one line per real hit landing on the local PC, naming the attacker and
    /// classifying it — the ground-truth read this investigation has been missing since
    /// round 9. Read-only: this never influences the engine's own damage application, which
    /// the stub always resumes into regardless of anything this method does.
    ///
    /// The forwarded-damage applier is recognised through <see cref="SuppressDealt"/> rather
    /// than the effect's own creator_id: <c>IncomingDamageController</c> dispatches its
    /// forwarded apply through <c>Odyssey.ApplyDamageEffect</c>, which is a VM-level
    /// EffectDamage/ApplyEffectToObject call scoped to the implementer's own script context,
    /// not to the original remote attacker — so the resulting effect's creator_id would name
    /// whatever that context is, not the id the network channel actually attributes the hit
    /// to (<c>block.IncomingDamageAttacker</c>, logged separately by
    /// <c>IncomingDamageController</c> itself). <see cref="SuppressDealt"/> is already set
    /// around every call site of <c>Odyssey.ApplyDamageEffect</c> for an unrelated reason
    /// (round 39's self-feeding-ledger fix); of those call sites, only
    /// <c>IncomingDamageController</c>'s ever targets the LOCAL PLAYER, so seeing it set here
    /// — where the target is already known to be the local PC — identifies the applier
    /// unambiguously without adding a second flag.
    /// </summary>
    private static void LogLocalHitGroundTruth(nint effect)
    {
        uint attacker = *(uint*)(effect + EffectCreator);
        int total = ReadTotal(effect, breakdown: false);
        if (total <= 0 || !ShouldLogLocalHit(attacker))
        {
            return;
        }

        string kind = SuppressDealt
            ? "forwarded-damage applier (IncomingDamageController's own EffectDamage apply)"
            : EnemyPresenceController.IsLocallyFightingCreature(attacker)
                ? "the currently locally-fighting creature (StartLocalFight's own id)"
                : EnemyPresenceController.IsDrivenCensusCreature(attacker)
                    ? "a driven/mirrored census creature, not the one currently fighting locally"
                    : IsWatchedPhantom(attacker)
                        ? "a watched phantom id (unexpected as an attacker on the local PC)"
                        : "unrecognized attacker id (not driven, not a phantom, not our own apply)";

        PluginHost.Log(
            $"combat ground-truth: local PC took {total} damage, attacker=0x{attacker:X} ({kind})");
    }

    /// <summary>Per-attacker, per-second edge limiter for <see cref="LogLocalHitGroundTruth"/>
    /// — a small fixed table (no allocation, no dictionary) rather than a per-frame log for
    /// the duration of a fight. Evicts the least-recently-logged slot when full.</summary>
    private static bool ShouldLogLocalHit(uint attacker)
    {
        long now = Environment.TickCount64;
        int oldest = 0;
        long oldestAt = long.MaxValue;

        for (int i = 0; i < LocalHitLogSlots; i++)
        {
            if (_localHitLoggedAttacker[i] == attacker && _localHitLoggedAtMs[i] != 0)
            {
                if (now - _localHitLoggedAtMs[i] < 1000)
                {
                    return false;
                }

                _localHitLoggedAtMs[i] = now;
                return true;
            }

            if (_localHitLoggedAtMs[i] < oldestAt)
            {
                oldestAt = _localHitLoggedAtMs[i];
                oldest = i;
            }
        }

        _localHitLoggedAttacker[oldest] = attacker;
        _localHitLoggedAtMs[oldest] = now;
        return true;
    }

    /// <summary>True when <paramref name="objectPointer"/> is a server creature (the vtable
    /// <see cref="IsOursToCredit"/> tests) — for a caller holding a resolved object pointer.</summary>
    public static bool IsServerCreature(nint objectPointer) =>
        objectPointer != 0 && *(nint*)objectPointer == ServerCreatureVtable;

    /// <summary>True on the mirroring side (the attacker set is non-empty) — the cheapest
    /// possible "should any capture on the effect dispatcher run at all" test. Empty on the
    /// enemies' owner, exactly as the damage path's own opt-out.</summary>
    public static bool HasAttackers => _ourAttackerCount != 0;

    /// <summary>
    /// <see cref="IsOursToCredit"/> for a NON-DAMAGE effect (docs/research/enemy-effects.md):
    /// the same question — is the creator one of our bodies, the target a live creature that is
    /// not one of our bodies — asked of the effect the dispatcher is about to apply. The
    /// creator field is the same <c>CGameEffect+0x18</c> the damage path reads.
    /// </summary>
    public static bool IsOursOnForeignCreature(nint target, uint targetId, nint effect) =>
        IsOursToCredit(target, targetId, effect);

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
