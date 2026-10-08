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
    // Periodic and explicit-disconnect character saves use the dedicated Co-op slot.
    // Native completion and stable outputs certify that exact own-character source;
    // neither path creates, moves or restores the user's QuickSave.

    /// <summary>coop.ini <c>character_capture_interval_min</c>; 0 = off; 3 when the ini is
    /// missing (the code default, same missing-ini note as the rejoin gates above).</summary>
    private int? _characterCaptureIntervalMin;

    private int CharacterCaptureIntervalMin =>
        _characterCaptureIntervalMin ??= loadConfig?.Invoke()?.CharacterCaptureIntervalMin ?? 3;

    /// <summary>When the last periodic capture ran (or the connection began) — the interval is
    /// measured from here. 0 = not yet baselined for this connection.</summary>
    private long _captureLastMs;

    /// <summary>When the in-flight dedicated save was requested, or 0 when none is.</summary>
    private long _captureRequestedMs;

    private uint? _lastOwnLoadProvenanceGeneration;
    private OwnLoadCharacterSource? _pendingOwnLoadCharacterSource;
    private OwnLoadCharacterSource? _pendingCommandOwnLoadCharacterSource;
    private bool _ownLoadVerdictPending;
    private bool _ownLoadCharacterCapturedForVerdict;
    private bool _ownLoadLoadVerdictSeen;
    private bool _ownLoadRefusalReady;
    private bool _ownLoadCaptureWaitNoticed;
    private long _ownLoadVerdictDeadlineMs;
    private bool _skipNextDisconnectCapture;
    private bool _ownLoadCaptureRequiresConfirmedSave;
    private bool _verifiedOwnLoadAwaitingRejoin;
    private uint _ownLoadCaptureEpoch;

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
            FileInfo save = new(Path.Combine(saveDirectory, "SAVEGAME.sav"));
            return save.Exists && save.Length != 0 ? save.LastWriteTimeUtc : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return DateTime.MinValue;
        }
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
    private bool _wipeTeardownWithoutOwnLoad;
    private uint? _lastNewGameIntentGeneration;

    /// <summary>See <see cref="HostWorldReplacedRule"/> — a host load whose serial edge never
    /// found a digest change to correlate with (live 2026-09-02: an in-world reload of the
    /// staged save). Reset with the three above, and disarmed by <see cref="TriggerRejoin"/>.</summary>
    private HostWorldReplacedState _hostWorldReplacedState = HostWorldReplacedState.Initial;

    private Task? _rejoinTask;
    private CancellationTokenSource? _rejoinCts;
    private int _rejoinGeneration;

    /// <summary>One warning-only check after an adopted world reconnects: the transferred
    /// save can match the host's saved map while the host engine is still running a different
    /// loaded map. This never changes the compatibility verdict or schedules another rejoin.</summary>
    private readonly PostAdoptionGitMismatchNotice _postAdoptionGitNotice = new();
    // RejoinAfterLoadAsync deliberately disconnects before fetching the adopted world. Keep the
    // watchdog from treating that owned gap as an external disconnect and cancelling its fetch.
    private bool _rejoinOwnsDisconnect;

    private const int SoloReturnRejoinMaxAttempts = 5;
    private const int SoloReturnRejoinRetryDelayMs = 20_000;
    // No-world refusals during an explicit host reload are transient while the host's save
    // snapshot catches up. Thirty checks with 2 s backoff bound the waiting between attempts;
    // each network check may itself take longer, so this is not a wall-clock deadline.
    private const int HostReloadRejoinMaxAttempts = 30;
    private const int HostReloadRejoinRetryDelayMs = 2_000;

    /// <summary>Cancel a rejoin only for an explicit external session change.</summary>
    private void CancelRejoinForExternalSessionChange()
    {
        _rejoinOwnsDisconnect = false;
        _rejoinGeneration++;
        CancellationTokenSource? cancellation = _rejoinCts;
        _rejoinCts = null;
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker can finish and dispose its owned source concurrently.
        }
        _rejoinState = RejoinDecisionState.Idle;
    }

    // A host's solo Manaan/mini-game leg is intentionally not followed. If it returns to
    // the origin after rewriting its saved object map, one bounded adoption restores the
    // id-keyed channels without treating the expected leg as a generic host reload.
    private SoloReturnRejoinState _soloReturnState = SoloReturnRejoinState.Initial;
    private bool _soloCapturePending;
    private bool _soloCaptureWaiting;
    private long _soloCaptureRequestedMs;
    private ReturnSaveCapture? _soloReturnSaveCapture;
    private string _soloCaptureModuleBefore = string.Empty;
    private string _soloCaptureHostModuleBefore = string.Empty;
    private string _soloObservedHostModule = string.Empty;
    private uint? _soloCaptureLocalSerialBefore;
    private uint? _soloCaptureHostSerialBefore;

    private string _hostSoloOriginModule = string.Empty;
    private string _hostLastModule = string.Empty;
    private bool _hostSoloLeg;
    private uint? _hostSoloLoadSerialBaseline;
    private bool _hostReturnSavePending;
    private long _hostReturnSaveRequestedMs;
    private long _hostReturnSaveRetryMs;
    private long _hostReturnSaveLastBlockedLogMs;
    private ReturnSaveCapture? _hostReturnSaveCapture;
    private bool _hostReturnSaveReady = true;

    /// <summary>Set on the world-transfer thread when a joiner's request found no save of the
    /// live world to serve; the dispatcher tick answers it with a fresh host save.</summary>
    private volatile bool _hostFreshSaveWanted;

    /// <summary>Edge detector for a joiner's save-mismatch note on the host's verdict.</summary>
    private bool _hostSawPeerSaveMismatch;
    private HostRestartWatch.State _hostRestartWatch;
    private long _hostPeerMismatchSaveAtMs;

    /// <summary>What the next <see cref="BeginHostReturnFreshSave"/> log line says it is for.</summary>
    private string _hostFreshSaveReason = "for a joiner's world request";

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
        // Observe even without a session so a previous character creation cannot be
        // mistaken for new intent when its eventual co-op connection opens.
        if (_kotor is { } gameBridge && gameBridge.TryReadNewGameIntent(out NewGameIntent? newGameIntent))
        {
            uint generation = newGameIntent?.Generation ?? 0;
            bool freshIntent = _lastNewGameIntentGeneration.HasValue
                && _lastNewGameIntentGeneration.Value != generation && generation != 0;
            _lastNewGameIntentGeneration = generation;
            bool joinOwnsCreation = gameBridge.TryGetNewCharacterCapture(out uint creationState, out _, out _)
                && creationState is ChargenStateArmed or ChargenStateSaving;
            if (freshIntent && !joinOwnsCreation && (manager.Current is CoopClient || _rejoinOwnsDisconnect))
            {
                // Stock chargen has no selected-save provenance. Keep the chosen new
                // game and the existing co-op checkpoint; recovery must not interrupt it.
                CancelRejoinForExternalSessionChange();
                _skipNextDisconnectCapture = manager.Current is CoopClient;
                Reply("new game selected - left co-op so character creation can continue; connect again when you are ready");
                manager.Disconnect();
                return;
            }
        }

        if (_sessionSaveWatch is null)
        {
            return;
        }

        if (manager.Current is not { } session)
        {
            if (_rejoinOwnsDisconnect)
            {
                return;
            }

            CancelRejoinForExternalSessionChange();
            _sessionSaveWatch.Reset();
            _rejoinState = RejoinDecisionState.Idle;
            _mismatchRejoinState = MismatchRejoinWatchState.Initial;
            _saveLineageAnnounced = false;
            _wipeRecoveryState = WipeRecoveryState.Initial;
            _wipeTeardownWithoutOwnLoad = false;
            _hostWorldReplacedState = HostWorldReplacedState.Initial;
            _soloReturnState = SoloReturnRejoinState.Initial;
            _postAdoptionGitNotice.Reset();
            _soloCapturePending = false;
            _soloCaptureWaiting = false;
            _soloCaptureRequestedMs = 0;
            _soloReturnSaveCapture = null;
            _soloCaptureModuleBefore = string.Empty;
            _soloCaptureHostModuleBefore = string.Empty;
            _soloObservedHostModule = string.Empty;
            _soloCaptureLocalSerialBefore = null;
            _soloCaptureHostSerialBefore = null;
            _hostSoloOriginModule = string.Empty;
            _hostLastModule = string.Empty;
            _hostSoloLeg = false;
            _hostSoloLoadSerialBaseline = null;
            _hostReturnSavePending = false;
            _hostReturnSaveCapture = null;
            _hostReturnSaveRetryMs = 0;
            _hostReturnSaveLastBlockedLogMs = 0;
            _hostReturnSaveReady = true;
            _captureLastMs = 0;
            _captureRequestedMs = 0;
            _lastOwnLoadProvenanceGeneration = null;
            _pendingOwnLoadCharacterSource = null;
            _pendingCommandOwnLoadCharacterSource = null;
            _ownLoadVerdictPending = false;
            _ownLoadCharacterCapturedForVerdict = false;
            _ownLoadLoadVerdictSeen = false;
            _ownLoadRefusalReady = false;
            _ownLoadCaptureWaitNoticed = false;
            _ownLoadVerdictDeadlineMs = 0;
            _skipNextDisconnectCapture = false;
            _automaticRejoinCapture = null;
            _controlledSceneReturnPending = false;
            _ownLoadCaptureRequiresConfirmedSave = false;
            _verifiedOwnLoadAwaitingRejoin = false;
            _ownLoadCaptureEpoch = 0;
            _periodicCapture = null;
            return;
        }

        uint digest = bridge.SaveIdentityDigest;
        long nowMs = SessionClock.NowMs;
        // Finish an already-owned periodic request before another capture waits for the
        // shared save channel. This also lets completion drain while automatic rejoin waits.
        if (_captureRequestedMs != 0)
        {
            MaintainPeriodicCapture(bridge.TryReadLocalState(out PlayerState periodicLocal) ? periodicLocal : null,
                nowMs, Path.Combine(gameDirectory, "saves"));
            if (_captureRequestedMs != 0) return;
        }
        if (MaintainAutomaticRejoinCapture(nowMs)) return;

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

            string hostModule = bridge.TryReadCurrentModule(out string liveHostModule)
                ? liveHostModule : string.Empty;
            if (hostModule.Length > 0)
            {
                if (_hostSoloLeg && _hostSoloLoadSerialBaseline.HasValue
                    && loadSerial.HasValue && loadSerial.Value != _hostSoloLoadSerialBaseline.Value)
                {
                    _hostSoloLeg = false;
                    _hostSoloOriginModule = string.Empty;
                    _hostReturnSavePending = false;
                    _hostReturnSaveReady = true;
                    _hostReturnSaveRetryMs = 0;
                    _hostReturnSaveLastBlockedLogMs = 0;
                    Log?.Invoke("session-save: host solo-return watch cancelled by a host load edge");
                }

                if (!_hostSoloLeg
                    && MinigameModuleRule.IsMinigameModule(hostModule)
                    && _hostLastModule.Length > 0
                    && !MinigameModuleRule.IsMinigameModule(_hostLastModule))
                {
                    _hostSoloOriginModule = _hostLastModule;
                    _hostSoloLeg = true;
                    _hostSoloLoadSerialBaseline = loadSerial;
                }
                else if (_hostSoloLeg
                    && string.Equals(hostModule, _hostSoloOriginModule, StringComparison.OrdinalIgnoreCase))
                {
                    _hostSoloLeg = false;
                    _hostSoloLoadSerialBaseline = null;
                    BeginHostReturnFreshSave(nowMs);
                }
                else if (_hostSoloLeg && !MinigameModuleRule.IsMinigameModule(hostModule))
                {
                    _hostSoloLeg = false;
                    _hostSoloOriginModule = string.Empty;
                    _hostSoloLoadSerialBaseline = null;
                }

                _hostLastModule = hostModule;
            }

            // A joiner whose world differs will re-adopt this host's world after its settle
            // grace (PersistentSaveMismatchRule.UnprovenMismatchGraceMs). Save NOW, on the edge,
            // so what it fetches is this live world and not the last save on disk — live
            // 2026-09-26 a re-adopt served a five-minute-old save and the joiner lost an item
            // the host had dropped since.
            bool peerSaveMismatch = session.Compatibility?.Notes
                .Any(n => n.Code == CompatibilityCode.Save) ?? false;
            // Once per minute at most: every host save moves the host's own identity, which
            // flickers this note and would re-fire the edge (live: a save every 8 s, and each
            // new host identity restarted the joiner's settle grace — two minutes, not 15 s).
            if (peerSaveMismatch && !_hostSawPeerSaveMismatch
                && (_hostPeerMismatchSaveAtMs == 0 || nowMs - _hostPeerMismatchSaveAtMs >= 60_000))
            {
                _hostPeerMismatchSaveAtMs = nowMs;
                Log?.Invoke("session-save: a joiner's world differs - saving so its re-adopt gets the live world");
                _hostFreshSaveWanted = true;
                _hostFreshSaveReason = "for a joiner's re-adopt";
            }

            _hostSawPeerSaveMismatch = peerSaveMismatch;

            // A joiner asked for the world and the newest save is not this live world (nothing
            // saved since the last module change, live 2026-09-26: every re-adopt after an
            // agent restart failed "host has no save to share yet"). Save now, through the
            // same fresh-save path a solo return uses, so the joiner's retry finds it.
            if (_hostFreshSaveWanted && !_hostReturnSavePending && !_scriptHostRefreshWanted)
            {
                _hostFreshSaveWanted = false;
                BeginHostReturnFreshSave(nowMs, _hostFreshSaveReason);
                _hostFreshSaveReason = "for a joiner's world request";
            }

            MaintainHostReturnFreshSave(nowMs);
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
            // One line per episode: the engine's own transition autosave gives each side its
            // own counters, so after a real door the raw identities differ for good while the
            // world is still shared (SaveLineage). Say so once, then stay quiet until it clears.
            if (client.SaveLineageAbsorbing != _saveLineageAnnounced)
            {
                _saveLineageAnnounced = client.SaveLineageAbsorbing;
                if (_saveLineageAnnounced)
                {
                    Log?.Invoke("save identity differs after a save on one side, with no load since the two last agreed - same world, not rejoining");
                }
            }

            SaveOrLoadVerdict ownVerdict = _sessionSaveWatch.ObserveOwnVerdict(
                digest, loadSerial, nowMs, out bool ownLoadEdge);
            string savesDirectory = Path.Combine(gameDirectory, "saves");
            LoadProvenance? loadProvenance = bridge.TryReadLoadProvenance(out LoadProvenance? observedProvenance)
                ? observedProvenance : null;
            PlayerState? localState = bridge.TryReadLocalState(out PlayerState localNow) ? localNow : null;
            bool confirmedWipeTeardown = WipeTeardownRule.MaySuppressOwnLoad(
                _wipeRecoveryState.Wiped, localState,
                _lastOwnLoadProvenanceGeneration, loadProvenance?.Generation,
                _pendingCommandOwnLoadCharacterSource is not null,
                _ownLoadCaptureRequiresConfirmedSave || _verifiedOwnLoadAwaitingRejoin);
            _wipeTeardownWithoutOwnLoad = confirmedWipeTeardown
                && (_wipeTeardownWithoutOwnLoad || ownLoadEdge);
            if (_wipeTeardownWithoutOwnLoad)
            {
                ownLoadEdge = false;
                if (ownVerdict == SaveOrLoadVerdict.Load)
                    ownVerdict = SaveOrLoadVerdict.None;
            }
            ObserveOwnLoadCharacterSource(loadProvenance, loadSerial, nowMs, savesDirectory);

            bool ownLoadCharacterCaptured = false;
            if (ownLoadEdge)
            {
                // A LOCAL load edge, unlike a save, really can put a different world under the
                // same host session id — see SessionManager's own doc on the field. Drop the
                // process-lifetime lineage seed so the NEXT reconnect (whether this one, after
                // this load, or a later one) is judged "never agreed" again rather than
                // trusting a checkpoint this load may have already invalidated.
                manager.ClearLineageSeed();
                _ownLoadCaptureEpoch = OwnLoadCaptureSafetyRule.NextEpoch(_ownLoadCaptureEpoch);
                _captureRequestedMs = 0;
                _periodicCapture = null;
                _ownLoadVerdictPending = true;
                _ownLoadLoadVerdictSeen = false;
                _ownLoadRefusalReady = false;
                _ownLoadCaptureWaitNoticed = false;
                _ownLoadCaptureRequiresConfirmedSave = true;
                _mismatchRejoinState = MismatchRejoinWatchState.Initial;
                _mismatchGaveUpAnnounced = false;
                _ownLoadCharacterCapturedForVerdict = TryCaptureCompletedOwnLoadCharacter(
                    loadProvenance, loadSerial, nowMs, savesDirectory);
                _ownLoadVerdictDeadlineMs = nowMs + OwnLoadCharacterSourceRule.MaxRequestAgeMs;
            }
            else if (_ownLoadVerdictPending && !_ownLoadCharacterCapturedForVerdict)
            {
                // Console load completion can lag the first serial edge; keep checking the
                // exact request acknowledgement during the classifier's settling window.
                _ownLoadCharacterCapturedForVerdict = TryCaptureCompletedOwnLoadCharacter(
                    loadProvenance, loadSerial, nowMs, savesDirectory);
            }
            bool suppressGenericCapture = _ownLoadVerdictPending;
            _ownLoadLoadVerdictSeen = OwnLoadCaptureSafetyRule.LatchLoadVerdict(
                _ownLoadLoadVerdictSeen, ownVerdict == SaveOrLoadVerdict.Load);
            bool ownLoadRejoinReady = OwnLoadCaptureSafetyRule.MayRejoinAfterCapture(
                _ownLoadVerdictPending, _ownLoadCharacterCapturedForVerdict);
            if (_ownLoadVerdictPending && ownLoadRejoinReady)
            {
                ownLoadCharacterCaptured = _ownLoadCharacterCapturedForVerdict;
                if (ownLoadCharacterCaptured)
                {
                    _skipNextDisconnectCapture = true;
                    _ownLoadCaptureRequiresConfirmedSave = false;
                    _verifiedOwnLoadAwaitingRejoin = true;
                }
                _ownLoadVerdictPending = false;
                _ownLoadCharacterCapturedForVerdict = false;
                _ownLoadLoadVerdictSeen = false;
                _ownLoadCaptureWaitNoticed = false;
                _pendingOwnLoadCharacterSource = null;
                _pendingCommandOwnLoadCharacterSource = null;
            }
            else if (_ownLoadVerdictPending && nowMs >= _ownLoadVerdictDeadlineMs)
            {
                _ownLoadVerdictPending = false;
                _ownLoadRefusalReady = OwnLoadCaptureSafetyRule.MustRefuseAfterDeadline(
                    _ownLoadLoadVerdictSeen, _ownLoadCharacterCapturedForVerdict, requestWindowPending: false);
                _ownLoadCharacterCapturedForVerdict = false;
                _pendingOwnLoadCharacterSource = null;
                _pendingCommandOwnLoadCharacterSource = null;
            }

            // Suppress capture throughout the digest classifier's settling window, not just
            // the raw serial edge tick; the save digest can trail that edge by several polls.
            string? currentSave = ClientCharacterPersistence.FindCaptureSource(savesDirectory);
            bool captureBlockedByOwnLoad = !OwnLoadCaptureSafetyRule.MayCaptureFromNewestSave(
                suppressGenericCapture,
                _ownLoadCaptureRequiresConfirmedSave,
                _verifiedOwnLoadAwaitingRejoin);
            string? captureOutcome = _sessionSaveWatch.ObserveClient(
                digest, captureBlockedByOwnLoad ? null : currentSave, out _);
            if (captureOutcome is not null)
            {
                // A diagnostic for the log, not the player's screen.
                Log?.Invoke(captureOutcome);
            }
            WorldDigest hostDigest = client.RemoteWorldDigest;
            SaveOrLoadVerdict remoteVerdict = _sessionSaveWatch.ObserveRemoteVerdict(
                hostDigest.SaveIdentity, hostDigest.LoadSerial, nowMs, out bool hostLoadEdge);

            string localModule = bridge.TryReadCurrentModule(out string liveModule)
                ? liveModule : string.Empty;
            uint localGit = localModule.Length == 0 ? 0 : bridge.ModuleGitDigest(localModule);
            if (_postAdoptionGitNotice.Observe(
                localModule,
                localGit,
                hostDigest.Module,
                hostDigest.ModuleGit,
                manager.LifecycleGeneration,
                nowMs)
                // The raw digests can differ after a clean adoption when the two sides took
                // their GIT baselines from different saves (ModuleGitLineage absorbs exactly
                // that); only speak when the verdict itself still drops the module's channels.
                && (client.Compatibility?.Notes.Any(n => n.Code == CompatibilityCode.ModuleGit) ?? false))
            {
                Reply("module data still differs after rejoining. Check matching game data. If the host has not loaded the intended save, have them load it, then rejoin.");
            }

            _soloObservedHostModule = hostDigest.Module;

            if (hostLoadEdge)
            {
                _soloReturnState = SoloReturnRejoinState.Initial;
                _soloCapturePending = false;
                _soloCaptureWaiting = false;
            }

            (SoloReturnRejoinState nextSolo, bool soloReturn) = SoloReturnRejoinRule.Observe(
                _soloReturnState,
                localModule,
                localGit,
                hostDigest.Module,
                hostDigest.ModuleGit,
                bridge.TryReadWorldLoadSerial(out uint localSerial) ? localSerial : null,
                hostDigest.LoadSerial,
                hostDigest.SaveIdentity,
                nowMs,
                AutoAdoptWorldEnabled);
            _soloReturnState = nextSolo;

            // The wipe watch runs every tick regardless of the verdicts below: its latch must
            // be armed during the ~13 s both parties lie wiped before the game-over teardown,
            // and it fires on the host's reappearance, which neither classifier reports.
            bool soloCaptureReady = MaintainSoloReturnCapture(localState, nowMs);
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
            // Keep the wipe armed until a recovery attempt is selected: another safety/
            // capture branch can defer this tick without consuming recovery.
            if (!hostBackAfterWipe)
                _wipeRecoveryState = nextWipeState;

            // The periodic dedicated save is watched every tick.
            MaintainPeriodicCapture(localState, nowMs, Path.Combine(gameDirectory, "saves"));

            // The serial-only fallback for the host's load, same lifecycle as the wipe watch:
            // arms on the raw edge, fires once the host stands in a named module. Runs every
            // tick too, so an edge that lands mid-teardown is not lost behind another branch.
            // The host process restarted under this same connection (its game crashed and was
            // relaunched on a save): its live world was freshly loaded, whatever the save
            // identities say. Live 2026-10-09: the relaunched host loaded the very save the joiner
            // had adopted from, the identities matched again, and the joiner kept a world the host
            // no longer had (a creature destroyed after that save stayed gone for good).
            bool hostRestarted = HostRestartWatch.Observe(ref _hostRestartWatch, client, client.HostSessionId);
            if (hostRestarted) Log?.Invoke("the host's game restarted under this connection - its world was reloaded; re-adopting it once the host is back in a module");
            (bool hostWorldReplaced, HostWorldReplacedState nextReplacedState) =
                HostWorldReplacedRule.Observe(_hostWorldReplacedState, hostLoadEdge || hostRestarted, hostState, hostDigest.Module);
            _hostWorldReplacedState = nextReplacedState;

            // Preserve the raw edge even when a higher-priority rule owns this tick's rejoin.
            // Otherwise OwnLoad/HostReload consumes the edge before the mismatch fallback can
            // correlate it. If both rules select recovery this tick, the earlier branch owns
            // the action and the mismatch rule's attempt is recorded for its later backoff.
            bool saveMismatchNow = client.Compatibility?.Notes
                .Any(n => n.Code == CompatibilityCode.Save) ?? false;
            bool isNewCharacterCapture = bridge.TryGetNewCharacterCapture(
                    out uint mismatchChargenState, out _, out _)
                && mismatchChargenState is ChargenStateArmed or ChargenStateSaving;
            bool mismatchShouldTrigger = false;
            MismatchRejoinWatchState mismatchStateForTick = _mismatchRejoinState;
            bool ownLoadSafetyHold = !OwnLoadCaptureSafetyRule.MayAutomaticallyAdopt(
                _ownLoadCaptureRequiresConfirmedSave,
                _verifiedOwnLoadAwaitingRejoin,
                ownLoadTrigger: ownVerdict == SaveOrLoadVerdict.Load && ownLoadCharacterCaptured);
            if (!isNewCharacterCapture && !ownLoadSafetyHold)
            {
                (mismatchShouldTrigger, mismatchStateForTick) = PersistentSaveMismatchRule.Observe(
                    _mismatchRejoinState, saveMismatchNow,
                    _rejoinState.Phase == RejoinPhase.InFlight,
                    nowMs,
                    hostWorldKey: client.RemoteWorldDigest.SaveIdentity,
                    loadEdge: ownLoadEdge || hostLoadEdge,
                    worldsProvenSame: client.SaveLineageAgreed);
                _mismatchRejoinState = mismatchStateForTick;
            }

            if (ownVerdict == SaveOrLoadVerdict.Load || ownLoadRejoinReady || _ownLoadRefusalReady)
            {
                if (ownLoadCharacterCaptured || ownLoadRejoinReady)
                {
                    Reply("you loaded a save - rejoining the host's world");
                    TriggerRejoin(RejoinTrigger.OwnLoad, session);
                }
                else
                {
                    if (_ownLoadVerdictPending)
                    {
                        // The world-load serial/digest edge can arrive before the plugin's
                        // request acknowledgement. Keep the exact source candidate alive and
                        // wait within its bounded request window before refusing.
                    }
                    else
                    {
                        Reply("could not verify which character save you loaded, so I left the session connected; save and reconnect manually to avoid carrying the wrong character");
                        Log?.Invoke("session-save: OwnLoad rejoin refused because no fresh, unchanged selected-save source was proven");
                        _ownLoadRefusalReady = false;
                        _ownLoadLoadVerdictSeen = false;
                    }
                }
            }
            else if (_ownLoadCaptureRequiresConfirmedSave || _verifiedOwnLoadAwaitingRejoin)
            {
                // An unproven load may have replaced the live PC while newest-on-disk still
                // belongs to a different save. Hold every automatic adoption path until a
                // later confirmed own save captures the current character.
            }
            else if (_scriptSaveCapture is not null)
            {
                // The exact dedicated client save owns adoption until it settles.
            }
            else if (ControlledSceneTravelRule.HoldAdoption(hostState))
            {
                _controlledSceneReturnPending = true;
                // Keep the selected own character and connected world while the host plays
                // a private controlled actor. Its save cannot be safely produced until return.
            }
            else if (remoteVerdict == SaveOrLoadVerdict.Load
                && HostWorldReplacedRule.IsHostReady(hostState, hostDigest.Module))
            {
                Reply("host loaded a save - rejoining their world");
                TriggerRejoin(_wipeRecoveryState.Wiped ? RejoinTrigger.WipeRecovery : RejoinTrigger.HostReload, session);
            }
            else if (hostBackAfterWipe)
            {
                Reply("the host is back in the world after the wipe - rejoining their world");
                TriggerRejoin(RejoinTrigger.WipeRecovery, session);
            }
            else if (hostWorldReplaced)
            {
                Reply("the host's world was reloaded - rejoining their world");
                TriggerRejoin(_wipeRecoveryState.Wiped ? RejoinTrigger.WipeRecovery : RejoinTrigger.HostReload, session);
            }
            else if (soloReturn || soloCaptureReady)
            {
                if (soloReturn)
                {
                    _soloCaptureWaiting = true;
                    soloCaptureReady = MaintainSoloReturnCapture(localState, nowMs);
                }

                if (soloCaptureReady)
                {
                    Reply("the host returned from a solo section - rejoining their fresh world");
                    TriggerRejoin(RejoinTrigger.SoloReturn, session);
                }
            }
            else if (isNewCharacterCapture)
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
                // No higher-priority recovery owns this tick. The mismatch rule permits adoption
                // after a raw load edge was correlated with this mismatch, or once a mismatch
                // this connection never proved harmless outlives its settle grace; a plain
                // save-identity drift inside an agreed lineage never gets here (absorbed).
                if (mismatchShouldTrigger)
                {
                    Reply(
                        $"your world still differs from the host's - rejoining automatically "
                        + $"(attempt {mismatchStateForTick.Attempts} of {PersistentSaveMismatchRule.MaxAttemptsPerWorld})");
                    TriggerRejoin(RejoinTrigger.SaveMismatch, session);
                }
                else if (mismatchStateForTick.GaveUp && _rejoinState.Phase != RejoinPhase.InFlight)
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
                else if (mismatchStateForTick.Attempts == 0)
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

    private sealed class ReturnSaveCapture(ICoopSession session, long generation, string module,
        uint loadSerial, string source, DateTime stampBefore)
    {
        public readonly ICoopSession Session = session;
        public readonly long Generation = generation;
        public readonly string Module = module;
        public readonly uint LoadSerial = loadSerial;
        public readonly string Source = source;
        public readonly DateTime StampBefore = stampBefore;
        public bool Answered;
        public bool Refused;
        public DateTime ObservedStamp;
        public long StableSinceMs;
    }

    private ReturnSaveCapture? BeginReturnSave()
    {
        ICoopSession? session = manager.Current;
        if (ReturnSaveCoordinationBusy || session is null || !manager.TryGetCurrentGeneration(session, out long generation)
            || !bridge.TryReadLocalState(out PlayerState state) || !ScriptSaveIsSafe(state)
            || !bridge.TryReadWorldLoadSerial(out uint serial)) return null;
        ReturnSaveCapture? capture = null;
        string? source = RequestDedicatedCoopSave(quiet: true,
            onPrepared: path =>
            {
                capture = new(session, generation, state.Module, serial, path, SaveStamp(path));
                return ReturnSaveIsCurrent(capture);
            },
            onApplied: () => { if (capture is not null) capture.Answered = true; },
            onRefused: () => { if (capture is not null) capture.Refused = true; });
        return source is null ? null : capture;
    }

    private bool ReturnSaveCoordinationBusy => _captureRequestedMs != 0 || _rejoinState.Phase == RejoinPhase.InFlight
        || _ownLoadVerdictPending || _ownLoadCaptureRequiresConfirmedSave || _verifiedOwnLoadAwaitingRejoin;

    private bool ReturnSaveIsCurrent(ReturnSaveCapture capture) =>
        !ReturnSaveCoordinationBusy && manager.Current == capture.Session && manager.LifecycleGeneration == capture.Generation
        && bridge.TryReadLocalState(out PlayerState state) && ScriptSaveIsSafe(state)
        && bridge.TryReadWorldLoadSerial(out uint serial)
        && ManualWorldRefreshRule.IsSameWorld(capture.Module, capture.LoadSerial, state, serial);

    private bool ReturnSaveHasLanded(ReturnSaveCapture capture, long nowMs)
    {
        if (!capture.Answered || capture.Refused || !ReturnSaveIsCurrent(capture)) return false;
        DateTime stamp = SaveStamp(capture.Source);
        if (stamp <= capture.StampBefore) return false;
        if (stamp != capture.ObservedStamp)
        {
            capture.ObservedStamp = stamp;
            capture.StableSinceMs = nowMs;
            return false;
        }
        return nowMs - capture.StableSinceMs >= 750
            && ClientCharacterPersistence.IsCaptureSourceReady(capture.Source)
            && SaveDigest.TryReadActiveModuleName(capture.Source, out string savedModule)
            && savedModule.Equals(capture.Module, StringComparison.OrdinalIgnoreCase)
            && ReturnSaveIsCurrent(capture) && SaveStamp(capture.Source) == stamp;
    }

    private void BeginSoloReturnCapture(long nowMs)
    {
        _soloReturnSaveCapture = BeginReturnSave();
        if (_soloReturnSaveCapture is null) return;
        _soloCaptureModuleBefore = bridge.TryReadCurrentModule(out string captureModule)
            ? captureModule : string.Empty;
        _soloCaptureHostModuleBefore = _soloObservedHostModule;
        _soloCaptureLocalSerialBefore = bridge.TryReadWorldLoadSerial(out uint captureSerial)
            ? captureSerial : null;
        _soloCaptureHostSerialBefore = _soloReturnState.HostLoadSerialKnown
            ? _soloReturnState.BaselineHostLoadSerial : null;
        _soloCaptureWaiting = false;
        _soloCapturePending = true;
        _soloCaptureRequestedMs = nowMs;
        Log?.Invoke("session-save: requesting a fresh client Co-op save before solo-return adoption");
    }

    private bool MaintainSoloReturnCapture(PlayerState? localState, long nowMs)
    {
        if ((_soloCaptureWaiting || _soloCapturePending)
            && (!AutoAdoptWorldEnabled || !_soloReturnState.InSoloLeg
                || !string.Equals(_soloObservedHostModule, _soloReturnState.OriginModule,
                    StringComparison.OrdinalIgnoreCase)
                || localState is not { } captureState
                || !string.Equals(captureState.Module, _soloReturnState.OriginModule,
                    StringComparison.OrdinalIgnoreCase)))
        {
            _soloCaptureWaiting = false;
            _soloCapturePending = false;
            Log?.Invoke("session-save: solo return capture cancelled before save by a lifecycle change");
            return false;
        }

        if (_soloCaptureWaiting && !_soloCapturePending)
        {
            if (!CaptureIsAllowed(localState) || !bridge.SupportsCheats)
            {
                return false;
            }

            BeginSoloReturnCapture(nowMs);
        }

        if (!_soloCapturePending)
        {
            return false;
        }

        if (!AutoAdoptWorldEnabled
            || _soloReturnSaveCapture is not { } returnSave || !ReturnSaveIsCurrent(returnSave)
            || returnSave.Refused
            || !_soloCaptureHostModuleBefore.Equals(_soloObservedHostModule, StringComparison.OrdinalIgnoreCase)
            || !bridge.TryReadCurrentModule(out string currentModule)
            || !_soloCaptureModuleBefore.Equals(currentModule, StringComparison.OrdinalIgnoreCase)
            || (_soloCaptureLocalSerialBefore.HasValue
                && bridge.TryReadWorldLoadSerial(out uint currentSerial)
                && currentSerial != _soloCaptureLocalSerialBefore.Value)
            || (_soloCaptureHostSerialBefore.HasValue
                && _soloReturnState.HostLoadSerialKnown
                && _soloReturnState.BaselineHostLoadSerial != _soloCaptureHostSerialBefore.Value)
            || !_soloReturnState.InSoloLeg)
        {
            _soloCapturePending = false;
            _soloCaptureWaiting = false;
            Log?.Invoke("session-save: solo return adoption capture cancelled by a lifecycle change");
            return false;
        }

        if (!CaptureIsAllowed(localState))
        {
            return false;
        }

        string sourceNow = returnSave.Source;
        bool landed = ReturnSaveHasLanded(returnSave, nowMs);
        if (!landed && !CharacterCaptureCadenceRule.WaitExpired(_soloCaptureRequestedMs, nowMs))
        {
            return false;
        }

        if (!landed)
        {
            _soloCapturePending = false;
            Log?.Invoke("session-save: solo return adoption aborted - fresh client Co-op save did not land");
            return false;
        }

        string? outcome = null;
        bool captured = _sessionSaveWatch is not null
            && _sessionSaveWatch.CaptureVerifiedRefreshSource(sourceNow, out outcome);
        if (outcome is not null)
        {
            Log?.Invoke(outcome);
        }

        _soloCapturePending = false;
        if (!captured)
        {
            Log?.Invoke("session-save: solo return adoption aborted - fresh character sidecar was not written");
        }
        return captured && ReturnSaveIsCurrent(returnSave)
            && SaveStamp(returnSave.Source) == returnSave.ObservedStamp;
    }

    private static bool CaptureIsAllowed(PlayerState? state) =>
        state is { IsInCombat: false, IsInDialogue: false, IsInMinigame: false,
            IsDowned: false, IsPartyWiped: false };

    private void BeginHostReturnFreshSave(long nowMs, string reason = "host returned from a solo section")
    {
        _hostReturnSaveReady = false;
        _hostReturnSavePending = false;
        _hostReturnSaveRequestedMs = 0;

        if (!bridge.SupportsCheats
            || !bridge.TryReadLocalState(out PlayerState state)
            || !CaptureIsAllowed(state))
        {
            _hostReturnSaveRetryMs = nowMs + 1000;
            if (_hostReturnSaveLastBlockedLogMs == 0 || nowMs - _hostReturnSaveLastBlockedLogMs >= 30_000)
            {
                _hostReturnSaveLastBlockedLogMs = nowMs;
                Log?.Invoke("session-save: host returned from a solo section but is not save-ready; "
                    + "world serving held until a fresh return save can be written");
            }
            return;
        }

        _hostReturnSaveCapture = BeginReturnSave();
        if (_hostReturnSaveCapture is not null)
        {
            _hostReturnSaveLastBlockedLogMs = 0;
            _hostReturnSavePending = true;
            _hostReturnSaveRequestedMs = nowMs;
            Log?.Invoke($"session-save: {reason} - requesting a fresh return save");
        }
        else
        {
            _hostReturnSaveRetryMs = nowMs + 1000;
        }
    }

    private void MaintainHostReturnFreshSave(long nowMs)
    {
        if (!_hostReturnSaveReady && !_hostReturnSavePending
            && _hostReturnSaveRetryMs != 0 && nowMs >= _hostReturnSaveRetryMs)
        {
            BeginHostReturnFreshSave(nowMs);
        }

        if (!_hostReturnSavePending)
        {
            return;
        }

        if (_hostReturnSaveCapture is not { } capture || capture.Refused || !ReturnSaveIsCurrent(capture))
        {
            _hostReturnSavePending = false;
            _hostReturnSaveRetryMs = nowMs + 1000;
            return;
        }
        bool landed = ReturnSaveHasLanded(capture, nowMs);

        if (landed)
        {
            _hostReturnSavePending = false;
            _hostReturnSaveReady = true;
            Log?.Invoke($"session-save: fresh host return save landed after {nowMs - _hostReturnSaveRequestedMs} ms");
            return;
        }

        if (CharacterCaptureCadenceRule.WaitExpired(_hostReturnSaveRequestedMs, nowMs))
        {
            _hostReturnSavePending = false;
            _hostReturnSaveRetryMs = nowMs + 1000;
            Log?.Invoke("session-save: fresh host return save did not land; world serving remains held");
        }
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
    /// started. Snapshot selection uses the shared staged-versus-quicksave resolver; an
    /// unreadable selected archive yields an unknown snapshot rather than raw partial bytes.</summary>
    private (IReadOnlyList<WorldFile> World, uint SaveIdentity) BuildWorldSnapshot(string gameDirectory)
    {
        if (!_hostReturnSaveReady || _hostReturnSavePending || _scriptHostRefreshWanted)
        {
            return ([], 0);
        }

        string? currentSave = SaveDigest.FindLiveSessionSave(gameDirectory);
        if (currentSave is null)
        {
            return ([], 0);
        }

        if (!SaveDigest.TryComputeSaveIdentity(currentSave, out uint identity))
        {
            return ([], 0);
        }

        if (!SaveDigest.TryReadActiveModuleName(currentSave, out string savedModule)
            || !bridge.TryReadCurrentModule(out string liveModule)
            || !string.Equals(savedModule, liveModule, StringComparison.OrdinalIgnoreCase))
        {
            Log?.Invoke("session-save: refusing world snapshot whose saved module is not the live return module"
                + " - requesting a fresh host save");
            _hostFreshSaveWanted = true;
            return ([], 0);
        }

        // The provider is invoked at transfer time, not when the listener starts. If the
        // resolver still points at a stale staged archive while the live bridge has already
        // observed the return-module save, refuse to serve that archive rather than handing a
        // client the solo-leg world. The next request retries after the cache settles.
        uint liveIdentity = bridge.SaveIdentityDigest;
        if (liveIdentity != 0 && liveIdentity != identity)
        {
            Log?.Invoke($"session-save: refusing stale world snapshot identity 0x{identity:X8}; "
                + $"live world is 0x{liveIdentity:X8} - requesting a fresh host save");
            _hostFreshSaveWanted = true;
            return ([], 0);
        }

        IReadOnlyList<WorldFile> world = WorldTransfer.ReadSaveDirectory(currentSave);
        (manager.Current as CoopHost)?.NoteServedWorld(identity);
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
    private void TriggerRejoin(RejoinTrigger trigger, ICoopSession expectedSession, Action? onCompleted = null,
        bool dedicatedCaptureReady = false)
    {
        if (!OwnLoadCaptureSafetyRule.MayAutomaticallyAdopt(
                _ownLoadCaptureRequiresConfirmedSave,
                _verifiedOwnLoadAwaitingRejoin,
                trigger == RejoinTrigger.OwnLoad))
        {
            Log?.Invoke($"session-save: {trigger} automatic adoption held until a safe own-character save is available");
            return;
        }

        if (!manager.TryGetCurrentGeneration(expectedSession, out long managerGeneration))
        {
            return;
        }

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
            RejoinTrigger.SoloReturn => AutoAdoptWorldEnabled,
            RejoinTrigger.ManualWorldRefresh => true,
            RejoinTrigger.ScriptWorldRefresh => AutoAdoptWorldEnabled,
            _ => false,
        };

        (GffStruct Player, SidecarMetadata Metadata)? worldlessCheckpoint = null;
        if (gateEnabled && !dedicatedCaptureReady
            && trigger is RejoinTrigger.HostReload or RejoinTrigger.SaveMismatch or RejoinTrigger.WipeRecovery)
        {
            bool recoveryReadable = bridge.TryReadLocalState(out PlayerState recoveryLocal);
            bool incapacitated = trigger == RejoinTrigger.WipeRecovery
                || recoveryReadable && (recoveryLocal.IsDowned || recoveryLocal.IsPartyWiped);
            bool controlledSceneAborted = ControlledSceneRecoveryRule.MayUseLivingCheckpoint(trigger,
                _controlledSceneReturnPending, recoveryReadable, recoveryLocal,
                _kotor?.IsPartyScriptControlled ?? false);
            bool worldlessReload = trigger == RejoinTrigger.HostReload && !recoveryReadable
                && bridge.IsVerifiedWorldlessMenu();
            if (worldlessReload)
            {
                if (!WorldlessRecoveryCheckpoint.TryRead(Path.Combine(gameDirectory, "saves"), out var own, out var metadata)
                    || !bridge.IsVerifiedWorldlessMenu())
                {
                    Reply("recovery held: no content-bound living own checkpoint is available; your connection and saves were retained");
                    return;
                }
                worldlessCheckpoint = (own, metadata);
                dedicatedCaptureReady = true;
                Log?.Invoke("host reload while at the verified main menu: carrying the content-bound living own checkpoint; unsaved changes cannot be recovered");
            }
            else if (incapacitated || controlledSceneAborted)
            {
                if (!TryUseAutomaticRecoveryCheckpoint())
                {
                    if (trigger == RejoinTrigger.WipeRecovery)
                        _wipeRecoveryState = WipeRecoveryState.Initial;
                    Reply("recovery held: no verified living character checkpoint is available; your connection and saves were retained");
                    return;
                }
                if (controlledSceneAborted)
                    Log?.Invoke("controlled scene interrupted by a host load: carrying the verified living character checkpoint instead of waiting for the abandoned local script to save");
                dedicatedCaptureReady = true; // Verified checkpoint; a dead/abandoned scripted actor cannot save safely.
            }
            else
            {
                if (_controlledSceneReturnPending) trigger = RejoinTrigger.SoloReturn;
                _automaticRejoinCapture ??= new(expectedSession, managerGeneration, trigger, onCompleted, SessionClock.NowMs);
                return;
            }
        }

        (GffStruct Player, SidecarMetadata Metadata)? ownLoadCharacter = worldlessCheckpoint;
        if (trigger == RejoinTrigger.OwnLoad && gateEnabled
            && ClientCharacterPersistence.TryReadSidecar(
                Path.Combine(gameDirectory, "saves"), out GffStruct ownPlayer, out SidecarMetadata ownMetadata, out _))
        {
            ownLoadCharacter = (ownPlayer, ownMetadata);
        }
        if (trigger == RejoinTrigger.OwnLoad && gateEnabled && ownLoadCharacter is null)
        {
            Log?.Invoke("session-save: OwnLoad rejoin refused because the verified character sidecar could not be read");
            return;
        }

        (bool shouldStart, RejoinDecisionState next) = ClientLoadRejoinRule.Decide(_rejoinState, trigger, gateEnabled);
        _rejoinState = next;

        // Whatever fired, the host's pending serial edge (if any) belongs to this same load:
        // one host load, one rejoin. Disarmed even when the gate refuses, for the same reason.
        _hostWorldReplacedState = HostWorldReplacedRule.Disarm();
        if (trigger == RejoinTrigger.WipeRecovery)
            _wipeRecoveryState = WipeRecoveryState.Initial;

        if (!shouldStart)
        {
            if (!gateEnabled)
            {
                Log?.Invoke($"session-save: {trigger} rejoin trigger seen - NOT acting (gate off)");
            }

            return;
        }

        PartyRoster adoptedParty = expectedSession.TryGetPartyRoster(out PartyRoster requestedParty) ? requestedParty : PartyRoster.Empty;
        _skipNextDisconnectCapture = true;
        _controlledSceneReturnPending = false;
        Log?.Invoke($"session-save: starting rejoin ({trigger})");

        // A queued worker carries the manager epoch captured synchronously with this trigger,
        // so it cannot adopt against a newer session even if cancellation has not reached it.
        _rejoinCts?.Cancel();
        CancellationTokenSource rejoinCts = new();
        _rejoinCts = rejoinCts;
        int rejoinGeneration = ++_rejoinGeneration;
        _rejoinOwnsDisconnect = true;

        (GffStruct Player, SidecarMetadata Metadata)? carried = ownLoadCharacter;
        if (carried is null && trigger != RejoinTrigger.OwnLoad && ClientCharacterPersistence.TryReadSidecar(
            Path.Combine(gameDirectory, "saves"), out GffStruct sidecarPlayer, out SidecarMetadata sidecarMeta, out _))
        {
            carried = (sidecarPlayer, sidecarMeta);
        }

        if (trigger == RejoinTrigger.OwnLoad)
        {
            // The worker now owns an immutable in-memory copy. Later saves may update the
            // sidecar without changing the character this rejoin carries.
            _verifiedOwnLoadAwaitingRejoin = false;
        }

        _rejoinTask = Task.Run(async () =>
        {
            try
            {
                RejoinAdoption? adoption = null;
                int maxAttempts = trigger switch
                {
                    RejoinTrigger.SoloReturn => SoloReturnRejoinMaxAttempts,
                    RejoinTrigger.HostReload or RejoinTrigger.SaveMismatch or RejoinTrigger.ManualWorldRefresh or RejoinTrigger.ScriptWorldRefresh => HostReloadRejoinMaxAttempts,
                    _ => 1,
                };
                TimeSpan retryDelay = trigger switch
                {
                    RejoinTrigger.SoloReturn => TimeSpan.FromMilliseconds(SoloReturnRejoinRetryDelayMs),
                    RejoinTrigger.HostReload or RejoinTrigger.SaveMismatch or RejoinTrigger.ManualWorldRefresh or RejoinTrigger.ScriptWorldRefresh
                        => TimeSpan.FromMilliseconds(HostReloadRejoinRetryDelayMs),
                    _ => TimeSpan.Zero,
                };
                bool hostWorldUnavailable = false;
                bool hostWorldWaitNoticeSent = false;
                int attemptsMade = 0;
                bool adoptedSuccessfully = await RejoinRetryRunner.RunAsync(
                    maxAttempts,
                    retryDelay,
                    async (attempt, cancellationToken) =>
                    {
                        attemptsMade = attempt;
                        hostWorldUnavailable = false;
                        adoption = await manager
                        .RejoinAfterLoadAsync(
                            bridge, gameDirectory, carried, managerGeneration, cancellationToken,
                            carriedIsAuthoritative: dedicatedCaptureReady || trigger is RejoinTrigger.SoloReturn or RejoinTrigger.OwnLoad or RejoinTrigger.ManualWorldRefresh or RejoinTrigger.ScriptWorldRefresh
                                || JoinModeSetting == JoinMode.Auto,
                            worldTransferUdpFallback: WorldTransferUdpFallbackEnabled,
                            onHostWorldUnavailable: trigger is RejoinTrigger.HostReload or RejoinTrigger.SaveMismatch or RejoinTrigger.ManualWorldRefresh or RejoinTrigger.ScriptWorldRefresh
                                ? () => hostWorldUnavailable = true
                                : null)
                        .ConfigureAwait(false);
                        if (adoption is null && attempt < maxAttempts
                            && (trigger == RejoinTrigger.SoloReturn
                                || trigger is RejoinTrigger.HostReload or RejoinTrigger.SaveMismatch or RejoinTrigger.ManualWorldRefresh or RejoinTrigger.ScriptWorldRefresh
                                    && hostWorldUnavailable))
                        {
                            string reason = trigger switch
                            {
                                RejoinTrigger.HostReload => "the host has not finished making the reloaded world available",
                                RejoinTrigger.SaveMismatch => "the host is still saving its current world",
                                _ => "SoloReturn adoption failed",
                            };
                            Log?.Invoke($"session-save: {reason} on attempt {attempt}; "
                                + $"retrying in {retryDelay.TotalSeconds:0}s");
                            if (trigger == RejoinTrigger.HostReload && !hostWorldWaitNoticeSent)
                            {
                                hostWorldWaitNoticeSent = true;
                                Reply("the host is finishing the reload; waiting for their world to become available");
                            }
                        }
                        return adoption is not null;
                    },
                    () => !rejoinCts.IsCancellationRequested
                        && rejoinGeneration == _rejoinGeneration
                        && manager.LifecycleGeneration == managerGeneration
                        && _rejoinState.Phase == RejoinPhase.InFlight,
                    rejoinCts.Token,
                    shouldRetryFailedAttempt: () =>
                        trigger is not (RejoinTrigger.HostReload or RejoinTrigger.SaveMismatch or RejoinTrigger.ManualWorldRefresh or RejoinTrigger.ScriptWorldRefresh) || hostWorldUnavailable)
                    .ConfigureAwait(false);

                if (!adoptedSuccessfully || adoption is null)
                {
                    if (rejoinCts.IsCancellationRequested
                        || rejoinGeneration != _rejoinGeneration
                        || manager.LifecycleGeneration != managerGeneration)
                    {
                        return;
                    }

                    // The rejoin disconnected to fetch the world; a failed fetch must not leave the
                    // joiner out of the session (live 2026-09-26: stranded "disconnected" until a
                    // manual reconnect). Reconnect as-is — degraded if the worlds still differ —
                    // and the mismatch rule's own backoff tries the adoption again.
                    Reply($"rejoin failed after {attemptsMade} attempt(s) - reconnecting as-is and trying again later");
                    manager.Reconnect();
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
                    if (rejoinCts.IsCancellationRequested
                        || rejoinGeneration != _rejoinGeneration
                        || manager.LifecycleGeneration != adoption.Generation
                        || _rejoinState.Phase != RejoinPhase.InFlight)
                    {
                        return;
                    }

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

                    await Task.Delay(500, rejoinCts.Token).ConfigureAwait(false);
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
                // The manager serializes this generation check with external connect and
                // disconnect commands. Its callback rebases the local load watch and world
                // shadows only while this adoption still owns the lifecycle.
                if (rejoinCts.IsCancellationRequested
                    || rejoinGeneration != _rejoinGeneration
                    || manager.LifecycleGeneration != adoption.Generation
                    || _rejoinState.Phase != RejoinPhase.InFlight
                    || !manager.CompleteRejoin(adoption.Generation, rejoinCts.Token, () =>
                    {
                        // This load belongs to this rejoin. The session watch is intentionally
                        // suspended while the socket is down, so consume the settled local world
                        // identity and serial as its new baseline before the first post-reconnect
                        // tick can mistake the adoption for another player load.
                        uint? adoptedLoadSerial = bridge.TryReadWorldLoadSerial(out uint settledSerial)
                            ? settledSerial : null;
                        _sessionSaveWatch?.RebaseOwnVerdictAfterAdoptedLoad(
                            bridge.SaveIdentityDigest, adoptedLoadSerial);

                        _wipeRecoveryState = WipeRecoveryState.Initial;
                        _wipeTeardownWithoutOwnLoad = false;
                        bridge.ForgetWorldShadows();
                        bridge.RecordPartyAdoptionProof(adoptedParty, adoption.World.CompanionCacheFingerprints ?? [], adoption.World.SaveIdentity);
                        if (adoption.World.StagedSaveDirectory is { } adoptedSource
                            && adoptedParty.RefreshSaveIdentity == adoption.World.SaveIdentity && adoptedParty.RefreshRevision != 0
                            && PartySaveProvenance.TryWriteVerified(adoptedSource, adoptedParty,
                                adoption.World.CompanionCacheFingerprints ?? [], out string partyOutcome))
                            Log?.Invoke(partyOutcome);
                        onCompleted?.Invoke();
                    }))
                {
                    return;
                }

                Reply($"rejoined '{adoption.World.Module}'");
                long reconnectedGeneration = adoption.Generation + 1;
                if (!rejoinCts.IsCancellationRequested
                    && rejoinGeneration == _rejoinGeneration
                    && manager.LifecycleGeneration == reconnectedGeneration
                    && manager.Current is not null)
                {
                    _postAdoptionGitNotice.Arm(
                        adoption.World.Module, SessionClock.NowMs, reconnectedGeneration);
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"session-save: rejoin failed - {ex.Message}");
            }
            finally
            {
                if (rejoinGeneration == _rejoinGeneration)
                {
                    _rejoinState = ClientLoadRejoinRule.Complete(_rejoinState);
                    _rejoinCts = null;
                    _rejoinOwnsDisconnect = false;
                }
                rejoinCts.Dispose();
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

        if (name.Equals(CoopSaveSlot.SlotLabel, StringComparison.OrdinalIgnoreCase))
        {
            if (RequestDedicatedCoopSave(quiet: false) is null)
                Reply("Co-op save unavailable: the game is busy or not ready to save; try again when normal control returns");
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

        string savesDirectory = Path.Combine(gameDirectory, "saves");
        if (!PluginMenuLoader.TryResolveSaveDirectory(gameDirectory, slotIndex, out string selectedSave, out string selectedDiagnostic))
        {
            Reply($"loadgame: {selectedDiagnostic}");
            return;
        }

        if (!ClientCharacterPersistence.TryHashSave(selectedSave, out string selectedHash, out long selectedLength))
        {
            Reply("loadgame: could not safely snapshot the selected save");
            return;
        }

        PartySaveProvenance.TryRead(selectedSave, out PartySaveProvenance.Snapshot? partyProvenance);
        if (!_kotor.TryStageMidSessionLoadFromDirectory(selectedSave, out string staging, out string stagedSave))
        {
            Reply($"loadgame: {staging}");
            return;
        }

        if (!ClientCharacterPersistence.TryHashSave(stagedSave, out string stagedHash, out long stagedLength)
            || stagedLength != selectedLength
            || !string.Equals(stagedHash, selectedHash, StringComparison.Ordinal))
        {
            Reply("loadgame: selected save changed while staging; request was not sent");
            return;
        }

        if (!bridge.TryReadWorldLoadSerial(out uint startingWorldLoadSerial))
        {
            Log?.Invoke("loadgame: could not read the current world-load serial; request was not sent");
            Reply("loadgame: not ready yet - try again after the area finishes loading");
            return;
        }

        if (!bridge.TryReadLivenessEpoch(out uint startingPartyEpoch) || startingPartyEpoch == 0)
        {
            Log?.Invoke("loadgame: native world epoch unavailable; request was not sent");
            Reply("loadgame: not ready yet - try again after the area finishes loading");
            return;
        }

        if (!_kotor.TryRequestStagedMidSessionLoad(out string request, out uint requestId, manualIntent: true))
        {
            Reply($"loadgame: {request}");
            return;
        }

        _partyReload = partyProvenance is null || partyProvenance.SaveLength != selectedLength
            || !partyProvenance.SaveSha256.Equals(selectedHash, StringComparison.Ordinal) ? null
            : new(selectedSave, stagedSave, partyProvenance, requestId,
            startingWorldLoadSerial, startingPartyEpoch, SessionClock.NowMs);
        _pendingCommandOwnLoadCharacterSource = new OwnLoadCharacterSource(
            ProvenanceGeneration: 0,
            WorldLoadSerialAtRequest: startingWorldLoadSerial,
            Directory: selectedSave,
            SaveSha256: selectedHash,
            SaveLength: selectedLength,
            ObservedAtMs: SessionClock.NowMs,
            CommandRequestId: requestId,
            CommandStartWorldLoadSerial: startingWorldLoadSerial);

        Reply($"loadgame: {staging}; {request}");

        KotorGameBridge kotor = _kotor;
        _loadGameTask = Task.Run(async () =>
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (kotor.TryGetAdoptedWorldLoadStatus(out uint applied, out int status)
                    && applied == requestId)
                {
                    if (status != 0)
                    {
                        _pendingCommandOwnLoadCharacterSource = null;
                    }
                    Reply(status == 0
                        ? "loadgame: the world is up"
                        : $"loadgame: the plugin refused (failure {status})");
                    return;
                }

                await Task.Delay(500).ConfigureAwait(false);
            }

            Reply("loadgame: no result within 90s - check the plugin log");
            if (_pendingCommandOwnLoadCharacterSource?.CommandRequestId == requestId)
            {
                _pendingCommandOwnLoadCharacterSource = null;
            }
        });
    }

    private void ObserveOwnLoadCharacterSource(
        LoadProvenance? provenance,
        uint? currentWorldLoadSerial,
        long nowMs,
        string savesDirectory)
    {
        if (provenance is null)
        {
            return;
        }

        if (_lastOwnLoadProvenanceGeneration is null)
        {
            // The first mailbox value may be retained from a load before this session.
            _lastOwnLoadProvenanceGeneration = provenance.Generation;
            return;
        }

        if (_lastOwnLoadProvenanceGeneration == provenance.Generation)
        {
            return;
        }

        uint? previousGeneration = _lastOwnLoadProvenanceGeneration;
        _lastOwnLoadProvenanceGeneration = provenance.Generation;
        _pendingOwnLoadCharacterSource = null;
        if (_pendingCommandOwnLoadCharacterSource is { CommandStartWorldLoadSerial: uint commandSerial })
        {
            if (provenance.Source == LoadProvenanceSource.ServerLoadRequest
                && provenance.Status == LoadProvenanceStatus.Captured
                && provenance.WorldLoadSerialAtRequest == commandSerial)
            {
                return;
            }

            // The mid-session wrapper first advances the world-load serial while tearing
            // down, then invokes the main-menu directory loader against the exact staged
            // AUTOSAVE. Retain the command's source only when that callback is at the
            // teardown's next serial and its bytes still match the explicitly selected save.
            if (OwnLoadCharacterSourceRule.IsCommandProvenanceFor(
                _pendingCommandOwnLoadCharacterSource, provenance, savesDirectory))
            {
                return;
            }

            _pendingCommandOwnLoadCharacterSource = null;
        }

        if (currentWorldLoadSerial.HasValue
            && OwnLoadCharacterSourceRule.TryCreate(
                provenance,
                previousGeneration,
                savesDirectory,
                currentWorldLoadSerial.Value,
                nowMs,
                out OwnLoadCharacterSource? candidate))
        {
            _pendingOwnLoadCharacterSource = candidate;
        }
    }

    private bool TryCaptureCompletedOwnLoadCharacter(
        LoadProvenance? provenance,
        uint? currentWorldLoadSerial,
        long nowMs,
        string savesDirectory)
    {
        OwnLoadCharacterSource? commandSource = _pendingCommandOwnLoadCharacterSource;
        OwnLoadCharacterSource? source = commandSource ?? _pendingOwnLoadCharacterSource;
        bool completed = false;
        if (currentWorldLoadSerial.HasValue && commandSource is not null
            && _kotor is not null
            && _kotor.TryGetAdoptedWorldLoadStatus(out uint applied, out int status))
        {
            completed = OwnLoadCharacterSourceRule.IsCommandCompletedFor(
                commandSource, currentWorldLoadSerial.Value, applied, status, nowMs);
        }
        else if (currentWorldLoadSerial.HasValue && commandSource is null)
        {
            completed = OwnLoadCharacterSourceRule.IsCompletedFor(
                source, provenance, true, currentWorldLoadSerial.Value, nowMs);
        }

        if (!completed || source is null)
        {
            if (!_ownLoadCaptureWaitNoticed)
            {
                _ownLoadCaptureWaitNoticed = true;
                Log?.Invoke("session-save: waiting for the exact selected-character source to be verified");
            }
            return false;
        }

        if (!_sessionSaveWatch!.CaptureVerifiedLoadSource(source!, out string outcome))
        {
            Log?.Invoke($"session-save: selected character capture failed: {outcome}");
            return false;
        }

        Log?.Invoke($"session-save: selected character captured from '{Path.GetFileName(source!.Directory)}': {outcome}");
        _ownLoadCaptureWaitNoticed = false;
        _pendingOwnLoadCharacterSource = null;
        _pendingCommandOwnLoadCharacterSource = null;
        return true;
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

        if (_ownLoadVerdictPending || _skipNextDisconnectCapture || _ownLoadCaptureRequiresConfirmedSave)
        {
            _skipNextDisconnectCapture = false;
            Log?.Invoke("character capture: selected-load character protection is active; skipped disconnect save");
            return;
        }

        string? currentSave = CaptureDedicatedDisconnectSave();
        if (currentSave is null)
        {
            Reply("character capture: fresh disconnect save unavailable; kept your last verified character checkpoint (QuickSave untouched)");
            return;
        }
        Reply("co-op character saved from the completed dedicated disconnect save (QuickSave untouched)");
    }
}
