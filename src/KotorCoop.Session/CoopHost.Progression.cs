using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — Shared gold/experience: the host's own absolute totals, broadcast every tick,
/// plus the per-peer contribution ledger merged from client reports.
/// </summary>
public sealed partial class CoopHost
{
    private uint _gold;
    private uint _experience;
    private bool _hasProgression;

    public void PublishProgression(uint gold, uint experience)
    {
        lock (_gate)
        {
            _gold = gold;
            _experience = experience;
            _hasProgression = true;
        }
    }

    /// <summary>The host is the source of progression; it never mirrors a peer's.</summary>
    public bool TryGetProgression(out uint gold, out uint experience)
    {
        gold = 0;
        experience = 0;
        return false;
    }

    /// <summary>Latest progression report per client, so a client's purchases and its plot
    /// XP reach the party.</summary>
    private sealed record ProgressionReport(
        uint Generation, uint GoldEarned, uint GoldSpent, uint Experience, long ReceivedMs);

    private readonly Dictionary<uint, ProgressionReport> _progression = [];

    /// <summary>
    /// How long a progression report stays actionable.
    ///
    /// ⚠ Generous where the damage channel is tight, and deliberately so: an enemy report
    /// going stale costs a moment of a fight, while a gold report going stale costs a
    /// purchase that never comes out of the purse. The ledger is cumulative, so a late report
    /// still carries everything missed — the freshness window only decides how long a
    /// DEPARTED peer's unspent contribution stays claimable.
    /// </summary>
    private const int ProgressionReportFreshMs = 15_000;

    public void ReportProgression(uint generation, uint goldEarned, uint goldSpent, uint experience)
    {
        // The host does not report to itself; its own party table is the purse.
    }

    public IReadOnlyList<PeerProgression> GetProgressionReports()
    {
        long now = SessionClock.NowMs;
        List<PeerProgression> reports = [];

        lock (_gate)
        {
            foreach (KeyValuePair<uint, ProgressionReport> entry in _progression)
            {
                ProgressionReport report = entry.Value;

                if (now - report.ReceivedMs > ProgressionReportFreshMs)
                {
                    continue;
                }

                reports.Add(new PeerProgression(
                    entry.Key, report.Generation, report.GoldEarned, report.GoldSpent, report.Experience));
            }
        }

        return reports;
    }

    private void HandleProgressionReport(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        ProgressionReportPacket? packet = PacketCodec.ReadProgressionReport(span);
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

            _progression[packet.PeerId] = new ProgressionReport(
                packet.Generation, packet.GoldEarned, packet.GoldSpent, packet.Experience,
                SessionClock.NowMs);
        }
    }

    /// <summary>
    /// Broadcasts the absolute shared progression each tick once the host has published it.
    /// Like the enemy/party snapshots: a lost packet costs one tick and the next carries the
    /// whole truth, and absolute values make replays idempotent.
    /// </summary>
    private void BroadcastProgression(long now, List<PeerConnection> recipients)
    {
        uint gold;
        uint experience;
        lock (_gate)
        {
            if (!_hasProgression)
            {
                return;
            }

            gold = _gold;
            experience = _experience;
        }

        if (recipients.Count == 0)
        {
            return;
        }

        int length = PacketCodec.Write(_sendBuffer, new ProgressionSnapshotPacket(now, gold, experience));
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
