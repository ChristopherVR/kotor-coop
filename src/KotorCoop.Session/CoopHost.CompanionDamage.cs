using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — cumulative damage a client's RELEASED companion has taken, kept as per-client
/// freshness-filtered reports for <see cref="CompanionDamageLedger"/> (one layer up, in
/// <c>Pumps.CombatPump.PumpCompanionDamage</c>) to bank into a rise-only owed total the host's
/// own engine spends as real damage. The companion sibling of CoopHost.Enemies.cs's
/// damage-report half — see docs/research/companion-state-sync.md §13 and
/// docs/research/peer-damage-ledger.md for the "cumulative total, never health" reasoning this
/// reuses wholesale.
/// </summary>
public sealed partial class CoopHost
{
    /// <summary>Latest companion-damage report per client.</summary>
    private sealed record CompanionDamageReport(
        uint Generation, IReadOnlyList<CompanionDamageEntry> Damage, long ReceivedMs);

    private readonly Dictionary<uint, CompanionDamageReport> _companionDamage = [];

    /// <summary>Same window as <see cref="EnemyReportFreshMs"/>, and for the identical reason:
    /// a companion's local fight goes stale exactly when its owning peer does.</summary>
    private const int CompanionDamageFreshMs = 2000;

    /// <summary>The host does not report to itself; its own companions are already
    /// authoritative.</summary>
    public void ReportCompanionDamage(uint generation, IReadOnlyList<CompanionDamageEntry> damage)
    {
    }

    public IReadOnlyList<PeerCompanionDamage> GetCompanionDamage()
    {
        long now = SessionClock.NowMs;
        List<PeerCompanionDamage> merged = [];

        lock (_gate)
        {
            foreach (KeyValuePair<uint, CompanionDamageReport> entry in _companionDamage)
            {
                CompanionDamageReport report = entry.Value;

                if (now - report.ReceivedMs > CompanionDamageFreshMs)
                {
                    continue;
                }

                foreach (CompanionDamageEntry damage in report.Damage)
                {
                    merged.Add(new PeerCompanionDamage(
                        entry.Key, report.Generation, damage.RosterSlotPlusOne, damage.CumulativeDamage));
                }
            }
        }

        return merged;
    }

    private void HandleCompanionDamage(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadCompanionDamage(span) is not { } report)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (!_peers.ContainsKey(report.PeerId))
            {
                return;
            }

            _companionDamage[report.PeerId] =
                new CompanionDamageReport(report.Generation, report.Damage, now);
        }
    }
}
