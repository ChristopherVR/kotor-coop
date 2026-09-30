using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — The mirrored party/companion snapshot and roster level — a client never owns
/// either, so publishing is a deliberate no-op; only the host's broadcast is applied.
/// </summary>
public sealed partial class CoopClient
{
    private IReadOnlyList<PartyMemberState> _party = [];
    private long _lastPartySnapshotTime = long.MinValue, _lastPartyRosterTime = long.MinValue;
    private uint _partySnapshotLoadSerial, _partySnapshotEpoch;

    public void PublishParty(IReadOnlyList<PartyMemberState> members)
    {
        // Only the host is authoritative for the party; a client's own view is dropped.
    }

    public IReadOnlyList<PartyMemberState> GetPartyMembers()
    {
        lock (_gate)
        {
            if (!PartyNamespaceMatches(_hostSessionId, _partySnapshotLoadSerial, _partySnapshotEpoch)) return [];
            return _party.Where(member => member.OccupantGeneration == _partyRoster.OccupantFor(member.SlotIndex).Generation
                && (member.OccupantGeneration == 0 || _partyRoster.OccupantFor(member.SlotIndex).Template.Length > 0)).ToArray();
        }
    }

    /// <summary>The host's roster, as last received, and whether one ever has been.</summary>
    private PartyRoster _partyRoster = PartyRoster.Empty;

    private bool _hasPartyRoster;

    public void PublishPartyRoster(PartyRoster roster)
    {
        // Only the host is authoritative for the roster; a client's own view is dropped, the
        // same way its party snapshot is.
    }

    /// <summary>
    /// ⚠ THE BOOL IS THE SAFETY, NOT A CONVENIENCE. An unheard-from roster and a genuinely
    /// empty one are the same two zero masks, and applying the second by mistake dismisses
    /// every companion the player has. So "no level yet" is reported as false and the plugin
    /// is handed nothing at all — a client with no level applies nothing, which is exactly
    /// what a client that has not been told anything should do.
    ///
    /// It is deliberately NOT aged out. A roster is save state, not a position sample: a
    /// stale one is still the last thing the host said about a table that only changes when
    /// somebody recruits or dismisses. Losing the connection is handled by the connection
    /// (the agent stops writing the block); losing a few datagrams is not a reason to stop
    /// reconciling.
    /// </summary>
    public bool TryGetPartyRoster(out PartyRoster roster)
    {
        lock (_gate)
        {
            roster = _partyRoster;
            return _hasPartyRoster && PartyNamespaceMatches(roster.HostSessionId, roster.HostLoadSerial, roster.HostNativeEpoch);
        }
    }

    private void HandlePartySnapshot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPartySnapshot(span) is { } party)
        {
            lock (_gate)
            {
                if (party.ServerTimeMs < _lastPartySnapshotTime || !PartyNamespaceMatches(party.HostSessionId, party.HostLoadSerial, party.HostNativeEpoch)) return;
                _lastPartySnapshotTime = party.ServerTimeMs;
                _partySnapshotLoadSerial = party.HostLoadSerial; _partySnapshotEpoch = party.HostNativeEpoch;
                _party = party.Members;
            }
        }
    }

    private void HandlePartyRoster(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPartyRoster(span) is { } rosterSnapshot)
        {
            lock (_gate)
            {
                if (rosterSnapshot.ServerTimeMs < _lastPartyRosterTime || !PartyNamespaceMatches(rosterSnapshot.Roster.HostSessionId,
                    rosterSnapshot.Roster.HostLoadSerial, rosterSnapshot.Roster.HostNativeEpoch)) return;
                _lastPartyRosterTime = rosterSnapshot.ServerTimeMs;
                // A LEVEL, replaced wholesale — the host sends the complete roster
                // every tick, so there is no "companion left the party" edge to miss.
                _partyRoster = rosterSnapshot.Roster;
                _hasPartyRoster = true;
            }
        }
    }
    private bool PartyNamespaceMatches(uint session, uint serial, uint epoch)
        => session == _hostSessionId && serial == _hostWorld.LoadSerial
            && epoch == _hostWorld.NativeWorldEpoch;
}
