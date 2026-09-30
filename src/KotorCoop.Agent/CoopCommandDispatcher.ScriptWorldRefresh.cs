using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

public sealed partial class CoopCommandDispatcher
{
    private ICoopSession? _scriptRefreshSession;
    private JftsClassRefreshState _scriptClassWatch;
    private bool _scriptHostRefreshWanted;
    private uint _scriptHostLoadSerial;
    private long _scriptRetryAtMs;
    private long _scriptCompletedToken;
    private ScriptSaveCapture? _scriptSaveCapture;

    private sealed class ScriptSaveCapture(ICoopSession session, long generation, string module,
        uint loadSerial, uint hostLoadSerial, long token, string source, DateTime stampBefore, long nowMs)
    {
        public readonly ICoopSession Session = session;
        public readonly long Generation = generation;
        public readonly string Module = module;
        public readonly uint LoadSerial = loadSerial;
        public readonly uint HostLoadSerial = hostLoadSerial;
        public readonly long Token = token;
        public readonly string Source = source;
        public readonly DateTime StampBefore = stampBefore;
        public readonly long RequestedMs = nowMs;
        public DateTime ObservedStamp;
        public long StableSinceMs;
        public bool Answered;
        public bool Refused;
    }

    private bool JftsDetected() => !string.IsNullOrEmpty(gameDirectory)
        && new[] { "jfts_c_jedi.ncs", "jfts_c_jfts.ncs", "jfts_ins.ncs" }
            .All(marker => File.Exists(Path.Combine(gameDirectory, "Override", marker)));

    private bool ScriptSaveIsSafe(PlayerState state) => ManualWorldRefreshRule.IsSafe(state)
        && !bridge.IsInDialogue && !(_kotor?.IsLocalFullScreenGuiOpen ?? false);

    private string? RequestDedicatedCoopSave(bool quiet, ScriptSaveCapture? capture = null)
    {
        if (string.IsNullOrEmpty(gameDirectory) || !bridge.SupportsCheats
            || _pendingCheatSerial != 0 || _queuedCheats.Count != 0
            || !bridge.TryReadLocalState(out PlayerState local) || !ScriptSaveIsSafe(local)) return null;
        string saves = Path.Combine(gameDirectory, "saves");
        Directory.CreateDirectory(saves);
        string[] folders = Directory.GetDirectories(saves).Select(Path.GetFileName).OfType<string>().ToArray();
        string folder = CoopSaveSlot.ResolveFolderName(folders);
        int dash = folder.IndexOf(" - ", StringComparison.Ordinal);
        if (dash < 0 || !int.TryParse(folder.AsSpan(0, dash), out int number) || number < 2) return null;
        string path = Path.Combine(saves, folder);
        SendCheat(new CheatRequest(CheatKinds.CoopSave, Amount: number, Text: CoopSaveSlot.SlotLabel),
            onApplied: capture is null ? null : () => capture.Answered = true,
            onRefused: capture is null ? null : () => capture.Refused = true, quiet: quiet);
        return path;
    }

    private void MaintainScriptWorldRefresh()
    {
        ICoopSession? session = manager.Current;
        long nowMs = SessionClock.NowMs;
        if (_scriptRefreshSession != session)
        {
            if (_rejoinOwnsDisconnect) return;
            _scriptRefreshSession = session;
            _scriptClassWatch = default;
            _scriptHostRefreshWanted = false;
            _scriptSaveCapture = null;
            _scriptRetryAtMs = 0;
        }
        if (session is null || !JftsDetected()) return;

        if (session is CoopHost && bridge.TryReadWorldLoadSerial(out uint hostSerial))
        {
            bridge.TryReadClassGrant(out uint classSerial, out int classId);
            string module = bridge.TryReadCurrentModule(out string currentModule) ? currentModule : string.Empty;
            (bool refresh, JftsClassRefreshState next) = ScriptWorldRefreshRule.ObserveHostClass(
                _scriptClassWatch, classSerial, classId, hostSerial, module, true);
            _scriptClassWatch = next;
            if (refresh)
            {
                _scriptHostRefreshWanted = true;
                _scriptHostLoadSerial = hostSerial;
                Log?.Invoke("script-world: host JFTS class changed; waiting for script and level-up to finish before saving");
            }
            if (_scriptHostRefreshWanted && hostSerial != _scriptHostLoadSerial)
            {
                _scriptHostRefreshWanted = false;
                _scriptSaveCapture = null;
            }
        }

        if (_scriptSaveCapture is { } capture)
        {
            MaintainScriptSaveCapture(capture, nowMs);
            return;
        }
        if (nowMs < _scriptRetryAtMs || _captureRequestedMs != 0 || _hostReturnSavePending
            || _soloCapturePending || _soloCaptureWaiting || _ownLoadVerdictPending
            || _ownLoadCaptureRequiresConfirmedSave || _verifiedOwnLoadAwaitingRejoin
            || _rejoinState.Phase == RejoinPhase.InFlight
            || bridge.TryGetNewCharacterCapture(out uint chargen, out _, out _)
                && chargen is ChargenStateArmed or ChargenStateSaving
            || !bridge.TryReadLocalState(out PlayerState state) || !ScriptSaveIsSafe(state)
            || !bridge.TryReadWorldLoadSerial(out uint loadSerial)
            || !manager.TryGetCurrentGeneration(session, out long generation)) return;

        long token = 0;
        uint remoteLoadSerial = 0;
        if (session is CoopClient client)
        {
            token = ScriptWorldRefreshRule.Token(client.HostSessionId, client.RemoteWorldDigest);
            if (!AutoAdoptWorldEnabled || token == 0 || token == Interlocked.Read(ref _scriptCompletedToken)
                || !ScriptWorldRefreshRule.CanCaptureCharacter(
                    manager.Agent?.ClassGrantSyncEnabled ?? true, bridge.ReadLocalClassIds())
                || client.GetRemotePeers().FirstOrDefault(p => p.PeerId == CoopHost.HostPeerId)?.State
                    is not { } hostState || !ManualWorldRefreshRule.IsSafe(hostState)
                || !HostWorldReplacedRule.IsHostReady(hostState, client.RemoteWorldDigest.Module)) return;
            remoteLoadSerial = client.RemoteWorldDigest.LoadSerial;
        }
        else if (!_scriptHostRefreshWanted) return;

        string saves = Path.Combine(gameDirectory, "saves");
        Directory.CreateDirectory(saves);
        string folder = CoopSaveSlot.ResolveFolderName(Directory.GetDirectories(saves)
            .Select(Path.GetFileName).OfType<string>());
        string source = Path.Combine(saves, folder);
        ScriptSaveCapture pending = new(session, generation, state.Module, loadSerial,
            remoteLoadSerial, token, source, SaveStamp(source), nowMs);
        if (RequestDedicatedCoopSave(quiet: true, pending) is null) return;
        _scriptSaveCapture = pending;
        Log?.Invoke(session is CoopHost
            ? "script-world: requesting fresh host Co-op save after JFTS"
            : "script-world: requesting fresh own-character Co-op save before adopting post-JFTS world");
    }

