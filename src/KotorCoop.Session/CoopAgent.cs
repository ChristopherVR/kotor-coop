using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session.Events;
using KotorCoop.Session.Pumps;

namespace KotorCoop.Session;

/// <summary>
/// The pump that joins the two halves: read the local player out of the game, hand it to
/// the session, take the session's view of everyone else, and write it back into the game.
///
/// Everything above this class is game-agnostic and everything below it is a JSON offset
/// profile, which is what makes the same binary work against the mock harness and against
/// swkotor.exe.
///
/// The per-feature work lives in <see cref="KotorCoop.Session.Pumps"/> — this class owns only
/// the scheduler (<see cref="PumpOnce"/>), the module/peer bookkeeping the typed events below
/// are raised from, and the handful of public properties/methods other components poll or call.
/// </summary>
public sealed class CoopAgent
{
    private readonly IGameBridge _bridge;
    private readonly ICoopSession _session;
    private readonly AgentSettings _settings;

    private readonly object _snapshotGate = new();
    private PlayerState _lastLocalState = PlayerState.Empty;
    private IReadOnlyList<RemotePeer> _lastPeers = [];

    /// <summary>TEMPORARY (live stall investigation): throttles the CanWriteRemotes=false
    /// diagnostic to once/sec.</summary>
    private long _lastCanWriteRemotesDiagMs;

    private long _lastHeavyPumpMs;
    private long _lastCombatPumpMs;

    /// <summary>The module we last saw ourselves in, and when we arrived — so a transition is
    /// never issued while the world we just entered is still filling with objects. The flag
    /// distinguishes "we changed module" from "this is the first module we ever saw", which
    /// is where we already were and must not be made to wait. Also what
    /// <see cref="ModuleChanged"/>/<see cref="WorldAdopted"/> are raised from.</summary>
    private string _settledModule = string.Empty;
    private long _moduleSettledAtMs;
    private bool _changedModule;

    /// <summary>Peer ids seen as of the last pump, for the <see cref="PeerJoined"/>/
    /// <see cref="PeerLeft"/> diff below. Purely additive bookkeeping — nothing else reads it.</summary>
    private readonly HashSet<uint> _knownPeerIds = [];

    // Wraps the mutable Log property so every pump gets a stable delegate at construction time
    // (before SessionManager has had a chance to assign Log) that still reflects whatever Log
    // is set to at the moment it is actually invoked.
    private readonly Action<string> _log;

    private readonly BarkPump _barkPump;
    private readonly TransitionPump _transitionPump;
    private readonly LootPump _lootPump;
    private readonly DialoguePump _dialoguePump;
    private readonly PartyPump _partyPump;
    private readonly TriggerPump _triggerPump;
    private readonly CombatPump _combatPump;
    private readonly LivenessPump _livenessPump;
    private readonly PausePump _pausePump;
    private readonly DeathPump _deathPump;
    private readonly WorldStatePump _worldStatePump;
    private readonly ScriptItemGrantPump _scriptItemGrantPump;
    private readonly PartyPoolPump _partyPoolPump;

    /// <summary>
    /// The suppression ledger shared by every pump that can put a new row into the local PC's
    /// inventory through a channel OTHER than a script — ground-item pickup
    /// (<see cref="WorldStatePump"/>), container/corpse loot and a peer's grant applied via
    /// giveloot (<see cref="LootPump"/>), and a human's own <c>giveloot</c>
    /// (<see cref="NoteLocalItemGiven"/>). <see cref="ScriptItemGrantPump"/> is the only
    /// consumer. See <see cref="RecentlySyncedItems"/>'s own doc for why this is keyed by
    /// resref, not by object id or channel.
    /// </summary>
    private readonly RecentlySyncedItems _recentlySyncedItems = new();

