using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

public sealed partial class CoopCommandDispatcher
{
    private sealed class AutomaticRejoinCapture(ICoopSession session, long generation, RejoinTrigger trigger,
        Action? completed, long requested)
    {
        public readonly ICoopSession Session = session;
        public readonly long Generation = generation;
        public readonly RejoinTrigger Trigger = trigger;
        public readonly Action? Completed = completed;
        public readonly long Requested = requested;
        public ReturnSaveCapture? Save;
    }
    private AutomaticRejoinCapture? _automaticRejoinCapture;
    private bool _controlledSceneReturnPending;

    private bool TryUseAutomaticRecoveryCheckpoint()
    {
        if (!ClientCharacterPersistence.TryReadSidecar(Path.Combine(gameDirectory, "saves"),
                out var player, out _, out _)
            || !player.TryGetShort("CurrentHitPoints", out short hp)
            || !player.TryGetShort("HitPoints", out short storedMaximum)
            || !player.TryGetShort("MaxHitPoints", out short derivedMaximum)
            || !SavedCharacterHealthRule.IsLiving(hp, storedMaximum, derivedMaximum)) return false;
        Log?.Invoke("automatic recovery: using the verified living character checkpoint; unsaved post-checkpoint changes cannot be recovered");
        return true;
    }

    // Automatic host reloads must carry the actual current character without replacing a
    // user's QuickSave. Keep the live connection until the dedicated slot has really landed.
    private bool MaintainAutomaticRejoinCapture(long nowMs)
    {
        if (_automaticRejoinCapture is not { } pending) return false;
        if (manager.Current != pending.Session || manager.LifecycleGeneration != pending.Generation
            || ReturnSaveCoordinationBusy || nowMs - pending.Requested > 90_000)
        {
            _automaticRejoinCapture = null;
            _hostWorldReplacedState = new(true);
            Log?.Invoke("automatic rejoin: fresh-character capture cancelled; connection retained");
            return true;
        }
        PlayerState? host = pending.Session.GetRemotePeers().FirstOrDefault(p => p.PeerId == CoopHost.HostPeerId)?.State;
        if (ControlledSceneTravelRule.HoldAdoption(host)) return true;
        if (pending.Save is null)
        {
            pending.Save = BeginReturnSave();
            return true;
        }
        ReturnSaveCapture save = pending.Save;
        if (!ReturnSaveIsCurrent(save) || save.Refused)
        {
            _automaticRejoinCapture = null;
            _hostWorldReplacedState = new(true);
            Log?.Invoke("automatic rejoin: current-character save refused or world changed; connection retained");
            return true;
        }
        if (!ReturnSaveHasLanded(save, nowMs)) return true;
        bool captured = _sessionSaveWatch is not null
            && _sessionSaveWatch.CaptureVerifiedRefreshSource(save.Source, out _);
        if (!captured || !ReturnSaveIsCurrent(save) || SaveStamp(save.Source) != save.ObservedStamp) return true;
        _automaticRejoinCapture = null;
        Log?.Invoke($"automatic rejoin: fresh dedicated character save verified ({pending.Trigger})");
        TriggerRejoin(pending.Trigger, pending.Session, pending.Completed, dedicatedCaptureReady: true);
        return true;
    }
}
