namespace KotorCoop.Plugin;

/// <summary>
/// Engine surface for party-placed floor-mine replication (docs/research/mines.md §14,
/// coop.ini <c>placed_mine_sync</c>): enumerate the party-placed mines in the local area, read
/// one back, validate a mapped id is still one, and BUILD one through the exact construction
/// sequence <c>CSWSCreature::AIActionSetMine 0x00519E30</c> runs in its no-target branch
/// (0x0051A607..0x0051A76B, VERIFIED(static), mines.md §7.1 and §14.2).
///
/// The build is a new engine write and is NEEDS-LIVE. It never runs against an image it has not
/// verified: <see cref="VerifyPlacedMineRoute"/> decodes every call and field write in the
/// retail sequence out of the running executable and refuses on the first byte that differs.
/// </summary>
internal static unsafe partial class Odyssey
{
    // ---- the retail construction route, VERIFIED(static) 2026-09-24 ------------------------

    /// <summary><c>operator new</c>, __cdecl(size) — 0x0051A60C <c>call 0x6fa7e6</c> after
    /// <c>push 0x318</c>.</summary>
    private static readonly delegate* unmanaged[Cdecl]<uint, nint> PlacedMineOperatorNewFn =
        (delegate* unmanaged[Cdecl]<uint, nint>)0x006FA7E6;

    /// <summary><c>CSWSTrigger::Constructor(ulong id)</c>, __thiscall, <c>ret 4</c>; 0x7F000000
    /// asks for a fresh dynamic id (0x0051A624).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint> PlacedMineTriggerCtorFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x0058EAE0;

    /// <summary><c>CSWSTrigger::SetCreator(ulong)</c>, __thiscall, <c>ret 4</c>. Writes +0x2E4 and
    /// sets +0x314 to 1 when the creator is the player or a party member (0x0058D741).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, void> PlacedMineSetCreatorFn =
        (delegate* unmanaged[Thiscall]<nint, uint, void>)0x0058D700;

    /// <summary><c>CExoString::operator=(CExoString const&amp;)</c>, __thiscall, <c>ret 4</c>.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, nint> PlacedMineStringAssignFn =
        (delegate* unmanaged[Thiscall]<nint, nint, nint>)0x005E5C50;

    /// <summary><c>CExoString::CExoString(char const*)</c>, __thiscall, <c>ret 4</c>.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, nint> PlacedMineStringCtorFn =
        (delegate* unmanaged[Thiscall]<nint, nint, nint>)0x005E5A90;

    /// <summary><c>CExoString::~CExoString()</c>, __thiscall, plain <c>ret</c>.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> PlacedMineStringDtorFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005E5C20;

    /// <summary><c>C2DA::GetCExoStringEntry(int row, CExoString const&amp; col, CExoString* out)</c>,
    /// __thiscall, <c>ret 12</c> (0x0051A4E4).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint, nint, int> PlacedMine2daStringFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint, nint, int>)0x00413270;

    /// <summary><c>C2DA::GetINTEntry(int row, CExoString const&amp; col, int* out)</c>, __thiscall,
    /// <c>ret 12</c> (0x0051A6F9).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint, int*, int> PlacedMine2daIntFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint, int*, int>)0x00413660;

    /// <summary><c>CSWSObject::SetPosition(Vector const&amp;, int, int, int)</c>, __thiscall,
    /// <c>ret 16</c>; the engine passes (setter position, 1, 1, 0) at 0x0051A67E-0x0051A6A8.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, Vec3*, int, int, int, void> PlacedMineSetPositionFn =
        (delegate* unmanaged[Thiscall]<nint, Vec3*, int, int, int, void>)0x004CD1A0;

    /// <summary><c>CSWSObject::GetArea()</c>, __thiscall, plain <c>ret</c>.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> PlacedMineGetAreaFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004CB120;

