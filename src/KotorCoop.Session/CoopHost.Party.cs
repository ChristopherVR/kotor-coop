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

    public void PublishParty(IReadOnlyList<PartyMemberState> members)
    {
        lock (_gate)
        {
            _party = members;
        }
    }

    public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

    /// <summary>The host's own companion roster, as its party table last read. Broadcast every
    /// tick as a LEVEL; <see cref="_hasPartyRoster"/> gates the very first tick, before which
    /// there is nothing to say and an all-zero mask would read as "dismiss everyone".</summary>
    private PartyRoster _partyRoster = PartyRoster.Empty;

    private bool _hasPartyRoster;

    public void PublishPartyRoster(PartyRoster roster)
    {
        lock (_gate)
        {
            _partyRoster = roster;
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
        lock (_gate)
        {
            party = _party;
        }

        if (party.Count == 0 || recipients.Count == 0)
        {
            return;
        }

        int length = PacketCodec.Write(_sendBuffer, new PartySnapshotPacket(now, party));
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
