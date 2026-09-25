// Local-player state: reading it for the wire (TryReadLocalState), publishing remote peers
// as phantoms (TryWriteRemotePeers), session teardown, and the client control lock that keeps
// a client's input pinned to its own PC instead of a shared companion.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    /// <summary>
    /// ⚠ SERIALIZED (2026-09-25, docs/research/disconnect-reconnect.md's bridge-concurrency
    /// section): this bridge is shared by the pump, the command dispatcher's own 150ms poll
    /// loop (<c>MaintainSessionSave</c>), the join flow and the server-discovery beacon, none
    /// of which coordinate with each other. A module transition makes THIS method (via the
    /// <c>moduleChanged || worldReloaded</c> branch below) clear <c>_creatureCache</c>/
    /// <c>_liveCache</c>/<c>_scanCache</c> (<see cref="InvalidateCreatureCache"/>) and
    /// <c>_itemTags</c>/<c>_itemAddresses</c> — plain Dictionary/List fields, not concurrent
    /// collections — while <see cref="TryResolve"/>'s own scan can be writing the SAME fields
    /// from another thread. Measured live: two threads both logged "adopted the engine's own
    /// creature" 1 ms apart right after a transition, then every later pump tick threw
    /// <c>InvalidOperationException</c> ("A concurrent update was performed on this
    /// collection and corrupted its state") forever, because a corrupted Dictionary/List stays
    /// corrupted. <see cref="KotorGameBridge.Sync"/> is taken here (and in
    /// <see cref="TryResolve"/>, <see cref="EnumerateCreatures"/>, <see cref="ClearCoopState"/>,
    /// <see cref="ReadEnemies"/>, <see cref="ReadPartyPortraits"/>,
    /// <see cref="ForgetWorldShadows"/> and the world/chargen-load pollers below) so every
    /// caller is automatically serialized without having to remember to lock at its own call
    /// site. <c>Lock</c> (Monitor semantics), not <c>SemaphoreSlim</c>: this method calls
    /// <see cref="TryResolve"/> below, which re-enters the same lock on the same thread.
    /// </summary>
    public bool TryReadLocalState(out PlayerState state)
    {
        lock (Sync)
        {
            return TryReadLocalStateCore(out state);
        }
    }

    private bool TryReadLocalStateCore(out PlayerState state)
    {
        state = PlayerState.Empty;

        // Re-resolve when the cached creature stops looking like the player. A module
        // transition frees every creature and the new module allocates its own, so a pointer
        // cached across a travel lands in reused memory: measured live after following a host
        // through a door, both agents reported pos (0,0,0) and hit points of 57/134 and
        // 21544/21544 — plausible-looking garbage, which is the worst kind, because the
        // phantom positions derived from it look like a network fault rather than a stale
        // pointer. Two small reads per tick buy immunity to that.
        // Prefer the engine's own answer to the scan. The scan can latch a FREED COPY of
        // the player — the copy keeps its vtable, stats pointer and is_pc flag, so every
        // cheap validation passes while its position and health stay frozen at whatever
        // the copy died with. Measured on tar_m03aa: this agent published the PC standing
        // at its spawn point for minutes while the real character crossed the map, so the
        // phantom never moved and every ambush the real player stood in was declined
        // against a body somewhere else. The plugin republishes the engine's
        // GetPlayerCreature() every frame (0 while no world is up); adopt it whenever it
        // names a different creature than the one the scan chose.
        // ⚠ Work on a snapshot of the field: SaveLoader's WaitFor(InWorld) calls TryResolve on
        // its own thread during a mid-session load and can null `_player` between any check
        // here and the use at the bottom (live 2026-09-02, client rejoin: NRE at
        // `_player!.Address`).
        CreatureRef? player = _player;
        nint pluginBlock = _pluginBlock;
        if (pluginBlock != 0
            && _memory.TryReadUInt32(pluginBlock + BlockEnginePlayerPointer, out uint enginePlayer)
            && enginePlayer != 0
            && (player is null || player.Address != (nint)enginePlayer)
            && _memory.TryReadUInt32(pluginBlock + BlockEnginePlayerId, out uint engineId)
            && StillLooksLikeThePlayer((nint)enginePlayer))
        {
            ResolveDiagnostic?.Invoke(
                $"player: adopted the engine's own creature 0x{enginePlayer:X} (id 0x{engineId:X})"
                + (player is null ? string.Empty : $" over the scan's 0x{player.Address:X}"));
            player = new CreatureRef((nint)enginePlayer, engineId, string.Empty, true, false);
            _player = player;
        }

        if (player is not null && !StillLooksLikeThePlayer(player.Address))
        {
            if (_playerLostLogged < 3)
            {
                _playerLostLogged++;
                ResolveDiagnostic?.Invoke(DescribePlayerMismatch(player.Address));
            }

            player = null;
            _player = null;
        }

        if (player is null)
        {
            // ⚠ RATE-LIMITED, and it has to be. Re-resolving means a full pattern scan of the
            // game's committed memory — hundreds of milliseconds. Running that on every failed
            // validation pinned a CPU core and dropped the pump from 60 Hz to under 3, which
            // measured end to end as a peer taking 3-7 SECONDS to move. The validation above
            // is cheap and correct; what was not affordable was acting on it immediately.
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastResolveAttemptMs < ResolveRetryMs)
            {
                return false;
            }

            _lastResolveAttemptMs = nowMs;

            if (!TryResolve(out _))
            {
                return false;
            }

            player = _player;
            if (player is null)
            {
                return false;
            }
        }

        // A lost plugin block does not retry itself once the player resolves independently of
        // it (the scan-based path above needs no block at all). Measured live: a module
        // transition's main-thread stall (a movie can hold it for seconds) is long enough for
        // TryFindPlugin's heartbeat check to read the SAME value twice and conclude the block
        // is stale, right as TryResolve is mid-call from the branch above. The player then
        // keeps resolving fine on every later tick — the scan path does not need the block at
        // all — so `_player is null` never goes true again and nothing ever calls
        // TryFindPlugin a second time. That silently downgrades PLUGIN mode to external mode
        // for the rest of the session: CanWriteRemotes falls back to phantom count alone, and
        // every plugin-block-gated write (enemies, phantom damage, materialised loot, party)
        // stops with no failed read and no thrown exception to explain why. Same rate limit as
        // the player retry above and the same reason (a full pattern scan is expensive), but a
        // separate timestamp so a lost block does not have to wait on player-retry cadence, and
        // a lost player does not have to wait on this one.
        if (_pluginBlock == 0)
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastPluginRetryMs >= ResolveRetryMs)
            {
                _lastPluginRetryMs = nowMs;
                TryFindPlugin();
            }
        }

        nint creature = player.Address;

        if (!TryReadVector(creature + OffPosition, out Vector3f position))
        {
            OnFailure();
            return false;
        }

        // orientation is a 3-float Vector. Treating (x, y) as a facing direction matches
        // how the GIT stores creature orientation; the sign convention is UNVERIFIED.
        float facing = 0f;
        if (TryReadVector(creature + OffOrientation, out Vector3f orientation))
        {
            facing = Angles.Normalize(MathF.Atan2(orientation.Y, orientation.X));
        }

        // SIGNED on purpose: a downed player's hit points go negative (the PC death
        // threshold is -10), and an unsigned read would turn -3 into 65533 — reading as
        // "alive and thriving" to every peer at the exact moment it matters most.
        int health = _memory.TryRead(creature + OffCurrentHitPoints, out short currentHp) ? currentHp : 0;
        int maxHealth = TryReadMaxHitPoints(creature);
        uint animation = ReadPlayedAnimation(creature);

        // The appearance.2da row travels with the avatar so the peer's phantom can wear
        // this player's actual look instead of the commoner blueprint.
        uint appearance = 0;
        if (_memory.TryRead(creature + OffAppearanceType, out ushort appearanceRow))
        {
            appearance = appearanceRow;
        }

        // The real module name ("end_m01aa"), not the area id. Area ids come from a
        // counter that resets on every module transition, so two different modules
        // routinely share one — which would make the module filter match items and peers
        // across a boundary they are not actually on the same side of. The name read
        // also fails during a transition, which usefully blanks the module until the new
        // world is stable. Falls back to the area id only if the name is unreadable.
        if (!EngineGlobals.TryReadModuleName(_memory, out string module)
            && _memory.TryReadUInt32(creature + OffAreaId, out uint areaId))
        {
            module = $"area{areaId:X}";
        }

        // ⚠ A TRANSITIONAL MODULE NAME MEANS THIS IS A GHOST, NOT A FAILURE TO CATCH LATER.
        // Live-measured 2026-08-27 (docs/research/disconnect-reconnect.md §10): quitting to the
        // main menu correctly invalidates the cached player pointer, but the very next tick's
        // scan-based TryResolve reacquires a transitional-looking placeholder (module 'area1',
        // plausible hit points) that passes every cheap check StillLooksLikeThePlayer applies.
        // Before this check, that "succeeded" against the ghost, so AwayRule's consecutive-
        // failure counter reset to 0 every tick and the Away flag never latched no matter how
        // long the player sat at the main menu. Treat it exactly like any other failed read:
        // publish nothing new this tick and let the next scan try again — the SAME "areaXX"
        // fallback also fires for a fraction of a second during an ordinary module transition
        // (IsTransitionalModuleName is shared with DisableLocalEncounters for exactly that
        // reason), which is fine: a real transition settles in well under AwayRule's ~1s
        // threshold, so this alone must never trip Away on its own.
        if (IsTransitionalModuleName(module))
        {
            OnFailure();
            return false;
        }

        // A module change rebuilds every object in the game, so every cached ADDRESS is now
        // meaningless. The caches exist because scanning for them is what starved this pump;
        // dropping them exactly here is what keeps them honest.
        //
        // ⚠ SO DOES A RELOAD OF THE SAME MODULE, and the module name cannot see one. Live
        // 2026-09-02 (death-handling.md §6.4): after a game-over reload of END_M01AA into
        // END_M01AA every object id was minted afresh, the (id -> resref) caches below kept
        // the OLD world's answers, and the inventory read through them named a DOOR
        // ("script item grant: ... end_door05"), granted the whole reloaded inventory to the
        // peer as new, and fed a party-pool add/reconcile loop that inflated the pool by one
        // stack every ten seconds. The plugin's WorldLoadSerial counts exactly those
        // teardowns (a load, not a door), so a changed serial is the same event as a changed
        // name. 0 without a plugin: never changes, never fires.
        uint worldSerial = WorldLoadSerial;
        bool moduleChanged = module.Length > 0
            && !string.Equals(module, _cachedForModule, StringComparison.OrdinalIgnoreCase);
        bool worldReloaded = worldSerial != _cachedForWorldSerial;
        // A fresh engine player can be adopted without running the full attach resolver.
        // Rebind the globals table too: a save load may have replaced its owning server.
        // TryResolveGlobals retains same-catalogue shadows so restored quest deltas survive.
        if (moduleChanged || worldReloaded || !_globals.IsResolved)
        {
            TryResolveGlobals();
        }

        if (moduleChanged || worldReloaded)
        {
            _cachedForModule = module;
            _cachedForWorldSerial = worldSerial;
            WorldEpoch++;
            _saveDigests?.ObserveLoadedWorld(WorldEpoch);
            InvalidateCreatureCache();
            _itemTags.Clear();
            _itemAddresses.Clear();
            _itemTagsBuiltAtMs = 0;
            _itemSweepProvenance.Reset(Environment.TickCount64);

            // Engine object ids are only meaningful within the module that minted them; a
            // stale (id -> resref) fact surviving a module change would be actively wrong
            // rather than merely uninformative — see the cache's own doc comment.
            ClearSpawnedItemResrefCache();

            // ⚠ NOT the creation facts the NEW world already produced. This reset runs when
            // the new world's player first resolves — seconds after its ambient scripts ran
            // and the spawn-ring drain recorded what they created. A plain clear here threw
            // those facts away, and the party-pool sweep that followed counted an ambient
            // deposit as the player's own (live 2026-09-02, shared-inventory-v2.md §17). The
            // drain tags each fact with the serial of the world that minted the id; keep
            // exactly those tagged with the world being entered. Without a plugin there are
            // no facts to keep and the serial never moves, so the plain clear stands.
            if (worldReloaded)
            {
                ForgetItemCreationsFromOtherWorlds(worldSerial);
            }
            else
            {
                ClearItemCreationObservationCache();
            }

            ClearPartyPoolSuppressionCache();
        }

        // Visible equipment travels as blueprint resrefs. An item's tag matches its
        // blueprint for stock gear; custom/mod items whose tag diverges simply fail to
        // materialise on the far side, which is the correct soft failure when the two
        // installs do not share a mod.
        string body = ReadEquippedResref(creature, InvSlotBody);
        string rightWeapon = ReadEquippedResref(creature, InvSlotRightWeapon);
        string leftWeapon = ReadEquippedResref(creature, InvSlotLeftWeapon);

        RefreshOneShotAnimation();

        state = new PlayerState(
            position, facing, module, health, animation, appearance, maxHealth,
            body, rightWeapon, leftWeapon, ReadLocalFlags(creature),
            _oneShotAnimation, _oneShotSequence);

        if (!state.IsPlausible)
        {
            OnFailure();
            state = PlayerState.Empty;
            return false;
        }

        _consecutiveFailures = 0;
        return true;
    }

    /// <summary>CSWSCreature + this = in-combat dword.</summary>
    private const int OffInCombat = 0x4E0;

    /// <summary>Latches "this player paused the game themselves" across the window where
    /// a peer's mirrored pause shares the same engine bit.</summary>
    private bool _ownPauseLatched;

    /// <summary>How long a pause release is announced on the wire. Long enough that a lost
    /// datagram cannot swallow it, short enough that it cannot end a LATER pause.</summary>
    private static readonly TimeSpan PauseReleaseAnnounceWindow = TimeSpan.FromMilliseconds(750);

    private uint _lastPauseReleaseSerial;
    private DateTime _pauseReleaseAnnouncedUntil = DateTime.MinValue;

    /// <summary>
    /// The presence bits that travel with the avatar: talking, paused, fighting.
    ///
    /// The pause bit is the player's OWN pause only. When the plugin is holding the pause
    /// bit on a remote peer's behalf, that mirrored pause must not be republished as
    /// ours — two instances would each mirror the other and latch paused forever. The
    /// engine has one bit for both, so "own" is decided when the bit RISES: set while no
    /// mirror was applied means the player did it, and that verdict is latched until the
    /// bit clears.
    /// </summary>
    /// <summary>
    /// The animation to replicate: the client twin's current played animation when the twin
    /// exists (captures weapon draws / swings / flourishes), else the server field (+0xD4,
    /// gait and scripted animations). A zero twin animation falls back too, since the twin
    /// reads 0 briefly around a spawn or transition.
    /// </summary>
    private uint ReadPlayedAnimation(nint creature)
    {
        // ⚠ SIXTEEN bits. The twin's current animation is a ushort (GetCurrentAnimation
        // 0x0063C060 is `mov ax,[ecx+0x48]`) and +0x4A is a SEPARATE ushort, the queued
        // animation that QueuedCallback swaps in. Reading 32 bits here returned
        // queued<<16 | played whenever anything was queued — a value that is not an
        // animation id at all, published as the peer's gait and mirrored onto the phantom.
        if (_memory.TryReadPointer(creature + OffClientTwin, out nint twin) && twin != 0
            && _memory.TryReadUInt16(twin + OffTwinAnimation, out ushort twinAnim) && twinAnim != 0)
        {
            return twinAnim;
        }

        _memory.TryReadUInt32(creature + OffAnimation, out uint anim);
        return anim;
    }

    private uint ReadLocalFlags(nint creature)
    {
        uint flags = 0;

        if (IsInDialogue)
        {
            flags |= PlayerState.FlagInDialogue;
        }

        bool paused = EngineGlobals.TryReadPauseMask(_memory, out byte pauseMask)
            && (pauseMask & EngineGlobals.PauseReasonPlayer) != 0;

        if (!paused)
        {
            _ownPauseLatched = false;
        }
        else if (!IsRemotePauseApplied)
        {
            _ownPauseLatched = true;
        }

        if (_ownPauseLatched)
        {
            flags |= PlayerState.FlagPaused;
        }

        if (_memory.TryReadInt32(creature + OffInCombat, out int inCombat) && inCombat != 0)
        {
            flags |= PlayerState.FlagInCombat;
        }

        // The engine's own stealth-mode byte, a level like in-combat: peers hold this
        // player's stand-in out of their enemies' target selection while it is set
        // (PlayerState.FlagStealth, stealth_sync). Protocol 53.
        if (_memory.TryRead(creature + OffStealthMode, out byte stealthMode) && stealthMode != 0)
        {
            flags |= PlayerState.FlagStealth;
        }

        if (IsInMinigame)
        {
            flags |= PlayerState.FlagInMinigame;
        }

        // Downed comes from the plugin, which asks the engine directly (GetDeadTemp): a
        // party member at zero hit points is temp-dead, not dead, and hit points alone
        // cannot distinguish "downed" from "the read failed".
        bool downed = IsLocalPlayerDowned;
        if (downed)
        {
            flags |= PlayerState.FlagDowned;
        }

        // Stronger than downed: the engine asked for game-over, so the whole local party is
        // down (a host's standing companion keeps this off). See PlayerState.FlagPartyWiped.
        if (TrackPartyWipe(downed))
        {
            flags |= PlayerState.FlagPartyWiped;
        }

        // Plugin-published (SoloSegmentController): this player's own local roster just got
        // stripped by story script in a catalogued module (the Bastila/Malak duels — see
        // docs/research/star-forge-finale.md). A LEVEL, republished every tick like FlagDowned.
        if (IsLocalPlayerInSoloSegment)
        {
            flags |= PlayerState.FlagSoloSegment;
        }

        // A release of a MIRRORED pause, announced for a short window so a lost datagram
        // does not lose it. The plugin counts them; the transition to a new count starts the
        // window. See PlayerState.FlagPauseReleased.
        uint releaseSerial = LocalPauseReleaseSerial;
        if (releaseSerial != _lastPauseReleaseSerial)
        {
            _lastPauseReleaseSerial = releaseSerial;

            // Zero is "never released", not a release: a fresh plugin publishes it.
            _pauseReleaseAnnouncedUntil = releaseSerial == 0
                ? DateTime.MinValue
                : DateTime.UtcNow + PauseReleaseAnnounceWindow;
        }

        if (DateTime.UtcNow < _pauseReleaseAnnouncedUntil)
        {
            flags |= PlayerState.FlagPauseReleased;
        }

        return flags;
    }

    /// <summary>Plugin-published: the local PC is in the engine's temp-dead (downed) state.</summary>
    public bool IsLocalPlayerDowned =>
        _pluginBlock != 0
        && _memory.TryReadUInt32(_pluginBlock + BlockLocalDowned, out uint downed)
        && downed != 0;

    private bool IsRemotePauseApplied =>
        _pluginBlock != 0
        && _memory.TryReadUInt32(_pluginBlock + BlockRemotePauseApplied, out uint applied)
        && applied != 0;

    /// <summary>
    /// EVERY agent-written LEVEL that belongs to a session rather than to the process, in the
    /// order <see cref="ClearCoopState"/> lowers them. This list is the teardown: a field that
    /// is written while connected and is not named here stays at its last session value for
    /// the rest of the process, because with the agent gone nothing else ever lowers it.
    ///
    /// ⚠ THIS EXISTS BECAUSE THE PER-FIELD VERSION KEPT MISSING ONE. The reported symptom was
    /// "after disconnecting, Trask comes back but clicking him does nothing": the client's
    /// swap suppression (0x1030) was still 1, so the plugin's byte patch kept turning
    /// CClientExoAppInternal::ChangeCharacter — the single funnel for Tab, the portraits and
    /// the death auto-swap — into an immediate return, for the rest of the process. Three more
    /// fields were leaking the same way beside it (the game-over suppression, the peer
    /// interaction locks, and the host's party roster, whose own comment already claimed a
    /// disconnected agent would clear it). One list, cleared in one place, is the fix for the
    /// CLASS rather than for the field.
    ///
    /// Only LEVELS belong here. Serials (travel, journal, revive, chat) are edge-compared
    /// against an applied counter, and zeroing one would re-fire the last event rather than
    /// cancel it.
    ///
    /// <c>BlockCommand</c> is deliberately LAST: the plugin reads it as "a session is driving
    /// this instance", every mirroring controller gates on it, and its falling edge is what
    /// runs the plugin's own release (PluginHost.ReleaseSession). Lowering it first would let
    /// that release run against a block still full of the previous session's levels.
    /// </summary>
    internal static readonly (string Name, int Offset)[] SessionScopedBlockLevels =
    [
        ("local session role", BlockLocalSessionRole),
        ("remote peers", BlockRemoteCount),
        ("enemy census", BlockEnemyCount),
        ("enemy presence", BlockEnemyPresenceCount),
        ("companion presence", BlockPartyPresenceCount),

        // Level-written every tick by the agent, so once the agent is gone nothing else would
        // ever lower it — a spectator mid-follow when the session died would stay locked out
        // of its own replies forever.
        ("remote dialogue", BlockRemoteDialogueActive),

        // A leftover request would keep offering an encounter on behalf of a peer that is no
        // longer in the session.
        ("encounter requests", BlockEncounterRequestCount),
        ("encounter watch", BlockEncounterWatch),

        // Same leak class: a leftover trigger request would keep asking the engine to fire a
        // generic trigger on behalf of a peer no longer in the session, and a stuck watch flag
        // would keep publishing polygon membership nobody is collecting.
        ("trigger requests", BlockTriggerRequestCount),
        ("trigger watch", BlockTriggerWatch),

        // ⚠ THE UNCLICKABLE-COMPANION BUG. Both of these drive byte patches in the plugin that
        // stay armed as long as the word reads non-zero: the swap guard makes Tab and every
        // party portrait inert, the game-over guard swallows the death screen.
        ("game-over suppression", BlockSuppressGameOver),
        ("control-swap suppression", BlockSuppressSwap),

        // The plugin holds every locked creature still (clearing its action queue several
        // times a second) for as long as the count says a peer owns it.
        ("peer interaction locks", BlockInteractionLockCount),

        // The roster the plugin reconciles this instance's party AGAINST. Left valid, a
        // disconnected client keeps adding and removing party members to match a party the
        // host had when the link died.
        ("peer party roster", BlockHostPartyRoster),

        ("damage owed", BlockDamageOwedCount),
        ("incoming damage", BlockIncomingDamage),

        // "This instance mirrors the world" — what the portrait hiding and the companion
        // faction hold both key on.
        ("mirroring", BlockDamageDealtEnabled),

        ("session combat", BlockSessionInCombat),
        ("session peers", BlockSessionPeers),
        ("mirrored pause", BlockRemotePauseDesired),
        ("door census", BlockDoorCount),
        ("placeable census", BlockPlaceableCount),
        ("ground loot", BlockLootCount),
        ("loot mailbox module", BlockLootMailboxModuleHash),
        ("loot mailbox ack", BlockLootMailboxAckSerial),
        ("loot mailbox page", BlockLootMailboxPageSerial),
        ("loot mailbox count", BlockLootMailboxCount),
        ("loot mailbox consumed", BlockLootMailboxConsumedCount),
        ("loot mailbox epoch", BlockLootMailboxEpoch),
        ("loot mailbox ack epoch", BlockLootMailboxAckEpoch),

        // A leftover override would keep telling the plugin the host still wants
        // effect_sync/trigger_relay/liveness_sync/map_reveal_sync/dialogue_recruit_sync
        // overridden on behalf of a session that has ended — see
        // docs/research/mod-compatibility.md's "Behaviour-flag negotiation" gap and
        // SetBehaviourOverrides. Values before mask, same reasoning as the write itself: a
        // reader must never see a stale value dword paired with a freshly-zeroed mask.
        ("behaviour override values", BlockBehaviourOverrideValues),
        ("behaviour override mask", BlockBehaviourOverrideMask),

        // A leftover resolved scaling would keep the plugin topping up enemy hit points and
        // rewriting enemy damage for a session that has ended; 0/0 is "nothing to apply".
        ("scaling economy", BlockScalingEconomy),
        ("scaling prices", BlockScalingPrices),
        ("scaling combat", BlockScalingCombat),

        // LAST, always. See the remarks.
        ("command", BlockCommand),
    ];

    /// <summary>
    /// Clears the co-op state the plugin is driving so a disconnect leaves a clean game: no
    /// remote peers (the plugin despawns the phantoms it spawned for them), no suppressions,
    /// no peer claims, the party freeze released and the spawn command idle. Best-effort — a
    /// disconnect must never throw.
    ///
    /// The set is <see cref="SessionScopedBlockLevels"/>; see its remarks for why teardown is
    /// a list rather than a run of hand-written writes.
    ///
    /// ⚠ SERIALIZED — see <see cref="TryReadLocalState"/>'s remarks. Called from
    /// <c>SessionManager.TearDownSession</c> while that manager's OWN lock (its private
    /// <c>_gate</c>) is held, so the lock taken here must never be the OUTER lock anywhere
    /// else in this codebase (i.e. nothing may hold <see cref="Sync"/> and then call into
    /// <c>SessionManager</c>) — that would invert the two locks' acquisition order across
    /// threads and deadlock. <see cref="Sync"/> stays the innermost lock for exactly that
    /// reason: this class never calls back into the session layer.
    /// </summary>
    public void ClearCoopState()
    {
        lock (Sync)
        {
            if (_pluginBlock != 0)
            {
                foreach ((_, int offset) in SessionScopedBlockLevels)
                {
                    _memory.TryWrite(_pluginBlock + offset, 0u);
                }

                // Every agent-side LATCH whose only job is "I have already raised that level".
                // Lowering a level without lowering its latch is worse than not lowering it at
                // all: the next session never raises it again.
                _encounterWatchWritten = false;
                _damageDealtEnabled = false;
            }

            // A new session deals fresh peer ids, so keeping the old slot map would hold slots
            // for players who no longer exist and push the new ones down the block.
            _slots.Clear();

            // Round 103 (docs/research/party-reconciliation.md): SessionManager keeps this SAME
            // bridge instance alive across a disconnect and the reconnect that follows it, so this
            // agent-side write cache is exactly the kind of latch the class comment above warns
            // about — outside the _pluginBlock guard because the cache must not survive into the
            // next session even if the plugin momentarily reads unattached right now (a transient
            // that must not leave stale entries for whenever it re-attaches). Without this, a
            // reconnect whose first PartySnapshot reads byte-identical to whatever was mirrored
            // right before the disconnect finds the cache still agreeing with it and skips
            // rewriting the very PartyPresenceCount level the loop above just zeroed — the
            // companion mirror never re-arms. See PartyPresenceWriteCache's own doc comment.
            _partyPresenceCache.Reset();

            ReleasePartyFreeze();
        }
    }

    /// <summary>
    /// Recovery primitive for <c>CoopAgent</c>'s pump-failure loop — see
    /// <see cref="IPlayerBridge.ResetCaches"/>'s remarks. Reuses the exact reset primitives
    /// <see cref="TryReadLocalStateCore"/> already runs on every module change
    /// (<see cref="InvalidateCreatureCache"/>, <see cref="ForgetWorldShadows"/>, the item tag/
    /// address cache, the spawned-item and item-creation observation caches, the party-pool
    /// suppression cache) plus the two small standalone caches those do not cover (module
    /// display names, portrait resrefs). Deliberately does NOT touch <c>_player</c>/
    /// <c>_phantoms</c> (the next <see cref="TryResolve"/> re-derives them from a live scan,
    /// same as any other resolve) or the plugin block / session state
    /// <see cref="ClearCoopState"/> owns.
    /// </summary>
    public void ResetCaches()
    {
        lock (Sync)
        {
            InvalidateCreatureCache();
            ForgetWorldShadows();
            _itemTags.Clear();
            _itemAddresses.Clear();
            _itemTagsBuiltAtMs = 0;
            ClearSpawnedItemResrefCache();
            ClearItemCreationObservationCache();
            ClearPartyPoolSuppressionCache();
            _moduleNames.Clear();
            _portraitResRefs.Clear();
        }
    }

    public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers)
    {
        // Preferred path: hand the peers to the in-process plugin, which spawns a real
        // creature per remote player and walks it with the engine's own movement step. That
        // gives ground snapping, walkmesh awareness and a walk animation, none of which an
        // external position poke can produce.
        if (_pluginBlock != 0)
        {
            return WriteToPlugin(peers);
        }

        if (_phantoms.Count == 0)
        {
            return false;
        }

        bool allOk = true;
        int count = Math.Min(peers.Count, _phantoms.Count);

        for (int i = 0; i < count; i++)
        {
            CreatureRef body = _phantoms[i];
            Vector3f target = peers[i].State.Position;

            if (!target.IsFinite)
            {
                continue;
            }

            // Writing the server-side position works because nothing else is driving this
            // creature. Measured: a write to an idle creature survives (0 engine overwrites
            // in 5s), while the same write to a party member running follow-AI is reverted
            // ~64 times a second. See docs/re/object-model.md.
            allOk &= _memory.TryWrite(body.Address + OffPosition, target.X);
            allOk &= _memory.TryWrite(body.Address + OffPosition + 4, target.Y);
            allOk &= _memory.TryWrite(body.Address + OffPosition + 8, target.Z);
        }

        return allOk;
    }
    /// <summary>Which peer owns which block slot, kept stable for the session. See
    /// <see cref="PeerSlotMap"/> for why that matters.</summary>
    private readonly PeerSlotMap _slots = new(BlockSlotCapacity);

    /// <summary>Scratch for one publication: slot -&gt; index into the peer list, -1 = empty.</summary>
    private readonly int[] _slotPeerIndex = new int[BlockSlotCapacity];

    /// <summary>Scratch for one publication: the peer ids present, for eviction decisions.</summary>
    private readonly List<uint> _presentPeerIds = [];

    /// <inheritdoc />
    public int RemoteSlotCapacity => BlockSlotCapacity;

    /// <inheritdoc />
    public uint PeerInSlot(int slot) => _slots.Owner(slot);

    // ---- client control lock ----------------------------------------------------
    //
    // The character the local player's input drives is a single object-id field at
    // CClientExoAppInternal + 0x20. VERIFIED live by toggling control (Tab) and watching it flip:
    //   0xFFFFFFFF                 = the PC / party leader is controlled
    //   0x80000000 | <objectId>    = a party NPC (companion) is controlled
    // The engine re-derives the cached controlled-creature pointer (+0x3C0) from this id each
    // frame, so writing 0xFFFFFFFF back reverts companion control to the PC within a tick (proven:
    // the write stuck and +0x3C0 returned to the PC). It is an id write resolved through a
    // null-safe map, so it can never crash. See docs/research/client-control-lock.md.
    //
    // Solo mode already stops companions FOLLOWING but not being SELECTED, so without this a
    // client player could Tab/click to a shared, host-owned companion and fight the host's
    // authority. We only revert an explicit party-NPC selection (high bit set, not all-ones), so
    // a PC-controlled state (0xFFFFFFFF) or any non-party value is left untouched.

    /// <summary>CClientExoAppInternal + this = the controlled-character id (PC = 0xFFFFFFFF).</summary>
    private const int ControlledCharacterId = 0x20;

    /// <summary>
    /// Pins the player's input control to their own PC, reverting any switch to a shared,
    /// host-owned companion. Object-id write only (crash-proof); a no-op while the PC (or any
    /// non-companion) is controlled. Call on clients each party pass.
    /// </summary>
    public bool LockControlToPlayer()
    {
        if (!EngineGlobals.TryGetClientInternal(_memory, out nint clientInternal)
            || !_memory.TryReadUInt32(clientInternal + ControlledCharacterId, out uint controlled))
        {
            return false;
        }

        // The PC's id can change on a reload (live: 0x7FFFFFFE). All-ones is only the
        // client-space encoding of 0x7FFFFFFF, not a universal "my player" sentinel.
        // Never redirect input until the current creature is independently verified.
        CreatureRef? player = _player;
        if (player is null || !StillLooksLikeThePlayer(player.Address)
            || !_memory.TryReadUInt32(player.Address + 4, out uint playerId))
        {
            return false;
        }

        if (PlayerControlIdentity.TryGetCorrection(controlled, playerId, out uint corrected))
        {
            return _memory.TryWrite(clientInternal + ControlledCharacterId, corrected);
        }

        return true;
    }

    /// <summary>
    /// Cheap validity check for a cached player pointer: the object still carries the
    /// creature vtable, and its stats block still says it is a PC. Both are what
    /// <see cref="TryResolve"/> selected on, so anything that fails here is no longer the
    /// object we resolved.
    /// </summary>
    /// <summary>At most one full re-scan per second, no matter how often validation fails.</summary>
    private const int ResolveRetryMs = 1000;

    private long _lastResolveAttemptMs;

    /// <summary>Separate from <see cref="_lastResolveAttemptMs"/> on purpose — a lost plugin
    /// block and a lost player are independent failures with independent retry cadences; see
    /// <see cref="TryReadLocalState"/>'s post-resolve check.</summary>
    private long _lastPluginRetryMs;
    private int _playerLostLogged;

    /// <summary>Optional sink for "why did the cached player stop validating" — the answer
    /// decides whether the validation is too strict or the pointer really did go stale.</summary>
    public Action<string>? ResolveDiagnostic { get; set; }

    /// <summary>Names the specific check that failed, so this is diagnosable from a log
    /// rather than by rebuilding with printfs.</summary>
    private string DescribePlayerMismatch(nint creature)
    {
        if (!_memory.TryReadUInt32(creature, out uint vtable))
        {
            return $"player 0x{creature:X}: vtable unreadable";
        }

        if (vtable != CreatureVtable)
        {
            return $"player 0x{creature:X}: vtable 0x{vtable:X} != 0x{CreatureVtable:X}";
        }

        if (!_memory.TryReadPointer(creature + OffCreatureStats, out nint stats) || stats == 0)
        {
            return $"player 0x{creature:X}: stats pointer missing";
        }

        return !_memory.TryReadInt32(stats + StatsIsPc, out int isPc)
            ? $"player 0x{creature:X}: is-PC flag unreadable"
            : $"player 0x{creature:X}: is-PC flag is {isPc}";
    }

    private bool StillLooksLikeThePlayer(nint creature) =>
        _memory.TryReadUInt32(creature, out uint vtable)
        && vtable == CreatureVtable
        && _memory.TryReadPointer(creature + OffCreatureStats, out nint stats)
        && stats != 0
        && _memory.TryReadInt32(stats + StatsIsPc, out int isPc)
        && isPc != 0;

    private bool TryReadFaction(nint creature, out int faction)
    {
        faction = -1;

        return _memory.TryReadPointer(creature + OffCreatureStats, out nint stats)
            && stats != 0
            && _memory.TryReadInt32(stats + StatsFaction, out faction);
    }

    /// <summary><c>CSWSCreatureStats::class_count</c> — byte, 0..2. Same offset the PLUGIN's
    /// <c>Odyssey.ClassGrant.cs</c> already reads natively; duplicated here because the x86
    /// NativeAOT plugin and this x64 agent cannot share an assembly (the same reason
    /// item-identity.md §14's stack-identity mask is duplicated).</summary>
    private const int StatsClassCount = 0x089;

    /// <summary><c>CSWSCreatureStats::classes</c> — <c>CSWSCreatureStats_ClassInfo[2]</c> at
    /// +0x08C, stride 0x28.</summary>
    private const int StatsClasses = 0x08C;

    /// <summary><c>CSWSCreatureStats_ClassInfo::class_id</c> — byte at +0x1B.</summary>
    private const int ClassInfoClassId = 0x1B;

    private const int ClassInfoStride = 0x28;

    /// <inheritdoc cref="IPlayerBridge.ReadLocalClassIds"/>
    public IReadOnlyList<int> ReadLocalClassIds()
    {
        if (_player is not { } player)
        {
            return [];
        }

        if (!_memory.TryReadPointer(player.Address + OffCreatureStats, out nint stats) || stats == 0)
        {
            return [];
        }

        if (!_memory.TryRead(stats + StatsClassCount, out byte classCount) || classCount == 0)
        {
            return [];
        }

        List<int> classIds = new(2);
        for (int slot = 0; slot < classCount && slot < 2; slot++)
        {
            if (_memory.TryRead(stats + StatsClasses + slot * ClassInfoStride + ClassInfoClassId, out byte classId))
            {
                classIds.Add(classId);
            }
        }

        return classIds;
    }
}