    /// <summary><c>CSWSTrigger::CreateNewGeometry(float radius, Vector*, CSWSArea*)</c>,
    /// __thiscall, <c>ret 12</c>. Builds a square of half-size <c>radius</c> around the
    /// trigger's OWN position (+0x90/+0x94; 0x0058D2C5-0x0058D482), which is why SetPosition
    /// must run first. The Vector argument is not read by the body.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, float, Vec3*, nint, void> PlacedMineCreateGeometryFn =
        (delegate* unmanaged[Thiscall]<nint, float, Vec3*, nint, void>)0x0058D210;

    /// <summary><c>CSWSTrigger::AddToArea(CSWSArea*, float x, float y, float z, int)</c>,
    /// __thiscall, <c>ret 0x14</c> (0x0051A741-0x0051A750).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, float, float, float, int, int> PlacedMineAddToAreaFn =
        (delegate* unmanaged[Thiscall]<nint, nint, float, float, float, int, int>)0x0058F030;

    /// <summary><c>CExoArrayList&lt;ulong&gt;::Add(ulong)</c>, __thiscall, <c>ret 4</c>; the
    /// engine appends the new id to area+0x12C at 0x0051A760-0x0051A766.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, void> PlacedMineListAddFn =
        (delegate* unmanaged[Thiscall]<nint, uint, void>)0x00671C00;

    /// <summary>g_pRules; traps.2da is <c>[[[0x7A3A28]+0xB8]+0x8C]</c> (0x0051A6D2-0x0051A6DE).</summary>
    private const nint PlacedMineRulesPtr = 0x007A3A28;
    private const int PlacedMineRules2daSet = 0xB8;
    private const int PlacedMineTraps2da = 0x8C;

    /// <summary>The engine's own column-name literals, pushed at 0x0051A4B0 / 0x0051A6BC.</summary>
    private const nint PlacedMineTrapScriptLiteral = 0x00747D50;
    private const nint PlacedMineTrapNameLiteral = 0x00747D44;

    /// <summary>Size AIActionSetMine allocates (<c>push 0x318</c>) — sizeof(CSWSTrigger).</summary>
    private const uint PlacedMineTriggerSize = 0x318;

    /// <summary>CreateNewGeometry radius, <c>push 0x40000000</c> (2.0f) at 0x0051A726.</summary>
    private const float PlacedMineGeometryRadius = 2.0f;

    private const nint PlacedMineTriggerVtable = 0x00749A38;

    // CSWSTrigger fields (mines.md §2, VERIFIED(static)).
    private const int PmObjectId = 0x04;

    /// <summary>CSWSObject tag, a CExoString (symbol database <c>CSWSObject +0x018 tag</c>).</summary>
    private const int PmTag = 0x018;
    private const int PmAreaId = 0x08C;
    private const int PmPosition = 0x090;
    private const int PmOrientation = 0x09C;
    private const int PmTrapName = 0x22C;
    private const int PmOnTrapTriggered = 0x264;
    private const int PmFaction = 0x2B8;
    private const int PmTrapGate = 0x2BC;
    private const int PmDetectable = 0x2C0;
    private const int PmDisarmable = 0x2C4;
    private const int PmTrapType = 0x2D4;
    private const int PmDetectDc = 0x2D8;
    private const int PmDisarmDc = 0x2DC;
    private const int PmCreator = 0x2E4;
    private const int PmPlacedByParty = 0x314;

    /// <summary>CSWSArea trigger-id list (CExoArrayList&lt;ulong&gt;): data at +0x12C, count at
    /// +0x130 — the list GetNumMinesSetByPlayerParty 0x00508960 walks (0x00508965/0x00508985).</summary>
    private const int PmAreaTriggerList = 0x12C;
    private const int PmAreaTriggerCount = 0x130;