    public CoopAgent(IGameBridge bridge, ICoopSession session, AgentSettings settings)
    {
        _bridge = bridge;
        _session = session;
        _settings = settings;

        _log = msg => Log?.Invoke(msg);

        _barkPump = new BarkPump(bridge, session);
        _transitionPump = new TransitionPump(bridge, session, settings, _log);
        _lootPump = new LootPump(bridge, session, _recentlySyncedItems, _log);
        _dialoguePump = new DialoguePump(bridge, session, settings, _log);
        _partyPump = new PartyPump(bridge, session, settings, _log);
        _partyPoolPump = new PartyPoolPump(bridge, session, _log);

        // TriggerPump before CombatPump: CombatPump.PumpEnemies calls into it for the
        // client-triggered encounter/generic-trigger sub-steps.
        _triggerPump = new TriggerPump(bridge, session, _log);
        _combatPump = new CombatPump(bridge, session, _log, _triggerPump);
        _livenessPump = new LivenessPump(bridge, session, _log);
        _pausePump = new PausePump(bridge, settings, _log);
        _deathPump = new DeathPump(bridge, session, _log);
        _worldStatePump = new WorldStatePump(bridge, session, settings, _recentlySyncedItems);
        _scriptItemGrantPump = new ScriptItemGrantPump(bridge, session, settings, _recentlySyncedItems, _log);
    }

    public IGameBridge Bridge => _bridge;

    public ICoopSession Session => _session;

    /// <summary>Optional sink for one-off notable events; null means silent.</summary>
    public Action<string>? Log { get; set; }

    public bool LastReadSucceeded { get; private set; }

    public bool LastWriteSucceeded { get; private set; }

    /// <summary>How long a pump tick takes, averaged over the last 100, and the worst of
    /// them — against the budget one tick has. Surfaced for the readout rather than printed,
    /// because the readout owns the console.</summary>
    public double PumpAverageMs { get; private set; }

    public double PumpPeakMs { get; private set; }

    public double PumpBudgetMs { get; private set; }

    public long ReadFailureCount { get; private set; }

    /// <summary>
    /// True once local reads have failed for <see cref="AwayRule"/>'s threshold in a row —
    /// this instance's own player is being published with <see cref="PlayerState.FlagAway"/>
    /// set. Distinct from <see cref="LastReadSucceeded"/>, which flips on the very first
    /// failed tick: this is the sustained, ~1 s-debounced signal peers actually see. See
    /// <see cref="AwayRule"/>.
    /// </summary>
    public bool IsAway { get; private set; }

    /// <summary>Consecutive failed local reads since the last success — reset to 0 the moment
    /// <see cref="IGameBridge.TryReadLocalState"/> succeeds again, unlike the cumulative
    /// <see cref="ReadFailureCount"/> above. What <see cref="AwayRule.IsAway"/> is judged
    /// against.</summary>
    private int _consecutiveReadFailures;

    public PlayerState LastLocalState
    {
        get
        {
            lock (_snapshotGate)
            {
                return _lastLocalState;
            }
        }
    }

    public IReadOnlyList<RemotePeer> LastPeers
    {
        get
        {
            lock (_snapshotGate)
            {
                return _lastPeers;
            }
        }
    }

    // ---- typed events -------------------------------------------------------------------
    //
    // Raised synchronously on the pump thread, in the same tick and from the same bookkeeping
    // that already detects each condition today. Subscribing is purely additive: no existing
    // logic moved onto an event, nothing here changes wire or timing behaviour.

    /// <inheritdoc cref="ModuleChangedHandler"/>
    public event ModuleChangedHandler? ModuleChanged;

    /// <inheritdoc cref="WorldAdoptedHandler"/>
    public event WorldAdoptedHandler? WorldAdopted;

    /// <inheritdoc cref="PeerJoinedHandler"/>
    public event PeerJoinedHandler? PeerJoined;

    /// <inheritdoc cref="PeerLeftHandler"/>
    public event PeerLeftHandler? PeerLeft;

