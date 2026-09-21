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
    /// <summary>Mirrors <c>SharedBlock.JoinChargenStateArmed</c>. Duplicated rather than
    /// referenced because the plugin is a separate x86 NativeAOT assembly this one cannot see —
    /// the same convention <c>JoinFlow.cs</c>'s own <c>CaptureStateCaptured</c>/
    /// <c>CaptureStateFailed</c>/<c>CaptureStateSaving</c> already use for the same block field.
    /// </summary>
    private const uint ChargenStateArmed = 1;

    /// <summary>Mirrors <c>SharedBlock.JoinChargenStateSaving</c>.</summary>
    private const uint ChargenStateSaving = 4;

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
    /// own doc comment. ON by default. The <c>?? true</c> matters: <paramref name="loadConfig"/>
    /// returns null when coop.ini is missing or unreadable, and until 2026-09-18 that null
    /// silently disabled both rejoin gates (and wipe recovery with them) while CONFIG.md
    /// promised the code default — docs/review-seamless-2026-09-18.md.</summary>
    private bool? _clientLoadRejoinEnabled;

    private bool ClientLoadRejoinEnabled =>
        _clientLoadRejoinEnabled ??= loadConfig?.Invoke()?.ClientLoadRejoin ?? true;

    /// <summary>coop.ini <c>host_load_readopt</c> — see <see cref="CoopConfig.HostLoadReadopt"/>'s
    /// own doc comment. ON by default (same missing-ini note as above).</summary>
    private bool? _hostLoadReadoptEnabled;

    private bool HostLoadReadoptEnabled =>
        _hostLoadReadoptEnabled ??= loadConfig?.Invoke()?.HostLoadReadopt ?? true;

    // ---- periodic + pre-disconnect character capture (2026-09-18) --------------------------
    //
    // docs/review-seamless-2026-09-18.md §2: the co-op character was captured only when a
    // door autosave or a clean disconnect happened to produce a save. Now a CLIENT asks its own
    // engine for a quicksave (the same CheatKinds.QuickSave the `save` verb drives) every
    // character_capture_interval_min minutes while idle, waits a bounded time for the save to
    // land (CharacterCaptureCadenceRule.SaveWaitMs), then captures; and the disconnect capture
    // quicksaves first the same way. The capture source is unchanged — this install's newest
    // NON-staged save (ClientCharacterPersistence.FindCaptureSource) — so the staged copy of the
    // host's world is never read (StagedSave). See docs/research/session-save.md.

    /// <summary>coop.ini <c>character_capture_interval_min</c>; 0 = off; 10 when the ini is
    /// missing (the code default, same missing-ini note as the rejoin gates above).</summary>
    private int? _characterCaptureIntervalMin;

    private int CharacterCaptureIntervalMin =>
        _characterCaptureIntervalMin ??= loadConfig?.Invoke()?.CharacterCaptureIntervalMin ?? 10;

    /// <summary>When the last periodic capture ran (or the connection began) — the interval is
    /// measured from here. 0 = not yet baselined for this connection.</summary>
    private long _captureLastMs;

    /// <summary>When the in-flight quicksave was requested, or 0 when none is.</summary>
    private long _captureRequestedMs;

    private string? _captureSourceBefore;
    private DateTime _captureStampBefore;

    /// <summary>The main archive's write time. The DIRECTORY's own time is not enough: a
    /// quicksave over an existing QUICKSAVE folder rewrites files in place, which on NTFS
    /// leaves the folder's timestamp alone. Watching auxiliary files made an intermediate
    /// write look like a landed save before the SAVEGAME.sav that character extraction reads
    /// had been replaced.</summary>
    private static DateTime SaveStamp(string? saveDirectory)
    {
        if (saveDirectory is null)
        {
            return DateTime.MinValue;
        }

        try
        {
            return File.GetLastWriteTimeUtc(Path.Combine(saveDirectory, "SAVEGAME.sav"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>The client's periodic capture, one step per tick — see the section comment.</summary>
    private void MaintainPeriodicCapture(PlayerState? localState, long nowMs, string savesDirectory)
    {
        if (_sessionSaveWatch is null)
        {
            return;
        }

        if (_captureLastMs == 0)
        {
            // First tick of this connection: the interval runs from here, not from the epoch —
            // a join must not be followed by an immediate quicksave into a world still landing.
            _captureLastMs = nowMs;
            return;
        }

        if (_captureRequestedMs != 0)
        {
            string? sourceNow = ClientCharacterPersistence.FindCaptureSource(savesDirectory);
            bool landed = CharacterCaptureCadenceRule.SaveLanded(
                _captureSourceBefore, _captureStampBefore, sourceNow, SaveStamp(sourceNow));

            if (!landed && !CharacterCaptureCadenceRule.WaitExpired(_captureRequestedMs, nowMs))
            {
                return;
            }

            Log?.Invoke(landed
                ? $"character capture: quicksave landed after {nowMs - _captureRequestedMs} ms - capturing"
                : "character capture: no save appeared within the wait (the engine may refuse to save here) - capturing the newest save as before");

            // Resolve once more after the wait. The engine can publish the quicksave's
            // SAVEGAME.sav after another file has already made SaveLanded true; carrying
            // sourceNow across that boundary would capture the previously newest named save
            // even though the requested quicksave is now newer. Keep the observed source as a
            // fallback when the save tree is being torn down concurrently.
            string? captureSource = ClientCharacterPersistence.RefreshCaptureSource(savesDirectory, sourceNow);
            string? outcome = _sessionSaveWatch.CaptureNow(captureSource);
            if (outcome is not null)
            {
                Log?.Invoke(outcome);
            }

            _captureRequestedMs = 0;
            _captureLastMs = nowMs;
            return;
        }

        if (!CharacterCaptureCadenceRule.IsDue(CharacterCaptureIntervalMin, nowMs, _captureLastMs, requestPending: false, localState))
        {
            return;
        }

        if (!bridge.SupportsCheats)
        {
            // No plugin block: nothing can ask the engine to save. Try again next interval
            // rather than every tick.
            _captureLastMs = nowMs;
            return;
        }

        _captureSourceBefore = ClientCharacterPersistence.FindCaptureSource(savesDirectory);
        _captureStampBefore = SaveStamp(_captureSourceBefore);
        _captureRequestedMs = nowMs;

        Log?.Invoke($"character capture: {CharacterCaptureIntervalMin} min elapsed - asking the engine for a quicksave");
        SendCheat(new CheatRequest(CheatKinds.QuickSave), quiet: true);
    }

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

    /// <summary>coop.ini <c>world_transfer_udp_fallback</c> — see
    /// <see cref="CoopConfig.WorldTransferUdpFallback"/>'s own doc comment. OFF by default;
    /// live-verified 2026-09-10.</summary>
    private bool? _worldTransferUdpFallbackEnabled;

    private bool WorldTransferUdpFallbackEnabled =>
        _worldTransferUdpFallbackEnabled ??= loadConfig?.Invoke()?.WorldTransferUdpFallback ?? true;

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

    /// <summary>Whether the "gave up after N attempts" line has been said for the current
    /// <see cref="_mismatchRejoinState"/> episode — said once, not every tick the cap holds.</summary>
    private bool _mismatchGaveUpAnnounced;

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
            _captureLastMs = 0;
            _captureRequestedMs = 0;
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
            // The digest watches the session's save (FindSessionSave: the staged autosave);
            // the capture reads this install's OWN newest save — never the staged copy of the
            // host's world, whose player is the host's PC. See StagedSave.
            string? currentSave = ClientCharacterPersistence.FindCaptureSource(Path.Combine(gameDirectory, "saves"));
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

            // Every tick too: a quicksave request in flight has to be watched for its save
            // landing regardless of what the verdict chain below decides this tick.
            MaintainPeriodicCapture(localState, nowMs, Path.Combine(gameDirectory, "saves"));

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
            else if (bridge.TryGetNewCharacterCapture(out uint chargenState, out _, out _)
                && chargenState is ChargenStateArmed or ChargenStateSaving)
            {
                // ⚠ CRASHED LIVE 2026-09-10 (docs/research/world-adoption.md's addendum) before
                // this guard existed: a first-time joiner's own freshly-created chargen module
                // NEVER matches the host's world — that is expected and exactly what the
                // NewCharacterCaptureController/JoinFlow.CreateCharacterThenAdoptAsync pair (the
                // plugin's in-process capture + the agent's own OWN adoption once it captures)
                // is already, actively resolving. This rule's edge/level detection cannot tell
                // "a mismatch nothing is doing anything about" from "a mismatch the OTHER flow
                // owns and is mid-flight on" — racing the two produced two independent
                // DestroyServer/CreateServer teardowns on the same session and an access
                // violation the game's own crash handler caught as EXITPROCESS(0xC0000005).
                // Suppressed here rather than in PersistentSaveMismatchRule itself: the rule is
                // pure and correctly pinned for the case it WAS built for (an established
                // player's world drifting out of sync), and deliberately does not know about a
                // second, unrelated adoption path. Not calling Observe() at all (rather than
                // calling it with a forced-false mismatch) leaves _mismatchRejoinState exactly
                // as an untouched tick would, so the very next tick after the capture leaves
                // this window reads as a fresh edge if a mismatch still genuinely persists
                // (the capture failed) — the ordinary fallback this rule already provides.
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
                        _rejoinState.Phase == RejoinPhase.InFlight, nowMs,
                        hostWorldKey: client.RemoteWorldDigest.SaveIdentity);
                _mismatchRejoinState = nextMismatchState;

                if (shouldTrigger)
                {
                    Reply(
                        $"your world still differs from the host's - rejoining automatically "
                        + $"(attempt {nextMismatchState.Attempts} of {PersistentSaveMismatchRule.MaxAttemptsPerWorld})");
                    TriggerRejoin(RejoinTrigger.SaveMismatch);
                }
                else if (nextMismatchState.GaveUp && _rejoinState.Phase != RejoinPhase.InFlight)
                {
                    if (!_mismatchGaveUpAnnounced)
                    {
                        _mismatchGaveUpAnnounced = true;
                        Reply(
                            $"auto-rejoin gave up after {PersistentSaveMismatchRule.MaxAttemptsPerWorld} attempts against "
                            + "this host world - type reconnect to try again, or load the host's save by hand");
                        Log?.Invoke("session-save: SaveMismatch auto-rejoin capped for this host world");
                    }
                }
                else if (nextMismatchState.Attempts == 0)
                {
                    _mismatchGaveUpAnnounced = false;
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

        if (manager.CurrentSettings is not { } settings)
        {
            return; // should not happen once Current is a CoopHost, but never assume it
        }

        string capturedGameDirectory = gameDirectory;
        uint passwordHash = SessionPassword.Hash(settings.Password);

        // Wires the UDP world-transfer fallback (KotorCoop.Session.CoopHost.WorldTransferUdp.cs)
        // to the SAME provider and password the TCP listener below uses. Idempotent and cheap —
        // safe to call every time this method runs (MaintainWorldServing is itself called on
        // every tick), unlike the TCP listener's Task.Run just below, which only ever starts once.
        if (manager.Current is CoopHost host)
        {
            host.SetWorldTransferProvider(() => BuildWorldSnapshot(capturedGameDirectory), passwordHash);
        }

        if (_worldServeTask is not null)
        {
            return; // TCP listener already running for this host session
        }

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
                        carriedIsAuthoritative: JoinModeSetting == JoinMode.Auto,
                        worldTransferUdpFallback: WorldTransferUdpFallbackEnabled)
                    .ConfigureAwait(false);

                if (adopted is null)
                {
                    Reply("rejoin failed - see the log; reconnect manually if it does not recover");
                    return;
                }

                // RejoinAfterLoadAsync only FIRES the local engine load (it cannot block on the
                // game there) — poll for it to settle before reopening the coop socket. Same
                // budget as a first join (JoinFlow.LoadTimeout, 180 s): a big module on a slow
                // disk can take longer than the 60 s this used to allow, and until 2026-09-18 an
                // expired poll fell straight through to the reconnect below, so the new
                // session's first silent baseline scanned a half-built world
                // (docs/review-seamless-2026-09-18.md). Now an unsettled load is a failed rejoin.
                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(180);
                bool settled = false;
                while (DateTime.UtcNow < deadline)
                {
                    if (bridge.TryGetAdoptedWorldLoadStatus(out uint applied, out int status) && applied != 0)
                    {
                        if (status != 0)
                        {
                            Reply($"rejoin: the local load failed (status {status})");
                            return;
                        }

                        settled = true;
                        break;
                    }

                    await Task.Delay(500).ConfigureAwait(false);
                }

                if (!settled)
                {
                    Log?.Invoke("session-save: rejoin abandoned - the local load did not settle within 180 s");
                    Reply("rejoin: the local load did not settle in 180 s; type reconnect once the world is up");
                    return;
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

    /// <summary>The in-flight named-save copy, if any (see <see cref="SaveGameCommand"/>).</summary>
    private Task? _namedSaveTask;

    /// <summary>
    /// <c>save [name]</c>. A bare <c>save</c> is the engine quicksave, as before. With a name it ALSO
    /// keeps a copy as a save folder <c>NNNNNN - name</c> (<see cref="NamedSaveRule"/>) once the engine
    /// has actually written the quicksave, so <c>loadgame &lt;name&gt;</c> can return to it later: a rig or
    /// a tester can <c>warp</c> to a late-game module, save it ONCE and come back without replaying
    /// anything. Saving under an existing name replaces that save in place.
    /// </summary>
    private void SaveGameCommand(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            SendCheat(new CheatRequest(CheatKinds.QuickSave));
            return;
        }

        if (string.IsNullOrEmpty(gameDirectory))
        {
            Reply("save <name> needs the game folder (not known on this connection); plain save still works");
            return;
        }

        string saves = Path.Combine(gameDirectory, "saves");
        string quick = Path.Combine(saves, NamedSaveRule.QuickSaveFolder);
        DateTime before = SaveStamp(quick);

        SendCheat(new CheatRequest(CheatKinds.QuickSave));

        _namedSaveTask = Task.Run(async () =>
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline && SaveStamp(quick) <= before)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }

            if (SaveStamp(quick) <= before)
            {
                Reply($"save {name}: the engine wrote no quicksave within 20 s (it refuses to save in some scenes) - nothing copied");
                return;
            }

            // The engine writes several files; let the last one land before copying.
            await Task.Delay(750).ConfigureAwait(false);

            try
            {
                string target = CopyQuickSaveAs(saves, quick, name);
                Reply($"saved '{Path.GetFileName(target)}' - load it with: loadgame {name}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Reply($"save {name}: could not copy the quicksave ({ex.Message})");
            }
        });
    }

    /// <summary>Copies the quicksave folder to <c>NNNNNN - name</c> (replacing an existing save of
    /// the same name in place) and returns the target path.</summary>
    public static string CopyQuickSaveAs(string savesDirectory, string quickSaveDirectory, string name)
    {
        string[] existing = [.. Directory.GetDirectories(savesDirectory).Select(d => Path.GetFileName(d))];
        string? same = existing.FirstOrDefault(
            f => !f.EndsWith("AUTOSAVE", StringComparison.OrdinalIgnoreCase)
                && f != NamedSaveRule.QuickSaveFolder
                && string.Equals(NamedSaveRule.NameOf(f), name, StringComparison.OrdinalIgnoreCase));

        string folder = same ?? NamedSaveRule.FolderName(NamedSaveRule.NextIndex(existing), name);
        string target = Path.Combine(savesDirectory, folder);

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(quickSaveDirectory))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        // Newest, so it is slot 0 in the loader's most-recent-first order right after saving.
        Directory.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        return target;
    }

    /// <summary><c>loadgame &lt;name&gt;</c>: resolves a save name to the slot the loader would use
    /// (same ordering as <c>PluginMenuLoader.TryResolveSaveDirectory</c>) and loads it.</summary>
    private void LoadGameByName(string name)
    {
        string saves = string.IsNullOrEmpty(gameDirectory) ? string.Empty : Path.Combine(gameDirectory, "saves");
        if (saves.Length == 0 || !Directory.Exists(saves))
        {
            Reply("loadgame <name> needs the game's saves folder (not found on this connection); use a slot number");
            return;
        }

        (string, DateTime)[] list =
        [
            .. new DirectoryInfo(saves).GetDirectories().Select(d => (d.Name, d.LastWriteTimeUtc)),
        ];

        switch (NamedSaveRule.FindSlot(list, name, out int slot, out string folder))
        {
            case NamedSaveRule.LookupResult.Found:
                Reply($"loadgame: '{folder}' is slot {slot}");
                TriggerMidSessionLoad(slot);
                break;
            case NamedSaveRule.LookupResult.Ambiguous:
                Reply($"loadgame: more than one save matches '{name}' - use a longer part of the name");
                break;
            default:
                Reply($"loadgame: no save matches '{name}' (saves: "
                    + string.Join(", ", list.Select(l => NamedSaveRule.NameOf(l.Item1))
                        .Where(n => !n.Equals("AUTOSAVE", StringComparison.OrdinalIgnoreCase)).Take(12)) + ")");
                break;
        }
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

        // Same source rule as the per-tick capture: this install's own newest save, not the
        // staged copy of the host's world (the 2026-09-03 live pass caught exactly this call
        // writing the host's PC into the sidecar).
        string savesDirectory = Path.Combine(gameDirectory, "saves");
        string? currentSave = ClientCharacterPersistence.FindCaptureSource(savesDirectory);

        // 2026-09-18: save FIRST, so the capture holds the levels and gear since the last door
        // rather than whatever the last autosave happened to hold. Straight through the bridge,
        // not SendCheat's queue — the session is being torn down and there is no next tick to
        // drain a queue on. Bounded: CSWSPlayer::GetIsAllowedToSave refuses silently, so after
        // SaveWaitMs the newest existing save is captured exactly as before.
        if (bridge.SupportsCheats && bridge.TryRequestCheat(new CheatRequest(CheatKinds.QuickSave), out _))
        {
            DateTime stampBefore = SaveStamp(currentSave);
            long requestedMs = SessionClock.NowMs;
            bool landed = false;

            while (!CharacterCaptureCadenceRule.WaitExpired(requestedMs, SessionClock.NowMs))
            {
                Thread.Sleep(100);

                string? sourceNow = ClientCharacterPersistence.FindCaptureSource(savesDirectory);
                if (CharacterCaptureCadenceRule.SaveLanded(currentSave, stampBefore, sourceNow, SaveStamp(sourceNow)))
                {
                    currentSave = sourceNow;
                    landed = true;
                    break;
                }
            }

            Log?.Invoke(landed
                ? $"character capture: pre-disconnect quicksave landed after {SessionClock.NowMs - requestedMs} ms"
                : "character capture: pre-disconnect quicksave did not land within the wait - capturing the newest save as before");

            // The loop can observe an intermediate write before the engine updates the
            // quicksave's SAVEGAME.sav timestamp. Re-resolve at the capture boundary so a
            // newly landed quicksave wins over the path selected before the request.
            currentSave = ClientCharacterPersistence.RefreshCaptureSource(savesDirectory, currentSave);
        }

        string? outcome = _sessionSaveWatch.CaptureOnDisconnect(currentSave);
        if (outcome is not null)
        {
            Reply(outcome);
        }
    }
}
