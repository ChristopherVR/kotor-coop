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
    private readonly uint[] _companionRefreshGeneration = new uint[9];
    private uint _scriptHostRefreshReason;

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
        public PartyRoster Party;
        public uint Reason;
        public PartyRoster NativeParty;
    }

    private bool JftsDetected() => !string.IsNullOrEmpty(gameDirectory)
        && new[] { "jfts_c_jedi.ncs", "jfts_c_jfts.ncs", "jfts_ins.ncs" }
            .All(marker => File.Exists(Path.Combine(gameDirectory, "Override", marker)));

    private bool ScriptSaveIsSafe(PlayerState state) => ManualWorldRefreshRule.IsSafe(state)
        && !bridge.IsInDialogue && !(_kotor?.IsLocalFullScreenGuiOpen ?? false);

    private string? RequestDedicatedCoopSave(bool quiet, ScriptSaveCapture? capture = null,
        Func<string, bool>? onPrepared = null, Action? onApplied = null, Action? onRefused = null)
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
        if (onPrepared is not null && !onPrepared(path)) return null;
        SendCheat(new CheatRequest(CheatKinds.CoopSave, Amount: number, Text: CoopSaveSlot.SlotLabel),
            onApplied: () => { if (capture is not null) capture.Answered = true; onApplied?.Invoke(); },
            onRefused: () => { if (capture is not null) capture.Refused = true; onRefused?.Invoke(); }, quiet: quiet);
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
            _scriptHostRefreshReason = 0;
            Array.Clear(_companionRefreshGeneration);
        }
        if (session is null) return;

        if (session is CoopHost && bridge.TryReadWorldLoadSerial(out uint hostSerial))
        {
            bridge.TryReadClassGrant(out uint classSerial, out int classId);
            string module = bridge.TryReadCurrentModule(out string currentModule) ? currentModule : string.Empty;
            (bool refresh, JftsClassRefreshState next) = ScriptWorldRefreshRule.ObserveHostClass(
                _scriptClassWatch, classSerial, classId, hostSerial, module, JftsDetected());
            _scriptClassWatch = next;
            if (refresh)
            {
                _scriptHostRefreshWanted = true;
                _scriptHostLoadSerial = hostSerial;
                _scriptHostRefreshReason |= 1;
                Log?.Invoke("script-world: host JFTS class changed; waiting for script and level-up to finish before saving");
            }
            if (bridge.TryReadLocalPartyRoster(out PartyRoster roster))
            {
                for (int slot = 0; slot < 9; slot++)
                {
                    PartyOccupantIdentity occupant = roster.OccupantFor(slot);
                    if (!roster.IsInParty(slot) || !PartySavedOccupantRule.RequiresBinding(slot, occupant) || occupant.Generation == 0
                        || occupant.Template.Length == 0 || _companionRefreshGeneration[slot] == occupant.Generation) continue;
                    _companionRefreshGeneration[slot] = occupant.Generation;
                    _scriptHostRefreshWanted = true;
                    _scriptHostLoadSerial = hostSerial;
                    _scriptHostRefreshReason |= 2;
                    Log?.Invoke($"script-world: verified active companion replacement in slot {slot}; waiting for a safe saved refresh");
                }
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
                || (client.RemoteWorldDigest.ScriptRefreshReason & 1) != 0
                    && !ScriptWorldRefreshRule.CanCaptureCharacter(
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
        pending.Reason = session is CoopClient refreshClient ? refreshClient.RemoteWorldDigest.ScriptRefreshReason : _scriptHostRefreshReason;
        if ((pending.Reason & 2) != 0)
        {
            if (session is CoopHost refreshHost)
            {
                if (!bridge.TryReadLocalPartyRoster(out pending.NativeParty)
                    || !refreshHost.TryCaptureCompanionRefreshRoster(out pending.Party)
                    || !SameNativeParty(pending.NativeParty, pending.Party)
                    || Enumerable.Range(0, 9).Any(slot => pending.Party.IsInParty(slot)
                        && PartySavedOccupantRule.RequiresBinding(slot, pending.Party.OccupantFor(slot))
                        && !ModSupportProfile.IsResref(pending.Party.OccupantFor(slot).Template))) return;
            }
            else if (session is CoopClient partyClient)
            {
                if (!partyClient.TryGetPartyRoster(out pending.Party)
                    || pending.Party.RefreshSaveIdentity != partyClient.RemoteWorldDigest.ScriptRefreshSaveIdentity
                    || pending.Party.RefreshRevision != partyClient.RemoteWorldDigest.ScriptRefreshRevision
                    || Enumerable.Range(0, 9).Any(slot => pending.Party.IsInParty(slot)
                        && PartySavedOccupantRule.RequiresBinding(slot, pending.Party.OccupantFor(slot))
                        && pending.Party.OccupantFor(slot).CacheFingerprint.Length != 64)) return;
            }
        }
        if (RequestDedicatedCoopSave(quiet: true, pending) is null) return;
        _scriptSaveCapture = pending;
        Log?.Invoke(session is CoopHost
            ? "script-world: requesting fresh host Co-op save after verified script changes"
            : "script-world: requesting fresh own-character Co-op save before adopting script changes");
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

    private static bool SameNativeParty(PartyRoster native, PartyRoster host) =>
        native.HostLoadSerial == host.HostLoadSerial && native.HostNativeEpoch == host.HostNativeEpoch
        && native.AvailableMask == host.AvailableMask && native.PartyMask == host.PartyMask
        && Enumerable.Range(0, 9).All(slot => native.ObjectIdFor(slot) == host.ObjectIdFor(slot)
            && native.OccupantFor(slot).Generation == host.OccupantFor(slot).Generation
            && native.OccupantFor(slot).Template == host.OccupantFor(slot).Template
            && native.OccupantFor(slot).Replaced == host.OccupantFor(slot).Replaced);

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
            || !ClientCharacterPersistence.TryHashSave(capture.Source, out string verifiedHash, out long verifiedLength)
            || !SaveDigest.TryComputeSaveIdentity(capture.Source, out uint savedIdentity)) return;

        if (capture.Session is CoopHost host)
        {
            if (!IsScriptSaveCaptureCurrent(capture) || SaveStamp(capture.Source) != stamp)
            {
                RetryScriptSaveCapture(nowMs, "host world changed while checking the save");
                return;
            }
            string[] savedCompanions = (capture.Reason & 2) != 0 ? PartyCacheFingerprint.ReadSave(capture.Source) : [];
            if (!IsScriptSaveCaptureCurrent(capture) || SaveStamp(capture.Source) != stamp
                || !ClientCharacterPersistence.TryHashSave(capture.Source, out string finalHash, out long finalLength)
                || verifiedLength != finalLength || !verifiedHash.Equals(finalHash, StringComparison.Ordinal))
            {
                RetryScriptSaveCapture(nowMs, "saved bytes changed while verifying companion payloads");
                return;
            }
            if ((capture.Reason & 2) != 0 && (!bridge.TryReadLocalPartyRoster(out PartyRoster currentParty)
                || !SameNativeParty(currentParty, capture.Party)
                || Enumerable.Range(0, 9).Any(slot => capture.Party.IsInParty(slot)
                    && PartySavedOccupantRule.RequiresBinding(slot, capture.Party.OccupantFor(slot))
                    && (capture.Party.OccupantFor(slot).Template.Length == 0 || savedCompanions[slot].Length != 64))
                || !host.PublishCompanionRefreshProof(capture.Party, savedCompanions, savedIdentity, capture.Reason)))
            {
                RetryScriptSaveCapture(nowMs, "saved companion identity no longer matches its captured native occupant");
                return;
            }
            if ((capture.Reason & 2) != 0) bridge.RecordHostPartyRefreshProof(capture.Party);
            _scriptHostRefreshWanted = false;
            _scriptSaveCapture = null;
            _scriptHostRefreshReason = 0;
            if ((capture.Reason & 2) == 0) host.PublishScriptWorldRefresh(savedIdentity, capture.Reason);
            Log?.Invoke($"script-world: post-script host save ready (0x{savedIdentity:X8}); announcing refresh");
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
            if ((capture.Reason & 2) != 0 && (capture.Session is not CoopClient partyClient
                || !partyClient.TryGetPartyRoster(out PartyRoster currentParty) || currentParty != capture.Party))
            {
                RetryScriptSaveCapture(nowMs, "companion occupant changed while capturing the character");
                return;
            }
            _scriptSaveCapture = null;
            _scriptRetryAtMs = nowMs + 30_000;
            _skipNextDisconnectCapture = true;
            Reply("Host script changes are ready - rejoining with your own character");
            TriggerRejoin(RejoinTrigger.ScriptWorldRefresh, capture.Session,
                () => Interlocked.Exchange(ref _scriptCompletedToken, capture.Token));
        }
        else
        {
            RetryScriptSaveCapture(nowMs, "fresh character extraction was refused");
        }
    }
}