    /// <inheritdoc cref="LocalStateReadHandler"/>
    public event LocalStateReadHandler? LocalStateRead;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Task sessionTask = _session.RunAsync(cancellationToken);

        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(1000.0 / _settings.PollRate));

        // Coarse pump-cost telemetry: if a pump takes anywhere near the tick budget it
        // serialises the position loop, so surface the average/peak every ~5s to the log.
        var sw = new System.Diagnostics.Stopwatch();
        double budgetMs = 1000.0 / _settings.PollRate;
        long ticks = 0;
        double sumMs = 0, maxMs = 0;

        // One bad tick must not end the session. The pump reads a live game's memory, and a
        // transient failure — a module mid-store, an object freed between two reads — throws
        // exactly when the game is doing something interesting. Killing the agent then is
        // what the players experience as "the other character vanished and never came back":
        // measured live, the joiner's agent silently exited mid-play and the host ran on
        // alone. Log the tick's failure and keep pumping; give up only when it fails
        // continuously for ~10 seconds, which means the game is actually gone.
        int consecutiveFailures = 0;
        int maxConsecutiveFailures = _settings.PollRate * 10;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                sw.Restart();
                try
                {
                    PumpOnce();
                    consecutiveFailures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (++consecutiveFailures == 1 || consecutiveFailures % _settings.PollRate == 0)
                    {
                        Log?.Invoke($"pump tick failed ({consecutiveFailures} in a row): {ex.Message}");
                    }

                    if (consecutiveFailures >= maxConsecutiveFailures)
                    {
                        Log?.Invoke("pump failing continuously; the game is gone, shutting down");
                        break;
                    }
                }

                double ms = sw.Elapsed.TotalMilliseconds;

                ticks++;
                sumMs += ms;
                if (ms > maxMs) maxMs = ms;
                if (ticks >= 100)
                {
                    // ⚠ PUBLISHED, never printed. This used to Console.WriteLine straight
                    // onto a screen the dashboard redraws in place, so the line appeared at
                    // the cursor and was painted over a fraction of a second later — seen as
                    // the terminal "flickering, showing a [pump] log then disappearing".
                    // Anything worth saying while the readout owns the console has to go
                    // through the readout.
                    PumpAverageMs = sumMs / ticks;
                    PumpPeakMs = maxMs;
                    PumpBudgetMs = budgetMs;
                    ticks = 0; sumMs = 0; maxMs = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        // Undo the party freeze so a single-player continuation is normal — leaving solo
        // mode set would give the player companions that will not follow, with no cause.
        _bridge.ReleasePartyFreeze();

        await sessionTask.ConfigureAwait(false);
    }

    public void PumpOnce()
    {
        if (_bridge.TryReadLocalState(out PlayerState local))
        {
            LastReadSucceeded = true;
            _consecutiveReadFailures = 0;

            if (IsAway)
            {
                // The drought ended: back from the main menu, a load screen finished, a
                // character was re-selected. local is a fresh read and by construction
                // carries no FlagAway of its own, so publishing it as-is IS the "cleared"
                // announcement peers see.
                IsAway = false;
                Log?.Invoke("away: local reads resumed - no longer away");
            }

            _session.PublishLocalState(local);

            lock (_snapshotGate)
            {
                _lastLocalState = local;
            }

            LocalStateRead?.Invoke(local);
        }
        else
        {
            LastReadSucceeded = false;
            ReadFailureCount++;
            _consecutiveReadFailures++;
            local = LastLocalState;

            // Below AwayRule's threshold this is left exactly as it always was: a transient
            // blip publishes nothing new, and the session's own snapshot buffer keeps
            // whatever it last sent. Only a SUSTAINED drought earns the away flag — see
            // AwayRule's remarks for why this must be wall-clock-scaled, not a bare tick
            // count.
            if (AwayRule.IsAway(_consecutiveReadFailures, _settings.PollRate))
            {
                if (!IsAway)
                {
                    IsAway = true;
                    Log?.Invoke("away: local reads have failed for ~1s; publishing last known state as away");
                }

                // Carried on `local` itself (not just the publish below) so every pump this
                // tick that reads the local parameter — death, travel, pause — sees the same
                // away state a peer's instance will.
                local = local with { Flags = local.Flags | PlayerState.FlagAway };

                _session.PublishLocalState(local);

                lock (_snapshotGate)
                {
                    _lastLocalState = local;
                }
            }
        }

        IReadOnlyList<RemotePeer> peers = _session.GetRemotePeers();

        lock (_snapshotGate)
        {
            _lastPeers = peers;
        }

        RaisePeerJoinLeaveEvents(peers);

        // Round 78 instrumentation — every tick, ahead of every cadence gate below, so the
        // logged edge is as close to the engine's own true transition as this process can see.
        _dialoguePump.LogHostDialogueEdge();

        if (!_bridge.CanWriteRemotes)
        {
            // TEMPORARY DIAGNOSTIC (live stall investigation) - throttled to once/sec so it
            // doesn't flood the log while stuck here every tick.
            long diagNowMs = Environment.TickCount64;
            if (diagNowMs - _lastCanWriteRemotesDiagMs >= 1000)
            {
                _lastCanWriteRemotesDiagMs = diagNowMs;
                Log?.Invoke("stall diag: CanWriteRemotes=false, blocked before the combat pump - retrying plugin block lookup on the bridge's own cadence");
            }

            LastWriteSucceeded = false;
            return;
        }

        // Each channel is gated on the negotiated capability set: a mod difference that
        // would corrupt a channel disables that channel for the session (a different
        // globals catalogue drops world state; a different save drops enemies/doors/items)
        // while everything unaffected keeps working. Full set until a peer is judged.
        SessionCapabilities caps = _session.Capabilities;

        // --- real-time path: player position + animation, EVERY tick --------------------
        // Only filter when both ends actually know their module. A profile that has not
        // located the module-name field reports an empty string, and treating "unknown"
        // as "somewhere else" would hide every peer and look exactly like a broken
        // network layer.
        IReadOnlyList<RemotePeer> visible = _settings.FilterByModule && local.Module.Length > 0
            ? [.. peers.Where(p => p.State.Module.Length == 0 || p.IsInModule(local.Module))]
            : peers;

        // A peer inside the engine's own minigame mode (swoop race, forced turret sequence)
        // is not meaningfully standing in this module either, even though their published
        // Module string still names it — same "park, do not mirror" treatment as the module
        // filter above, and the same idiom: simply absent from what TryWriteRemotePeers
        // writes, which is what already deactivates a slot for a peer elsewhere. See
        // PlayerState.FlagInMinigame, MinigameRule.ShouldHidePeer and docs/research/minigames.md.
        MinigameRule minigameRule = new(HidePeersEnabled: _settings.HidePeersInMinigame, SpectateEnabled: false);
        visible = _settings.HidePeersInMinigame
            ? [.. visible.Where(p => !minigameRule.ShouldHidePeer(p.State.IsInMinigame))]
            : visible;

        // A peer whose OWN agent has judged them away (AwayRule — quit to the main menu, a
        // load screen, the character screen) is exactly as "not meaningfully standing here"
        // as one in a minigame, and gets the identical treatment: simply absent from what
        // TryWriteRemotePeers writes, which is what already parks/despawns a slot's phantom
        // (PhantomController.OnFrame destroys a slot the instant RemoteView.Active goes
        // false) instead of leaving it frozen at its last position. Unlike the minigame
        // filter this has no coop.ini escape hatch — there is no "old behaviour" a frozen
        // statue was ever the intended experience of.
        visible = [.. visible.Where(p => !p.State.IsAway)];

        // Mask the per-avatar fields a mod mismatch makes unsafe BEFORE they reach the
        // game. A different appearance.2da means a replicated row index could paint the
        // wrong body or index off the end (a crash), so the appearance is cleared and the
        // phantom keeps its blueprint look; a cleared row and empty resrefs are exactly
        // what the plugin already treats as "leave it alone".
        visible = MaskAvatarFields(visible, caps);

        LastWriteSucceeded = _bridge.TryWriteRemotePeers(visible);

        // Track how long we have been in this module, for the travel settle window below,
        // and raise ModuleChanged/WorldAdopted for whoever is listening. Only real names
        // count: mid-transition the bridge reports empty or an "areaXX" fallback, and
        // treating those as arrivals would restart the clock (and fire an event) on noise.
        if (local.Module.Length > 0
            && !ModuleNameGuard.IsFallbackModuleName(local.Module)
            && !string.Equals(local.Module, _settledModule, StringComparison.OrdinalIgnoreCase))
        {
            string previousModule = _settledModule;
            _changedModule = _settledModule.Length > 0;
            _settledModule = local.Module;
            _moduleSettledAtMs = SessionClock.NowMs;

            if (_changedModule)
            {
                ModuleChanged?.Invoke(previousModule, _settledModule);
            }
            else
            {
                WorldAdopted?.Invoke(_settledModule);
            }
        }

        // BEFORE PumpTravel, and that ordering is the whole point: a follow decided this tick
        // must carry the cinematic announced this tick. Below it, every announcement would be
        // one tick late — which happens to work in the field, because a movie buys seconds of
        // slack, and is exactly the kind of accidental correctness that breaks silently the
        // first time a transition carries no movie.
        _transitionPump.PumpTransitionMovies(local);

        // Same ordering rule, for the same reason: PumpTravel's fast path must see this
        // tick's announce, not last tick's.
        _transitionPump.PumpTransitionAnnounce(local);

        // Pause and travel are cheap flag checks and must feel immediate.
        _transitionPump.PumpTravel(local, peers, _changedModule, _moduleSettledAtMs);
        _pausePump.PumpPause(local, visible);

        // Deliberately NOT inside PumpPause: that returns early when pause sharing is
        // switched off, and combat state is not an optional courtesy — it decides which
        // ruleset each engine is running.
        _combatPump.PumpSharedCombat(visible);

        // Death is arbitrated every tick too: a rescue window measured in seconds cannot
        // wait for the ~5 Hz heavy pass, and a swallowed game-over must be answered before
        // the engine tries again on the next frame.
        // ⚠ ALL peers, not `visible`. `visible` has already been filtered to the local
        // module, so a peer one module away is absent rather than standing - and "everyone is
        // down" then becomes true while they are alive and on their way. Two players hid this
        // because the client follows the host's module; three do not.
        _deathPump.PumpDeath(local, peers);

        // Client-only, every tick: keep the player's input locked to their own PC (a cheap id
        // write) so an attempted switch to a shared, host-owned companion reverts within a frame
        // rather than waiting for the ~5 Hz heavy party pass. Host owns the companions; it is free
        // to control them.
        if (_settings.SyncParty && _bridge.SupportsPartySync && !_session.IsEnemyAuthority
            && (caps & SessionCapabilities.Party) != 0)
        {
            _bridge.LockControlToPlayer();
        }

        // --- combat path: a few times a second -------------------------------------------
        //
        // Enemy presence, damage in both directions, and the party mirror. Fast enough that
        // a mirrored enemy's movement reads as movement (at the heavy pass's 1 Hz it read as
        // a lurch covering a second of running per hop, smoothed into a glide), and that a
        // hit taken through a phantom lands in the same beat of the fight it happened in —
        // but still gated, because the enemy census is a creature-table scan and does not
        // belong on the 30 Hz position path.
        long combatNowMs = Environment.TickCount64;
        if (combatNowMs - _lastCombatPumpMs >= _settings.CombatPumpIntervalMs)
        {
            _lastCombatPumpMs = combatNowMs;
            _combatPump.PumpEnemies(local, visible, caps);
            _livenessPump.PumpCreatureLiveness(local, caps);
            _combatPump.PumpPhantomDamage(caps);
            _combatPump.PumpPeerEffects(caps);
            _lootPump.PumpMaterialisedLoot(local.Module);

            // ⚠ PARTY RODE HERE TOO, ONCE — same shape as dialogue below. Left as the
            // fallback here (settings.PartyFastPump off) so turning the flag off is a true
            // rollback to exactly this cadence; the fast path below is where it moves to when
            // the flag is on. See docs/research/lag-audit.md's companion-mirror-latency
            // suspect.
            if (!_settings.PartyFastPump)
            {
                _partyPump.PumpParty(caps);
                _partyPump.PumpPartyRoster(caps);
            }

            // ⚠ DIALOGUE RODE HERE TOO, ONCE. It is the same kind of traffic — small block
            // reads, no world scan — and sharing this 150 ms gate with the enemy census (which
            // genuinely needs it) was the reason a followed conversation felt second-hand: the
            // owner's partner tag, line and node crossed agent→wire at this cadence and
            // wire→block at this SAME cadence again on the far side, so a line the owner had
            // already heard could take up to two gates' worth to reach the follower's screen —
            // see docs/research/dialogue-join-latency.md for the measured numbers. Left as the
            // fallback here (settings.DialogueFastJoin off) so turning the flag off is a true
            // rollback to exactly this cadence; the fast path below is where it moves to when
            // the flag is on.
            if (!_settings.DialogueFastJoin)
            {
                _dialoguePump.PumpDialogue(local, caps);
            }

            _worldStatePump.PumpInteractionLocks(local);
            _barkPump.PumpBarks(local);
            _transitionPump.PumpMidModuleMovies(local);

            // ⚠ CONTAINER STATE RODE HERE TOO, ONCE — same shape as dialogue/party above.
            // PumpInteractionLocks (just above) already publishes the open/close ANIMATION and
            // the use-lock on this same 150 ms cadence; PumpPlaceables carries the DURABLE
            // open-or-locked STATE that is what actually keeps a closed container looking
            // closed. Left on the heavy 1 s cadence by default (settings.ContainerFastPump
            // off) so turning the flag off is a true rollback to exactly today's behaviour —
            // see docs/research/container-sync.md for the measured gap this closes.
            if (_settings.ContainerFastPump)
            {
                _worldStatePump.PumpPlaceables(caps);
            }
        }

        // Dialogue, on the fast path: every poll tick (as fast as the position write above),
        // gated internally by DialogueTickGate rather than by this method's own cadence — a
        // genuine change is acted on the very next tick, and an unchanged read costs nothing
        // beyond the block read itself. coop.ini dialogue_fast_join=1. See
        // docs/research/dialogue-join-latency.md for why this is the biggest single latency
        // win available in the join path, and DialogueTickGate for the dedup rule.
        if (_settings.DialogueFastJoin)
        {
            _dialoguePump.PumpDialogue(local, caps);
        }

        // Party, on the fast path: every poll tick, same reasoning and same rollout shape as
        // dialogue above — PumpParty/PumpPartyRoster are cheap block reads/writes, not a world
        // scan, so nothing about running them every tick costs materially more than the 150 ms
        // cadence did. coop.ini party_fast_pump=1. See docs/research/lag-audit.md.
        if (_settings.PartyFastPump)
        {
            _partyPump.PumpParty(caps);
            _partyPump.PumpPartyRoster(caps);
        }

        // Script-item grants: called every tick, same as the two fast-path calls above, so it
        // always reads _dialoguePump.IsOwningConversation as of THIS tick regardless of which
        // of the two branches above (or below, on the legacy cadence) last updated it. Cheap
        // when off (settings.ScriptItemSync) or between its own ~1s samples — see the pump's
        // own doc for why it is not itself tied to HeavyPumpIntervalMs. Rides the same
        // Dialogue capability the grant wire (DialogueGrantPacket) already needs, since a mod
        // difference that would corrupt one grant channel would corrupt the other identically.
        if ((caps & SessionCapabilities.Dialogue) != 0)
        {
            _scriptItemGrantPump.PumpScriptItemGrants(local.Module, _dialoguePump.IsOwningConversation);
        }

        // --- heavy world-state path ------------------------------------------------------
        //
        // The syncs below scan the game's memory for creatures, items and doors. One pass
        // costs orders of magnitude more than the position write above, and they share this
        // thread with it — so their cadence decides how fresh a peer's POSITION is.
        //
        // ⚠ This is measured on a WALL CLOCK, not a tick count, and that distinction is the
        // whole bug. Counting ticks ties the scan rate to the send rate, so raising the send
        // rate to reduce latency multiplies the scans instead — at 60 Hz with a divisor of 4
        // the heavy pass ran 15x a second, each pass outlasting its slot, and the loop simply
        // never caught up. Measured end to end: a position took 3.5-7.4 SECONDS to travel
        // from one game's memory to the other's. On a wall clock the scans stay at a fixed
        // few per second no matter how fast state is sent, so the fast path is free to run at
        // whatever rate the tick asks for.
        long nowMs = Environment.TickCount64;
        if (nowMs - _lastHeavyPumpMs < _settings.HeavyPumpIntervalMs)
        {
            return;
        }

        _lastHeavyPumpMs = nowMs;

        // Report our in-world content fingerprint (the session rate-limits the wire
        // anyway). The save identity and the current module's GIT digest ride along with
        // the globals catalogue: passing the LIVE module here is what makes the GIT
        // comparison per-module — it re-judges itself on every module change, restoring
        // the id-keyed channels wherever the two installs' GITs agree again.
        _session.PublishWorldDigest(new WorldDigest(
            _bridge.GlobalsCatalogueDigest,
            _bridge.SaveIdentityDigest,
            local.Module,
            _bridge.ModuleGitDigest(local.Module),
            _bridge.WorldLoadSerial));

        _worldStatePump.PumpItems(local, caps);

        // Progression and journal are both DERIVED from the local player's own read (gold/XP
        // off the party table, quest state off the globals catalogue reachable only through a
        // live player object) rather than taking `local` as a parameter, so nothing upstream
        // already gates them on this tick's read having actually succeeded. Skipping both
        // while the last read failed stops a stale republish while away — see
        // docs/research/disconnect-reconnect.md — though each also fails its own internal
        // bridge read gracefully on its own, so this is defence in depth, not the only guard.
        if (LastReadSucceeded)
        {
            _partyPump.PumpProgression(caps);
            _worldStatePump.PumpJournal(caps);
        }

        _worldStatePump.PumpWorldState(caps);
        _worldStatePump.PumpDoors(caps);

        // Moved to the 150 ms combat cadence above when settings.ContainerFastPump is on —
        // see the call site there for why. Left here, unchanged, as the fallback cadence.
        if (!_settings.ContainerFastPump)
        {
            _worldStatePump.PumpPlaceables(caps);
        }

        _lootPump.PumpContainerLoot(caps);

        // Off by default (coop.ini party_pool_sync) — a researched-but-not-yet-live-verified
        // offset chain and engine calls, per the project's engine-write rules. See
        // docs/research/party-pool-sync.md.
        if (_settings.PartyPoolSyncEnabled)
        {
            _partyPoolPump.Pump(caps);
        }
    }

    /// <summary>Diffs <see cref="_knownPeerIds"/> against this tick's peer set and raises
    /// <see cref="PeerJoined"/>/<see cref="PeerLeft"/> for whatever changed. Additive
    /// telemetry only — nothing downstream of PumpOnce reads <see cref="_knownPeerIds"/>.</summary>
    private void RaisePeerJoinLeaveEvents(IReadOnlyList<RemotePeer> peers)
    {
        HashSet<uint> currentPeerIds = new(peers.Count);
        foreach (RemotePeer peer in peers)
        {
            currentPeerIds.Add(peer.PeerId);
        }

        foreach (uint id in currentPeerIds)
        {
            if (_knownPeerIds.Add(id))
            {
                PeerJoined?.Invoke(id);
            }
        }

        List<uint>? left = null;
        foreach (uint id in _knownPeerIds)
        {
            if (!currentPeerIds.Contains(id))
            {
                (left ??= []).Add(id);
            }
        }

        if (left is not null)
        {
            foreach (uint id in left)
            {
                _knownPeerIds.Remove(id);
                PeerLeft?.Invoke(id);
            }
        }
    }

    /// <summary>
    /// One item still owed to a target, queued for the automatic give-loot path to submit
    /// through the same arbitrated cheat channel a human's <c>giveloot</c> console command
    /// uses — see <see cref="TryDequeuePendingLoot"/>.
    ///
    /// <paramref name="TargetObjectId"/> carries TWO different id spaces depending on where the
    /// give came from, and <c>CheatController.GiveLoot</c> is what tells them apart: a
    /// materialised-loot give (docs/research/enemy-materialisation.md §6) carries the OWNER's
    /// enemy object id, translated locally via <c>MaterialisedEnemies.ToLocal</c>; a
    /// dialogue-grant give (docs/research/dialogue-consequences.md §3) carries the well-known
    /// local-player sentinel <c>SharedBlock.CheatTargetLocalPlayer</c>, which needs no
    /// translation because "the local player" means the same thing on every instance.
    /// </summary>
    public readonly record struct PendingMaterialisedLootGive(uint TargetObjectId, MaterialisedLootItem Item);

    /// <summary>
    /// Called by whatever already arbitrates the single-slot staging-cheat channel (the
    /// command dispatcher's pending-cheat-serial check), at most once per ITS tick, so an
    /// automatic give-loot request is submitted through the exact same call path a human's
    /// typed <c>giveloot</c> takes and can never be filed alongside one in flight. Returns
    /// false with nothing queued to give.
    /// </summary>
    public bool TryDequeuePendingLoot(out PendingMaterialisedLootGive next)
    {
        return _lootPump.TryDequeuePendingLoot(out next);
    }

    /// <summary>One party-pool add/remove this instance's own engine still owes, queued for the
    /// automatic apply path to submit through the same arbitrated cheat channel a human's own
    /// party-pool console verb would use — see <see cref="TryDequeuePendingPartyPoolApply"/>.
    /// Public (rather than living on the internal <c>PartyPoolPump</c>) for the same reason
    /// <see cref="PendingMaterialisedLootGive"/> is: <c>KotorCoop.Agent</c>, a separate
    /// assembly, needs to see it.</summary>
    public readonly record struct PendingPartyPoolApply(
        KotorCoop.Protocol.PartyPoolOpKind Kind, KotorCoop.Protocol.PartyPoolStackKey Key, ushort Count);

    /// <summary>
    /// Called by whatever already arbitrates the single-slot staging-cheat channel, at most
    /// once per ITS tick, so an automatic party-pool apply is submitted through the same call
    /// path a human's own party-pool console verb would take and can never be filed alongside
    /// one in flight. Mirrors <see cref="TryDequeuePendingLoot"/> exactly.
    /// </summary>
    public bool TryDequeuePendingPartyPoolApply(out PendingPartyPoolApply next)
    {
        return _partyPoolPump.TryDequeuePendingApply(out next);
    }

    /// <summary>
    /// Records that a <c>giveloot</c> targeting THIS instance's own local player is about to
    /// add <paramref name="resref"/> to the PC's inventory — called by
    /// <c>CoopCommandDispatcher.GiveLoot</c> (the human-typed console verb; the automatic
    /// drain already feeds the SAME ledger from <see cref="LootPump.PumpMaterialisedLoot"/>,
    /// since it queues through the identical give-loot channel). A no-op for any other target
    /// (a materialised enemy's stand-in): only a give that lands in the PC's own inventory can
    /// ever collide with <see cref="ScriptItemGrantPump"/>'s sweep of it.
    ///
    /// Always ONE unit of budget, never the requested stack size: <c>CheatController.GiveLoot</c>
    /// (docs/research/dialogue-consequences.md §3) mints a single fresh item object per call —
    /// the stack argument sets that one object's <see cref="ItemIdentity.StackSize"/> field, it
    /// does not create several rows — and <see cref="RecentlySyncedItems"/>/<see cref="DialogueItemDiff"/>
    /// both count rows, exactly like every other feed into this ledger.
    /// </summary>
    public void NoteLocalItemGiven(uint targetObjectId, string resref)
    {
        if (targetObjectId != AgentWellKnownIds.LocalPlayerObjectId)
        {
            return;
        }

        _recentlySyncedItems.Record(resref, 1, Environment.TickCount64);
    }

    /// <summary>
    /// Clears the avatar fields the negotiated capabilities forbid, so a mod mismatch
    /// cannot reach the game as a bad appearance row or a wrong-item equip. Returns the
    /// list unchanged when everything is permitted, which is the common case.
    /// </summary>
    private static IReadOnlyList<RemotePeer> MaskAvatarFields(
        IReadOnlyList<RemotePeer> peers, SessionCapabilities caps)
    {
        bool appearance = (caps & SessionCapabilities.Appearance) != 0;
        bool equipment = (caps & SessionCapabilities.Equipment) != 0;

        if (appearance && equipment)
        {
            return peers;
        }

        RemotePeer[] masked = new RemotePeer[peers.Count];
        for (int i = 0; i < peers.Count; i++)
        {
            PlayerState state = peers[i].State;

            if (!appearance)
            {
                state = state with { Appearance = 0 };
            }

            if (!equipment)
            {
                state = state with { EquippedBody = "", EquippedRightWeapon = "", EquippedLeftWeapon = "" };
            }

            masked[i] = peers[i] with { State = state };
        }

        return masked;
    }
}