    /// <summary>(call site, expected target) for every call the route makes, read out of
    /// AIActionSetMine. Any mismatch means a different executable: refuse.</summary>
    private static readonly (nint Site, nint Target)[] PlacedMineRouteCalls =
    [
        (0x0051A4E4, 0x00413270), // GetCExoStringEntry(TrapScript)
        (0x0051A60C, 0x006FA7E6), // operator new(0x318)
        (0x0051A62B, 0x0058EAE0), // CSWSTrigger::Constructor
        (0x0051A644, 0x0058D700), // SetCreator
        (0x0051A65A, 0x005E5C50), // CExoString::operator= (+0x264)
        (0x0051A6A8, 0x004CD1A0), // SetPosition
        (0x0051A6CD, 0x005E5A90), // CExoString(char const*) "TrapName"
        (0x0051A6F9, 0x00413660), // GetINTEntry(TrapName)
        (0x0051A70A, 0x005E5C20), // ~CExoString
        (0x0051A71B, 0x004CB120), // GetArea
        (0x0051A72D, 0x0058D210), // CreateNewGeometry
        (0x0051A750, 0x0058F030), // AddToArea
        (0x0051A766, 0x00671C00), // CExoArrayList::Add
    ];

    /// <summary>(address, bytes) of every field write and constant the route relies on.</summary>
    private static readonly (nint Address, byte[] Bytes)[] PlacedMineRouteBytes =
    [
        (0x0051A607, [0x68, 0x18, 0x03, 0x00, 0x00]),                   // push 0x318
        (0x0051A624, [0x68, 0x00, 0x00, 0x00, 0x7F]),                   // push 0x7F000000
        (0x0051A64E, [0x8D, 0x8F, 0x64, 0x02, 0x00, 0x00]),             // lea ecx,[edi+0x264]
        (0x0051A654, [0x88, 0x9F, 0xD4, 0x02, 0x00, 0x00]),             // mov [edi+0x2D4], bl
        (0x0051A670, [0x89, 0x87, 0xD8, 0x02, 0x00, 0x00]),             // mov [edi+0x2D8], eax
        (0x0051A680, [0x89, 0x87, 0xDC, 0x02, 0x00, 0x00]),             // mov [edi+0x2DC], eax
        (0x0051A696, [0x89, 0x87, 0xBC, 0x02, 0x00, 0x00]),             // mov [edi+0x2BC], eax (1)
        (0x0051A69C, [0x89, 0x87, 0xC0, 0x02, 0x00, 0x00]),             // mov [edi+0x2C0], eax (1)
        (0x0051A6A2, [0x89, 0x87, 0xC4, 0x02, 0x00, 0x00]),             // mov [edi+0x2C4], eax (1)
        (0x0051A6B6, [0x89, 0x87, 0xB8, 0x02, 0x00, 0x00]),             // mov [edi+0x2B8], eax (faction)
        (0x0051A4B0, [0x68, 0x50, 0x7D, 0x74, 0x00]),                   // push "TrapScript"
        (0x0051A6BC, [0x68, 0x44, 0x7D, 0x74, 0x00]),                   // push "TrapName"
        (0x0051A6D2, [0x8B, 0x0D, 0x28, 0x3A, 0x7A, 0x00, 0x8B, 0x81, 0xB8, 0x00, 0x00, 0x00,
                      0x8B, 0x80, 0x8C, 0x00, 0x00, 0x00]),             // traps.2da chain
        (0x0051A715, [0x89, 0x97, 0x2C, 0x02, 0x00, 0x00]),             // mov [edi+0x22C], edx
        (0x0051A726, [0x68, 0x00, 0x00, 0x00, 0x40]),                   // push 2.0f
        (0x0051A760, [0x8D, 0x88, 0x2C, 0x01, 0x00, 0x00]),             // lea ecx,[eax+0x12C]
        (0x00747D50, "TrapScript\0"u8.ToArray()),
        (0x00747D44, "TrapName\0"u8.ToArray()),
    ];

    private static int _placedMineRouteVerdict; // 0 unknown, 1 verified, -1 refused
    private static string _placedMineRouteDiagnostic = string.Empty;

