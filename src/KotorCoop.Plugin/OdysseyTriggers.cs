namespace KotorCoop.Plugin;

/// <summary>
/// The engine surface <c>TriggerRelayController</c> needs to watch and fire GENERIC triggers
/// (a <c>CSWSTrigger</c> whose <c>OnEnter</c> starts a cutscene/dialogue/scripted event — the
/// classic "walk into the room, the scene starts" — never a trap, never an area transition).
///
/// A NEW, STANDALONE file rather than an addition to <c>Odyssey.cs</c>, deliberately: several
/// concurrent worktree agents already touch that file, and every primitive this class needs
/// either already has a PUBLIC twin there (<see cref="Odyssey.ResolveServerObject"/>,
/// <see cref="Odyssey.GetPlayerCreaturePointer"/>, <see cref="Odyssey.GetPosition"/>,
/// <see cref="Odyssey.IsConversationActive"/>) that this class calls directly, or is a
/// PRIVATE primitive re-declared here from the SAME already-verified address Odyssey.cs
/// documents (cited below at each re-declaration) — never a new guess.
///
/// Everything here was read out of `swkotor.exe` (GOG 1.0.3) with `tools/disasm.py` and
/// `tools/xrefs.py`, cross-checked against `tools/kotor1_0_3.db` (the imported symbol
/// database) with `tools/q.py`. Tag key: **VERIFIED** = read directly out of the binary ·
/// **INFERRED** = a reasonable reading that was not independently confirmed a second way ·
/// **DESIGN** = a choice made here. See docs/research/trigger-relay.md for the full write-up,
/// mirroring docs/research/client-encounters.md's structure for the encounter channel this
/// one runs parallel to.
/// </summary>
internal static unsafe class OdysseyTriggers
{
    // ---- primitives re-declared from Odyssey.cs's own already-verified addresses ----------
    //
    // Every address below is cited in Odyssey.cs today (line references as of this pass):
    // VirtualMachinePtr / RunScriptFn at Odyssey.cs:54 and Odyssey.cs:7296-7297 (RunScriptFn
    // is VERIFIED LIVE there via the door-OnOpen and creature+0x260 call sites); AppManagerPtr
    // / AppServerOffset at Odyssey.cs:100-101; GetModuleFn / ModuleAreaOffset at
    // Odyssey.cs:2698-2701; AreaSubareaList / AreaSubareaCount at Odyssey.cs:2263-2269 (used
    // by ListAreaEncounters — the SAME array holds trigger ids, since CSWSArea::InSubAreas
    // walks it for triggers, doors, areas of effect and encounters alike); ObjectIdOffset at
    // Odyssey.cs:8249.

    private const nint VirtualMachinePtr = 0x007A3A00;

    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppServerOffset = 0x08;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetModuleFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AE6B0;
    private const int ModuleAreaOffset = 0x220;

    private const int AreaSubareaList = 0x1A0;
    private const int AreaSubareaCount = 0x1A4;

    private const int ObjectIdOffset = 0x04;

    // CVirtualMachine::RunScript (0x005D0FC0). VERIFIED LIVE elsewhere in this codebase
    // (Odyssey.cs FireCreatureScriptSlot0x260, and the door OnOpen call CSWSDoor::OpenDoor
    // itself makes at 0x00589D32) as `RunScript(Deref(VirtualMachinePtr), &object+scriptSlot,
    // *(object+4) [object's own id, i.e. OBJECT_SELF], 1)`. Re-declared here rather than
    // exposed from Odyssey.cs per this file's own header note.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, uint, int, void> RunScriptFn =
        (delegate* unmanaged[Thiscall]<nint, nint, uint, int, void>)0x005D0FC0;

    private static nint Deref(nint p) => *(nint*)p;

    private static nint GetServer()
    {
        nint appManager = Deref(AppManagerPtr);
        return appManager == 0 ? 0 : Deref(appManager + AppServerOffset);
    }

