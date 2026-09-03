using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// Roadmap item 14's session-save/rejoin machinery: watching this instance's own save-identity
/// digest, serving the host's world to a rejoining client, running the rejoin itself, and the
/// <c>loadgame</c> live-test primitive.
/// </summary>
public sealed partial class CoopCommandDispatcher
{
    /// <summary>Roadmap item 14 phase 1's live wiring — null when no game directory was
    /// supplied, which every existing caller/test that omits the trailing constructor
    /// parameter falls back to: <see cref="MaintainSessionSave"/> and
    /// <see cref="CaptureCharacterOnDisconnect"/> are then no-ops rather than throwing.</summary>
    private readonly SessionSaveWatch? _sessionSaveWatch =
        string.IsNullOrEmpty(gameDirectory) ? null : new SessionSaveWatch(gameDirectory);

    /// <summary>
    /// coop.ini's <c>session_save_host_refusal</c>, cached the same way
    /// <see cref="CoopCommandDispatcher.StatusToastsEnabled"/> is. OFF by default — see
    /// <see cref="CoopConfig.SessionSaveHostRefusal"/>'s own doc comment for the live-verified
    /// false-positive (docs/research/shared-state-audit.md §2) this gate exists to prevent: an
    /// ordinary module transition, not just an actual different-save load, rewrites the watched
    /// autosave file and would otherwise disconnect the host from its own session.
    /// </summary>
    private bool? _sessionSaveHostRefusalEnabled;

    private bool SessionSaveHostRefusalEnabled =>
        _sessionSaveHostRefusalEnabled ??= loadConfig?.Invoke()?.SessionSaveHostRefusal ?? false;

    /// <summary>coop.ini <c>client_load_rejoin</c> — see <see cref="CoopConfig.ClientLoadRejoin"/>'s
    /// own doc comment. OFF by default.</summary>
    private bool? _clientLoadRejoinEnabled;

    private bool ClientLoadRejoinEnabled =>
        _clientLoadRejoinEnabled ??= loadConfig?.Invoke()?.ClientLoadRejoin ?? false;

    /// <summary>coop.ini <c>host_load_readopt</c> — see <see cref="CoopConfig.HostLoadReadopt"/>'s
    /// own doc comment. OFF by default.</summary>
    private bool? _hostLoadReadoptEnabled;

    private bool HostLoadReadoptEnabled =>
        _hostLoadReadoptEnabled ??= loadConfig?.Invoke()?.HostLoadReadopt ?? false;

    /// <summary>
    /// coop.ini <c>auto_adopt_world</c> — ON by default, unlike its two neighbours above,
    /// because it is the gate on the thing a joiner almost always wants: a world that differs
    /// from the host's gets ADOPTED instead of played degraded.
    ///
    /// ⚠ THIS TOOK OVER <see cref="RejoinTrigger.SaveMismatch"/> FROM
    /// <see cref="ClientLoadRejoinEnabled"/>, and that is a deliberate behaviour change rather
    /// than a tidy-up. The two triggers are different questions that happened to share an
    /// answer: <c>client_load_rejoin</c> is "when I load a save mid-session, put me back in the
    /// host's world", while this is "when my world simply is not the host's, fix it". A joiner
    /// who has turned the first off — they never load mid-session — was silently also refusing
    /// the second, which is the only mechanism that could ever have admitted them to the
    /// object-id-keyed channels at all. Both default ON, so nothing changes for an install that
    /// has not opted out of either.
    /// </summary>
    private bool? _autoAdoptWorldEnabled;

    private bool AutoAdoptWorldEnabled =>
        _autoAdoptWorldEnabled ??= loadConfig?.Invoke()?.AutoAdoptWorld ?? true;

    /// <summary>coop.ini <c>join_mode</c> — see <see cref="JoinModeRule"/>. In the default
    /// <see cref="JoinMode.Auto"/> the sidecar is THE co-op character, so a rejoin carries it
    /// unconditionally instead of weighing it against this install's own most recent save.</summary>
    private JoinMode? _joinMode;