    /// <summary>
    /// Verify-bytes-or-refuse for the whole construction route. Cached: the image does not
    /// change under us. Returns false (and a reason) on the first mismatch.
    /// </summary>
    public static bool VerifyPlacedMineRoute(out string diagnostic)
    {
        if (_placedMineRouteVerdict != 0)
        {
            diagnostic = _placedMineRouteDiagnostic;
            return _placedMineRouteVerdict > 0;
        }

        foreach ((nint site, nint expected) in PlacedMineRouteCalls)
        {
            if (!NativeMethods.IsReadable(site, 5) || *(byte*)site != 0xE8)
            {
                return RefusePlacedMineRoute(out diagnostic, $"no call at 0x{site:X}");
            }

            nint target = site + 5 + *(int*)(site + 1);
            if (target != expected)
            {
                return RefusePlacedMineRoute(out diagnostic, $"call at 0x{site:X} goes to 0x{target:X}, expected 0x{expected:X}");
            }
        }

        foreach ((nint address, byte[] bytes) in PlacedMineRouteBytes)
        {
            if (!NativeMethods.IsReadable(address, (nuint)bytes.Length))
            {
                return RefusePlacedMineRoute(out diagnostic, $"0x{address:X} unreadable");
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                if (((byte*)address)[i] != bytes[i])
                {
                    return RefusePlacedMineRoute(out diagnostic, $"byte mismatch at 0x{address + i:X}");
                }
            }
        }

        _placedMineRouteVerdict = 1;
        _placedMineRouteDiagnostic = "AIActionSetMine route verified (13 calls, 18 byte runs)";
        diagnostic = _placedMineRouteDiagnostic;
        return true;

    }

    private static bool RefusePlacedMineRoute(out string diagnostic, string why)
    {
        _placedMineRouteVerdict = -1;
        _placedMineRouteDiagnostic = $"placed-mine route refused: {why}";
        diagnostic = _placedMineRouteDiagnostic;
        return false;
    }

    /// <summary>The local player's CSWSArea, through the engine's own GetArea — the same area
    /// AIActionSetMine uses for its setter. 0 when there is none.</summary>
    public static nint GetPlacedMineArea()
    {
        nint player = GetPlayerCreaturePointer();
        return player == 0 ? 0 : PlacedMineGetAreaFn(player);
    }

    /// <summary>A live party-placed mine: trigger vtable, id round-trips, trap gate set, and the
    /// party-placed marker set. Everything a mapped id must still be before we act on it.</summary>
    public static bool IsLivePartyPlacedMine(uint objectId, out nint trigger)
    {
        trigger = 0;
        if (objectId is 0 or ObjectInvalid)
        {
            return false;
        }

        nint candidate = ResolveServerObject(objectId);
        if (candidate == 0 || !NativeMethods.IsReadable(candidate, PlacedMineTriggerSize)
            || *(nint*)candidate != PlacedMineTriggerVtable
            || *(uint*)(candidate + PmObjectId) != objectId
            || *(int*)(candidate + PmTrapGate) == 0
            || *(int*)(candidate + PmPlacedByParty) == 0)
        {
            return false;
        }

        trigger = candidate;
        return true;
    }

    /// <summary>Reads a party-placed mine back into a <see cref="PlacedMineSpec"/>.</summary>
    public static PlacedMineSpec ReadPartyPlacedMine(nint trigger) => new(
        *(byte*)(trigger + PmTrapType),
        *(float*)(trigger + PmPosition),
        *(float*)(trigger + PmPosition + 4),
        *(float*)(trigger + PmPosition + 8),
        *(float*)(trigger + PmOrientation),
        *(float*)(trigger + PmOrientation + 4),
        (short)Math.Clamp(*(int*)(trigger + PmDetectDc), short.MinValue, short.MaxValue),
        (short)Math.Clamp(*(int*)(trigger + PmDisarmDc), short.MinValue, short.MaxValue),
        (ushort)Math.Clamp(*(int*)(trigger + PmFaction), 0, ushort.MaxValue));

