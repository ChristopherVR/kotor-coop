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

    public void PublishCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot)
    {
        lock (_gate)
        {
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
    private sealed record CompanionXpPeerReport(ushort PresentMask, uint[] Xp, long ReceivedMs);

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

                reports.Add(new PeerCompanionXp(entry.Key, entry.Value.PresentMask, entry.Value.Xp));
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
            if (!_peers.ContainsKey(packet.PeerId))
            {
                return;
            }

            _companionXpReports[packet.PeerId] =
                new CompanionXpPeerReport(packet.PresentMask, packet.Xp, SessionClock.NowMs);
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
        lock (_gate)
        {
            if (!_hasCompanionXp)
            {
                return;
            }

            mask = _companionXpMask;
            xp = (uint[])_companionXp.Clone();
        }

        if (recipients.Count == 0)
        {
            return;
        }

        int length = PacketCodec.Write(_sendBuffer, new CompanionXpSnapshotPacket(now, mask, xp));
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
