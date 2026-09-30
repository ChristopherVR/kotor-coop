using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — The authoritative party/companion snapshot and the roster level (who is currently
/// recruited) — both broadcast every tick once the host has read them at least once.
/// </summary>
public sealed partial class CoopHost
{
    private IReadOnlyList<PartyMemberState> _party = [];
    private uint _partyLoadSerial, _partyNativeEpoch;

    public void PublishParty(IReadOnlyList<PartyMemberState> members)
    {
        lock (_gate) PublishParty(members, _localWorld.LoadSerial, _localWorld.NativeWorldEpoch);
    }

    public void PublishParty(IReadOnlyList<PartyMemberState> members, uint capturedLoadSerial, uint capturedNativeEpoch)
    {
        lock (_gate)
        {
            if (capturedLoadSerial != _localWorld.LoadSerial || capturedNativeEpoch != _localWorld.NativeWorldEpoch) return;
            _party = members;
            _partyLoadSerial = capturedLoadSerial; _partyNativeEpoch = capturedNativeEpoch;
        }
    }

    public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

    /// <summary>The host's own companion roster, as its party table last read. Broadcast every
    /// tick as a LEVEL; <see cref="_hasPartyRoster"/> gates the very first tick, before which
    /// there is nothing to say and an all-zero mask would read as "dismiss everyone".</summary>
    private PartyRoster _partyRoster = PartyRoster.Empty;

    private bool _hasPartyRoster;
    private PartyRoster _companionRefreshProof;

    /// <summary>Bind exact saved UTC bytes to the native identity captured before this save.
    /// Later roster publications carry this fixed proof, never a hash of mutated live HP.</summary>
    public bool TryCaptureCompanionRefreshRoster(out PartyRoster roster)
    {
        lock (_gate) { roster = _partyRoster; return _hasPartyRoster
            && roster.HostSessionId == _sessionId && roster.HostLoadSerial == _localWorld.LoadSerial
            && roster.HostNativeEpoch == _localWorld.NativeWorldEpoch; }
    }

    public bool PublishCompanionRefreshProof(PartyRoster captured, IReadOnlyList<string> fingerprints, uint saveIdentity, uint reason)
    {
        lock (_gate)
        {
            if (saveIdentity == 0 || captured.HostSessionId != _sessionId || !_hasPartyRoster || captured.HostLoadSerial != _localWorld.LoadSerial
                || captured.HostNativeEpoch != _localWorld.NativeWorldEpoch) return false;
            PartyOccupantIdentity[] identities = new PartyOccupantIdentity[9];
            bool known = false;
            for (int slot = 0; slot < 9; slot++)
            {
                PartyOccupantIdentity occupant = captured.OccupantFor(slot);
                PartyOccupantIdentity current = _partyRoster.OccupantFor(slot);
                if (occupant.Generation != current.Generation || occupant.Template != current.Template
                    || occupant.Replaced != current.Replaced) return false;
                string fingerprint = slot < fingerprints.Count ? fingerprints[slot] : "";
                if (fingerprint.Length != 0 && !KotorCoop.Memory.PartyCacheFingerprint.IsValid(fingerprint)) return false;
                identities[slot] = occupant with { CacheFingerprint = fingerprint };
                known |= PartySavedOccupantRule.RequiresBinding(slot, occupant) && occupant.Template.Length > 0 && fingerprint.Length == 64;
            }
            if (!known) return false;
            PublishScriptWorldRefresh(saveIdentity, reason);
            _companionRefreshProof = _partyRoster with { Occupants = identities, RefreshSaveIdentity = saveIdentity, RefreshRevision = _scriptRefreshRevision };
            _partyRoster = _companionRefreshProof;
            return true;
        }
    }

    public void PublishPartyRoster(PartyRoster roster)
    {
        lock (_gate)
        {
            if (roster.HostNativeEpoch != 0 && (roster.HostNativeEpoch != _localWorld.NativeWorldEpoch
                || roster.HostLoadSerial != _localWorld.LoadSerial)) return;
            _partyRoster = roster with { HostSessionId = _sessionId,
                HostLoadSerial = _localWorld.LoadSerial, HostNativeEpoch = _localWorld.NativeWorldEpoch };
            if (_companionRefreshProof.HostSessionId == _sessionId
                && _companionRefreshProof.HostLoadSerial == _localWorld.LoadSerial
                && _companionRefreshProof.HostNativeEpoch == _localWorld.NativeWorldEpoch)
            {
                PartyOccupantIdentity[] identities = Enumerable.Range(0, 9).Select(_partyRoster.OccupantFor).ToArray();
                for (int slot = 0; slot < 9; slot++)
                {
                    PartyOccupantIdentity proof = _companionRefreshProof.OccupantFor(slot), current = identities[slot];
                    if (proof.Generation == current.Generation && proof.Template == current.Template && proof.Replaced == current.Replaced)
                        identities[slot] = current with { CacheFingerprint = proof.CacheFingerprint };
                }
                _partyRoster = _partyRoster with { Occupants = identities, RefreshSaveIdentity = _companionRefreshProof.RefreshSaveIdentity,
                    RefreshRevision = _companionRefreshProof.RefreshRevision };
            }
            _hasPartyRoster = true;
        }
    }

    /// <summary>The host IS the roster; it never mirrors anyone's.</summary>
    public bool TryGetPartyRoster(out PartyRoster roster)
    {
        roster = PartyRoster.Empty;
        return false;
    }

    /// <summary>
    /// Sends the authoritative party companions. Unreliable and repeated every tick, like
    /// the enemy snapshot: the host owns companion movement through its own engine AI, and
    /// each client mirrors this onto its frozen copies.
    /// </summary>
    private void BroadcastParty(long now, List<PeerConnection> recipients)
    {
        IReadOnlyList<PartyMemberState> party;
        uint serial, epoch;
        lock (_gate)
        {
            if (_partyLoadSerial != _localWorld.LoadSerial || _partyNativeEpoch != _localWorld.NativeWorldEpoch) return;
            serial = _partyLoadSerial; epoch = _partyNativeEpoch;
            party = _party;
        }

        if (party.Count == 0 || recipients.Count == 0)
        {
            return;
        }

        int length = PacketCodec.Write(_sendBuffer, new PartySnapshotPacket(now, party, _sessionId, serial, epoch));
        if (length == 0)
        {
            return;
        }

        foreach (PeerConnection peer in recipients)
        {
            _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    /// <summary>
    /// Sends the authoritative companion roster. Twenty bytes, every tick, unconditionally
    /// once the host has read its party table at least once.
    ///
    /// ⚠ IT IS SENT EVEN WHEN NOTHING HAS CHANGED, and unlike the party/enemy snapshots it is
    /// NOT skipped when the payload is empty. Both matter. "Nobody is recruited yet" is a real
    /// roster and the first module of the game is exactly that; skipping it would leave a
    /// joiner with no level at all, and a client with no level applies nothing — so a
    /// divergence created before the first recruitment would never be corrected. And a change
    /// sent once is an edge, which is the shape this channel exists to avoid: a roster
    /// divergence is permanent, so the correction has to keep arriving.
    /// </summary>
    private void BroadcastPartyRoster(long now, List<PeerConnection> recipients)
    {
        PartyRoster roster;
        lock (_gate)
        {
            if (!_hasPartyRoster)
            {
                return;
            }

            roster = _partyRoster;
            if (roster.HostLoadSerial != _localWorld.LoadSerial || roster.HostNativeEpoch != _localWorld.NativeWorldEpoch) return;
        }

        if (recipients.Count == 0)
        {
            return;
        }

        int length = PacketCodec.Write(_sendBuffer, new PartyRosterPacket(now, roster));
        if (length == 0)
        {
            return;
        }

        foreach (PeerConnection peer in recipients)
        {
            _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
        }
    }
}