    private JoinMode JoinModeSetting =>
        _joinMode ??= loadConfig?.Invoke()?.JoinMode ?? JoinMode.Auto;

    /// <summary>Roadmap item 14 phase 2's rejoin decision state — see
    /// <see cref="ClientLoadRejoinRule"/>. Best-effort threading: written synchronously from
    /// this dispatcher's own tick when a rejoin starts, and once more (back to Idle) by the
    /// background rejoin task's own completion — the two never race in practice because the
    /// tick thread never writes it again until <see cref="TriggerRejoin"/> is called, which
    /// only happens after a fresh verdict, itself gated by this same field already reading
    /// <see cref="RejoinPhase.InFlight"/>.</summary>
    private RejoinDecisionState _rejoinState = RejoinDecisionState.Idle;

    /// <summary>See <see cref="PersistentSaveMismatchRule"/> — the level-driven fallback trigger
    /// for a save mismatch that a debug-cheat module warp produces with no edge on either
    /// <see cref="RejoinTrigger.OwnLoad"/> or <see cref="RejoinTrigger.HostReload"/>'s own
    /// signal. Reset alongside <see cref="_rejoinState"/> so a fresh session never inherits a
    /// cooldown timer started against a previous one.</summary>
    private MismatchRejoinWatchState _mismatchRejoinState = MismatchRejoinWatchState.Initial;

    /// <summary>Edge state for the one-line <see cref="CoopClient.SaveLineageAbsorbing"/>
    /// announcement.</summary>
    private bool _saveLineageAnnounced;

    /// <summary>See <see cref="WipeRecoveryRule"/> — the host's reload from the game-over menu
    /// after a session wipe, which neither classifier can see. Reset with the two above.</summary>
    private WipeRecoveryState _wipeRecoveryState = WipeRecoveryState.Initial;

    /// <summary>See <see cref="HostWorldReplacedRule"/> — a host load whose serial edge never
    /// found a digest change to correlate with (live 2026-09-02: an in-world reload of the
    /// staged save). Reset with the three above, and disarmed by <see cref="TriggerRejoin"/>.</summary>
    private HostWorldReplacedState _hostWorldReplacedState = HostWorldReplacedState.Initial;

    private Task? _rejoinTask;

    /// <summary>The in-flight <c>loadgame</c> poll, if any — see <see cref="TriggerMidSessionLoad"/>.
    /// Held only so the fire-and-forget <c>Task.Run</c> is not flagged as unobserved; nothing
    /// reads it back (a second <c>loadgame</c> before the first settles just starts a second
    /// poll racing the first for the SAME status field, which is a harmless double-read, not a
    /// double-fire — the plugin's own serial guard is what actually prevents a double-apply).</summary>
    private Task? _loadGameTask;

    /// <summary>
    /// The host's own world-serve listener for roadmap item 14's mid-session rejoin gates —
    /// see <see cref="MaintainWorldServing"/>'s own doc comment for the bug this closes.
    /// Held only so the fire-and-forget <c>Task.Run</c> is not flagged as unobserved, the same
    /// pattern <see cref="_loadGameTask"/> already uses; cancellation (via
    /// <see cref="_worldServeCts"/>), not awaiting this task, is what actually stops it.
    /// </summary>
    private Task? _worldServeTask;

    private CancellationTokenSource? _worldServeCts;

