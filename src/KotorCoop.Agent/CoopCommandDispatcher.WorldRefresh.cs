using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

public sealed partial class CoopCommandDispatcher
{
    private void BeginManualWorldRefresh()
    {
        if (manager.Current is not CoopClient client)
        {
            Reply("reconnect world needs a connected joiner; both players must save first");
            return;
        }

        if (_rejoinState.Phase == RejoinPhase.InFlight
            || _captureRequestedMs != 0 || _soloCapturePending || _soloCaptureWaiting
            || _ownLoadVerdictPending || _ownLoadCaptureRequiresConfirmedSave
            || _verifiedOwnLoadAwaitingRejoin || _pendingCheatSerial != 0 || _queuedCheats.Count != 0
            || bridge.TryGetNewCharacterCapture(out uint chargen, out _, out _)
                && chargen is ChargenStateArmed or ChargenStateSaving)
        {
            Reply("reconnect world: another save, load or character creation is busy; try again when it finishes");
            return;
        }

        if (_sessionSaveWatch is null
            || !bridge.TryReadLocalState(out PlayerState state)
            || !ManualWorldRefreshRule.IsSafe(state)
            || !bridge.TryReadWorldLoadSerial(out uint loadSerial)
            || !manager.TryGetCurrentGeneration(client, out long generation)
            || !HostWorldReplacedRule.IsHostReady(
                client.GetRemotePeers().FirstOrDefault(p => p.PeerId == CoopHost.HostPeerId)?.State,
                client.RemoteWorldDigest.Module)
            || client.GetRemotePeers().FirstOrDefault(p => p.PeerId == CoopHost.HostPeerId)?.State
                is not { } hostState || !ManualWorldRefreshRule.IsSafe(hostState))
        {
            Reply("reconnect world: both players must be in a loaded world; finish movies, conversations and combat first");
            return;
        }

        // This command never originates a native save: restoring a held-back QUICKSAVE
        // while a delayed engine request is outstanding could overwrite the user's save.
        // Instead require the player to save first and prove that exact archive is current.
        string saves = Path.Combine(gameDirectory, "saves");
        uint hostLoadSerial = client.RemoteWorldDigest.LoadSerial;
        string? source = ClientCharacterPersistence.FindCaptureSource(saves);
        DateTime stamp = SaveStamp(source);
        uint liveIdentity = bridge.SaveIdentityDigest;
        if (!ManualWorldRefreshRule.IsFreshSave(stamp, DateTime.UtcNow)
            || !ClientCharacterPersistence.IsCaptureSourceReady(source)
            || !SaveDigest.TryReadActiveModuleName(source!, out string savedModule)
            || !savedModule.Equals(state.Module, StringComparison.OrdinalIgnoreCase)
            || !SaveDigest.TryComputeSaveIdentity(source!, out uint savedIdentity)
            || liveIdentity == 0 || savedIdentity != liveIdentity)
        {
            Reply("reconnect world: save your character now, then retry within 30 seconds; the host must also save after their script changes");
            return;
        }

        if (!_sessionSaveWatch.CaptureVerifiedRefreshSource(source!, out string outcome))
        {
            if (outcome is not null) Log?.Invoke(outcome);
            Reply("reconnect world cancelled: could not capture your freshly saved character; session left connected");
            return;
        }
        if (outcome is not null) Log?.Invoke(outcome);

        if (manager.Current != client || manager.LifecycleGeneration != generation
            || !bridge.TryReadLocalState(out PlayerState current)
            || !bridge.TryReadWorldLoadSerial(out uint currentSerial)
            || !ManualWorldRefreshRule.IsSameWorld(state.Module, loadSerial, current, currentSerial)
            || client.RemoteWorldDigest.LoadSerial != hostLoadSerial
            || !HostWorldReplacedRule.IsHostReady(
                client.GetRemotePeers().FirstOrDefault(p => p.PeerId == CoopHost.HostPeerId)?.State,
                client.RemoteWorldDigest.Module)
            || client.GetRemotePeers().FirstOrDefault(p => p.PeerId == CoopHost.HostPeerId)?.State
                is not { } currentHost || !ManualWorldRefreshRule.IsSafe(currentHost)
            || SaveStamp(source) != stamp)
        {
            Reply("reconnect world cancelled: your world changed while capturing the character; save and retry");
            return;
        }

        _skipNextDisconnectCapture = true;
        Reply("reconnect world: loading the host's latest save with your freshly saved character");
        TriggerRejoin(RejoinTrigger.ManualWorldRefresh, client);
    }
}
