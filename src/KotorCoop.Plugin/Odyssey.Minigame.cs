// Odyssey.Minigame.cs - the `mgkill` live-test primitive (docs/research/minigames.md): finishes
// a forced turret/gunnery minigame ambush (e.g. the Ebon Hawk Sith-fighter sequence, module
// m12ab) that the unattended rig cannot play by actually shooting down the enemy fighters, by
// running the ENGINE'S OWN minigame damage path so the shipped per-fighter OnDeath scripts
// (e.g. k_pebo_sthdeath2..7) fire exactly as they would for a real kill.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- CSWMiniGameObjectArray enumeration, VERIFIED(static), READ-ONLY -----------------
    //
    // CClientExoAppInternal.mgo_array @ 0x000 (tools/kotor1_0_3.db offsets table) is a
    // CSWMiniGameObjectArray* - a SEPARATE pointer chain from CSWCArea::mini_game (the one
    // MinigameController/EngineGlobals already resolve for the "am I in a minigame" flag).
    // Independently cross-checked against CClientExoApp::GetMGOArray's own disassembly
    // (0x005EDAF0): `mov eax,[ecx+4]; mov eax,[eax]; ret` - the exact same two hops
    // (CClientExoApp -> CClientExoAppInternal -> mgo_array), read directly here in-process
    // rather than called, the same way MinigameController reads `mini_game` as a plain pointer
    // chase - this is a READ, not a mutating engine call, so it does not need the
    // verify-bytes-or-refuse treatment the actual KILL below gets.
    //
    // CSWMiniGameObjectArray (tools/kotor1_0_3.db: size 0x400) is a flat, 255-slot table:
    // `CSWMiniGameObjectArray::Constructor` (0x0066BF10) zeroes exactly 255
    // dwords starting at +4 and sets [+0]=0 (a round-robin search cursor
    // `findNextEmptyIndex`/0x0066C060 advances and wraps - NOT a count, confirmed by its own
    // disassembly). `CSWMiniGameObjectArray::GetMiniGameObject` (0x0066BF30) VERIFIES this
    // shape directly: `cmp eax,0xFF; jae refuse; mov ecx,[ecx+eax*4+4]` - slots 0..254 at
    // +4+i*4. This is also the EXACT index space `SWMG_AdjustFollowerHitPoints`'s own
    // nFollower parameter indexes into (see TryKillMiniGameFighter below), so a slot number
    // read here is already the correct argument to pass there - no translation needed.
    //
    // Each enemy fighter is a CSWMiniEnemy (size 0x1A4, vtable 0x007528A8 per
    // tools/kotor1_0_3.db's class row) - VERIFIED independently by `CSWMiniGame::AddEnemy`'s
    // own construction code (0x00671E40), which allocates 0x1A4 bytes and literally writes
    // `mov dword ptr [edi], 0x7528a8` (0x00671E92) into the new object's vtable slot before
    // adding it to this same array. CSWMiniEnemy embeds a CSWTrackFollower at its own offset
    // +0 (tools/kotor1_0_3.db: `CSWMiniEnemy.follower @ 0x000`), so the CSWTrackFollower field
    // offsets below (flags/current hp/max hp) apply directly to the object's own base address.
    private const int ClientMgoArrayOffset = 0x00;
    private const int MgoArraySlotBase = 0x04;
    private const int MgoArraySlotCount = 255;

    private const nint CswMiniEnemyVtable = 0x007528A8;

    // CSWTrackFollower fields, VERIFIED by disassembly of the three functions that read/write
    // them (CSWTrackFollower::OnDamage 0x0066E7A0, ::SetHitPoints 0x0066C900) - NOT present as
    // named rows in tools/kotor1_0_3.db (only `object`/`mini_game`/`scripts`/... at 0x00-0x174
    // are), so these are read straight out of the verified instructions rather than an
    // offsets-table lookup:
    //   +0x88 flags: OnDamage's death branch (0x0066E852-0x0066E85A) does `mov eax,[esi+0x88];
    //     test al,2; jne <already-dead, skip>` before ever calling OnDeath - bit 0x2 is "this
    //     follower already died", set the same place (0x0066E85C: `or eax,2`) immediately
    //     before the one and only OnDeath call in that function.
    //   +0x8C current hit points: OnDamage's arithmetic (`mov eax,[esi+0x19c] /* pending delta
    //     */; mov ecx,[esi+0x8c]; add ecx,eax; mov [esi+0x8c],ecx`) and SetHitPoints
    //     (0x0066C900: `mov [ecx+0x8c],eax` when the requested value is > 0) both agree.
    //   +0x90 max hit points: SetHitPoints' sibling entry point (0x0066C950) writes the same
    //     shape into +0x90; m12ab.are's own MiniGame/Enemies[i]/Max_HPs GFF field (100 for
    //     every one of the six Sith fighters) is what CSWMiniGame::Load stores there.
    private const int TrackFollowerFlagsOffset = 0x88;
    private const int TrackFollowerDeadBit = 0x2;
    private const int TrackFollowerCurrentHpOffset = 0x8C;
    private const int TrackFollowerMaxHpOffset = 0x90;

    /// <summary>One live-or-dead minigame enemy-fighter slot, as read by
    /// <see cref="ListMiniGameFighters"/>. <see cref="Slot"/> is the same array index
    /// <see cref="TryKillMiniGameFighter"/> takes.</summary>
    public readonly record struct MiniGameFighter(int Slot, int CurrentHp, int MaxHp, bool Dead);

    /// <summary>
    /// Enumerates every <c>CSWMiniEnemy</c> currently held in the client's minigame object
    /// array - READ-ONLY, no engine call. Empty when no minigame is running (the array pointer
    /// itself is null, exactly like <c>CSWCArea::mini_game</c>) or none of its slots are
    /// enemy-typed (a swoop race has none; the array also holds the player follower, obstacles
    /// and track objects, filtered out here by their own, DIFFERENT vtable).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static MiniGameFighter[] ListMiniGameFighters()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return [];
        }

        nint clientInternal = Deref(client + AppClientOffset);
        if (clientInternal == 0)
        {
            return [];
        }

        nint array = Deref(clientInternal + ClientMgoArrayOffset);
        if (array == 0 || !NativeMethods.IsReadable(array, (nuint)(MgoArraySlotBase + MgoArraySlotCount * 4)))
        {
            return [];
        }

        List<MiniGameFighter> fighters = [];
        for (int slot = 0; slot < MgoArraySlotCount; slot++)
        {
            nint objectPtr = *(nint*)(array + MgoArraySlotBase + slot * 4);
            if (objectPtr == 0 || !NativeMethods.IsReadable(objectPtr, (nuint)(TrackFollowerMaxHpOffset + 4)))
            {
                continue;
            }

            if (*(nint*)objectPtr != CswMiniEnemyVtable)
            {
                continue;
            }

            int flags = *(int*)(objectPtr + TrackFollowerFlagsOffset);
            int currentHp = *(int*)(objectPtr + TrackFollowerCurrentHpOffset);
            int maxHp = *(int*)(objectPtr + TrackFollowerMaxHpOffset);
            fighters.Add(new MiniGameFighter(slot, currentHp, maxHp, (flags & TrackFollowerDeadBit) != 0));
        }

        return [.. fighters];
    }

    // ---- SWMG_AdjustFollowerHitPoints (routine 590), VERIFIED(static) --------------------
    //
    // nwscript.nss (read straight out of this install's own resources,
    // `python tools/kotorfiles.py` reads it via chitin.key): routine 590 is
    // `int SWMG_AdjustFollowerHitPoints(object oFollower, int nHP, int nAbsolute=0);` -
    // "adjusts a followers hit points, can specify the absolute value to set to". Routine 604
    // is the DIFFERENT, sibling command `void SWMG_SetFollowerHitPoints(object oFollower, int
    // nHP);` - the two are easy to confuse from the DB alone, since
    // `CSWVirtualMachineCommands::ExecuteCommandSWMG_AdjustFollowerHitPoint` (0x005CB990, the
    // literal symbol name in tools/kotor1_0_3.db) is COMDAT-folded to implement BOTH: full
    // disassembly shows it reads its own routine-number argument back (`[esp+0xc]`, the same
    // "which command is this" argument the VM dispatcher always passes - see
    // Odyssey.Spells.cs's CastSpellAtObject for the identical `handler(implementer, routine,
    // nParams)` convention this file reuses) and branches on it: `- 0x24E (590) -> call
    // 0x0066E8E0` (CSWTrackFollower::AdjustHitPoints, a RELATIVE delta that tail-jumps into
    // ::OnDamage and DOES run the death check below); `- 0x25C (604) -> call 0x0066C900`
    // (CSWTrackFollower::SetHitPoints, a raw field write that explicitly REFUSES a value <= 0
    // and never calls OnDamage/OnDeath at all - useless for killing anything). 590 is
    // therefore the only route that can end a fighter's life; this file never calls 604.
    //
    // Only TWO StackPop calls appear in the 590 branch - StackPopObject (0x005D10C0, matching
    // Odyssey.Core.cs's own verified binding) for oFollower, then StackPopInteger (0x005D1000)
    // for nHP - never a third pop for nAbsolute, so this build's native handler always applies
    // nHP as a RELATIVE delta regardless of what a script would pass for the optional third
    // argument; a plain 2-arg call (the overwhelmingly likely shape any real script uses,
    // matching the default) is what this file replicates.
    //
    // oFollower is popped as an "object" but is layout-identical to a plain array index: the
    // 590 branch's own resolve chain is `mov eax,[esp+4] /* the popped object value */; ...;
    // push 4; push eax; call 0x005EDAF0 (GetMGOArray); mov ecx,eax; call 0x0066BF30
    // (GetMiniGameObject(index=oFollower, kind=4))` - GetMiniGameObject's own bounds check
    // (`cmp eax,0xFF; jae refuse`) is exactly the same 0..254 range ListMiniGameFighters walks,
    // and kind=4 is the "downcast to CSWTrackFollower" selector (jump-table entry 4:
    // `mov edx,[ecx]; call [edx+0x1c]`) - a VIRTUAL call, so it works correctly on the
    // CSWMiniEnemy objects this array actually holds regardless of their concrete C++ type. A
    // fighter's own array SLOT NUMBER (from ListMiniGameFighters) is therefore exactly the
    // right raw value to push as oFollower - no separate SWMG_GetEnemy/SWMG_GetEnemyCount
    // round trip is needed to translate it.
    //
    // CSWTrackFollower::OnDamage (0x0066E7A0), the function 604's AdjustHitPoints tail-jumps
    // into: `eax=[esi+0x19c] (the delta AdjustHitPoints just stored); ecx=[esi+0x8c] (current
    // hp); ecx+=eax; [esi+0x8c]=ecx; if (ecx>0) <play a hit sound, done>; else <if not already
    // dead (flags bit 0x2): set the bit, call CSWTrackFollower::OnDeath(this, 0)>`. OnDeath
    // (0x0066E2A0), with param 0: fetches "script slot 6" off this object's own scripts[10]
    // array and, if assigned, runs it (the same script-exec helper OnHitBullet/OnHitFollower
    // use) and returns - it does NOT fall through to the generic built-in explosion/cleanup
    // path in that case. For the m12ab fighters, script slot 6 is populated: m12ab.are's own
    // MiniGame/Enemies[i]/Scripts/OnDeath GFF field names k_pebo_sthdeath2..7 (one per fighter,
    // `python tools/kotorfiles.py gff "<...>/M12ab.rim:m12ab.are" --path MiniGame`), so a
    // sufficiently negative delta here runs THAT script through the engine, exactly as a real
    // bullet kill would - never StartNewModule, never a direct call to OnDamage/OnDeath by
    // address, never a raw poke of +0x8C/+0x88.
    private const int RoutineSwmgAdjustFollowerHitPoints = 590;
    private const nint ExpectedSwmgAdjustFollowerHitPoints = 0x005CB990;

    /// <summary>
    /// The first 27 bytes of <c>CSWVirtualMachineCommands::ExecuteCommandSWMG_AdjustFollowerHitPoint</c>
    /// (0x005CB990), read directly off the sha256-matched GOG v1.0.3 <c>swkotor.exe</c>:
    /// <c>mov ecx,[0x7A3A00]; sub esp,8; lea eax,[esp+4]; push eax; call 0x5D10C0; test eax,eax;
    /// jz 0x5CB9BB; lea ecx,[esp]</c>. Checked before every dispatch, the same belt-and-braces
    /// pattern <see cref="VerifyCastSpellBytes"/> uses.
    /// </summary>
    private static ReadOnlySpan<byte> SwmgAdjustFollowerHitPointsPrologueBytes =>
    [
        0x8B, 0x0D, 0x00, 0x3A, 0x7A, 0x00, 0x83, 0xEC, 0x08, 0x8D, 0x44, 0x24, 0x04, 0x50,
        0xE8, 0x1D, 0x57, 0x00, 0x00, 0x85, 0xC0, 0x74, 0x14, 0x8D, 0x4C, 0x24, 0x00,
    ];

    private static bool VerifySwmgAdjustFollowerHitPointsBytes()
    {
        if (!NativeMethods.IsReadable(
            ExpectedSwmgAdjustFollowerHitPoints, (nuint)SwmgAdjustFollowerHitPointsPrologueBytes.Length))
        {
            return false;
        }

        byte* actual = (byte*)ExpectedSwmgAdjustFollowerHitPoints;
        for (int i = 0; i < SwmgAdjustFollowerHitPointsPrologueBytes.Length; i++)
        {
            if (actual[i] != SwmgAdjustFollowerHitPointsPrologueBytes[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A large enough negative relative HP delta to guarantee any minigame follower's current
    /// hit points reach zero or below in one call, regardless of its own max (the m12ab Sith
    /// fighters are 100/100 per m12ab.are's own GFF data) - never an exact "just enough"
    /// number, since <see cref="TryKillMiniGameFighter"/> reads no prior HP itself and a
    /// caller's stale read must not under-kill.
    /// </summary>
    private const int LethalHitPointDelta = -999999;

    /// <summary>
    /// Kills ONE minigame enemy fighter through the engine's own <c>SWMG_AdjustFollowerHitPoints</c>
    /// VM routine (<paramref name="slot"/> is the array slot <see cref="ListMiniGameFighters"/>
    /// reported) - the identical route a compiled script's own call takes, so
    /// <c>CSWTrackFollower::OnDamage</c>/<c>::OnDeath</c> run exactly as for a real bullet kill
    /// and the fighter's own OnDeath script (e.g. <c>k_pebo_sthdeath2..7</c>) fires through the
    /// engine, never called directly.
    ///
    /// Failure codes: 1 no VM, 2 no routine table, 3 the table entry no longer matches the
    /// verified handler, 4 the handler's own prologue bytes no longer match, 5 a script
    /// currently owns the VM stack, 6 a stack push refused, 7 the handler returned a nonzero
    /// status (an out-of-range slot, most plausibly - <see cref="ListMiniGameFighters"/> should
    /// have already filtered that out, so this means the array changed between the two calls).
    /// The VM stack is not checked for neutrality afterward: this routine's own disassembly
    /// pushes no return value for a caller of THIS shape to pop (see the file header), so
    /// popping one back off would risk consuming unrelated stack content instead.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryKillMiniGameFighter(int slot, out int failure)
    {
        failure = 0;

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return false;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0)
        {
            failure = 2;
            return false;
        }

        if (*(nint*)(table + (RoutineSwmgAdjustFollowerHitPoints * 4)) != ExpectedSwmgAdjustFollowerHitPoints)
        {
            failure = 3;
            return false;
        }

        if (!VerifySwmgAdjustFollowerHitPointsBytes())
        {
            failure = 4;
            return false;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 5;
            return false;
        }

        // SWMG_AdjustFollowerHitPoints(object oFollower, int nHP, int nAbsolute=0): pushed in
        // reverse declared order (nHP first, oFollower last/top - popped first) - the same
        // convention CastSpellAtObject documents and this build's handler's own pop order
        // confirms (StackPopObject runs before StackPopInteger).
        if (StackPushInteger(vm, LethalHitPointDelta) == 0
            || StackPushObject(vm, (uint)slot) == 0)
        {
            failure = 6;
            return false;
        }

        var handler =
            (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedSwmgAdjustFollowerHitPoints;
        int status = handler(implementer, RoutineSwmgAdjustFollowerHitPoints, 2);

        if (status != 0)
        {
            failure = 7;
            return false;
        }

        return true;
    }
}
