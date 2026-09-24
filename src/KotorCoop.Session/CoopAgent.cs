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
public sealed partial class CoopAgent
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
    private WorldDigestReadinessState _worldDigestReadinessState = WorldDigestReadinessState.Initial;

    /// <summary>coop.ini <c>perf_trace</c>'s accumulator, or null when the trace is off — which
    /// is what keeps every call site below a single null check rather than a flag test plus a
    /// clock read. See <see cref="PerfTrace"/>.</summary>
    private readonly PerfTrace? _perfTrace;

    /// <summary>Wall clock for the perf trace's phase timings. One instance reused for every
    /// phase of every tick (start/stop, never allocated per measurement) because the trace must
    /// not itself be a per-tick allocation on the path it is measuring.</summary>
    private readonly System.Diagnostics.Stopwatch _perfClock = new();

    /// <summary>Where the running phase measurement started — <see cref="_perfClock"/> elapsed
    /// and the cross-process read counters, captured by <see cref="PerfPhaseStart"/>.</summary>
    private double _perfPhaseFromMs;
    private (long Reads, long ReadBytes) _perfPhaseFromReads;

    /// <summary>What this tick's real-time half cost, handed from <see cref="PumpOnce"/> to the
    /// loop in <see cref="RunAsync"/> that owns the total. Meaningless while the trace is off.</summary>
    private double _perfPositionPathMs;

    /// <summary>The session's own <see cref="ICoopSession.HostLost"/> as of the last
    /// <see cref="PumpOnce"/> tick, for the edge-detect in that method — the falling/rising
    /// edge is what runs the plugin-side release pass and resumes normal driving, never a
    /// per-tick re-check. See docs/research/disconnect-reconnect.md's "host loss" section.</summary>
    private bool _lastHostLost;

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
    private readonly LocalVarPump _localVarPump;
    private readonly ScriptItemGrantPump _scriptItemGrantPump;
    private readonly ClassGrantPump _classGrantPump;
    private readonly PlacedMinePump _placedMinePump;
    private readonly EnemyEffectPump _enemyEffectPump;
    private readonly PartyPoolPump _partyPoolPump;
    private readonly StorePump _storePump;

    /// <summary>v2 (party_pool_sync=2/store_sync=2, docs/research/shared-inventory-v2.md) — new,
    /// separate pump instances alongside the v1 pair above, never both active for the same
    /// channel at once (see <see cref="PumpOnce"/>'s own mode dispatch): v1's
    /// <see cref="_partyPoolPump"/>/<see cref="_storePump"/> are untouched by this redesign.</summary>
    private readonly PartyPoolPumpV2 _partyPoolPumpV2;
    private PartyPoolLoadGateState _partyPoolLoadGateState = PartyPoolLoadGateState.Initial;
    private bool _partyPoolHostLoadPending;
    private bool _partyPoolClientLoadQuarantined;
    private bool _partyPoolHostKnownUnavailable;
    private volatile bool _partyPoolApplyBlocked;
    private readonly StorePumpV2 _storePumpV2;

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

    /// <summary>What was last pushed through <see cref="IPlayerBridge.SetBehaviourOverrides"/>,
    /// so an unchanged <see cref="ICapabilitySession.Overrides"/> costs nothing beyond the
    /// encode compare — see <see cref="PumpBehaviourOverrides"/>.</summary>
    private (uint Mask, uint Values) _lastBehaviourOverrides;

    /// <summary>See the constructor's player-count scaling remarks and
    /// <see cref="PumpPlayerCountScaling"/>.</summary>
    private readonly PlayerCountScaling _scaling;
    private readonly PlayerCountSettleRule _playerCountSettle = new();

    private readonly StoreStockScaleLatch _stockLatch;

    /// <summary>The <see cref="PlayerCountScaling.Generation"/> last pushed into the plugin
    /// block, so the two dwords are written on a genuine change only. Starts at
    /// uint.MaxValue so the very first tick always writes (a fresh block reads 0/0, which the
    /// plugin treats as "nothing to apply", and the resolved 100/100 must replace it).</summary>
    private uint _lastScalingGenerationPushed = uint.MaxValue;

    /// <summary>The resolved player-count scaling for this session, for <c>agent info</c> and
    /// the dispatcher's status line.</summary>
    public PlayerCountScaling Scaling => _scaling;

    public CoopAgent(IGameBridge bridge, ICoopSession session, AgentSettings settings)
    {
        _bridge = bridge;
        _session = session;
        _settings = settings;

        _log = msg => Log?.Invoke(msg);

        if (settings.PerfTrace)
        {
            _perfTrace = new PerfTrace();

            // Counting is process-wide and off until something asks for it — see
            // MemoryTelemetry. Switched on here rather than in the composition root so the
            // ONLY way it turns on is an agent that will actually drain and report it.
            MemoryTelemetry.Enabled = true;
        }

        _barkPump = new BarkPump(bridge, session);
        _transitionPump = new TransitionPump(bridge, session, settings, _log);
        _lootPump = new LootPump(bridge, session, _recentlySyncedItems, _log);
        _dialoguePump = new DialoguePump(bridge, session, settings, _log);
        // Player-count scaling (docs/research/player-count-scaling.md): one resolved set of
        // percents per agent, recomputed every tick from the effective settings and the live
        // player count, handed to the two pumps that apply their half and pushed into the
        // plugin's block for the other half (PumpPlayerCountScaling).
        _scaling = new PlayerCountScaling(settings.Scaling);
        _stockLatch = new StoreStockScaleLatch(
            settings.GameDirectory is { Length: > 0 } gameDirectory
                ? Path.Combine(gameDirectory, "saves")
                : null);

        _partyPump = new PartyPump(bridge, session, settings, _log, _scaling);
        _partyPoolPump = new PartyPoolPump(bridge, session, _log);
        _storePump = new StorePump(bridge, session, _log);
        _partyPoolPumpV2 = new PartyPoolPumpV2(bridge, session, _log, _settings.PartyPoolTrace);
        _storePumpV2 = new StorePumpV2(
            bridge, session, _log, _settings.PartyPoolTrace, scaling: _scaling, stockLatch: _stockLatch);

        // TriggerPump before CombatPump: CombatPump.PumpEnemies calls into it for the
        // client-triggered encounter/generic-trigger sub-steps.
        _triggerPump = new TriggerPump(bridge, session, _log);
        _combatPump = new CombatPump(bridge, session, _log, _triggerPump);
        _livenessPump = new LivenessPump(bridge, session, _log);
        _pausePump = new PausePump(bridge, settings, _log);
        _deathPump = new DeathPump(bridge, session, _log);
        _worldStatePump = new WorldStatePump(bridge, session, settings, _recentlySyncedItems);
        _localVarPump = new LocalVarPump(bridge, session, _log);
        _scriptItemGrantPump = new ScriptItemGrantPump(bridge, session, settings, _recentlySyncedItems, _log);
        _classGrantPump = new ClassGrantPump(bridge, session, settings, _log);
        _placedMinePump = new PlacedMinePump(bridge, session, settings, _log);
        _enemyEffectPump = new EnemyEffectPump(bridge, session, settings, _log);
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

    // ---- menu-guard timer (MenuGuardRule) ------------------------------------------------
    // The bounded protection window behind PlayerState.FlagMenuGuard: 0 while no menu opening
    // is being timed, held/advanced exactly like AwayRule's own state, driven once per pump
    // from a raw engine read (IsLocalFullScreenGuiOpen) rather than anything in `local` itself
    // — a full-screen menu has nothing to do with the player creature TryReadLocalState reads.

    private long _menuGuardOpenSinceMs;
    private long _menuGuardCooldownUntilMs;

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
                        // Name the frame: a transient read failure and a null-reference bug
                        // print the same one-line message otherwise (live 2026-09-02, a host's
                        // in-world reload logged an NRE with nowhere to look).
                        Log?.Invoke($"pump tick failed ({consecutiveFailures} in a row): {ex.Message}"
                            + $" [{ex.GetType().Name} at {TopFrame(ex)}]");
                    }

                    if (consecutiveFailures >= maxConsecutiveFailures)
                    {
                        Log?.Invoke("pump failing continuously; the game is gone, shutting down");
                        break;
                    }
                }

                double ms = sw.Elapsed.TotalMilliseconds;

                if (_perfTrace is not null)
                {
                    _perfTrace.NoteTick(ms, _perfPositionPathMs);
                    _perfPositionPathMs = 0;

                    if (_perfTrace.TryDrain(
                            Environment.TickCount64, PerfTraceFlushIntervalMs, _session.GetStats(),
                            out PerfTraceSnapshot perf))
                    {
                        Log?.Invoke(PerfTrace.Format(perf));
                    }
                }

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

    /// <summary>How often <c>perf_trace</c> writes a line. One second: short enough that a
    /// two-minute walk-around is a hundred-and-twenty-row table rather than a single averaged
    /// number, long enough that the line itself never becomes the load.</summary>
    private const int PerfTraceFlushIntervalMs = 1000;

    /// <summary>Opens a phase measurement. Paired with <see cref="PerfPhaseEnd"/>; a no-op
    /// (two field writes) while the trace is off, and never nested — the phases are a flat
    /// partition of the tick, not a tree.</summary>
    private void PerfPhaseStart()
    {
        if (_perfTrace is null)
        {
            return;
        }

        _perfPhaseFromMs = _perfClock.Elapsed.TotalMilliseconds;
        _perfPhaseFromReads = MemoryTelemetry.Peek();
    }

    private void PerfPhaseEnd(PumpPhase phase)
    {
        if (_perfTrace is null)
        {
            return;
        }

        (long reads, long readBytes) = MemoryTelemetry.Peek();
        _perfTrace.NotePhase(
            phase,
            _perfClock.Elapsed.TotalMilliseconds - _perfPhaseFromMs,
            reads - _perfPhaseFromReads.Reads,
            readBytes - _perfPhaseFromReads.ReadBytes);
    }

    /// <summary>
    /// Folds <see cref="PlayerState.FlagMenuGuard"/> into a freshly-read local state — see
    /// <see cref="MenuGuardRule"/>'s own doc comment for the shape. <see cref="_bridge"/> is
    /// checked against the concrete <c>KotorGameBridge</c> rather than added to
    /// <see cref="IGameBridge"/> itself: the interface is implemented by a long tail of test
    /// fakes across this project (<c>AgentComposition</c>'s own <c>ResolveDiagnostic</c> wiring
    /// uses the same pattern), and a full-screen-GUI read is meaningless against any of them.
    /// A non-<c>KotorGameBridge</c> bridge simply never publishes the bit.
    /// </summary>
    private PlayerState ApplyMenuGuard(PlayerState local)
    {
        if (!_settings.MenuGuardSyncEnabled || _bridge is not KotorGameBridge bridge)
        {
            return local;
        }

        long now = Environment.TickCount64;
        bool menuOpenNow = bridge.IsLocalFullScreenGuiOpen;

        _menuGuardOpenSinceMs = MenuGuardRule.TrackOpenSince(_menuGuardOpenSinceMs, menuOpenNow, now);
        bool capExhausted = MenuGuardRule.CapExhausted(_menuGuardOpenSinceMs, _settings.MenuGuardCapMs, now);
        _menuGuardCooldownUntilMs = MenuGuardRule.TrackCooldown(
            _menuGuardCooldownUntilMs, capExhausted, _settings.MenuGuardCooldownMs, now);

        return MenuGuardRule.IsProtected(_menuGuardOpenSinceMs, _menuGuardCooldownUntilMs, _settings.MenuGuardCapMs, now)
            ? local with { Flags = local.Flags | PlayerState.FlagMenuGuard }
            : local;
    }

    public void PumpOnce()
    {
        if (_perfTrace is not null)
        {
            _perfClock.Restart();
        }

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

            local = ApplyMenuGuard(local);

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

        ObservePartyPoolLoadGate(local);

        IReadOnlyList<RemotePeer> peers = _session.GetRemotePeers();

        lock (_snapshotGate)
        {
            _lastPeers = peers;
        }

        RaisePeerJoinLeaveEvents(peers);

        PumpPlayerCountScaling(peers.Count + 1);

        // Round 78 instrumentation — every tick, ahead of every cadence gate below, so the
        // logged edge is as close to the engine's own true transition as this process can see.
        _dialoguePump.LogHostDialogueEdge();

        // ⚠ HOST LOSS: run the plugin-side release exactly once, on the edge, and then touch
        // NOTHING else in the game while the host stays gone. Before this, a client whose host
        // vanished never lowered the plugin's block.Command — TryWriteRemotePeers below writes
        // it unconditionally every tick it runs — so PluginHost.SessionLifecycle's whole
        // ReleaseSession list (control lock, party-select suppression, follow mirroring,
        // dialogue spectate, minigame lock, ...) never saw the falling edge it exists to react
        // to, and LockControlToPlayer further down kept reverting the player's own companion
        // selection every tick forever. bridge.ClearCoopState() is the SAME call
        // SessionManager.TearDownSession already makes on every ordinary disconnect — reusing
        // it here means the release path stays the one already proven correct there, not a
        // second copy of the list. The rising edge (host back) needs nothing special: this
        // method simply resumes below, exactly as if nothing had happened, on the very next
        // tick GetRemotePeers() etc. reflect the fresh Welcome.
        bool hostLost = _session.HostLost;
        if (hostLost != _lastHostLost)
        {
            _lastHostLost = hostLost;
            if (hostLost)
            {
                _bridge.ClearCoopState();
                if (_session.HostAway)
                {
                    // Same release, truthful wording: the link is fine, the host is parked at
                    // the main menu or a load screen (HostAwayRule).
                    _bridge.PostChatLine("Host has left the world (main menu / loading) - playing solo until they return.");
                    Log?.Invoke("host away past host_away_timeout_ms - released every plugin-side lock, playing solo");
                }
                else
                {
                    _bridge.PostChatLine("Host connection lost - playing solo, will rejoin automatically.");
                    Log?.Invoke("host connection lost - released every plugin-side lock, playing solo");
                }
            }
            else
            {
                _bridge.PostChatLine("Host connection restored - rejoining.");
                Log?.Invoke("host connection restored - rejoining");
            }
        }

        if (hostLost)
        {
            // The session's own RunAsync loop (a separate task, started once in this class's
            // own RunAsync) keeps re-Helloing on its own cadence regardless of whether this
            // pump runs at all — nothing here needs to drive that. LastLocalState/PublishLocalState
            // already ran above, so a returning host sees fresh state the instant it is back.
            LastWriteSucceeded = true;
            return;
        }

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

        // Publish an explicit local role proof rather than inferring host from the absence of
        // the host-party-roster valid bit (which is also false before a client's first roster,
        // when the channel is disabled, or during teardown). The packed field is a level and
        // is refreshed every pump; ClearCoopState zeros it on disconnect/host-loss.
        _bridge.SetLocalSessionRole(
            sessionActive: _session.IsConnected,
            isHost: _session.IsConnected && _session.IsEnemyAuthority);

        // Each channel is gated on the negotiated capability set: a mod difference that
        // would corrupt a channel disables that channel for the session (a different
        // globals catalogue drops world state; a different save drops enemies/doors/items)
        // while everything unaffected keeps working. Full set until a peer is judged.
        SessionCapabilities caps = _session.Capabilities;

        PumpBehaviourOverrides();

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

        // ⚠ BEFORE the hide filter below, and against the UNFILTERED `peers` list on purpose.
        // "One player races, everybody else waits" needs to see the racer, and the very next
        // statement is what removes them from `visible` — a lock computed from the filtered
        // list could never begin. See MinigameLockRule and docs/research/minigames.md §24.
        PumpMinigameLock(local, peers);

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

        // A story-forced solo segment (the Star Forge's Bastila and Malak duels — see
        // PlayerState.FlagSoloSegment, SoloSegmentRule.ShouldHideAllPeers and
        // docs/research/star-forge-finale.md's product decision "the OWNER's fight, the other
        // human SPECTATES"): while the LOCAL player owns an active segment, every remote peer
        // is hidden from what this instance writes locally, so the duel script never sees a
        // second body. Triggered off the LOCAL flag, not a peer's own — unlike the minigame
        // and away filters above, this is not about whether a PEER opted out, it is about
        // whether THIS instance is mid-duel. coop.ini solo_segment_spectate, default ON
        // (live-verified 2026-08-28 - docs/research/star-forge-finale.md §12).
        SoloSegmentRule soloSegmentRule = new(Enabled: _settings.SoloSegmentSpectateEnabled);
        if (soloSegmentRule.ShouldHideAllPeers(local.IsInSoloSegment))
        {
            visible = [];
        }

        // Mask the per-avatar fields a mod mismatch makes unsafe BEFORE they reach the
        // game. A different appearance.2da means a replicated row index could paint the
        // wrong body or index off the end (a crash), so the appearance is cleared and the
        // phantom keeps its blueprint look; a cleared row and empty resrefs are exactly
        // what the plugin already treats as "leave it alone".
        visible = MaskAvatarFields(visible, caps);

        LastWriteSucceeded = _bridge.TryWriteRemotePeers(visible);

        if (_perfTrace is not null)
        {
            // ⚠ Measured to HERE, not to the end of the method. Everything above is the
            // real-time half — read the local player, publish it, sample the peers, write the
            // phantoms — and its cadence is what a player watching the other character
            // actually sees. Everything below is world state whose freshness nobody can see
            // frame to frame, so folding the two into one number would hide the only part of
            // the tick that has to stay cheap.
            _perfPositionPathMs = _perfClock.Elapsed.TotalMilliseconds;
            _perfTrace.NotePhase(PumpPhase.Position, _perfPositionPathMs, 0, 0);

            long perfNowMs = SessionClock.NowMs;
            foreach (RemotePeer peer in visible)
            {
                _perfTrace.NotePhantomWrite(peer.PeerId, peer.State.Position, perfNowMs);
            }
        }

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

        // And once more: a peer's galaxy-map departure taken this tick is a "the host is
        // transitioning" signal PumpTravel's door-refusal arm must see this tick, and its
        // replay must beat the slow path's reading of the leader's mid-leg STUNT_* module.
        _transitionPump.PumpGalaxyDeparture(local, peers, _changedModule, _moduleSettledAtMs);

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

        // Drains KotorCoop.Plugin's item-template-spawn ring (docs/research/item-identity.md
        // §13) BEFORE anything below samples an item resref, for the same "order matters"
        // reason PumpContainerLoot's own comment (further down) gives for THAT ordering: a
        // resref this call learns feeds TryGetItemResref, which ScriptItemGrantPump's diff,
        // DialoguePump's reward diff, AND (2026-08-28) PartyPump's companion-equipment read
        // (KotorGameBridge.ReadEquippedResref, via ReadPartyMembers(includeEquipment: true) a
        // few lines below in both the fast- and slow-pump branches) all read through —
        // recording it a tick late would mean a companion re-equipped THIS tick still
        // publishes its OLD gear (or an empty "leave it alone" placeholder) on the one sample
        // that matters, exactly the mid-session propagation gap
        // docs/research/companion-state-sync.md's 2026-08-28 round observed live (an 8-plus-
        // minute stall, since ReadEquippedResref used to skip this cache entirely — see that
        // method's own remarks). Cheap when nothing is new: one dword read, unconditionally,
        // every tick, regardless of which capabilities/settings are on — the same "always keep
        // the cache warm" treatment the tag/address caches already get.
        PerfPhaseStart();
        _bridge.DrainItemTemplateSpawns();
        PerfPhaseEnd(PumpPhase.SpawnRing);

        // The class-grant relay (docs/research/class-and-power-grants.md): announce a class a
        // script just added to this character, apply a peer's to it. Every tick because the
        // apply is a request + ack loop against the plugin; a no-op (one dword read) when
        // nothing is pending, and nothing at all when class_grant_sync is off.
        _classGrantPump.Pump(local, peers, _changedModule, _moduleSettledAtMs);

        // Party-placed floor mines (docs/research/mines.md §14): publish a local placement or a
        // local removal, hand the plugin the host's canonical set, one spawn request at a time.
        // Nothing at all when placed_mine_sync is off.
        _placedMinePump.Pump(local);

        // The client-cast enemy-effect relay (docs/research/enemy-effects.md): report a cast on a
        // mirrored enemy to the host, hand a client's reported cast to the plugin. Nothing at
        // all when enemy_effect_sync is off.
        _enemyEffectPump.Pump(local, _changedModule);

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
            PerfPhaseStart();
            _combatPump.PumpEnemies(local, visible, caps);
            _livenessPump.PumpCreatureLiveness(local, caps);
            _combatPump.PumpPhantomDamage(caps);
            _combatPump.PumpPeerEffects(caps);

            // Companion damage TAKEN by a companion_local_combat-released companion, forwarded
            // to the host — same cadence as the enemy damage-dealt channel above, and for the
            // same reason (docs/research/companion-state-sync.md §13).
            _combatPump.PumpCompanionDamage(_settings.SyncCompanionDamage);
            _lootPump.PumpMaterialisedLoot(local.Module);

            // ⚠ PARTY RODE HERE TOO, ONCE — same shape as dialogue below. Left as the
            // fallback here (settings.PartyFastPump off) so turning the flag off is a true
            // rollback to exactly this cadence; the fast path below is where it moves to when
            // the flag is on. See docs/research/lag-audit.md's companion-mirror-latency
            // suspect.
            if (!_settings.PartyFastPump)
            {
                _partyPump.PumpParty(caps, local.Module);
                _partyPump.PumpPartyRoster(caps, local.Module);
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

            PerfPhaseEnd(PumpPhase.Combat);
        }

        // Dialogue, on the fast path: every poll tick (as fast as the position write above),
        // gated internally by DialogueTickGate rather than by this method's own cadence — a
        // genuine change is acted on the very next tick, and an unchanged read costs nothing
        // beyond the block read itself. coop.ini dialogue_fast_join=1. See
        // docs/research/dialogue-join-latency.md for why this is the biggest single latency
        // win available in the join path, and DialogueTickGate for the dedup rule.
        if (_settings.DialogueFastJoin)
        {
            PerfPhaseStart();
            _dialoguePump.PumpDialogue(local, caps);
            PerfPhaseEnd(PumpPhase.Dialogue);
        }

        // Party, on the fast path: every poll tick, same reasoning and same rollout shape as
        // dialogue above — PumpParty/PumpPartyRoster are cheap block reads/writes, not a world
        // scan, so nothing about running them every tick costs materially more than the 150 ms
        // cadence did. coop.ini party_fast_pump=1. See docs/research/lag-audit.md.
        if (_settings.PartyFastPump)
        {
            PerfPhaseStart();
            _partyPump.PumpParty(caps, local.Module);
            _partyPump.PumpPartyRoster(caps, local.Module);
            PerfPhaseEnd(PumpPhase.Party);
        }

        // ⚠ ORDER MATTERS, AND USED NOT TO. PumpContainerLoot moved here (off the heavy 1 Hz
        // pass, which is where it lived until a live loottake round on 2026-08-27 caught it
        // double-reporting) SPECIFICALLY so it runs immediately BEFORE the script-item sample
        // below, every tick. Before this fix the two ran on independently-clocked ~1s timers
        // (HeavyPumpIntervalMs for this call, ScriptItemGrantSampleIntervalMs for the one
        // below) with no ordering guarantee between them: a real container/corpse take could
        // have its new inventory row sampled and reported by ScriptItemGrantPump BEFORE this
        // pump's own throttled scan ever got a chance to detect the removal and feed
        // RecentlySyncedItems — a suppression entry recorded after the fact cannot un-report
        // something already published. Calling it every tick costs nothing extra:
        // CollectContainerLootRemovals self-throttles its own expensive scan to once a second
        // internally (`_nextLootScanTicks`) regardless of how often the caller asks, so this
        // is a cheap early-return on all but the one tick a second the scan is actually due —
        // it just now ALWAYS gets to run, and record, before the script-item pump can sample
        // past it. See docs/research/dialogue-consequences.md's script-item-sync section for
        // the live evidence (Controls 2/3).
        PerfPhaseStart();
        _lootPump.PumpContainerLoot(caps, local.Module);
        PerfPhaseEnd(PumpPhase.ContainerLoot);

        // Script-item grants: called every tick, same as the two fast-path calls above, so it
        // always reads _dialoguePump.IsOwningConversation as of THIS tick regardless of which
        // of the two branches above (or below, on the legacy cadence) last updated it. Cheap
        // when off (settings.ScriptItemSync) or between its own ~1s samples — see the pump's
        // own doc for why it is not itself tied to HeavyPumpIntervalMs. Rides the same
        // Dialogue capability the grant wire (DialogueGrantPacket) already needs, since a mod
        // difference that would corrupt one grant channel would corrupt the other identically.
        if ((caps & SessionCapabilities.Dialogue) != 0)
        {
            PerfPhaseStart();
            _scriptItemGrantPump.PumpScriptItemGrants(
                local.Module, _dialoguePump.IsOwningConversation, localReadable: LastReadSucceeded);
            PerfPhaseEnd(PumpPhase.ScriptItemGrant);
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
        double perfHeavyStartMs = _perfTrace is null ? 0 : _perfClock.Elapsed.TotalMilliseconds;
        PerfPhaseStart();

        // Report our in-world fingerprint (the session rate-limits the wire anyway). The
        // runtime difficulty comes from the plugin's live CClientOptions sample, not the
        // startup INI; it is warning-only and remains unknown when the read is unavailable.
        // The save identity and current module's GIT digest ride with the globals catalogue:
        // passing the LIVE module makes the GIT comparison per-module.
        byte? difficultyLevel = _bridge.TryReadDifficultyLevel(out byte sampledDifficulty)
            ? sampledDifficulty
            : null;
        // A changed load serial is first published without a ready module even if this tick's
        // player read succeeds. The next successful digest sample at that same serial can
        // advertise the module, proving the row was sampled after the load edge was observed.
        bool loadSerialReadable = _bridge.TryReadWorldLoadSerial(out uint sampledLoadSerial);
        uint loadSerial = loadSerialReadable
            ? sampledLoadSerial
            : _worldDigestReadinessState.LastSerial;
        (WorldDigestReadinessState nextReadiness, bool moduleReady) = WorldDigestReadinessRule.Observe(
            _worldDigestReadinessState, loadSerial, LastReadSucceeded && loadSerialReadable);
        _worldDigestReadinessState = nextReadiness;
        uint digestModuleGit = moduleReady ? _bridge.ModuleGitDigest(local.Module) : 0;
        WorldDigest digest = new WorldDigest(
            _bridge.GlobalsCatalogueDigest,
            _bridge.SaveIdentityDigest,
            local.Module,
            digestModuleGit,
            loadSerial,
            difficultyLevel);
        _session.PublishWorldDigest(ForCurrentReadAvailability(digest, moduleReady));

        if (!_session.IsEnemyAuthority && _session is ICapabilitySession capabilitySession)
        {
            _partyPoolLoadGateState = PartyPoolLoadGateRule.ObserveInitialWorldAlignment(
                _partyPoolLoadGateState,
                ForCurrentReadAvailability(digest, moduleReady),
                capabilitySession.RemoteWorldDigest);
        }

        if (_session.IsEnemyAuthority && moduleReady)
        {
            _partyPoolHostLoadPending = false;
        }
        bool partyPoolLocalWorldReady = LastReadSucceeded
            && !_bridge.IsWorldBetweenLives
            && loadSerialReadable
            && moduleReady
            && !_partyPoolHostLoadPending
            && (_session.IsEnemyAuthority || _partyPoolLoadGateState.HasInitialAlignedWorld);
        _partyPoolApplyBlocked = !LastReadSucceeded
            || _bridge.IsWorldBetweenLives
            || !loadSerialReadable
            || _partyPoolHostLoadPending
            || _partyPoolClientLoadQuarantined
            || _partyPoolHostKnownUnavailable
            || (!_session.IsEnemyAuthority && !_partyPoolLoadGateState.HasInitialAlignedWorld);

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
            _partyPump.PumpProgression(caps, local.Module);
            _partyPump.PumpCompanionExperience(caps);
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

        // Enabled by default (coop.ini mine_sync); see docs/research/mines.md §9.
        if (_settings.MineSync)
        {
            _worldStatePump.PumpMines(caps);
        }

        // Off by default (coop.ini local_var_sync); see docs/research/quest-sync.md §8.3.
        if (_settings.LocalVarSync)
        {
            _localVarPump.Pump(caps);
        }

        // PumpContainerLoot itself moved to the fast per-tick path above, ahead of the
        // script-item-grant sample — see that call site's own doc for why.

        // Off by default (coop.ini party_pool_sync). Mode 1 is the original op-log
        // (docs/research/party-pool-sync.md); mode 2 is the host-authoritative redesign
        // (docs/research/shared-inventory-v2.md) — mutually exclusive by construction, so the
        // two never run against the same engine at once.
        switch (_settings.EffectivePartyPoolSyncMode)
        {
            case 1:
                if (partyPoolLocalWorldReady && !_partyPoolClientLoadQuarantined
                    && !_partyPoolHostKnownUnavailable)
                {
                    _partyPoolPump.Pump(caps, local.Module);
                }
                break;
            case 2:
                _partyPoolPumpV2.Pump(
                    caps,
                    local.Module,
                    localWorldReady: partyPoolLocalWorldReady,
                    clientLoadQuarantined: _partyPoolClientLoadQuarantined
                        || _partyPoolHostKnownUnavailable
                        || (!_session.IsEnemyAuthority && !_partyPoolLoadGateState.HasInitialAlignedWorld));
                break;
        }

        // Off by default (coop.ini store_sync) — same mode gating as party_pool_sync above.
        switch (_settings.EffectiveStoreSyncMode)
        {
            case 1:
                _storePump.Pump(local.Module, caps);
                break;
            case 2:
                _storePumpV2.Pump(caps, local.Module);
                break;
        }

        // Last statement in the method on purpose: every heavy sub-pass above is inside the
        // window, and the only paths that skip it are the early returns ABOVE the heavy gate,
        // which by definition ran no heavy pass to time.
        _perfTrace?.NoteHeavyPass(_perfClock.Elapsed.TotalMilliseconds - perfHeavyStartMs);
        PerfPhaseEnd(PumpPhase.Heavy);
    }

    /// <summary>
    /// Pushes <see cref="ICapabilitySession.Overrides"/> into the plugin's shared block
    /// whenever it changes — docs/research/mod-compatibility.md's "Behaviour-flag negotiation"
    /// gap, closed 2026-08-27. Cheap every tick: <see cref="BehaviourOverrideEncoding.Encode"/>
    /// is five null checks and a couple of ORs, and the bridge write only fires on a genuine
    /// change (a client's Welcome just landed, or a fresh <see cref="CoopAgent"/> after a
    /// reconnect). <see cref="_lastBehaviourOverrides"/> starts at (0, 0) — "no overrides" — so
    /// a session that never adopts anything (a host, or a client whose behaviour matched)
    /// never writes at all, which is exactly the block's own post-teardown/never-written state
    /// (see <c>KotorGameBridge.SessionScopedBlockLevels</c>).
    /// </summary>
    private void PumpBehaviourOverrides()
    {
        (uint mask, uint values) = BehaviourOverrideEncoding.Encode(_session.Overrides);

        if (mask == _lastBehaviourOverrides.Mask && values == _lastBehaviourOverrides.Values)
        {
            return;
        }

        _lastBehaviourOverrides = (mask, values);
        _bridge.SetBehaviourOverrides(mask, values);
    }

    /// <summary>
    /// Resolves the five player-count scaling percents for this tick (the host's adopted
    /// settings where the session has them, else this install's; the player count is this
    /// instance plus every peer in the list) and, on a change, logs one line and pushes the
    /// plugin-side pair (enemy health, enemy damage) into the shared block — the same
    /// change-only idiom <see cref="PumpBehaviourOverrides"/> uses. Cheap every tick: four
    /// integer resolves and one compare. See docs/research/player-count-scaling.md.
    /// </summary>
    private void PumpPlayerCountScaling(int playerCount)
    {
        // A count that falls is trusted only after it holds for PlayerCountSettleRule.FallSettleMs:
        // the joiner's arrival flapped 2 -> 1 -> 2 live, and a one-player reading at an enemy's
        // first sighting would have locked it at 100% for the module.
        playerCount = _playerCountSettle.Observe(playerCount, Environment.TickCount64);

        if (_scaling.Update(_session.Overrides, playerCount))
        {
            _log($"scaling: {_scaling.Describe()}");
        }

        if (_scaling.Generation == _lastScalingGenerationPushed)
        {
            return;
        }

        _lastScalingGenerationPushed = _scaling.Generation;
        _bridge.SetPlayerCountScaling(
            _scaling.PackCombatWord(), _scaling.PackEconomyWord(), _scaling.PackPricesWord());
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
        KotorCoop.Protocol.PartyPoolOpKind Kind, KotorCoop.Protocol.PartyPoolStackKey Key, ushort Count,
        uint IntentPeerId = 0, uint IntentSerial = 0, uint? WorldLoadSerial = null);

    /// <summary>Completes a host-side v2 intent after the plugin has answered the engine apply.
    /// The wire ACK is sent only for this result, so a queued-but-refused spawn cannot leave the
    /// client believing it was accepted.</summary>
    public void CompletePartyPoolIntentV2(uint peerId, uint intentSerial, bool accepted)
    {
        _partyPoolPumpV2.CompleteHostIntentV2(peerId, intentSerial);
        _session.CompletePartyPoolIntentV2(peerId, intentSerial, accepted);
    }

    /// <summary>Clear module-scoped readiness when the local player read failed. The world
    /// load serial can still advance during teardown, but the retained player row and module
    /// must not be advertised as a newly loaded, ready world.</summary>
    internal static WorldDigest ForCurrentReadAvailability(WorldDigest digest, bool readSucceeded) =>
        readSucceeded ? digest : digest with { Module = string.Empty, ModuleGit = 0 };

    /// <summary>Quarantine shared-pool replication across an observed native load. Save
    /// identity divergence alone is deliberately ignored: ordinary saves are allowed to
    /// diverge during play. The client quarantine is sticky until SessionManager replaces
    /// this agent after host-world adoption.</summary>
    private void ObservePartyPoolLoadGate(PlayerState local)
    {
        if (!_bridge.TryReadWorldLoadSerial(out uint serial))
        {
            _partyPoolApplyBlocked = true;
            return;
        }
        bool readable = LastReadSucceeded && !_bridge.IsWorldBetweenLives;

        if (_session.IsEnemyAuthority)
        {
            (PartyPoolLoadGateState next, bool hold) = PartyPoolLoadGateRule.ObserveHost(
                _partyPoolLoadGateState,
                serial,
                readable && !string.IsNullOrWhiteSpace(local.Module));
            bool loadEdge = _partyPoolLoadGateState.HasLocalSerial
                && _partyPoolLoadGateState.LocalSerial != serial;
            _partyPoolLoadGateState = next;
            if (loadEdge)
            {
                _partyPoolPumpV2.ResetForWorldReplacement(client: false);
                _session.NotifyWorldLoadEdge(serial);
            }

            _partyPoolHostLoadPending = next.ClientHeldAfterLoad;
            _partyPoolHostKnownUnavailable = false;
            _partyPoolClientLoadQuarantined = false;
            _partyPoolApplyBlocked = hold;
            return;
        }

        WorldDigest remoteHost = _session is ICapabilitySession capability
            ? capability.RemoteWorldDigest
            : WorldDigest.Empty;
        (PartyPoolLoadGateState clientNext, bool clientHold) = PartyPoolLoadGateRule.ObserveClient(
            _partyPoolLoadGateState,
            serial,
            remoteHost,
            readable);
        bool localEdgeClient = _partyPoolLoadGateState.HasLocalSerial
            && _partyPoolLoadGateState.LocalSerial != serial;
        bool remoteEdgeClient = !remoteHost.IsEmpty
            && _partyPoolLoadGateState.HasRemoteSerial
            && _partyPoolLoadGateState.RemoteSerial != remoteHost.LoadSerial;
        _partyPoolLoadGateState = clientNext;
        if (localEdgeClient || remoteEdgeClient)
        {
            _partyPoolPumpV2.ResetForWorldReplacement(client: true);
        }

        _partyPoolClientLoadQuarantined = clientNext.ClientHeldAfterLoad;
        _partyPoolHostKnownUnavailable = !remoteHost.IsEmpty && string.IsNullOrWhiteSpace(remoteHost.Module);
        _partyPoolHostLoadPending = false;
        _partyPoolApplyBlocked = clientHold;
    }

    /// <summary>
    /// Called by whatever already arbitrates the single-slot staging-cheat channel, at most
    /// once per ITS tick, so an automatic party-pool apply is submitted through the same call
    /// path a human's own party-pool console verb would take and can never be filed alongside
    /// one in flight. Mirrors <see cref="TryDequeuePendingLoot"/> exactly.
    /// </summary>
    public bool TryDequeuePendingPartyPoolApply(out PendingPartyPoolApply next)
    {
        if (_partyPoolApplyBlocked)
        {
            next = default;
            return false;
        }

        return _partyPoolPump.TryDequeuePendingApply(out next);
    }

    /// <summary>One store buy/sell this instance's own engine still owes for a specific store
    /// (<see cref="StoreStackKey.StoreObjectId"/>), queued for the automatic apply path — see
    /// <see cref="PendingPartyPoolApply"/>'s own remarks for why this is public.</summary>
    public readonly record struct PendingStoreApply(
        KotorCoop.Protocol.StoreOpKind Kind, KotorCoop.Protocol.StoreStackKey Key, ushort Count);

    /// <summary>
    /// Called by whatever already arbitrates the single-slot staging-cheat channel, at most once
    /// per ITS tick — mirrors <see cref="TryDequeuePendingPartyPoolApply"/> exactly.
    /// </summary>
    public bool TryDequeuePendingStoreApply(out PendingStoreApply next)
    {
        return _storePump.TryDequeuePendingApply(out next);
    }

    /// <summary>v2 (docs/research/shared-inventory-v2.md): the same single-slot arbitrated drain
    /// as <see cref="TryDequeuePendingPartyPoolApply"/>, reusing the identical
    /// <see cref="PendingPartyPoolApply"/> record type — a v2 apply is structurally the same
    /// (Kind, Key, Count) request into the same staging-cheat primitive, so no new public type
    /// was needed anywhere in this assembly's surface.</summary>
    public bool TryDequeuePendingPartyPoolApplyV2(out PendingPartyPoolApply next)
    {
        if (_partyPoolApplyBlocked)
        {
            next = default;
            return false;
        }

        return _partyPoolPumpV2.TryDequeuePendingApply(out next);
    }

    /// <summary>v2 mirror of <see cref="TryDequeuePendingStoreApply"/>.</summary>
    public bool TryDequeuePendingStoreApplyV2(out PendingStoreApply next)
    {
        return _storePumpV2.TryDequeuePendingApply(out next);
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
    /// Records that THIS instance's own party-pool channel is about to add
    /// <paramref name="resref"/> to the shared party pool — a peer's accepted intent being
    /// applied on the host, this instance's own reconcile toward the host's snapshot, or a
    /// human's <c>pooladd</c>. Feeds the SAME <see cref="RecentlySyncedItems"/> ledger
    /// <see cref="NoteLocalItemGiven"/> does, for the same reason and with the same one-row
    /// budget.
    ///
    /// ⚠ 2026-08-29, live-caught. This looks like it should not be necessary — the party pool is
    /// not the PC's inventory — but it is, and the chain is documented elsewhere in this
    /// repository: <c>CreateItemOnObject(resref, aPartyMember, n)</c> routes through
    /// <c>CSWSCreature::AcquireItem</c> into the repository <c>GetItemRepository(creature, 1)</c>
    /// returns, and for a live party member that IS the shared party pool
    /// (docs/research/shared-inventory-v2.md §15.1). So a pool add lands squarely inside what
    /// <c>bridge.ReadPossessedItems(localPlayer)</c> sweeps, and
    /// <see cref="Pumps.ScriptItemGrantPump"/> — which exists precisely to catch an item a SCRIPT
    /// put in the PC's inventory — read our own pool applies as script grants and re-broadcast
    /// them. Live, at connect, on shipped defaults plus <c>party_pool_sync=2</c>:
    ///
    /// <code>
    /// client 19:00:16  party pool: local add of 1x 'g_w_blstrpstl001' ... requesting (serial 1)
    /// host   19:00:17  party pool v2: submitting add of 1x 'g_w_blstrpstl001' ... added to the party pool
    /// host   19:00:18  script item grant: 2 new item(s) in the local PC's inventory outside dialogue:
    ///                  g_w_blstrpstl001, g_a_clothes01
    /// client 19:00:18  materialised loot: submitting 'g_w_blstrpstl001' ... gave to 0x7FFFFFFF
    /// </code>
    ///
    /// — one deposit turned into a second copy on the other instance, through a completely
    /// different channel. <see cref="Pumps.ScriptItemGrantPump"/>'s own class doc already names
    /// this exact class of item ("a peer's own grant applied via giveloot ... must never be
    /// re-broadcast as if a script had just created it"); the party-pool channel was simply never
    /// wired into the ledger that says so.
    /// </summary>
    public RecentlySyncedItems.Reservation NoteLocalPartyPoolAdd(string resref)
    {
        if (string.IsNullOrEmpty(resref))
        {
            return default;
        }

        return _recentlySyncedItems.RecordReservation(resref, Environment.TickCount64);
    }

    /// <summary>Releases the exact echo-suppression unit reserved for a party-pool add when its
    /// staging-cheat result is refused or times out before creating an item.</summary>
    public void UnnoteLocalPartyPoolAdd(RecentlySyncedItems.Reservation reservation)
    {
        _recentlySyncedItems.TryCancel(reservation);
    }

    /// <summary>The innermost frame of an exception's stack, one line, for the pump's failure
    /// log — "Type.Method in file:line N" when symbols are present, the method alone otherwise.</summary>
    internal static string TopFrame(Exception ex)
    {
        var frame = new System.Diagnostics.StackTrace(ex, fNeedFileInfo: true).GetFrame(0);
        if (frame is null)
        {
            return "?";
        }

        string method = frame.GetMethod() is { } m
            ? $"{m.DeclaringType?.Name}.{m.Name}"
            : "?";
        string? file = frame.GetFileName();
        return file is null
            ? method
            : $"{method} in {Path.GetFileName(file)}:line {frame.GetFileLineNumber()}";
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
