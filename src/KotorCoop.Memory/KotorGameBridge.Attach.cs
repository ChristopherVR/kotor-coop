// Engine-symbol offsets (CSWSCreature/CSWSObject fields, verified live against this build)
// plus discovery/scan/attach: enumerating creatures, confirming they are still live, and
// resolving the player and the in-process plugin.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- symbols (verified against this build) --------------------------------

    /// <summary>CSWSCreature vtable. Every 4-byte occurrence of this value is a creature.</summary>
    public const uint CreatureVtable = 0x007470C8;

    // CSWSObject, embedded at CSWSCreature+0.
    private const int OffObjectId = 0x04;
    private const int OffTag = 0x18;          // CExoString { char* @0, ulong len @4 }
    private const int OffAreaId = 0x8C;
    private const int OffPosition = 0x90;     // Vector, 3 floats
    private const int OffOrientation = 0x9C;  // Vector, 3 floats (Euler, not a quaternion)
    private const int OffAnimation = 0xD4;

    // The server animation field (+0xD4) carries gait and scripted animations, but weapon
    // draws / attack swings / flourishes (the "x" key) are fire-and-forget on the CLIENT twin
    // and never reach it. The twin's current-animation field reflects what the model actually
    // plays, so reading it captures those too. See docs/research/animation-sync.md.
    private const int OffClientTwin = 0x224;    // CSWSCreature + this = client twin CSWCObject*
    private const int OffTwinAnimation = 0x48;  // CSWCObject + this = current played animation

    /// <summary>
    /// Current hit points, u16. NOT +0xE0: that is the STORED MAX, which is why the
    /// dashboard used to read a health of 0 while the character sheet showed 25/28.
    /// Verified through CSWSObject::SetCurrentHitPoints (0x004CC280), which writes +0xDC,
    /// and ExecuteCommandSetMaxHitPoints, which writes +0xE0 and then calls it.
    /// </summary>
    private const int OffCurrentHitPoints = 0xDC;

    /// <summary>
    /// The STORED max hit points, u16 — and it is <b>zero on every player character</b>,
    /// which is why the readout said "12 (maximum unknown)" for days.
    ///
    /// <c>CSWSObject::GetMaxHitPoints</c> (0x004D01A0) is a one-line <c>mov ax,[ecx+0xE0]</c>,
    /// so this field is right for doors, placeables and ordinary creatures. But
    /// <c>CSWSCreature::GetMaxHitPoints</c> (0x004ED310) overrides it and branches on
    /// <c>stats+0x6C</c> (<c>is_pc</c>): for a PC the engine never stores a total at all, it
    /// re-adds one up from the per-level records every time it is asked. Reading the field
    /// on a PC therefore reads a slot the engine simply never writes.
    /// See <see cref="TryReadMaxHitPoints"/>, which reproduces the branch.
    /// </summary>
    private const int OffMaxHitPoints = 0xE0;

    // CSWSCreatureStats — the pieces CSWSCreature::GetMaxHitPoints sums for a PC.
    // (StatsIsPc, the flag that selects the accumulate path, is declared with the other
    // stats offsets below.)
    private const int StatsLevelListData = 0x28;    // CExoArrayList<CSWLevelStats*>: T* data
    private const int StatsLevelListCount = 0x2C;   // ...then the count, then the capacity
    private const int StatsConModifier = 0xEE;      // byte, added once PER LEVEL (0x004ED372)
    private const int LevelStatsHitPoints = 0x2B;   // byte, the hit points rolled at that level

    /// <summary>A sanity bound on the level list, so a torn read cannot make this walk far.</summary>
    private const int MaxCharacterLevels = 40;

    /// <summary>
    /// Max hit points, following the same branch the engine follows.
    ///
    /// For a PC: sum <c>(level.hitPoints + conModifier)</c> over the character's levels,
    /// which is the loop at 0x004ED360..0x004ED3CA. Verified live against both instances —
    /// a level-1 PC with a rolled 10 and a +2 constitution modifier came out at 12, matching
    /// a full-health reading exactly.
    ///
    /// For anything else: the stored field, which is what the engine returns.
    ///
    /// Returns 0 when nothing can be read, which every caller already treats as "unknown".
    /// </summary>
    private int TryReadMaxHitPoints(nint creature)
    {
        bool isPc = _memory.TryRead(creature + OffCreatureStats, out uint stats)
            && stats != 0
            && _memory.TryReadInt32((nint)stats + StatsIsPc, out int pcFlag)
            && pcFlag == 1;

        if (!isPc)
        {
            return _memory.TryRead(creature + OffMaxHitPoints, out ushort stored) ? stored : 0;
        }

        if (!_memory.TryRead((nint)stats + StatsLevelListData, out uint levels)
            || !_memory.TryReadInt32((nint)stats + StatsLevelListCount, out int levelCount)
            || levels == 0
            || levelCount <= 0
            || levelCount > MaxCharacterLevels)
        {
            return 0;
        }

        int conModifier = _memory.TryRead((nint)stats + StatsConModifier, out byte con) ? con : 0;
        int total = 0;

        for (int i = 0; i < levelCount; i++)
        {
            if (!_memory.TryRead((nint)levels + (i * 4), out uint level)
                || level == 0
                || !_memory.TryRead((nint)level + LevelStatsHitPoints, out byte rolled))
            {
                return 0;
            }

            total += rolled + conModifier;
        }

        return total;
    }

    /// <summary>Damage floors at 1 instead of killing. Used to make a peer's enemies non-lethal.</summary>
    private const int OffMinOneHitPoint = 0x200;

    private const int OffPlot = 0xF8;

    // CSWSCreature.
    private const int OffPartyFollowInfo = 0x4C0;   // CSWSCreaturePartyFollowInfo*
    private const int OffAppearanceType = 0xA60;    // u16, the appearance.2da row
    private const int OffCreatureStats = 0xA74;     // CSWSCreatureStats*
    private const int OffInventory = 0xA2C;         // CSWInventory*

    // CSWInventory slot pointers (symbol database): base 0x4 + 4 * nwscript slot number.
    private const int InvSlotBody = 0x08;           // INVENTORY_SLOT_BODY = 1
    private const int InvSlotRightWeapon = 0x14;    // INVENTORY_SLOT_RIGHTWEAPON = 4
    private const int InvSlotLeftWeapon = 0x18;     // INVENTORY_SLOT_LEFTWEAPON = 5

    // CSWSCreatureStats.
    private const int StatsIsPc = 0x6C;
    private const int StatsFaction = 0x78;
    // ---- discovery ------------------------------------------------------------

    /// <summary>
    /// Finds every live creature by scanning for the vtable pointer, reusing the last result
    /// for a short while.
    ///
    /// ⚠ The cache is not an optimisation, it is the difference between working and not. A
    /// full scan of the game's committed memory takes on the order of the interval it was
    /// being called at, so calling it fresh on every world-state pass consumed an entire CPU
    /// core and starved the pump that replicates POSITION — measured end to end, a peer took
    /// 3-7 seconds to move, and the pump ran at ~4 Hz instead of 60. Creature addresses are
    /// stable while a module is loaded (the object array is rebuilt on load, which is what
    /// <see cref="Invalidate"/> is for), so re-scanning several times a second bought nothing.
    ///
    /// Callers that need fresh FIELD values still get them: only the address list is cached,
    /// and every read through those addresses is live.
    /// </summary>
    public List<CreatureRef> EnumerateCreatures()
    {
        long nowMs = Environment.TickCount64;

        // ⚠ An EMPTY result ages out fast. "No creatures" means "no world yet" — a state
        // that flips exactly once, at load — and caching it for the full 15 s is what made
        // the join arrival randomly awful: the agent attaches at the menu, scans, caches
        // the emptiness, and then every resolve attempt while the save loads returns that
        // stale nothing until the cache happens to expire. Measured live: the same flow
        // resolved 1.7 s after world-ready on one run and 15.7 s on the next, purely on
        // cache-expiry luck — and the seamless-arrival hold timed out watching it. A short
        // TTL keeps the melt-down protection (still at most ~1 scan/s while worldless,
        // the same rate the resolve limiter allows) without ever hiding a fresh world.
        int ttlMs = _creatureCache is { Count: 0 } ? EmptyCreatureCacheMs : CreatureCacheMs;

        // A sweep that finished on the background thread is turned into creature records here,
        // on this thread — the reads-per-hit half was never the expensive part.
        if (_creatureCache is { Count: > 0 } && ItemScanWorker.TryTakeHits(CreatureVtable, out List<nint> fresh))
        {
            _creatureCache = ReadCreatures(fresh);
            _creatureCacheAtMs = nowMs;
            _liveCache = null;
        }
        else if (_creatureCache is null || _creatureCache.Count == 0)
        {
            // ⚠ BLOCKING, and deliberately. This is the "no world yet" path the warning above
            // is about: it flips exactly once, at load, and the seamless-arrival hold is
            // watching for it. Handing back a stale nothing while a background sweep runs
            // would reintroduce precisely the arrival delay EmptyCreatureCacheMs exists to
            // prevent. It is also the cheapest case to block on — there is no world.
            if (_creatureCache is null || nowMs - _creatureCacheAtMs >= ttlMs)
            {
                _creatureCache = ScanCreatures();
                _creatureCacheAtMs = nowMs;
                _liveCache = null;
            }
        }
        else if (nowMs - _creatureCacheAtMs >= ttlMs)
        {
            // In-session refresh: serve the addresses we have and let the sweep run off the
            // pump thread. Creature addresses are stable while a module is loaded (Invalidate
            // is what a load calls), so a list a little past its TTL is the same list — and
            // the alternative, measured on the rig (2026-08-29, perf_trace), was an ~800 ms
            // tick in which no remote player's position reached the game at all.
            ItemScanWorker.Request(CreatureVtable, 512);
        }

        // ⚠ CONFIRMED FAR MORE OFTEN THAN IT IS SCANNED, and that is the point. The scan
        // answers "where are the creature-shaped objects", which is stable for a module; the
        // confirmation answers "which of them does the engine still own", which changes the
        // moment anything dies and is destroyed. Publishing an id the engine no longer holds
        // is not a cosmetic staleness — a peer's forwarded damage keyed by it is owed for
        // ever (docs/research/enemy-presence-sync.md).
        if (_liveCache is null || nowMs - _liveCacheAtMs >= LiveConfirmMs)
        {
            _liveCache = Confirm(_creatureCache);
            _liveCacheAtMs = nowMs;
        }

        return _liveCache;
    }

    /// <summary>How long a creature-address scan is reused. Long enough that scanning stops
    /// dominating the pump, short enough that a creature spawned mid-fight is picked up
    /// quickly.</summary>
    private const int CreatureCacheMs = 15_000;

    /// <summary>How long an EMPTY creature scan is reused — see the warning above.</summary>
    private const int EmptyCreatureCacheMs = 750;

    /// <summary>How long a liveness confirmation is reused. Two small reads per creature, so
    /// four times a second costs a few hundred microseconds — cheap enough to sit in front of
    /// every census publish, which is where an id that has stopped existing does its damage.</summary>
    private const int LiveConfirmMs = 250;

    private List<CreatureRef>? _creatureCache;
    private long _creatureCacheAtMs;

    private List<CreatureRef>? _liveCache;
    private long _liveCacheAtMs;

    /// <summary>
    /// Optional sink for census-liveness notes. An id that the publisher's own engine cannot
    /// resolve MUST NOT leave silently — a silent skip on the receiving end is what made this
    /// whole failure class invisible for weeks (see PeerDamageController).
    ///
    /// ⚠ AND SILENCE MUST NOT BE AMBIGUOUS EITHER. The first version of this only spoke when
    /// it dropped something, so "no line in the log" meant any of "no ghosts", "the gate never
    /// ran", or "the sink was never wired" — and a whole live round was spent not being able
    /// to tell those apart. Every pass now reports, including the do-nothing ones, on a
    /// heartbeat (see <see cref="GateHeartbeatMs"/>).
    /// </summary>
    public Action<string>? CensusDiagnostic { get; set; }

    /// <summary>What the last liveness pass did. Read by the <c>census</c> console verb, so
    /// the state can be asked for on demand instead of waited for in a log.</summary>
    public CensusGateReport LastCensusGate { get; private set; } =
        new(CensusGateState.NeverRun, 0, 0, 0, 0, []);

    /// <summary>However quiet the state is, say it at least this often. A log that has said
    /// nothing for a minute is then provably a log whose agent is not running this code.</summary>
    private const int GateHeartbeatMs = 30_000;

    private long _gateSpokeAtMs;
    private string _gateLastLine = string.Empty;

    /// <summary>
    /// Drops the creature-shaped memory the engine does not actually own.
    ///
    /// Fails SAFE in both directions: with no server (the main menu, a transition) there is
    /// nothing to ask, so the scan is passed through unchanged; and a gate that confirms
    /// nothing at all distrusts itself rather than emptying the world — see
    /// <see cref="LiveObjectGate"/>.
    /// </summary>
    private List<CreatureRef> Confirm(List<CreatureRef> scanned)
    {
        if (scanned.Count == 0)
        {
            Report(new CensusGateReport(CensusGateState.NothingToCheck, 0, 0, 0, 0, []));
            return scanned;
        }

        if (!EngineGlobals.TryGetServerObjectArray(_memory, out nint objectArray))
        {
            // ⚠ THIS CASE USED TO BE SILENT, and it is the one that most resembles the bug:
            // the census goes out ungated and nothing says so. It is legitimate at the main
            // menu and mid-transition; in a loaded world it means the pointer chain is wrong.
            Report(new CensusGateReport(CensusGateState.NoObjectArray, 0, scanned.Count, scanned.Count, 0, []));
            return scanned;
        }

        LiveObjectGate gate = new(id =>
            EngineGlobals.TryResolveServerObject(_memory, objectArray, id, out nint address) ? address : 0);

        List<CreatureRef> live = gate.Confirm(scanned, c => (c.ObjectId, c.Address), out LiveObjectVerdict verdict);

        // A sample of what was dropped, because "7 ghosts" and "7 ghosts, and 0x35 was one of
        // them" are different findings and only the second one settles an argument.
        uint[] droppedIds = [];
        if (verdict.Dropped > 0)
        {
            HashSet<nint> keptAddresses = [.. live.Select(c => c.Address)];
            droppedIds =
            [
                .. scanned.Where(c => !keptAddresses.Contains(c.Address))
                    .Select(c => c.ObjectId)
                    .Take(DroppedIdSample),
            ];
        }

        Report(new CensusGateReport(
            verdict.Trusted ? CensusGateState.Confirmed : CensusGateState.Distrusted,
            objectArray,
            scanned.Count,
            verdict.Kept,
            verdict.Dropped,
            droppedIds));

        return live;
    }

    /// <summary>How many dropped ids are named in the report. Enough to recognise a specific
    /// id in an argument, few enough that the line stays one line.</summary>
    private const int DroppedIdSample = 8;

    /// <summary>Publishes the gate's state and says it out loud when it changes — or every
    /// <see cref="GateHeartbeatMs"/> regardless, so silence can only ever mean "this code is
    /// not running".</summary>
    private void Report(CensusGateReport report)
    {
        LastCensusGate = report;

        string line = report.Describe();
        long nowMs = Environment.TickCount64;

        if (line == _gateLastLine && nowMs - _gateSpokeAtMs < GateHeartbeatMs)
        {
            return;
        }

        _gateLastLine = line;
        _gateSpokeAtMs = nowMs;
        CensusDiagnostic?.Invoke(line);
    }

    /// <summary>
    /// The last-mile gate, applied to the list that is actually PUBLISHED.
    ///
    /// ⚠ Deliberately a second application of the same rule, and not redundant. The list this
    /// filters has been through a cache and a cap since it was confirmed, and after a live
    /// round spent asking "is the gate even on the publish path?" the answer needs to be
    /// structural rather than argued: nothing leaves this method that the engine's own object
    /// array did not confirm at the moment of leaving.
    /// </summary>
    private List<EnemyState> ConfirmPublished(List<EnemyState> enemies)
    {
        CensusPublishRejected = [];

        if (enemies.Count == 0 || !EngineGlobals.TryGetServerObjectArray(_memory, out nint objectArray))
        {
            return enemies;
        }

        LiveObjectGate gate = new(id =>
            EngineGlobals.TryResolveServerObject(_memory, objectArray, id, out nint address) ? address : 0);

        List<EnemyState> kept = gate.ConfirmIds(enemies, e => e.ObjectId, out LiveObjectVerdict verdict);

        // Same self-distrust as the scan gate: resolving none of a full census is a broken
        // check, not an empty world.
        if (!verdict.Trusted)
        {
            Speak(
                $"census publish: the object array resolved NONE of {enemies.Count} ids - "
                + "publishing them all unchecked (the check is broken, not the world)");
            return kept;
        }

        if (verdict.Dropped == 0)
        {
            return kept;
        }

        HashSet<uint> keptIds = [.. kept.Select(e => e.ObjectId)];
        List<uint> rejected = [.. enemies.Where(e => !keptIds.Contains(e.ObjectId)).Select(e => e.ObjectId)];

        CensusPublishRejected = [.. rejected.Take(DroppedIdSample)];
        Speak(
            $"census publish: refusing to publish {rejected.Count} id(s) this engine cannot "
            + $"resolve ({string.Join(", ", CensusPublishRejected.Select(id => $"0x{id:X}"))}"
            + (rejected.Count > CensusPublishRejected.Count ? ", ..." : string.Empty)
            + $") - {kept.Count} published");

        return kept;
    }

    private string _publishLastLine = string.Empty;
    private long _publishSpokeAtMs;

    /// <summary>Says a publish-gate line when it changes, and on the same heartbeat as the
    /// scan gate otherwise. The census runs several times a second — an unthrottled line here
    /// would bury the log it is meant to make readable.</summary>
    private void Speak(string line)
    {
        long nowMs = Environment.TickCount64;

        if (line == _publishLastLine && nowMs - _publishSpokeAtMs < GateHeartbeatMs)
        {
            return;
        }

        _publishLastLine = line;
        _publishSpokeAtMs = nowMs;
        CensusDiagnostic?.Invoke(line);
    }

    /// <summary>Ids the last publish refused, for the <c>census</c> verb.</summary>
    public IReadOnlyList<uint> CensusPublishRejected { get; private set; } = [];

    /// <summary>The engine's own id → object answer, for callers that want to check an id
    /// this instance did not scan (the <c>census</c> verb asks it about a peer's ids).</summary>
    public bool TryResolveObjectHere(uint objectId, out nint address) =>
        EngineGlobals.TryResolveServerObject(_memory, objectId, out address);

    /// <summary>
    /// The creature scan BEFORE the liveness gate — what memory LOOKS like, as opposed to what
    /// the engine owns.
    ///
    /// Diagnostics only; publishing from this is the bug the gate exists for. It is here
    /// because the difference between the two lists is the whole question when an id is
    /// disputed: in the raw scan but not the array means a ghost the gate should have caught,
    /// while in neither means the id never came from this instance at all.
    /// </summary>
    public IReadOnlyList<CreatureRef> UnconfirmedCreatureScan()
    {
        EnumerateCreatures();
        return _creatureCache ?? [];
    }

    /// <summary>Drops the cached creature list — call when the world changes underneath it.</summary>
    public void InvalidateCreatureCache()
    {
        _creatureCache = null;
        _liveCache = null;
        _gateLastLine = string.Empty;
        _scanCache.Clear();
    }

    /// <summary>
    /// A vtable scan whose ADDRESSES are reused for a while.
    ///
    /// Doors, placeables and containers do not move while a module is loaded, but each of
    /// their state-collection passes was running its own full scan of the game's committed
    /// memory — and they all run on the same world-state pass, so the agent was doing a
    /// dozen full scans a second. Together with the item and creature scans that consumed a
    /// whole CPU core and starved the loop that replicates POSITION, which is what made
    /// everything appear to lag by seconds.
    ///
    /// Only the address list is cached; every field read through those addresses is live, so
    /// a door opening is still seen on the next pass.
    /// </summary>
    private List<nint> CachedScan(uint vtable, int limit, int ttlMs)
    {
        long nowMs = Environment.TickCount64;

        // A sweep that finished on the background thread is adopted here, on the caller's
        // thread, before anything else looks at the cache.
        if (ItemScanWorker.TryTakeHits(vtable, out List<nint> fresh))
        {
            _scanCache[vtable] = (fresh, nowMs);
            return fresh;
        }

        if (_scanCache.TryGetValue(vtable, out (List<nint> Hits, long AtMs) cached))
        {
            // ⚠ STALE IS SERVED, and a refresh is merely REQUESTED. This used to block for a
            // full ~180 MiB sweep the moment the TTL expired — measured at 554 ms inside one
            // heavy pass on the rig (2026-08-29, perf_trace), which is 554 ms during which no
            // remote player's position was written into the game. These are addresses of
            // objects that "do not move while a module is loaded" (this cache's own premise),
            // so handing back a list a few hundred milliseconds past its TTL while the
            // replacement is on its way is strictly inside the contract the cache already had.
            // A module change clears the cache outright, which is the case that actually
            // invalidates an address.
            if (nowMs - cached.AtMs >= ttlMs)
            {
                ItemScanWorker.Request(vtable, limit);
            }

            return cached.Hits;
        }

        // Nothing cached at all: block once. Every consumer of this list treats "empty" as
        // "there are none of these in the world", so serving an empty first answer would have
        // doors, placeables and encounters all reporting themselves absent for as long as the
        // first sweep took.
        PatternScanner scanner = new(_memory);
        List<nint> hits = scanner.ScanAll(
            BytePattern.FromBytes(BitConverter.GetBytes(vtable)), ScanScope.PrivateCommitted, limit);

        _scanCache[vtable] = (hits, nowMs);
        return hits;
    }

    /// <summary>How long a world-object address scan is reused. These are static objects in a
    /// loaded module, so this can be generous; a module change clears the cache.</summary>
    private const int ObjectScanCacheMs = 30_000;

    private readonly Dictionary<uint, (List<nint> Hits, long AtMs)> _scanCache = [];

    private List<CreatureRef> ScanCreatures()
    {
        BytePattern pattern = BytePattern.FromBytes(BitConverter.GetBytes(CreatureVtable));
        PatternScanner scanner = new(_memory);

        return ReadCreatures(scanner.ScanAll(pattern, ScanScope.PrivateCommitted, limit: 512));
    }

    /// <summary>
    /// Turns one sweep's raw hit addresses into creature records. Split out of
    /// <see cref="ScanCreatures"/> so the SWEEP can run on <see cref="ItemScanWorker"/>'s
    /// thread while this — the part that reads fields and builds objects — stays on the
    /// caller's, which is the same split the item cache makes for the same reason.
    /// </summary>
    private List<CreatureRef> ReadCreatures(List<nint> hits)
    {
        List<CreatureRef> creatures = [];

        foreach (nint address in hits)
        {
            if (!_memory.TryReadUInt32(address + OffObjectId, out uint objectId))
            {
                continue;
            }

            string tag = ReadExoString(address + OffTag);

            bool isPlayer = false;
            if (_memory.TryReadPointer(address + OffCreatureStats, out nint stats) && stats != 0
                && _memory.TryReadInt32(stats + StatsIsPc, out int isPc))
            {
                isPlayer = isPc != 0;
            }

            // Party members run follow-AI, which rewrites their position every frame, so
            // they are unusable as phantom bodies. The follow-info pointer identifies them.
            bool isParty = _memory.TryReadPointer(address + OffPartyFollowInfo, out nint follow) && follow != 0;

            creatures.Add(new CreatureRef(address, objectId, tag, isPlayer, isParty));
        }

        return creatures;
    }

    /// <summary>
    /// Locates the player and claims phantom bodies. Called at startup and again if reads
    /// start failing, since a module transition destroys and rebuilds every object.
    /// </summary>
    public bool TryResolve(out string diagnostic)
    {
        // Find the plugin's shared block FIRST, before any world state. The block is created
        // when the plugin loads and has nothing to do with a loaded module, so it must be
        // discoverable at the main menu — that is exactly where the Co-op menu button lives and
        // where the agent waits in --menu mode. Resolving it only after a player was found made
        // the menu button impossible to enable (the early returns below fire at the menu).
        TryFindPlugin();

        List<CreatureRef> creatures = EnumerateCreatures();

        if (creatures.Count == 0)
        {
            _player = null;
            _phantoms = [];
            diagnostic = "no creature objects found; is a module loaded?";
            return false;
        }

        // ⚠ Two threads resolve at once during a mid-session load: the pump re-resolves on its
        // first failed read of the torn-down world while SaveLoader's WaitFor(InWorld) polls
        // this from the command thread. Every field write below is an atomic reference swap,
        // but reading a FIELD back afterwards (for the diagnostic) can see the other call's
        // "no creatures yet" null — live 2026-09-02, the host's own in-world `loadgame 0`:
        // `pump tick failed: Object reference ... at KotorGameBridge.TryResolve`. Work on
        // locals and publish them; never read the fields back.
        CreatureRef? player = creatures.FirstOrDefault(c => c.IsPlayer);
        _player = player;

        if (player is null)
        {
            _phantoms = [];
            diagnostic = $"found {creatures.Count} creatures but none flagged is_pc";
            return false;
        }

        // Anything that is neither the player nor a party member is idle enough to drive.
        // Ordering by object id keeps the choice stable between resolves.
        //
        // ⚠ Membership through PartyCensusRule, not the follow pointer alone: claiming a
        // companion as a phantom body is the worst version of the census leak this rule exists
        // for — the body would be driven to a peer's position AND run follow-AI back toward the
        // player, every frame, in the same instance.
        HashSet<uint> partyRoster = ReadPartyRosterIds();

        List<CreatureRef> phantoms =
        [
            .. creatures
                .Where(c => !PartyCensusRule.IsOwnParty(c.ObjectId, c.IsPlayer, c.IsPartyMember, partyRoster))
                .OrderBy(c => c.ObjectId)
                .Take(_maxPhantoms),
        ];
        _phantoms = phantoms;

        _consecutiveFailures = 0;

        TryFindPlugin();
        TryResolveGlobals();

        nint pluginBlock = _pluginBlock;
        string mode = pluginBlock != 0
            ? $"PLUGIN mode: block at 0x{pluginBlock:X}, the plugin will spawn and walk real creatures"
            : $"external mode: claimed {phantoms.Count} existing creature(s) as phantom bodies: "
                + string.Join(", ", phantoms.Select(p => p.Tag.Length > 0 ? p.Tag : $"0x{p.ObjectId:X}"));

        diagnostic =
            $"player at 0x{player.Address:X} (id 0x{player.ObjectId:X}); "
            + $"{creatures.Count} creatures; {mode}";
        return true;
    }

    /// <summary>Locates the plugin's shared block by its 16-byte signature.</summary>
    private void TryFindPlugin()
    {
        _pluginBlock = 0;

        PatternScanner scanner = new(_memory);
        List<nint> hits = scanner.ScanAll(BytePattern.Parse(PluginSignature), ScanScope.PrivateCommitted, limit: 4);

        // TEMPORARY DIAGNOSTIC (live stall investigation) - settling whether the scan finds
        // the block at all versus finding it but failing the heartbeat check.
        ResolveDiagnostic?.Invoke($"TryFindPlugin: scan found {hits.Count} candidate(s): {string.Join(", ", hits.Select(h => $"0x{h:X}"))}");

        foreach (nint candidate in hits)
        {
            // A heartbeat that advances distinguishes a live plugin from a stale block left
            // behind in freed memory, which the signature alone cannot do.
            if (!_memory.TryReadUInt32(candidate + BlockHeartbeat, out uint first))
            {
                ResolveDiagnostic?.Invoke($"TryFindPlugin: 0x{candidate:X} first heartbeat read FAILED");
                continue;
            }

            Thread.Sleep(60);

            bool secondOk = _memory.TryReadUInt32(candidate + BlockHeartbeat, out uint second);
            ResolveDiagnostic?.Invoke(
                $"TryFindPlugin: 0x{candidate:X} heartbeat {first} -> {(secondOk ? second.ToString() : "READ-FAILED")} "
                + $"(second read ok={secondOk}, advanced={secondOk && second != first})");

            if (secondOk && second != first)
            {
                _pluginBlock = candidate;
                ResetItemSlots();
                BaselineTakeSerials();
                return;
            }
        }
    }

    /// <summary>
    /// Adopts the block's CURRENT take-serials as already-seen. The plugin outlives the
    /// agent, so a restarted agent otherwise finds serials above its zero baselines and
    /// replays the previous session's last console command and browser connect the moment
    /// it attaches (observed live: a fresh agent immediately reported "that server is no
    /// longer listed" from a click made minutes earlier).
    /// </summary>
    private void BaselineTakeSerials()
    {
        if (_memory.TryReadUInt32(_pluginBlock + BlockConsoleSerial, out uint console))
        {
            _lastConsoleSerial = console;
        }

        if (_memory.TryReadUInt32(_pluginBlock + BlockBrowserConnectSerial, out uint browser))
        {
            _lastBrowserConnectSerial = browser;
        }

        if (_memory.TryReadUInt32(_pluginBlock + BlockBrowserHostSerial, out uint browserHost))
        {
            _lastBrowserHostSerial = browserHost;
        }

        if (_memory.TryReadUInt32(_pluginBlock + BlockConnectSerial, out uint connect))
        {
            _lastConnectSerial = connect;
        }
    }

    /// <summary>
    /// Withdraws every ground-item slot on attach. The plugin outlives the agent - it is
    /// loaded by the game itself - so a restarted agent inherits whatever items the
    /// previous session had spawned. Without this they would stay on the floor forever,
    /// owned by a slot no agent is tracking any more.
    /// </summary>
    private void ResetItemSlots()
    {
        Array.Clear(_itemSlotIds);

        for (int i = 0; i < BlockItemCapacity; i++)
        {
            _memory.TryWrite(_pluginBlock + BlockItemSlots + (i * BlockItemSlotStride), 0u);
        }
    }

    /// <summary>
    /// Asks the plugin to keep this instance simulating while its window is unfocused.
    /// Only one of two co-op windows can hold focus, and the loser would otherwise stop
    /// dead — see <c>FocusKeeper</c>. Off unless a session actually needs it.
    /// </summary>
    public bool SetKeepAliveUnfocused(bool enabled) =>
        _pluginBlock != 0
        && _memory.TryWrite(_pluginBlock + BlockKeepAliveUnfocused, enabled ? 1u : 0u);

    /// <summary>
    /// Asks the plugin to have the local player pick up the ground item with this id,
    /// through the engine's own pick-up action. Test hook for the item sync; returns false
    /// if the item is not one this instance currently has on the floor.
    /// </summary>
    public bool TryRequestPickUp(uint itemId)
    {
        int slot = FindItemSlot(itemId);

        return _pluginBlock != 0
            && slot >= 0
            && _memory.TryWrite(_pluginBlock + BlockPickUpRequest, (uint)(slot + 1));
    }

    /// <summary>How many focus-loss pauses the plugin has cleared. 0 if no plugin.</summary>
    public uint PausesCleared =>
        _pluginBlock != 0 && _memory.TryReadUInt32(_pluginBlock + BlockPausesCleared, out uint n) ? n : 0;
}
