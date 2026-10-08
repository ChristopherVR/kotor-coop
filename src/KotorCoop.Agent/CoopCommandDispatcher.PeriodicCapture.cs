using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

public sealed partial class CoopCommandDispatcher
{
    private sealed class PeriodicSaveCapture(ICoopSession session, long generation, string module,
        uint serial, uint epoch, uint ownLoadEpoch, string source, DateTime stamp, uint completionBefore,
        string? fingerprint = null)
    {
        public readonly string? Fingerprint = fingerprint;
        public readonly ICoopSession Session = session;
        public readonly long Generation = generation;
        public readonly string Module = module;
        public readonly uint Serial = serial;
        public readonly uint Epoch = epoch;
        public readonly uint OwnLoadEpoch = ownLoadEpoch;
        public readonly string Source = source;
        public readonly DateTime StampBefore = stamp;
        public readonly uint CompletionBefore = completionBefore;
        public readonly DisconnectSaveCompletionRule Stability = new();
        public bool Answered;
        public bool Refused;
    }
    private PeriodicSaveCapture? _periodicCapture;
    private bool _periodicBusyNoticed;

    /// <summary>The personal state the last completed periodic capture saved; see
    /// <see cref="PersonalCaptureFingerprint"/>. Null until the first one, so it always runs.</summary>
    private string? _capturedFingerprint;
    private bool _unchangedNoticed;

    /// <summary>Credits, experience and this player's own items (theirs or in the party pool),
    /// or null when any part cannot be read; null never skips a capture.</summary>
    private string? PersonalCaptureFingerprintNow()
    {
        if (_kotor is null || !bridge.TryReadProgression(out uint gold, out uint experience)) return null;
        uint localPlayer = _kotor.Player?.ObjectId ?? 0;
        HashSet<uint>? pool = _kotor.TryReadPartyPoolItemIds();
        if (localPlayer == 0 || pool is null) return null;
        IReadOnlyList<KotorGameBridge.ItemListing> items = _kotor.ListItems(force: false);
        return PersonalCaptureFingerprint.Compose(gold, experience, items
            .Where(i => !i.IsDeleting && (i.Possessor == localPlayer || pool.Contains(i.ObjectId)))
            .Select(i => (i.Resref, i.Identity)));
    }

    private bool PeriodicSaveIsCurrent(PeriodicSaveCapture pending) =>
        manager.Current == pending.Session && manager.LifecycleGeneration == pending.Generation
        && _ownLoadCaptureEpoch == pending.OwnLoadEpoch && !_ownLoadVerdictPending
        && !_verifiedOwnLoadAwaitingRejoin
        && bridge.TryReadLocalState(out PlayerState local) && ScriptSaveIsSafe(local)
        && bridge.TryReadWorldLoadSerial(out uint serial)
        && ManualWorldRefreshRule.IsSameWorld(pending.Module, pending.Serial, local, serial)
        && bridge.TryReadLivenessEpoch(out uint epoch) && epoch == pending.Epoch;

    private void FinishPeriodicCapture(long nowMs, string outcome)
    {
        Log?.Invoke(outcome);
        _periodicCapture = null;
        _captureRequestedMs = 0;
        _captureLastMs = nowMs;
        _periodicBusyNoticed = false;
    }

