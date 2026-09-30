using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

public sealed partial class CoopCommandDispatcher
{
    private sealed record PartyReload(string Directory, string StagedDirectory, PartySaveProvenance.Snapshot Provenance,
        uint RequestId, uint StartingSerial, uint StartingEpoch, long RequestedMs);
    private PartyReload? _partyReload;
    private sealed class PartySaveAttempt(string directory, PartyRoster party, DateTime before, long requested)
    {
        public readonly string Directory = directory;
        public readonly PartyRoster Party = party;
        public readonly DateTime Before = before;
        public readonly long Requested = requested;
        public DateTime Observed;
        public long StableSince;
    }
    private PartySaveAttempt? _partySaveAttempt;

    private void MaintainPartyProvenanceSave()
    {
        if (_partySaveAttempt is not { } attempt) return;
        if (SessionClock.NowMs - attempt.Requested > 30_000
            || !bridge.TryReadLocalPartyRoster(out PartyRoster current) || !SameNativeParty(current, attempt.Party))
        { _partySaveAttempt = null; return; }
        DateTime stamp = SaveStamp(attempt.Directory);
        if (stamp <= attempt.Before) return;
        if (stamp != attempt.Observed)
        { attempt.Observed = stamp; attempt.StableSince = SessionClock.NowMs; return; }
        if (SessionClock.NowMs - attempt.StableSince < 750) return;
        string[] payloads = PartyCacheFingerprint.ReadSave(attempt.Directory);
        if (!bridge.TryReadLocalPartyRoster(out current) || !SameNativeParty(current, attempt.Party)
            || SaveStamp(attempt.Directory) != stamp) { _partySaveAttempt = null; return; }
        if (PartySaveProvenance.TryWriteVerified(attempt.Directory, attempt.Party, payloads, out string outcome)) Log?.Invoke(outcome);
        _partySaveAttempt = null;
    }

    private void MaintainPartyProvenanceReload()
    {
        MaintainPartyProvenanceSave();
        if (_partyReload is not { } pending) return;
        if (SessionClock.NowMs - pending.RequestedMs > 90_000)
        { _partyReload = null; return; }
        if (!bridge.TryGetAdoptedWorldLoadStatus(out uint applied, out int status) || applied != pending.RequestId) return;
        if (status != 0) { _partyReload = null; return; }
        if (string.IsNullOrEmpty(pending.Provenance.Module) || !bridge.TryReadLocalState(out PlayerState state)
            || !state.Module.Equals(pending.Provenance.Module, StringComparison.OrdinalIgnoreCase)) return;
        if (!bridge.TryReadWorldLoadSerial(out uint serial) || serial != unchecked(pending.StartingSerial + 1)
            || !bridge.TryReadLivenessEpoch(out uint epoch) || epoch == 0 || epoch == pending.StartingEpoch
            || !bridge.TryReadLocalPartyRoster(out PartyRoster current)
            || current.HostLoadSerial != serial || current.HostNativeEpoch != epoch) return;
        if (!PartySaveProvenance.MatchesSnapshot(pending.Directory, pending.Provenance)
            || !PartySaveProvenance.MatchesSnapshot(pending.StagedDirectory, pending.Provenance))
        { _partyReload = null; Log?.Invoke("party provenance: selected save changed; identity restoration refused"); return; }
        if (bridge.TryRestoreSavedPartyOccupants(pending.Provenance.Occupants, current))
        {
            _partyReload = null;
            Log?.Invoke("party provenance: exact observed load submitted for native occupant identity validation");
        }
    }
}