    /// <summary>
    /// Roadmap item 14 phases 1 AND 2: watches this instance's own save-identity digest (and,
    /// since phase 2, the world-load edge alongside it) once per tick and reacts per role.
    ///
    /// Phase 1, unchanged: a CLIENT saving mid-session gets its progressed character captured
    /// into the "resume tomorrow" sidecar (<see cref="SessionSaveWatch.ObserveClient"/>, on
    /// EVERY digest change — a save and a load alike, deliberately: this already captures the
    /// character out of whatever the client just loaded too, for free).
    ///
    /// Phase 2, new: <see cref="SessionSaveWatch.ObserveOwnVerdict"/>/<see cref="SessionSaveWatch.ObserveRemoteVerdict"/>
    /// classify the SAME edges into <see cref="SaveOrLoadVerdict.Load"/> vs
    /// <see cref="SaveOrLoadVerdict.Save"/> — see <c>WorldLoadRule</c>/<c>SaveOrLoadClassifier</c>.
    /// A HOST's own Load verdict now drives the (still gated) legacy refusal INSTEAD of the
    /// raw digest edge — closing the false-positive where an ordinary transition tripped the
    /// same digest — and, when <c>host_load_readopt</c> is on, announces re-adoption instead. A
    /// CLIENT's own Load verdict, or a Load verdict on the HOST's REMOTE digest (received over
    /// the wire, <see cref="ICoopSession.RemoteWorldDigest"/>), triggers <see cref="TriggerRejoin"/>
    /// when its gate is on.
    /// </summary>
    private void MaintainSessionSave()
    {
        if (_sessionSaveWatch is null)
        {
            return;
        }

        if (manager.Current is not { } session)
        {
            _sessionSaveWatch.Reset();
            _rejoinState = RejoinDecisionState.Idle;
            _mismatchRejoinState = MismatchRejoinWatchState.Initial;
            _saveLineageAnnounced = false;
            _wipeRecoveryState = WipeRecoveryState.Initial;
            _hostWorldReplacedState = HostWorldReplacedState.Initial;
            return;
        }

        uint digest = bridge.SaveIdentityDigest;
        long nowMs = SessionClock.NowMs;

        // ⚠ null, not 0, while the bridge has no plugin block to read it from — see
        // SessionSaveWatch.ObserveOwnVerdict(uint, uint?, long) for the four-rejoin loop a 0
        // fed through here produced live.
        uint? loadSerial = bridge.TryReadWorldLoadSerial(out uint serialNow) ? serialNow : null;

        if (session is CoopHost)
        {
            // Legacy phase-1 edge: still computed (SessionSaveWatchTests pins its pure
            // behaviour), no longer what gates the action below.
            _sessionSaveWatch.ObserveHost(digest);

            SaveOrLoadVerdict verdict = _sessionSaveWatch.ObserveOwnVerdict(digest, loadSerial, nowMs);
            if (verdict == SaveOrLoadVerdict.Load)
            {
                Log?.Invoke("session-save: host-side load verdict"
                    + (HostLoadReadoptEnabled ? " - announcing re-adoption"
                        : SessionSaveHostRefusalEnabled ? " - refusing"
                        : " - NOT acting (host_load_readopt=0, session_save_host_refusal=0)"));

                if (HostLoadReadoptEnabled)
                {
                    // Nothing further to DO from the host's own instance — re-adoption is a
                    // CLIENT action, and every connected client already sees this edge for
                    // itself the moment its own WorldDigestPacket poll runs
                    // ObserveRemoteVerdict (LoadSerial rides that packet unmodified). This
                    // line is purely so the host's own player knows what is about to happen.
                    Reply("host loaded a save - everyone re-joins");
                }
                else if (SessionSaveHostRefusalEnabled)
                {
                    Reply(SessionSaveWatch.HostRefusalMessage);
                    manager.Disconnect();
                }
            }
        }
        else if (session is CoopClient client)
        {
            string? currentSave = SaveDigest.FindSessionSave(gameDirectory);
            string? outcome = _sessionSaveWatch.ObserveClient(digest, currentSave);
            if (outcome is not null)
            {
                Reply(outcome);
            }

            // One line per episode: the engine's own transition autosave gives each side its
            // own counters, so after a real door the raw identities differ for good while the
            // world is still shared (SaveLineage). Say so once, then stay quiet until it clears.
            if (client.SaveLineageAbsorbing != _saveLineageAnnounced)
            {
                _saveLineageAnnounced = client.SaveLineageAbsorbing;
                if (_saveLineageAnnounced)
                {
                    Reply("save identity differs after a save on one side, with no load since the two last agreed - same world, not rejoining");
                }
            }

            SaveOrLoadVerdict ownVerdict = _sessionSaveWatch.ObserveOwnVerdict(digest, loadSerial, nowMs);
            WorldDigest hostDigest = client.RemoteWorldDigest;
            SaveOrLoadVerdict remoteVerdict = _sessionSaveWatch.ObserveRemoteVerdict(
                hostDigest.SaveIdentity, hostDigest.LoadSerial, nowMs, out bool hostLoadEdge);

            // The wipe watch runs every tick regardless of the verdicts below: its latch must
            // be armed during the ~13 s both parties lie wiped before the game-over teardown,
            // and it fires on the host's reappearance, which neither classifier reports.
            PlayerState? localState = bridge.TryReadLocalState(out PlayerState localNow) ? localNow : null;
            PlayerState? hostState = null;
            IReadOnlyList<RemotePeer> peersNow = session.GetRemotePeers();
            for (int i = 0; i < peersNow.Count; i++)
            {
                if (peersNow[i].PeerId == CoopHost.HostPeerId)
                {
                    hostState = peersNow[i].State;
                    break;
                }
            }

            (bool hostBackAfterWipe, WipeRecoveryState nextWipeState) =
                WipeRecoveryRule.Observe(_wipeRecoveryState, localState, hostState);
            _wipeRecoveryState = nextWipeState;

            // The serial-only fallback for the host's load, same lifecycle as the wipe watch:
            // arms on the raw edge, fires once the host stands in a named module. Runs every
            // tick too, so an edge that lands mid-teardown is not lost behind another branch.
            (bool hostWorldReplaced, HostWorldReplacedState nextReplacedState) =
                HostWorldReplacedRule.Observe(_hostWorldReplacedState, hostLoadEdge, hostState);
            _hostWorldReplacedState = nextReplacedState;

            if (ownVerdict == SaveOrLoadVerdict.Load)
            {
                Reply("you loaded a save - rejoining the host's world");
                TriggerRejoin(RejoinTrigger.OwnLoad);
            }
            else if (remoteVerdict == SaveOrLoadVerdict.Load)
            {
                Reply("host loaded a save - rejoining their world");
                TriggerRejoin(RejoinTrigger.HostReload);
            }
            else if (hostBackAfterWipe)
            {
                Reply("the host is back in the world after the wipe - rejoining their world");
                TriggerRejoin(RejoinTrigger.WipeRecovery);
            }
            else if (hostWorldReplaced)
            {
                Reply("the host's world was reloaded - rejoining their world");
                TriggerRejoin(RejoinTrigger.HostReload);
            }
            else
            {
                // Neither classifier saw an EDGE this tick (a debug-cheat module warp never
                // produces one on either side's digest). Watch the compared VERDICT itself
                // instead — a level, not an edge — so a save mismatch that a real Load's
                // edge-based triggers above will never see still gets the same self-heal
                // action the "rejoin to adopt" message promises, rather than sitting degraded
                // for the rest of the session.
                bool saveMismatchNow = client.Compatibility?.Notes
                    .Any(n => n.Code == CompatibilityCode.Save) ?? false;

                (bool shouldTrigger, MismatchRejoinWatchState nextMismatchState) =
                    PersistentSaveMismatchRule.Observe(
                        _mismatchRejoinState, saveMismatchNow,
                        _rejoinState.Phase == RejoinPhase.InFlight, nowMs);
                _mismatchRejoinState = nextMismatchState;

                if (shouldTrigger)
                {
                    Reply("your world still differs from the host's - rejoining automatically");
                    TriggerRejoin(RejoinTrigger.SaveMismatch);
                }
            }
        }
    }