    private bool IsScriptSaveCaptureCurrent(ScriptSaveCapture capture) =>
        manager.Current == capture.Session && manager.LifecycleGeneration == capture.Generation
        && bridge.TryReadLocalState(out PlayerState local) && ScriptSaveIsSafe(local)
        && bridge.TryReadWorldLoadSerial(out uint serial)
        && ManualWorldRefreshRule.IsSameWorld(capture.Module, capture.LoadSerial, local, serial)
        && (capture.Session is not CoopClient client
            || ScriptWorldRefreshRule.MatchesCapturedHost(capture.HostLoadSerial, capture.Token,
                client.HostSessionId, client.RemoteWorldDigest,
                client.GetRemotePeers().FirstOrDefault(p => p.PeerId == CoopHost.HostPeerId)?.State));

    private void RetryScriptSaveCapture(long nowMs, string reason)
    {
        _scriptSaveCapture = null;
        _scriptRetryAtMs = nowMs + 5_000;
        Log?.Invoke($"script-world: {reason}; retrying after the world settles");
    }

    private void MaintainScriptSaveCapture(ScriptSaveCapture capture, long nowMs)
    {
        if (!IsScriptSaveCaptureCurrent(capture) || capture.Refused || nowMs - capture.RequestedMs > 30_000)
        {
            RetryScriptSaveCapture(nowMs, "fresh save not confirmed safely");
            return;
        }
        DateTime stamp = SaveStamp(capture.Source);
        if (!capture.Answered || stamp <= capture.StampBefore) return;
        if (stamp != capture.ObservedStamp)
        {
            capture.ObservedStamp = stamp;
            capture.StableSinceMs = nowMs;
            return;
        }
        if (nowMs - capture.StableSinceMs < 750
            || !ClientCharacterPersistence.IsCaptureSourceReady(capture.Source)
            || !SaveDigest.TryReadActiveModuleName(capture.Source, out string savedModule)
            || !savedModule.Equals(capture.Module, StringComparison.OrdinalIgnoreCase)
            || !SaveDigest.TryComputeSaveIdentity(capture.Source, out uint savedIdentity)) return;

        if (capture.Session is CoopHost host)
        {
            if (!IsScriptSaveCaptureCurrent(capture) || SaveStamp(capture.Source) != stamp)
            {
                RetryScriptSaveCapture(nowMs, "host world changed while checking the save");
                return;
            }
            _scriptHostRefreshWanted = false;
            _scriptSaveCapture = null;
            host.PublishScriptWorldRefresh(savedIdentity);
            Log?.Invoke($"script-world: post-JFTS host save ready (0x{savedIdentity:X8}); announcing refresh");
        }
        else if (ClientCharacterPersistence.TryHashSave(capture.Source, out string hash, out long length)
            && ClientCharacterPersistence.TryCaptureVerifiedLoadSource(
                Path.Combine(gameDirectory, "saves"), capture.Source, hash, length,
                capture.Session.RemoteWorldDigest.SaveIdentity, allowStagedDirectory: false, out string outcome))
        {
            if (outcome is not null) Log?.Invoke(outcome);
            // Character extraction reads several resources from disk. Session or either
            // engine may have moved while that happened; never adopt on its earlier verdict.
            if (!IsScriptSaveCaptureCurrent(capture) || SaveStamp(capture.Source) != stamp)
            {
                RetryScriptSaveCapture(nowMs, "world changed while capturing the character");
                return;
            }
            _scriptSaveCapture = null;
            _scriptRetryAtMs = nowMs + 30_000;
            _skipNextDisconnectCapture = true;
            Reply("Jedi story changes are ready - rejoining the host with your own character");
            TriggerRejoin(RejoinTrigger.ScriptWorldRefresh, capture.Session,
                () => Interlocked.Exchange(ref _scriptCompletedToken, capture.Token));
        }
        else
        {
            RetryScriptSaveCapture(nowMs, "fresh character extraction was refused");
        }
    }
}