    private void MaintainPeriodicCapture(PlayerState? localState, long nowMs, string savesDirectory)
    {
        if (_periodicCapture is { } pending)
        {
            if (pending.Refused || !PeriodicSaveIsCurrent(pending) || nowMs - _captureRequestedMs >= 10_000)
            {
                FinishPeriodicCapture(nowMs, "character capture: periodic dedicated save refused, timed out or world changed; kept the verified checkpoint (QuickSave untouched)");
                return;
            }
            bool completed = bridge.TryReadNativeSaveCompletion(out uint sequence, out string folder, out PartyRoster saved)
                && sequence != pending.CompletionBefore && folder.Equals(Path.GetFileName(pending.Source), StringComparison.OrdinalIgnoreCase)
                && saved.HostLoadSerial == pending.Serial && saved.HostNativeEpoch == pending.Epoch;
            bool ready = SaveStamp(pending.Source) > pending.StampBefore && NativeSaveArtifactRule.Complete(pending.Source)
                && SaveDigest.TryReadActiveModuleName(pending.Source, out string module)
                && module.Equals(pending.Module, StringComparison.OrdinalIgnoreCase);
            string outputs = "";
            ready = ready && NativeSaveArtifactRule.TryHashOutputs(pending.Source, out outputs);
            if (!pending.Stability.Observe(pending.Answered, completed, PeriodicSaveIsCurrent(pending), ready, outputs, nowMs)) return;
            if (!ClientCharacterPersistence.TryHashSave(pending.Source, out string expected, out long length)
                || !NativeSaveArtifactRule.TryHashOutputs(pending.Source, out string fencedOutputs) || fencedOutputs != outputs)
            {
                FinishPeriodicCapture(nowMs, "character capture: periodic save changed before capture; kept the verified checkpoint");
                return;
            }
            bool CurrentSource() => PeriodicSaveIsCurrent(pending)
                && NativeSaveArtifactRule.TryHashOutputs(pending.Source, out string currentOutputs) && currentOutputs == outputs;
            string captureOutcome = "character watch unavailable";
            bool captured = _sessionSaveWatch is not null && _sessionSaveWatch.CaptureVerifiedRefreshSource(
                pending.Source, expected, length, CurrentSource, out captureOutcome);
            if (OwnLoadCaptureSafetyRule.MayReleaseWithPeriodicSave(
                pending.OwnLoadEpoch, _ownLoadCaptureEpoch, completed, captured))
                _ownLoadCaptureRequiresConfirmedSave = false;
            if (!captured) Log?.Invoke(captureOutcome);
            if (captured) _capturedFingerprint = pending.Fingerprint;
            FinishPeriodicCapture(nowMs, captured
                ? "character capture: completed periodic dedicated Co-op save captured (QuickSave untouched)"
                : "character capture: periodic completed-source proof changed; kept the verified checkpoint");
            return;
        }

        if (_sessionSaveWatch is null) return;
        if (_captureRequestedMs != 0) { _captureRequestedMs = 0; _captureLastMs = nowMs; return; }
        if (_captureLastMs == 0) { _captureLastMs = nowMs; return; }
        if (!CharacterCaptureCadenceRule.IsDue(CharacterCaptureIntervalMin, nowMs, _captureLastMs, false, localState)) return;
        ICoopSession? session = manager.Current;
        if (_rejoinState.Phase == RejoinPhase.InFlight || _ownLoadVerdictPending || _verifiedOwnLoadAwaitingRejoin
            || _scriptSaveCapture is not null
            || _soloCapturePending || _soloCaptureWaiting || _automaticRejoinCapture is not null || _hostReturnSavePending
            || session is null || !manager.TryGetCurrentGeneration(session, out long generation)
            || localState is not { } local || !ScriptSaveIsSafe(local)
            || !bridge.TryReadWorldLoadSerial(out uint serial) || !bridge.TryReadLivenessEpoch(out uint epoch) || epoch == 0)
        {
            if (!_periodicBusyNoticed) Log?.Invoke("character capture: periodic save deferred while busy; verified checkpoint and QuickSave retained");
            _periodicBusyNoticed = true;
            return;
        }
        // Nothing personal changed since the last capture: no save, no hitch. Checked again next
        // interval; anything gained since then is still captured within one interval.
        string? fingerprint = PersonalCaptureFingerprintNow();
        if (fingerprint is not null && fingerprint == _capturedFingerprint)
        {
            if (!_unchangedNoticed) Log?.Invoke("character capture: nothing personal changed since the last capture - no save needed");
            _unchangedNoticed = true;
            _captureLastMs = nowMs;
            return;
        }

        _unchangedNoticed = false;
        uint beforeSequence = bridge.TryReadNativeSaveCompletion(out uint oldSequence, out _, out _) ? oldSequence : 0;
        PeriodicSaveCapture? prepared = null;
        string? source = RequestDedicatedCoopSave(quiet: true,
            onPrepared: path =>
            {
                prepared = new(session, generation, local.Module, serial, epoch, _ownLoadCaptureEpoch,
                    path, SaveStamp(path), beforeSequence, fingerprint);
                return PeriodicSaveIsCurrent(prepared);
            },
            onApplied: () => { if (prepared is not null) prepared.Answered = true; },
            onRefused: () => { if (prepared is not null) prepared.Refused = true; });
        if (source is null || prepared is null) return;
        _periodicCapture = prepared;
        _captureRequestedMs = nowMs;
        _periodicBusyNoticed = false;
        Log?.Invoke($"character capture: {CharacterCaptureIntervalMin} min elapsed - requesting a dedicated Co-op save");
    }
}