    /// <summary>
    /// Roadmap item 14's mid-session rejoin gates need the HOST to actually SERVE its world
    /// over <see cref="WorldTransfer"/> — <see cref="TriggerRejoin"/>'s background task calls
    /// <see cref="SessionManager.RejoinAfterLoadAsync"/>, which fetches the host's world via
    /// <see cref="WorldAdoption.TryAdoptAsync"/>/<see cref="WorldTransfer.FetchAsync"/> exactly
    /// the way the standalone <c>agent adopt</c>/<c>agent serve-world</c> CLI pair already
    /// does — but until this method existed, nothing in a live <c>agent run --host</c> session
    /// ever served the world at all: there was never a listener on the other end, so every
    /// rejoin fetch was refused outright, not raced (see docs/research/session-save.md).
    /// Starting the listener HERE, once, for the life of the host session — rather than only
    /// around a reload — and reading the CURRENT session save fresh on every accepted
    /// connection removes the race entirely rather than narrowing its window.
    ///
    /// Gated the same way every other roadmap item 14 feature is — off unless a coop.ini gate
    /// that could actually need it is on — because it opens a new TCP listener on the session
    /// port, a real (if modest) new attack surface a host that never uses this feature should
    /// not carry. Either gate can trigger a CLIENT's own fetch (<see cref="ClientLoadRejoinEnabled"/>
    /// for the client's own load, <see cref="HostLoadReadoptEnabled"/> for the host's), and the
    /// host cannot see which gate any given connected client has set, so it serves whenever
    /// EITHER of its own is on.
    /// </summary>
    private void MaintainWorldServing()
    {
        // auto_adopt_world joins the OR for the same reason the other two are in it: a client
        // with it on will fetch this host's world, and the host cannot see which gates any
        // given client has set. It is ON by default, so in practice a host now serves its world
        // unless it has explicitly turned all three off — which is the behaviour a joiner who
        // has never played needs, since there is no other way for them to obtain a world at all.
        bool shouldServe = manager.Current is CoopHost
            && !string.IsNullOrEmpty(gameDirectory)
            && (ClientLoadRejoinEnabled || HostLoadReadoptEnabled || AutoAdoptWorldEnabled);

        if (!shouldServe)
        {
            if (_worldServeCts is { } stale)
            {
                stale.Cancel();
                stale.Dispose();
                _worldServeCts = null;
                _worldServeTask = null;
            }

            return;
        }

        if (_worldServeTask is not null)
        {
            return; // already running for this host session
        }

        if (manager.CurrentSettings is not { } settings)
        {
            return; // should not happen once Current is a CoopHost, but never assume it
        }

        string capturedGameDirectory = gameDirectory;
        uint passwordHash = SessionPassword.Hash(settings.Password);
        CancellationTokenSource cts = new();
        _worldServeCts = cts;

        _worldServeTask = Task.Run(async () =>
        {
            try
            {
                await WorldTransfer.ServeAsync(
                    () => BuildWorldSnapshot(capturedGameDirectory),
                    settings.Port,
                    passwordHash,
                    Log,
                    cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log?.Invoke($"session-save: world-serve listener failed - {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Runs one of the character-creation drive verbs (<c>newgame</c>, <c>chargen quick</c>,
    /// <c>chargen accept</c>) by asking the plugin to call the engine's own button handler.
    ///
    /// Synchronous by design, unlike <see cref="TriggerMidSessionLoad"/>: these are single
    /// engine calls the plugin makes on its next MainLoop frame, not module loads, so there is
    /// nothing to poll and nothing that could block this dispatcher's tick.
    /// </summary>
    private void DriveChargen(uint kind, int classIndex)
    {
        if (_kotor is null)
        {
            Reply("chargen: this agent is not attached to a KOTOR process (needs --kotor)");
            return;
        }

        // The diagnostic says what happened either way — a refusal is not an error here, it is
        // an answer ("character creation is not open", "not at the main menu").
        _kotor.TryDriveChargen(kind, classIndex, out string diagnostic);
        Reply(diagnostic);
    }

    /// <summary>Reads the CURRENT session save fresh — the world+identity pair a rejoining
    /// client should receive if it connects RIGHT NOW, never a snapshot captured when serving
    /// started. Same resolution <see cref="MaintainSessionSave"/>'s client branch already uses
    /// for its own capture (<see cref="SaveDigest.FindSessionSave"/>).</summary>
    private static (IReadOnlyList<WorldFile> World, uint SaveIdentity) BuildWorldSnapshot(string gameDirectory)
    {
        string? currentSave = SaveDigest.FindSessionSave(gameDirectory);
        if (currentSave is null)
        {
            return ([], 0);
        }

        IReadOnlyList<WorldFile> world = WorldTransfer.ReadSaveDirectory(currentSave);
        uint identity = SaveDigest.TryComputeSaveIdentity(currentSave, out uint computed) ? computed : 0;
        return (world, identity);
    }

    /// <summary>
    /// Roadmap item 14 phase 2's rejoin action, gated by <see cref="ClientLoadRejoinRule.Decide"/>
    /// (idempotent while a rejoin is already <see cref="RejoinPhase.InFlight"/>). Runs
    /// <see cref="SessionManager.RejoinAfterLoadAsync"/> in the background — network I/O and a
    /// polled engine load, neither of which may block this dispatcher's own tick — then, once
    /// the local load settles, calls <see cref="SessionManager.Reconnect"/> to reopen the coop
    /// socket against the same host. Carries whatever the phase-1 "resume tomorrow" sidecar
    /// currently holds as the character to bring in — <see cref="SessionSaveWatch.ObserveClient"/>
    /// already wrote it fresh this same tick for an own-load trigger.
    /// </summary>
    private void TriggerRejoin(RejoinTrigger trigger)
    {
        bool gateEnabled = trigger switch
        {
            RejoinTrigger.OwnLoad => ClientLoadRejoinEnabled,
            RejoinTrigger.HostReload => HostLoadReadoptEnabled,
            RejoinTrigger.WipeRecovery => HostLoadReadoptEnabled,
            // ⚠ auto_adopt_world, NOT client_load_rejoin — see AutoAdoptWorldEnabled's own doc
            // comment. This is "the client notices its own world is not the host's and adopts
            // the host's", which is a different question from "put me back after I load a
            // save", and had no gate of its own until it needed one for join_mode.
            RejoinTrigger.SaveMismatch => AutoAdoptWorldEnabled,
            _ => false,
        };

        (bool shouldStart, RejoinDecisionState next) = ClientLoadRejoinRule.Decide(_rejoinState, trigger, gateEnabled);
        _rejoinState = next;

        // Whatever fired, the host's pending serial edge (if any) belongs to this same load:
        // one host load, one rejoin. Disarmed even when the gate refuses, for the same reason.
        _hostWorldReplacedState = HostWorldReplacedRule.Disarm();

        if (!shouldStart)
        {
            if (!gateEnabled)
            {
                Log?.Invoke($"session-save: {trigger} rejoin trigger seen - NOT acting (gate off)");
            }

            return;
        }

        Log?.Invoke($"session-save: starting rejoin ({trigger})");

        (GffStruct Player, SidecarMetadata Metadata)? carried = null;
        if (ClientCharacterPersistence.TryReadSidecar(
            Path.Combine(gameDirectory, "saves"), out GffStruct sidecarPlayer, out SidecarMetadata sidecarMeta, out _))
        {
            carried = (sidecarPlayer, sidecarMeta);
        }

        _rejoinTask = Task.Run(async () =>
        {
            try
            {
                AdoptedWorld? adopted = await manager
                    .RejoinAfterLoadAsync(
                        bridge, gameDirectory, carried, CancellationToken.None,
                        carriedIsAuthoritative: JoinModeSetting == JoinMode.Auto)
                    .ConfigureAwait(false);

                if (adopted is null)
                {
                    Reply("rejoin failed - see the log; reconnect manually if it does not recover");
                    return;
                }

                // RejoinAfterLoadAsync only FIRES the local engine load (it cannot block on the
                // game there) — poll for it to settle before reopening the coop socket.
                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    if (bridge.TryGetAdoptedWorldLoadStatus(out uint applied, out int status) && applied != 0)
                    {
                        if (status != 0)
                        {
                            Reply($"rejoin: the local load failed (status {status})");
                            return;
                        }

                        break;
                    }

                    await Task.Delay(500).ConfigureAwait(false);
                }

                // ⚠ BEFORE THE SOCKET REOPENS, AND THIS ORDER IS THE FIX. The world under the
                // diff shadows has been replaced (a different save of, usually, the SAME
                // module), so the first scan of the new session must be a fresh silent baseline
                // — otherwise it diffs the host's staged world against the one we left and
                // announces the difference as our own changes, with a Lamport serial that
                // outranks the host's. Safe here because the session is torn down for the whole
                // load window, so nothing has scanned since. See ForgetWorldShadows.
                bridge.ForgetWorldShadows();

                manager.Reconnect();
                Reply($"rejoined '{adopted.Value.Module}'");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"session-save: rejoin failed - {ex.Message}");
            }
            finally
            {
                _rejoinState = ClientLoadRejoinRule.Complete(_rejoinState);
            }
        });
    }

    /// <summary>
    /// <c>loadgame &lt;slot&gt;</c> — LIVE-TEST PRIMITIVE (docs/research/session-save.md).
    /// Fires <see cref="KotorGameBridge.TryRequestMidSessionLoad"/> so a live round can
    /// exercise <c>WorldLoadRule.IsUnannouncedTeardown</c>'s POSITIVE case (a genuine in-game
    /// Load Game action) deterministically: the real pause-menu path needs mouse clicks a
    /// working screenshot loop could confirm landed, and both were found broken on the rig by
    /// tooling gaps rather than a code defect. This bypasses the GUI entirely, the same
    /// pattern <c>skipline</c>/<c>walkto</c>/<c>usedoor</c> already established.
    ///
    /// Needs the in-process bridge — refused, not silently no-op'd, when it is unavailable.
    /// Polls <see cref="KotorGameBridge.TryGetAdoptedWorldLoadStatus"/> (the SAME status
    /// fields <see cref="TriggerRejoin"/> polls, since both requests write through the
    /// identical channel) for up to 90s, matching <see cref="TriggerRejoin"/>'s own deadline
    /// for the same reason: a mid-session load goes through the SAME connect-grace-then-load-
    /// then-present sequence.
    /// </summary>
    private void TriggerMidSessionLoad(int slotIndex)
    {
        if (_kotor is null)
        {
            Reply("loadgame needs the in-process bridge (not available on this connection)");
            return;
        }

        if (slotIndex < 0)
        {
            Reply("loadgame needs a save slot 0 or higher (0-based, most-recent-first)");
            return;
        }

        if (!_kotor.TryRequestMidSessionLoad(slotIndex, out string request))
        {
            Reply($"loadgame: {request}");
            return;
        }

        Reply($"loadgame: {request}");

        KotorGameBridge kotor = _kotor;
        _loadGameTask = Task.Run(async () =>
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (kotor.TryGetAdoptedWorldLoadStatus(out uint applied, out int status) && applied != 0)
                {
                    Reply(status == 0
                        ? "loadgame: the world is up"
                        : $"loadgame: the plugin refused (failure {status})");
                    return;
                }

                await Task.Delay(500).ConfigureAwait(false);
            }

            Reply("loadgame: no result within 90s - check the plugin log");
        });
    }

    /// <summary>
    /// Roadmap item 14 phase 1's "OR on clean disconnect" trigger — wire this onto
    /// <see cref="SessionManager.OnDisconnecting"/> so it fires while the session is still
    /// alive (<see cref="SessionManager.Current"/> not yet cleared), with time to read the
    /// client's current save directory before the teardown makes that moot.
    /// </summary>
    public void CaptureCharacterOnDisconnect()
    {
        if (_sessionSaveWatch is null || manager.Current is not CoopClient)
        {
            return;
        }

        string? currentSave = SaveDigest.FindSessionSave(gameDirectory);
        string? outcome = _sessionSaveWatch.CaptureOnDisconnect(currentSave);
        if (outcome is not null)
        {
            Reply(outcome);
        }
    }
}
