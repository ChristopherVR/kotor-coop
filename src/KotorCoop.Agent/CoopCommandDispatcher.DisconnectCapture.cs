using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

public sealed partial class CoopCommandDispatcher
{
    // OnDisconnecting runs synchronously, so the normal queued cheat pump cannot deliver
    // this request. The dedicated slot also makes a late completion safe after timeout:
    // no user QuickSave has been moved and no pending engine write can replace it.
    private string? CaptureDedicatedDisconnectSave()
    {
        ICoopSession? session = manager.Current;
        if (_kotor is null || session is null || _pendingCheatSerial != 0 || _queuedCheats.Count != 0
            || _captureRequestedMs != 0 || _soloCapturePending || _automaticRejoinCapture is not null
            || !manager.TryGetCurrentGeneration(session, out long generation)
            || !bridge.TryReadLocalState(out PlayerState local) || !ScriptSaveIsSafe(local)
            || !bridge.TryReadWorldLoadSerial(out uint loadSerial)
            || !bridge.TryReadLivenessEpoch(out uint epoch) || epoch == 0) return null;

        bool CurrentWorld() => manager.Current == session && manager.LifecycleGeneration == generation
            && bridge.TryReadLocalState(out PlayerState current) && ScriptSaveIsSafe(current)
            && ManualWorldRefreshRule.IsSameWorld(local.Module, loadSerial, current,
                bridge.TryReadWorldLoadSerial(out uint serial) ? serial : uint.MaxValue)
            && bridge.TryReadLivenessEpoch(out uint currentEpoch) && currentEpoch == epoch;

        string saves = Path.Combine(gameDirectory, "saves");
        Directory.CreateDirectory(saves);
        string folder = CoopSaveSlot.ResolveFolderName(Directory.GetDirectories(saves)
            .Select(Path.GetFileName).OfType<string>().ToArray());
        int dash = folder.IndexOf(" - ", StringComparison.Ordinal);
        if (dash < 0 || !int.TryParse(folder.AsSpan(0, dash), out int number) || number < 2) return null;
        string source = Path.Combine(saves, folder);
        uint beforeSequence = _kotor.TryReadNativeSaveCompletion(out uint previous, out _, out _) ? previous : 0;
        DateTime beforeStamp = SaveStamp(source);
        if (!CurrentWorld() || !bridge.TryRequestCheat(
            new CheatRequest(CheatKinds.CoopSave, Amount: number, Text: CoopSaveSlot.SlotLabel), out uint request)) return null;

        long started = SessionClock.NowMs;
        var completion = new DisconnectSaveCompletionRule();
        while (SessionClock.NowMs - started < 10_000)
        {
            Thread.Sleep(100);
            if (!CurrentWorld()) return null;
            bool answered = bridge.TryReadCheatAnswer(out uint applied, out int status, out string detail) && applied == request;
            if (answered && status != 1)
            {
                Log?.Invoke($"character capture: dedicated disconnect save refused: {detail}");
                return null;
            }
            bool completed = _kotor.TryReadNativeSaveCompletion(out uint sequence, out string directory, out PartyRoster saved)
                && sequence != beforeSequence && directory.Equals(folder, StringComparison.OrdinalIgnoreCase)
                && saved.HostLoadSerial == loadSerial && saved.HostNativeEpoch == epoch;
            bool ready = SaveStamp(source) > beforeStamp && NativeSaveArtifactRule.Complete(source)
                && SaveDigest.TryReadActiveModuleName(source, out string module)
                && module.Equals(local.Module, StringComparison.OrdinalIgnoreCase);
            string hash = "";
            ready = ready && NativeSaveArtifactRule.TryHashOutputs(source, out hash);
            if (!completion.Observe(answered && status == 1, completed, CurrentWorld(), ready, hash, SessionClock.NowMs)) continue;
            // Bind extraction to the stable four-file proof, not a hash independently
            // selected later. The callback runs inside capture before the sidecar write.
            if (!ClientCharacterPersistence.TryHashSave(source, out string expectedHash, out long expectedLength)
                || !NativeSaveArtifactRule.TryHashOutputs(source, out string beforeCaptureHash)
                || beforeCaptureHash != hash || !CurrentWorld()) return null;
            bool SourceStillValid() => CurrentWorld()
                && NativeSaveArtifactRule.TryHashOutputs(source, out string currentHash) && currentHash == hash;
            if (!_sessionSaveWatch!.CaptureVerifiedRefreshSource(source, expectedHash, expectedLength,
                SourceStillValid, out string outcome)) return null;
            Log?.Invoke($"character capture: completed dedicated disconnect save after {SessionClock.NowMs - started} ms: {outcome}");
            return source;
        }
        Log?.Invoke("character capture: dedicated disconnect save did not complete within 10 seconds; retained the last verified character checkpoint");
        return null;
    }
}