    private static nint GetAreaPointer()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint module = GetModuleFn(server);
        return module == 0 ? 0 : Deref(module + ModuleAreaOffset);
    }

    // ---- CSWSTrigger shape — VERIFIED this pass ------------------------------------------
    //
    // q.py: `CSWSTrigger` size=0x318 vtable=0x00749A38; `CSWSTrigger::EventHandler 0x0058F140`,
    // `CSWSTrigger::InTrigger 0x0058CE40`, `CSWSTrigger::LoadTrigger 0x0058DA80`.

    private const nint TriggerVtable = 0x00749A38;

    // CSWSTrigger::InTrigger(Vector3*) -> int, __thiscall. VERIFIED by disassembling its
    // prologue: `mov eax,[ecx+0x2BC]; sub esp,0x14; push ebx; xor ebx,ebx; cmp eax,ebx; je
    // +0x6E` then three `fld`/`fsub` pairs against `[ecx+0x90]`/`[ecx+0x94]`/`[ecx+0x98]` (the
    // trigger's own cached position, distance-checked against the point argument before the
    // real polygon test) — the same point-in-polygon shape
    // `CSWSEncounter::GetInActivateArea 0x00590090` has, on a different class. This is the
    // per-frame watch's polygon test, the trigger analogue of
    // <see cref="Odyssey.IsInsideEncounter"/>.
    private static readonly delegate* unmanaged[Thiscall]<nint, Vec3*, int> InTriggerFn =
        (delegate* unmanaged[Thiscall]<nint, Vec3*, int>)0x0058CE40;

    private static ReadOnlySpan<byte> InTriggerPrologue =>
        [0x8B, 0x81, 0xBC, 0x02, 0x00, 0x00, 0x83, 0xEC, 0x14, 0x53, 0x33, 0xDB, 0x3B, 0xC3];

    /// <summary>
    /// CSWSTrigger + this = the OnEnter script (a CExoString: 4-byte data pointer, 4-byte
    /// length — the same shape <see cref="Odyssey.PeekCreatureScriptSlot0x260"/> already
    /// documents for a creature's own event slot). VERIFIED TWICE independently:
    ///
    /// 1. `CSWSTrigger::LoadTrigger 0x0058DA80` assigns the GFF field labelled
    ///    <c>"ScriptOnEnter"</c> (string literal at 0x00749A28, confirmed by byte search and
    ///    xref) into <c>[esi+0x24C]</c> at 0x0058DBAD-0x0058DBBF.
    /// 2. `CSWSTrigger::EventHandler 0x0058F140`'s event-0xC (subarea-entered) handler, in the
    ///    branch reached when the mine/trap gate at <see cref="TriggerMineGate"/> is 0, calls
    ///    `RunScript(Deref(VirtualMachinePtr), esi+0x24C, [esi+4], 1)` at
    ///    0x0058F504-0x0058F517 — the exact same field, read back by the same engine call this
    ///    class's <see cref="FireTrigger"/> reproduces.
    /// </summary>
    private const int TriggerScriptOnEnter = 0x24C;

    /// <summary>
    /// CSWSTrigger + this = the linked-to-module CExoString (symbol database: this trigger IS
    /// an area transition when non-empty). VERIFIED: EventHandler's event-0xC handler reads
    /// this field's length first (`lea ecx,[esi+0x238]; call CExoString::GetLength 0x5E5790`)
    /// and branches away from the generic-script path whenever it is non-zero
    /// (0x0058F267-0x0058F274). This class never fires a trigger with a non-empty
    /// linked-to-module — see <see cref="IsRelayEligible"/>.
    /// </summary>
    private const int TriggerLinkedToModule = 0x238;

    /// <summary>
    /// CSWSTrigger + this = the entering object's id. VERIFIED: at the very start of
    /// EventHandler's event-0xC handler, BEFORE any branching,
    /// `CSWCCMessageData::GetObjectID(0) 0x004D64C0` is called on the event and the result is
    /// stored here (0x0058F1FA-0x0058F203) — the exact same "cache who entered before firing
    /// anything" shape `CSWSEncounter::EventHandler` uses at its own undocumented +0x2E0
    /// (docs/research/client-encounters.md §3). INFERRED, not independently re-confirmed
    /// against the NWScript engine function backing <c>GetEnteringObject()</c>: this class
    /// writes the phantom's id here on the strong analogy to the encounter channel's proven
    /// pattern, but whether <c>GetEnteringObject()</c> inside the fired script actually reads
    /// this exact field has not been checked a second way. See
    /// docs/research/trigger-relay.md §4 NEEDS-LIVE.
    /// </summary>
    private const int TriggerLastEntered = 0x29C;

    /// <summary>
    /// CSWSTrigger + this = a mine/trap-activity gate, undocumented in the symbol database.
    /// VERIFIED as a live gate, not fully decoded: <see cref="InTriggerFn"/>'s own prologue
    /// tests it first (`je` past the whole polygon test when 0), and EventHandler's event-0xC
    /// handler tests the SAME offset to choose between the generic-script path (0x0058F504,
    /// zero) and <c>CSWSTrigger::OnEnterMine 0x0058D570</c> (non-zero). This class only ever
    /// fires when it reads 0 — a defensive re-check, since <see cref="IsRelayEligible"/> also
    /// gates on the offline TrapFlag classification (docs/research/trigger-relay.md's
    /// inventory) before a trigger id is even reported.
    /// </summary>
    private const int TriggerMineGate = 0x2BC;

    // ---- last-local-fire correlation (2026-09-25, party-select relay's second pass) --------
    //
    // docs/research/galaxy-map-and-party-select.md's 2026-09-25 relay section, live pass 2:
    // calling CGuiInGame::ShowPartySelection directly (skipping whatever the retail script
    // chain around it also does — input class, pause, and possibly more never fully RE'd)
    // produced a live false "refused - busy" while the screen never actually opened. The fix
    // is to run the SAME retail path a real walk-in takes — RunScript on the causal trigger's
    // OnEnter, exactly what FireTrigger/FireTriggerForLocalPlayer already do — rather than call
    // ShowPartySelection ourselves. PartySelectSuppressionController needs to know WHICH
    // trigger (if any) most recently fired for THIS instance's own real player, so it can relay
    // that trigger id instead of guessing at the panel's own (possibly stale — CGuiInGame+0x78
    // is a persistent, reused object, see PartySelectSuppressionController's own header)
    // exit_script field. Recorded from BOTH fire paths: this class's own FireTriggerForLocalPlayer
    // (the dev-verb path, which never reaches TriggerFireHook — see that class's own header,
    // "firing THROUGH the relay never reaches this call site") and TriggerRelayController's
    // native-fire observation (the real walk-in path), so either one leaves a trail here.
    private static uint _lastLocalTriggerId;
    private static long _lastLocalTriggerAtMs;

    /// <summary>Records that <paramref name="triggerId"/> just fired for THIS instance's own
    /// real player (never a phantom, never a relayed/remote fire). Overwrites unconditionally —
    /// only the MOST RECENT local fire is remembered, the same "one slot, always the freshest"
    /// shape the party-select capture that reads this back needs.</summary>
    public static void RecordLocalFire(uint triggerId)
    {
        _lastLocalTriggerId = triggerId;
        _lastLocalTriggerAtMs = Environment.TickCount64;
    }

    /// <summary>The most recent locally-fired trigger, if it fired within
    /// <paramref name="maxAgeMs"/> of now — the causal-correlation window
    /// <see cref="PartySelectSuppressionController"/> uses to decide which trigger (if any) to
    /// relay for a captured party-select panel. False (triggerId 0) when nothing has fired
    /// locally yet, or the last one is older than the window.</summary>
    public static bool TryGetRecentLocalTrigger(long maxAgeMs, out uint triggerId)
    {
        if (_lastLocalTriggerId != 0 && Environment.TickCount64 - _lastLocalTriggerAtMs <= maxAgeMs)
        {
            triggerId = _lastLocalTriggerId;
            return true;
        }

        triggerId = 0;
        return false;
    }

    /// <summary>Module change: the remembered trigger id belongs to the old area and may not
    /// even resolve (or, worse, coincidentally resolve to something unrelated) in the new one.
    /// </summary>
    public static void InvalidateLocalFire()
    {
        _lastLocalTriggerId = 0;
        _lastLocalTriggerAtMs = 0;
    }

    private static int _triggerCallsVerified; // 0 unknown, 1 ok, -1 refused

    /// <summary>Verify-bytes-or-refuse, exactly like <see cref="Odyssey.VerifyEncounterCalls"/>:
    /// nothing here calls the engine until this has passed once.</summary>
    public static bool VerifyTriggerCalls(out string diagnostic)
    {
        if (_triggerCallsVerified != 0)
        {
            diagnostic = _triggerCallsVerified > 0 ? "already verified" : "already refused";
            return _triggerCallsVerified > 0;
        }

        byte* site = (byte*)0x0058CE40;
        for (int i = 0; i < InTriggerPrologue.Length; i++)
        {
            if (site[i] != InTriggerPrologue[i])
            {
                _triggerCallsVerified = -1;
                diagnostic = $"InTrigger 0x58CE40 prologue mismatch at +{i}"
                    + $" (saw {site[i]:X2}, expected {InTriggerPrologue[i]:X2})";
                return false;
            }
        }

        _triggerCallsVerified = 1;
        diagnostic = "trigger calls verified at 0x58CE40";
        return true;
    }

    /// <summary>Resolves a trigger by object id, vtable-checked. 0 if absent or not one.</summary>
    public static nint ResolveTrigger(uint objectId)
    {
        nint trigger = Odyssey.ResolveServerObject(objectId);
        return trigger != 0 && *(nint*)trigger == TriggerVtable ? trigger : 0;
    }

    /// <summary>
    /// Copies the object ids of every trigger in the loaded area's subarea list into
    /// <paramref name="ids"/>. Same list <see cref="Odyssey.ListAreaEncounters"/> walks
    /// (doors, triggers, areas of effect and encounters all share it), filtered here to the
    /// trigger vtable instead.
    /// </summary>
    public static int ListAreaTriggers(Span<uint> ids)
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
            if (id != 0 && id != Odyssey.ObjectInvalid && ResolveTrigger(id) != 0)
            {
                ids[written++] = id;
            }
        }

        return written;
    }

    public static bool IsInsideTrigger(nint trigger, Vec3 position) =>
        InTriggerFn(trigger, &position) != 0;

    /// <summary>
    /// True when <paramref name="trigger"/> is safe for this channel at all: not an area
    /// transition (empty linked-to-module) and not currently mine/trap-active. DESIGN: this
    /// is a defensive re-check, not the primary gate — the primary gate is the coop.ini
    /// allowlist pattern applied to the OnEnter script name, checked by the caller
    /// (TriggerRelayController) before a trigger id is ever published, exactly mirroring how
    /// the offline inventory in docs/research/trigger-relay.md classifies traps and
    /// transitions before a script name pattern is even considered.
    /// </summary>
    public static bool IsRelayEligible(nint trigger)
    {
        int linkedLength = *(int*)(trigger + TriggerLinkedToModule + 4); // CExoString length
        if (linkedLength > 0)
        {
            return false;
        }

        return *(int*)(trigger + TriggerMineGate) == 0;
    }

    /// <summary>
    /// Reads the trigger's OnEnter script name for the allowlist pattern check. Same
    /// pointer+length CExoString read <see cref="Odyssey.PeekCreatureScriptSlot0x260"/> uses,
    /// applied to <see cref="TriggerScriptOnEnter"/> instead. Empty on anything unreadable.
    /// </summary>
    public static string PeekScriptOnEnter(nint trigger)
    {
        nint* p = (nint*)(trigger + TriggerScriptOnEnter);
        nint dataPtr = p[0];
        int length = (int)p[1];

        if (dataPtr == 0 || length <= 0 || length > 32)
        {
            return string.Empty;
        }

        byte* text = (byte*)dataPtr;
        System.Text.StringBuilder sb = new(length);
        for (int i = 0; i < length && text[i] != 0; i++)
        {
            char c = (char)text[i];
            if (c is < (char)0x20 or >= (char)0x7F)
            {
                return string.Empty; // not printable ASCII — treat as unreadable, not garbage
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    public enum TriggerFireResult
    {
        Fired = 0,
        NotATrigger = 1,
        NoPhantom = 2,
        NotEligible = 3,
        NoVm = 4,
    }

    /// <summary>
    /// Runs the SAME code EventHandler's event-0xC generic-script branch runs
    /// (0x0058F1FA-0x0058F517, cited field-by-field above), with <paramref name="enteringObjectId"/>
    /// (the requesting peer's phantom) standing in for the player who walked in: cache the
    /// entering object, then call RunScript on the OnEnter script field with the TRIGGER's own
    /// id as OBJECT_SELF — bit for bit what the real engine call site does. MAIN THREAD ONLY.
    ///
    /// Unlike <see cref="Odyssey.FireEncounter"/>, there is nothing to suspend: generic
    /// triggers carry no player_only-style gate in the runtime object (docs/research/trigger-relay.md
    /// §2) — the SCRIPT itself decides who counts as having entered, typically via
    /// <c>GetEnteringObject() == GetFirstPC()</c>, which is exactly the case this exists to
    /// cover: it never resolves true on any instance but the phantom's owner's without this.
    /// </summary>
    public static TriggerFireResult FireTrigger(uint triggerId, uint enteringObjectId)
    {
        nint trigger = ResolveTrigger(triggerId);
        if (trigger == 0)
        {
            return TriggerFireResult.NotATrigger;
        }

        if (!IsRelayEligible(trigger))
        {
            return TriggerFireResult.NotEligible;
        }

        nint vm = Deref(VirtualMachinePtr);
        if (vm == 0)
        {
            return TriggerFireResult.NoVm;
        }

        // A trigger that spawns hostiles runs as if THIS player walked in. Its script checks the
        // entering object is the player (GetIsPC, == GetFirstPC, IsObjectPartyMember, a party
        // member by index) and the peer's phantom passes none of those, so the host spawned
        // nothing while the joiner's own engine spawned the enemies in its world alone. The
        // joiner no longer runs these itself (TriggerFireHook.DeferHostileSpawnsToHost), so
        // they exist once, here, and reach it as stand-ins. tools/hostile_spawn_triggers.py
        // audited all 27: none moves, talks to or teleports the entering object.
        if (HostileSpawnTriggers.ContainsExoString(trigger + TriggerScriptOnEnter))
        {
            // On a joiner (the host's own walk-in replayed here for its body) a hostile spawn
            // never runs: the host already spawned the enemies and they arrive as stand-ins.
            if (TriggerFireHook.DeferHostileSpawnsToHost)
            {
                PluginHost.Log($"triggers: 0x{*(uint*)(trigger + ObjectIdOffset):X} spawns hostiles - the host's copy "
                    + "already did; not replayed here");
                return TriggerFireResult.Fired;
            }

            uint hostPlayer = Odyssey.GetPlayerObjectId();
            if (hostPlayer != 0 && hostPlayer != Odyssey.ObjectInvalid)
            {
                enteringObjectId = hostPlayer;
            }
        }

        *(uint*)(trigger + TriggerLastEntered) = enteringObjectId;

        uint triggerObjectId = *(uint*)(trigger + ObjectIdOffset);
        RunScriptFn(vm, trigger + TriggerScriptOnEnter, triggerObjectId, 1);

        return TriggerFireResult.Fired;
    }

    /// <summary>
    /// DEV-ONLY (`firetrigger` verb): fires a trigger's OnEnter for the local player as if they
    /// walked in, found by tag or object id, WITHOUT the relay allowlist - so a live test can
    /// take an exit trigger (e.g. ebn12_ebonexit) that a rig walk cannot reach. MAIN THREAD ONLY.
    /// </summary>
    public static string FireTriggerForLocalPlayer(string tagOrId, uint playerId)
    {
        Span<uint> ids = stackalloc uint[256];
        int count = ListAreaTriggers(ids);
        bool byId = tagOrId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(tagOrId.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint wantedId);
        uint id = byId ? uint.Parse(tagOrId.AsSpan(2), System.Globalization.NumberStyles.HexNumber) : 0;
        byte* tag = stackalloc byte[64];
        for (int i = 0; i < count; i++)
        {
            nint trigger = ResolveTrigger(ids[i]);
            if (trigger == 0)
            {
                continue;
            }

            bool match = byId && ids[i] == id;
            if (!byId)
            {
                Odyssey.ReadObjectTag(trigger, tag, 64);
                int n = 0;
                while (n < 64 && tag[n] != 0)
                {
                    n++;
                }

                match = System.Text.Encoding.ASCII.GetString(tag, n).Equals(tagOrId, StringComparison.OrdinalIgnoreCase);
            }

            if (!match)
            {
                continue;
            }

            nint vm = Deref(VirtualMachinePtr);
            if (vm == 0)
            {
                return "firetrigger: no script VM";
            }

            *(uint*)(trigger + TriggerLastEntered) = playerId;
            uint triggerObjectId = *(uint*)(trigger + ObjectIdOffset);
            string script = PeekScriptOnEnter(trigger);
            RecordLocalFire(triggerObjectId);
            RunScriptFn(vm, trigger + TriggerScriptOnEnter, triggerObjectId, 1);
            return $"firetrigger: fired 0x{ids[i]:X} OnEnter '{script}' for the local player";
        }

        return $"firetrigger: no trigger '{tagOrId}' in this area ({count} listed)";
    }

    /// <summary>
    /// Minimal <c>*</c>-only glob, duplicated on purpose from
    /// <c>KotorCoop.Session.TriggerScriptPatterns</c> (see that class's header for why the
    /// plugin cannot reference it — zero project references, NativeAOT publish). Keep the two
    /// algorithms in lockstep; that one is the one the test suite exercises.
    /// </summary>
    public static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        return MatchesFrom(name, 0, pattern, 0);
    }

    private static bool MatchesFrom(string name, int ni, string pattern, int pi)
    {
        while (pi < pattern.Length && pattern[pi] != '*')
        {
            if (ni >= name.Length || char.ToLowerInvariant(name[ni]) != char.ToLowerInvariant(pattern[pi]))
            {
                return false;
            }

            ni++;
            pi++;
        }

        if (pi >= pattern.Length)
        {
            return ni == name.Length;
        }

        pi++; // consume '*'
        for (int consumed = ni; consumed <= name.Length; consumed++)
        {
            if (MatchesFrom(name, consumed, pattern, pi))
            {
                return true;
            }
        }

        return false;
    }
}
