// Odyssey.Encounters.cs - encounter spawn-pool state, the co-op auto-pause
// suppression (docs/research), and the spectate camera's follow handoff.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- encounters ----------------------------------------------------------------
    //
    // Everything here is read out of the binary and written up in
    // docs/research/client-encounters.md; the addresses are repeated in each member's
    // comment because that is what makes a wrong one findable.
    //
    // The shape of an encounter, as the engine sees it: a polygon (the "activate area")
    // plus a spawn list. `CSWSArea::InSubAreas 0x004BF470` tests a point against every
    // subarea object in the area — triggers, doors, areas of effect and ENCOUNTERS, the
    // last identified by the vtable slot at +0x5C returning non-null (self_return on
    // 0x00749D98, return_zero on everyone else). `CSWSCreature::HandleSubAreaStateChanges
    // 0x00516020` turns a newly-entered subarea into event 0xC, and
    // `CSWSEncounter::EventHandler 0x00594220` answers that event with exactly two things:
    // store the entering object at +0x2E0, then call SpawnIfAppropriate. That is the whole
    // "an encounter fired" path, and it is what FireEncounter reproduces.
    //
    // ⚠ The subarea machinery only runs from the engine's own movement functions
    // (WalkUpdateLocation, DriveUpdateLocation, DoCombatStep, the jump actions). A phantom
    // is placed by direct SetPosition writes, so it never generates event 0xC on its own —
    // which is precisely why the host has to be asked.

    private const nint EncounterVtable = 0x00749D98;

    /// <summary>CSWSEncounter + this = active (symbol database). 0 = the encounter is off.</summary>
    private const int EncounterActive = 0x238;

    /// <summary>CSWSEncounter + this = the spawn list's count (list at +0x258). AIUpdate
    /// drains it through SpawnCreatureFromSpawnList 0x00591CA0.</summary>
    private const int EncounterSpawnListCount = 0x25C;

    /// <summary>CSWSEncounter + this = started (symbol database). 1 once it has fired, and
    /// the reason a repeated fire request costs nothing: SpawnIfAppropriate's second
    /// instruction pair tests it and returns.</summary>
    private const int EncounterStarted = 0x278;

    /// <summary>CSWSEncounter + this = the object that last entered. EventHandler stores it
    /// here (0x00594253) BEFORE calling SpawnIfAppropriate, and
    /// SpawnCreatureFromSpawnList reads it back (0x00591D0F) to anchor the spawn near
    /// whoever walked in. Undocumented in the symbol database; named from those two uses.</summary>
    private const int EncounterLastEntered = 0x2E0;

    /// <summary>CSWSEncounter + this = "did an eligible object turn up". UpdateActivateAreaList
    /// sets it (0x00590F4E) and SpawnIfAppropriate refuses unless it reads 1. Undocumented.</summary>
    private const int EncounterEligible = 0x28C;

    /// <summary>CSWSEncounter + this = the computed spawn pool. SpawnIfAppropriate requires
    /// it to be &gt; 0.0f after CalculateSpawnPool or nothing is built. Undocumented.</summary>
    private const int EncounterSpawnPool = 0x2D8;

    /// <summary>CSWSEncounter + this = player_only (symbol database). When set,
    /// UpdateActivateAreaList only accepts a creature whose stats say is_pc, or one the
    /// party table claims (0x00590F18..0x00590F4C). 34 of the base game's ~47 encounter
    /// placements set it, including every Taris, Korriban and Star Forge fight — so this
    /// is the gate, not a corner case.</summary>
    private const int EncounterPlayerOnly = 0x318;

    /// <summary>CSWSArea + this = the subarea object-id list (CExoArrayList: data @+0x1A0,
    /// count @+0x1A4). CSWSEncounter::AddToArea 0x00593520 appends every encounter here
    /// (the tail at 0x00593588..0x005935BB), and InSubAreas walks it — so this list is the
    /// complete, engine-maintained set of encounters in the module.</summary>
    private const int AreaSubareaList = 0x1A0;

    private const int AreaSubareaCount = 0x1A4;

    // CSWSEncounter::GetInActivateArea(Vector*) -> int. The engine's own point-in-polygon
    // test over the encounter's geometry (+0x290/+0x294 with the index list at
    // +0x298/+0x29C). Reads x and y only. Pure: it touches no global state, which is why
    // it is safe to call once per encounter per pass.
    private static readonly delegate* unmanaged[Thiscall]<nint, Vec3*, int> GetInActivateAreaFn =
        (delegate* unmanaged[Thiscall]<nint, Vec3*, int>)0x00590090;

    // CSWSEncounter::SpawnIfAppropriate(uint enteringObjectId, float difficultyOverride).
    // Reached from SetActive 0x00593C50, AIUpdate's reset path 0x00594150 and EventHandler
    // 0x00594261 — all three pass -1.0f for the override, so we do too. It does NOT spawn
    // anything itself: it decides, builds the spawn list and sets started, and the server's
    // next AIUpdate creates the creatures.
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, float, void> SpawnIfAppropriateFn =
        (delegate* unmanaged[Thiscall]<nint, uint, float, void>)0x00593BE0;

    /// <summary>The eligibility argument every engine call site passes.</summary>
    private const float EncounterDefaultDifficulty = -1.0f;

    /// <summary>
    /// First 14 bytes of GetInActivateArea: <c>sub esp,0x14</c>, <c>mov eax,[ecx+0x29C]</c>,
    /// <c>push ebx</c>, <c>xor ebx,ebx</c>, <c>cmp eax,ebx</c>. The <c>[ecx+0x29C]</c>
    /// displacement is the geometry index count, so a mismatch here means either the wrong
    /// address or a different build's class layout — exactly the two things worth refusing on.
    /// </summary>
    private static ReadOnlySpan<byte> GetInActivateAreaPrologue =>
        [0x83, 0xEC, 0x14, 0x8B, 0x81, 0x9C, 0x02, 0x00, 0x00, 0x53, 0x33, 0xDB, 0x3B, 0xC3];

    /// <summary>
    /// First 20 bytes of SpawnIfAppropriate: <c>push esi</c>, <c>mov esi,ecx</c>, then the
    /// two early-outs this whole feature leans on — <c>mov eax,[esi+0x238]</c> (active) and
    /// <c>cmp [esi+0x278],1</c> (started). Verifying these bytes verifies the two field
    /// offsets at the same time as the entry point.
    /// </summary>
    private static ReadOnlySpan<byte> SpawnIfAppropriatePrologue =>
    [
        0x56, 0x8B, 0xF1, 0x8B, 0x86, 0x38, 0x02, 0x00, 0x00, 0x85, 0xC0, 0x74, 0x56,
        0x83, 0xBE, 0x78, 0x02, 0x00, 0x00, 0x01,
    ];

    private static int _encounterCallsVerified;   // 0 unknown, 1 ok, -1 refused

    /// <summary>
    /// Verify-bytes-or-refuse, checked once and cached. Nothing in this section calls the
    /// engine until this has passed; a mismatch disables client-triggered encounters for the
    /// run rather than calling into whatever is actually there.
    /// </summary>
    public static bool VerifyEncounterCalls(out string diagnostic)
    {
        if (_encounterCallsVerified != 0)
        {
            diagnostic = _encounterCallsVerified > 0 ? "already verified" : "already refused";
            return _encounterCallsVerified > 0;
        }

        if (!Matches((byte*)0x00590090, GetInActivateAreaPrologue, out int index, out byte saw))
        {
            _encounterCallsVerified = -1;
            diagnostic = $"GetInActivateArea 0x590090 prologue mismatch at +{index}"
                + $" (saw {saw:X2}, expected {GetInActivateAreaPrologue[index]:X2})";
            return false;
        }

        if (!Matches((byte*)0x00593BE0, SpawnIfAppropriatePrologue, out index, out saw))
        {
            _encounterCallsVerified = -1;
            diagnostic = $"SpawnIfAppropriate 0x593BE0 prologue mismatch at +{index}"
                + $" (saw {saw:X2}, expected {SpawnIfAppropriatePrologue[index]:X2})";
            return false;
        }

        _encounterCallsVerified = 1;
        diagnostic = "encounter calls verified at 0x590090 / 0x593BE0";
        return true;
    }

    private static bool Matches(byte* site, ReadOnlySpan<byte> expected, out int index, out byte saw)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            if (site[i] != expected[i])
            {
                index = i;
                saw = site[i];
                return false;
            }
        }

        index = 0;
        saw = 0;
        return true;
    }

    /// <summary>Resolves an encounter by object id, vtable-checked. 0 if absent or not one.</summary>
    public static nint ResolveEncounter(uint objectId)
    {
        nint encounter = ResolveServerObject(objectId);
        return encounter != 0 && *(nint*)encounter == EncounterVtable ? encounter : 0;
    }

    /// <summary>
    /// Copies the object ids of every encounter in the loaded area into
    /// <paramref name="ids"/> and returns how many were written. Reads the area's own
    /// subarea list, so it costs one dereference plus a vtable check per subarea — no
    /// memory sweep, and nothing is missed that the engine itself would test.
    /// </summary>
    public static int ListAreaEncounters(Span<uint> ids)
    {
        nint area = GetAreaPointer();
        if (area == 0)
        {
            return 0;
        }

        nint list = *(nint*)(area + AreaSubareaList);
        int count = *(int*)(area + AreaSubareaCount);
        if (list == 0 || count <= 0 || count > 4096)
        {
            return 0;
        }

        int written = 0;
        for (int i = 0; i < count && written < ids.Length; i++)
        {
            uint id = *(uint*)(list + (i * 4));
            if (id != 0 && id != ObjectInvalid && ResolveEncounter(id) != 0)
            {
                ids[written++] = id;
            }
        }

        return written;
    }

    /// <summary>True when <paramref name="position"/> is inside the encounter's activate
    /// polygon, by the engine's own test. Caller must have passed
    /// <see cref="VerifyEncounterCalls"/>.</summary>
    public static bool IsInsideEncounter(nint encounter, Vec3 position) =>
        GetInActivateAreaFn(encounter, &position) != 0;

    /// <summary>Why a fire request did nothing. Published for the log and the battery.</summary>
    public enum EncounterFireResult
    {
        Started = 0,
        NotAnEncounter = 1,
        NoPhantom = 2,
        NotArmed = 3,
        /// <summary>The engine ran its test and said no — out of the polygon, not hostile to
        /// the encounter's faction, dead, or an empty spawn pool. A legitimate answer.</summary>
        EngineDeclined = 4,
    }

    /// <summary>
    /// Runs the engine's own "should this encounter fire" decision with
    /// <paramref name="enteringObjectId"/> standing in for the player who walked in, and
    /// reports whether it started. MAIN THREAD ONLY.
    ///
    /// This is `CSWSEncounter::EventHandler`'s event-0xC body, reproduced: store the
    /// entering object at +0x2E0 (so the spawn is anchored on that body, exactly as it would
    /// be for a local player), then SpawnIfAppropriate. The engine still decides — it
    /// re-walks the area, checks the body is hostile to the encounter's faction
    /// (GetAIStateReputation == 2), alive, and computes a spawn pool. We supply the body,
    /// not the verdict.
    ///
    /// ⚠ The one thing we do override is <c>player_only</c>, and only for the duration of
    /// the call. The remote player IS a player; the engine cannot tell, because their body
    /// here is a spawned creature with is_pc clear and no party-table membership. Suspending
    /// the flag is far narrower than making a phantom claim to be a PC, which would change
    /// how it is treated by XP, death and saving. It is restored on every path.
    /// </summary>
    public static EncounterFireResult FireEncounter(uint encounterId, uint enteringObjectId)
    {
        nint encounter = ResolveEncounter(encounterId);
        if (encounter == 0)
        {
            return EncounterFireResult.NotAnEncounter;
        }

        // Cheap pre-check purely so the caller can tell "already fired / switched off" apart
        // from "the engine looked and declined". SpawnIfAppropriate tests both itself.
        if (*(int*)(encounter + EncounterActive) == 0 || *(int*)(encounter + EncounterStarted) == 1)
        {
            return EncounterFireResult.NotArmed;
        }

        int playerOnly = *(int*)(encounter + EncounterPlayerOnly);

        *(uint*)(encounter + EncounterLastEntered) = enteringObjectId;
        *(int*)(encounter + EncounterEligible) = 0;

        try
        {
            if (playerOnly != 0)
            {
                *(int*)(encounter + EncounterPlayerOnly) = 0;
            }

            SpawnIfAppropriateFn(encounter, enteringObjectId, EncounterDefaultDifficulty);
        }
        finally
        {
            if (playerOnly != 0)
            {
                *(int*)(encounter + EncounterPlayerOnly) = playerOnly;
            }
        }

        return *(int*)(encounter + EncounterStarted) == 1
            ? EncounterFireResult.Started
            : EncounterFireResult.EngineDeclined;
    }

    /// <summary>How many creatures the encounter still has queued to spawn. Diagnostics only
    /// — the server's own AIUpdate drains this.</summary>
    public static int EncounterPendingSpawns(nint encounter) =>
        *(int*)(encounter + EncounterSpawnListCount);

    /// <summary>The spawn pool the last eligibility pass computed, for the log.</summary>
    public static float EncounterPool(nint encounter) => *(float*)(encounter + EncounterSpawnPool);

    // ---- pause state -----------------------------------------------------------

    /// <summary>
    /// All six auto-pause options, as a mask over CClientOptions + 0x14: end of combat
    /// round, enemy sighted, mine sighted, party killed, action menu, new target selected.
    /// </summary>
    private const uint AutoPauseOptionMask = 0x0001F800;

    /// <summary>
    /// Tutorial windows, the same dword, bit 1.
    ///
    /// ⚠ These are a single-player convenience that stalls a co-op fight. Measured: a player
    /// who walked into a room of hostiles ALONE — their partner elsewhere in the module — got
    /// "The game pauses when you sight an enemy", and that box sat on screen for six minutes
    /// while enemies beat on them. Their character sheathed its weapon, every queued action
    /// was ignored, and the fight simply stopped. The auto-pause options beside it are already
    /// cleared for exactly this reason; the tutorial box is the same problem one layer up,
    /// and it is worse because it needs a click that a player being hit is not free to give.
    ///
    /// Two independent confirmations of the bit: <c>SetTutorialWindowsEnabled 0x005472F0</c>
    /// toggles it, and <c>CGuiInGame::TutorialReasonWillShowWindow 0x0062F420</c> opens with
    /// <c>test byte ptr [eax+0x14], 2</c> and returns "no window" when it is clear — so
    /// clearing it suppresses every tutorial reason, not just this one.
    /// </summary>
    private const uint TutorialWindowMask = 0x00000002;

    private const int ClientOptionsFlags = 0x14;

    /// <summary>
    /// Turns off every auto-pause. Two instances that pause themselves independently
    /// desynchronise on their own, and "party killed" in particular fires constantly once
    /// players start dying.
    ///
    /// This clears the same option bits the in-game options screen writes, rather than
    /// fighting the pause bitmask every frame — all six off is a configuration the game
    /// ships as valid, and manual and GUI pauses keep working.
    ///
    /// <see cref="TutorialWindowMask"/> goes with them: a tutorial box needs a CLICK to
    /// dismiss, which a player mid-fight cannot give, and it stalls their whole combat until
    /// they do.
    /// </summary>
    public static bool SuppressAutoPause()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint options = Deref(Deref(client + 0x04) + 0x04);
        if (options == 0)
        {
            return false;
        }

        const uint unwanted = AutoPauseOptionMask | TutorialWindowMask;

        uint* flags = (uint*)(options + ClientOptionsFlags);
        if ((*flags & unwanted) == 0)
        {
            return false;
        }

        *flags &= ~unwanted;
        return true;
    }

    /// <summary>
    /// The pause reason the window-deactivation path sets. `Global::DeactivateRenderWindow`
    /// (`0x00401D90`) calls `SetPauseState(2, 1)` when the window loses focus, and that
    /// runs `CWorldTimer::PauseWorldTimer`, so an unfocused instance stops simulating
    /// entirely. Fatal for co-op, where at most one of the two windows can be focused.
    /// </summary>
    public const int PauseReasonUnfocused = 2;

    /// <summary>CServerExoAppInternal + this = the byte of pause-reason bits.</summary>
    private const int ServerPauseMaskOffset = 0x10078;

    private const int AppServerOffsetInternal = 0x04;

    /// <summary>Resolves CServerExoApp, or 0 before a module is loaded.</summary>
    private static nint GetServer()
    {
        nint appManager = Deref(AppManagerPtr);
        return appManager == 0 ? 0 : Deref(appManager + AppServerOffset);
    }

    /// <summary>Resolves CClientExoApp, which exists from startup, menu included.</summary>
    private static nint GetClient()
    {
        nint appManager = Deref(AppManagerPtr);
        return appManager == 0 ? 0 : Deref(appManager + AppClientOffset);
    }

    /// <summary>
    /// Asks the game to load a save, exactly as the quick-load path does: it is the same
    /// client-to-server request the save menu ends up sending, so the game goes straight
    /// into the world without the menu being involved.
    ///
    /// <paramref name="saveId"/> is the numeric prefix of the save directory, so
    /// `000002 - Game1` is 2.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>

    /// <summary>True once the server object exists (it does not at the main menu).</summary>
    public static bool HasServer => GetServer() != 0;

    // ---- spectate camera: the engine's own follow handoff -------------------------
    //
    // CSWCModule::SetCameraFollow(clientObjectId, 1) is how the engine itself moves the
    // third-person camera between creatures: ChangeCharacter (0x5F6C27), the
    // ControlObject network handler (0x64CBB8/0x64CC76) and area attach (0x5F5D8B) all
    // funnel through it. It resolves the id via CClientExoApp::GetCreatureByGameObjectID,
    // reuses the existing CAurBehaviorCameraFollow when one is installed, queues into the
    // module's pending fields (+0x314/+0x318) when the module is still loading, and
    // early-outs while the client is in combat MODE (weapons-drawn UI state) — so a
    // caller must treat it as a LEVEL and re-assert, not fire it once and trust it.

    /// <summary>CClientExoApp::GetModule — the CSWCModule the camera belongs to; null
    /// until a client world is up.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetClientModuleFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED530;

    /// <summary>CSWCModule::SetCameraFollow — thiscall (uint clientObjectId, int bImmediate).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int, void> SetCameraFollowFn =
        (delegate* unmanaged[Thiscall]<nint, uint, int, void>)0x0063F9D0;

    /// <summary>
    /// Points the camera at a SERVER object (the id space every co-op channel uses); the
    /// client id is the documented `id | 0x80000000` conversion (see PlayBark's notes on
    /// ServerToClientObjectId, 0x005F2AC0). MAIN THREAD ONLY. Returns false when no
    /// client world is up — and silently does nothing while the client is in combat mode,
    /// which is why callers re-assert.
    /// </summary>
    public static bool SetCameraFollowServerObject(uint serverObjectId)
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint module = GetClientModuleFn(client);
        if (module == 0)
        {
            return false;
        }

        SetCameraFollowFn(module, serverObjectId | 0x80000000, 1);
        return true;
    }

    /// <summary>Hands the camera back to the creature the player controls, through the
    /// same engine call. MAIN THREAD ONLY.</summary>
    public static bool RestoreCameraToControlledCreature()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint module = GetClientModuleFn(client);
        nint controlled = GetClientPlayerCreatureFn(client);
        if (module == 0 || controlled == 0)
        {
            return false;
        }

        // A client creature's +0x04 already holds its CLIENT-space id.
        SetCameraFollowFn(module, *(uint*)(controlled + 0x04), 1);
        return true;
    }

}
