using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — per-companion experience (docs/research/companion-state-sync.md, roadmap item
/// 11). Same shape as CoopHost.Progression.cs's PC-experience half — absolute,
/// merged upward by max — split into its own file because the channel is independently
/// toggled (coop.ini <c>companion_xp_sync</c>, OFF by default). The MERGE itself happens one
/// layer up, in <c>Pumps.PartyPump.PumpCompanionExperience</c>: this class only stores what it
/// is told and broadcasts it, exactly like <see cref="PublishProgression"/> does for gold/XP.
/// </summary>
public sealed partial class CoopHost
{
    private ushort _companionXpMask;
    private readonly uint[] _companionXp = new uint[ProtocolConstants.PartyRosterSlots];
    private bool _hasCompanionXp;
    private PartyRoster _companionXpRoster;

    public void PublishCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot, PartyRoster captured)
    {
        lock (_gate)
        {
            if (captured.HostLoadSerial != _localWorld.LoadSerial || captured.HostNativeEpoch != _localWorld.NativeWorldEpoch
                || Enumerable.Range(0, 9).Any(slot => captured.OccupantFor(slot).Generation != _partyRoster.OccupantFor(slot).Generation)) return;
            PublishCompanionExperience(presentMask, xpBySlot);
        }
    }

    public void PublishCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot)
    {
        lock (_gate)
        {
            _companionXpRoster = _partyRoster;
            _companionXpMask = presentMask;
            for (int slot = 0; slot < ProtocolConstants.PartyRosterSlots; slot++)
            {
                _companionXp[slot] = slot < xpBySlot.Count ? xpBySlot[slot] : 0;
            }

            _hasCompanionXp = true;
        }
    }

    /// <summary>The host is the source of companion experience; it never mirrors a peer's.</summary>
    public bool TryGetCompanionExperience(out ushort presentMask, out uint[] xpBySlot)
    {
        presentMask = 0;
        xpBySlot = new uint[ProtocolConstants.PartyRosterSlots];
        return false;
    }

    /// <summary>Latest companion-experience report per client, mirroring
    /// <see cref="ProgressionReport"/>'s freshness discipline.</summary>
    private sealed record CompanionXpPeerReport(CompanionXpReportPacket Packet, long ReceivedMs);

    private readonly Dictionary<uint, CompanionXpPeerReport> _companionXpReports = [];

    /// <summary>Same window as <see cref="ProgressionReportFreshMs"/> and for the same reason:
    /// generous, because a stale report only costs how long a departed peer's own reading
    /// stays claimable, not a live fight's outcome.</summary>
    private const int CompanionXpReportFreshMs = 15_000;

    public void ReportCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot)
    {
        // The host does not report to itself; its own read is already the truth.
    }

    public IReadOnlyList<PeerCompanionXp> GetCompanionExperienceReports()
    {
        long now = SessionClock.NowMs;
        List<PeerCompanionXp> reports = [];

        lock (_gate)
        {
            foreach (KeyValuePair<uint, CompanionXpPeerReport> entry in _companionXpReports)
            {
                if (now - entry.Value.ReceivedMs > CompanionXpReportFreshMs)
                {
                    continue;
                }

                CompanionXpReportPacket packet = entry.Value.Packet;
                if (packet.HostSessionId != _sessionId || packet.HostLoadSerial != _localWorld.LoadSerial || packet.HostNativeEpoch != _localWorld.NativeWorldEpoch) continue;
                ushort mask = packet.PresentMask;
                for (int slot = 0; slot < 9; slot++)
                    if (packet.OccupantGenerations is not { } gens || gens[slot] != _partyRoster.OccupantFor(slot).Generation) mask &= (ushort)~(1 << slot);
                reports.Add(new PeerCompanionXp(entry.Key, mask, packet.Xp));
            }
        }

        return reports;
    }

    private void HandleCompanionXpReport(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        CompanionXpReportPacket? packet = PacketCodec.ReadCompanionXpReport(span);
        if (packet is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_peers.TryGetValue(packet.PeerId, out PeerConnection? peer) || !peer.EndPoint.Equals(from)
                || packet.HostSessionId != _sessionId || packet.HostLoadSerial != _localWorld.LoadSerial
                || packet.HostNativeEpoch == 0 || packet.HostNativeEpoch != _localWorld.NativeWorldEpoch)
            {
                return;
            }

            _companionXpReports[packet.PeerId] =
                new CompanionXpPeerReport(packet, SessionClock.NowMs);
        }
    }

    /// <summary>
    /// Broadcasts the absolute per-companion experience each tick once the host has published
    /// it — same idempotent-replay reasoning as <see cref="BroadcastProgression"/>.
    /// </summary>
    private void BroadcastCompanionExperience(long now, List<PeerConnection> recipients)
    {
        ushort mask;
        uint[] xp;
        PartyRoster roster;
        lock (_gate)
        {
            if (!_hasCompanionXp)
            {
                return;
            }

            roster = _companionXpRoster;
            if (roster.HostLoadSerial != _localWorld.LoadSerial || roster.HostNativeEpoch != _localWorld.NativeWorldEpoch) return;
            mask = _companionXpMask;
            xp = (uint[])_companionXp.Clone();
        }

        if (recipients.Count == 0)
        {
            return;
        }

        int length = PacketCodec.Write(_sendBuffer, new CompanionXpSnapshotPacket(now, mask, xp, _sessionId, roster.HostLoadSerial, roster.HostNativeEpoch,
            Enumerable.Range(0, 9).Select(slot => roster.OccupantFor(slot).Generation).ToArray()));
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