    /// <summary>
    /// The spec the <c>placemine</c> test verb builds: the local player's position and faction
    /// (<c>[[creature+0xA74]+0x78]</c>, the read AIActionSetMine makes at 0x0051A6AD-0x0051A6B3)
    /// with caller-chosen DCs. False when there is no player.
    /// </summary>
    public static bool TryBuildLocalPlacedMineSpec(byte trapType, short detectDc, short disarmDc, out PlacedMineSpec spec, out uint creatorId)
    {
        spec = default;
        creatorId = 0;
        nint player = GetPlayerCreaturePointer();
        if (player == 0 || !NativeMethods.IsReadable(player, 0xA78))
        {
            return false;
        }

        nint stats = *(nint*)(player + 0xA74);
        int faction = stats != 0 && NativeMethods.IsReadable(stats + 0x78, 4) ? *(int*)(stats + 0x78) : 0;
        creatorId = *(uint*)(player + PmObjectId);
        spec = new PlacedMineSpec(
            trapType,
            *(float*)(player + PmPosition),
            *(float*)(player + PmPosition + 4),
            *(float*)(player + PmPosition + 8),
            0f,
            0f,
            detectDc,
            disarmDc,
            (ushort)Math.Clamp(faction, 0, ushort.MaxValue));
        return true;
    }

    /// <summary>The trigger's creator id (+0x2E4).</summary>
    public static uint ReadPlacedMineCreator(nint trigger) => *(uint*)(trigger + PmCreator);

    /// <summary>
    /// Copies the ids of every party-placed mine in <paramref name="area"/>'s trigger list
    /// (area+0x12C/+0x130) into <paramref name="ids"/>; returns how many.
    /// </summary>
    public static int ListPartyPlacedMines(nint area, Span<uint> ids)
    {
        if (area == 0 || !NativeMethods.IsReadable(area + PmAreaTriggerList, 8))
        {
            return 0;
        }

        nint list = *(nint*)(area + PmAreaTriggerList);
        int count = *(int*)(area + PmAreaTriggerCount);
        if (list == 0 || count <= 0 || count > 4096 || !NativeMethods.IsReadable(list, (nuint)(count * 4)))
        {
            return 0;
        }

        int found = 0;
        for (int i = 0; i < count && found < ids.Length; i++)
        {
            uint id = ((uint*)list)[i];
            if (IsLivePartyPlacedMine(id, out _))
            {
                ids[found++] = id;
            }
        }

        return found;
    }

    /// <summary>
    /// Builds one party-placed floor mine, replaying <c>AIActionSetMine</c>'s no-target branch
    /// step for step (docs/research/mines.md §14.2). <paramref name="creatorId"/> is the local
    /// player, so <c>SetCreator</c> marks the trigger party-placed (+0x314 = 1) exactly as it
    /// does the placer's own. <c>TrapScript</c> and <c>TrapName</c> come from this instance's
    /// own traps.2da row, the engine's way; the DCs and faction come from the placer's record
    /// because they carry the placer's d20 and skill. MAIN THREAD ONLY. NEEDS-LIVE.
    /// </summary>
    public static bool TrySpawnPartyPlacedMine(in PlacedMineSpec spec, uint creatorId, string? tag, out uint objectId, out string diagnostic)
    {
        objectId = 0;
        if (!spec.IsValid)
        {
            diagnostic = "record is not a valid mine";
            return false;
        }

        if (!VerifyPlacedMineRoute(out diagnostic))
        {
            return false;
        }

        nint area = GetPlacedMineArea();
        if (area == 0 || creatorId is 0 or ObjectInvalid)
        {
            diagnostic = "no local player or area yet";
            return false;
        }

        nint rules = Deref(PlacedMineRulesPtr);
        nint twoDaSet = rules == 0 ? 0 : Deref(rules + PlacedMineRules2daSet);
        nint traps = twoDaSet == 0 ? 0 : Deref(twoDaSet + PlacedMineTraps2da);
        if (traps == 0)
        {
            diagnostic = "traps.2da is not loaded";
            return false;
        }

        // Resolve the row BEFORE allocating anything: a row this install's traps.2da does not
        // have is a refusal, not a half-built trigger.
        long column = 0;
        long trapScript = 0;
        nint columnPtr = (nint)(&column);
        nint trapScriptPtr = (nint)(&trapScript);
        PlacedMineStringCtorFn(columnPtr, PlacedMineTrapScriptLiteral);
        int haveScript = PlacedMine2daStringFn(traps, spec.TrapType, columnPtr, trapScriptPtr);
        PlacedMineStringDtorFn(columnPtr);
        if (haveScript == 0)
        {
            PlacedMineStringDtorFn(trapScriptPtr);
            diagnostic = $"traps.2da has no TrapScript for row {spec.TrapType}";
            return false;
        }

        int trapName = 0;
        column = 0;
        PlacedMineStringCtorFn(columnPtr, PlacedMineTrapNameLiteral);
        int haveName = PlacedMine2daIntFn(traps, spec.TrapType, columnPtr, &trapName);
        PlacedMineStringDtorFn(columnPtr);

        nint trigger = PlacedMineOperatorNewFn(PlacedMineTriggerSize);
        if (trigger == 0)
        {
            PlacedMineStringDtorFn(trapScriptPtr);
            diagnostic = "operator new returned null";
            return false;
        }

        PlacedMineTriggerCtorFn(trigger, 0x7F000000);
        PlacedMineSetCreatorFn(trigger, creatorId);

        // The ONE step the retail branch does not take: a replica carries a recognisable tag
        // (CSWSObject +0x18, a CExoString) so it can be told apart in a save, a script's
        // GetObjectByTag or a debugger. Written through the engine's own CExoString
        // constructor/assignment, before AddToArea registers the object anywhere. A placer's
        // own mine (and the placemine test verb) passes null and stays untagged like retail.
        if (!string.IsNullOrEmpty(tag) && tag.Length < 32)
        {
            byte* text = stackalloc byte[32];
            for (int i = 0; i < tag.Length; i++)
            {
                text[i] = (byte)tag[i];
            }

            text[tag.Length] = 0;
            long tagString = 0;
            PlacedMineStringCtorFn((nint)(&tagString), (nint)text);
            PlacedMineStringAssignFn(trigger + PmTag, (nint)(&tagString));
            PlacedMineStringDtorFn((nint)(&tagString));
        }
        PlacedMineStringAssignFn(trigger + PmOnTrapTriggered, trapScriptPtr);
        PlacedMineStringDtorFn(trapScriptPtr);
        *(byte*)(trigger + PmTrapType) = spec.TrapType;
        *(int*)(trigger + PmDetectDc) = spec.DetectDc;
        *(int*)(trigger + PmDisarmDc) = spec.DisarmDc;
        *(int*)(trigger + PmTrapGate) = 1;
        *(int*)(trigger + PmDetectable) = 1;
        *(int*)(trigger + PmDisarmable) = 1;

        Vec3 position = new() { X = spec.X, Y = spec.Y, Z = spec.Z };
        PlacedMineSetPositionFn(trigger, &position, 1, 1, 0);
        *(int*)(trigger + PmFaction) = spec.Faction;
        if (haveName != 0)
        {
            *(int*)(trigger + PmTrapName) = trapName;
        }

        PlacedMineCreateGeometryFn(trigger, PlacedMineGeometryRadius, &position, area);
        PlacedMineAddToAreaFn(trigger, area, spec.X, spec.Y, spec.Z, 1);

        uint id = *(uint*)(trigger + PmObjectId);
        PlacedMineListAddFn(area + PmAreaTriggerList, id);

        if (!IsLivePartyPlacedMine(id, out _))
        {
            // Built but not recognisable as a party mine (SetCreator refused the creator, or the
            // id does not resolve). Report it and let the caller log it; the object is in the
            // area like any placed mine, so the engine owns its lifetime from here.
            diagnostic = $"built trigger 0x{id:X} but it does not validate as a party-placed mine "
                + $"(+0x314={*(int*)(trigger + PmPlacedByParty)})";
            return false;
        }

        objectId = id;
        diagnostic = $"built party-placed mine 0x{id:X} (row {spec.TrapType}, DC {spec.DetectDc}/{spec.DisarmDc}, "
            + $"faction {spec.Faction}) at ({spec.X:F2}, {spec.Y:F2}, {spec.Z:F2})";
        return true;
    }
}
