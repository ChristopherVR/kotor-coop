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
        uint Generation, IReadOnlyList<CompanionDamageEntry> Damage, long ReceivedMs, uint Incarnation, uint HostLoadSerial, uint HostNativeEpoch);

    private readonly Dictionary<uint, CompanionDamageReport> _companionDamage = [];
    private readonly DamageGenerationWatch _companionDamageGenerations = new();

    /// <summary>Same window as <see cref="EnemyReportFreshMs"/>, and for the identical reason:
    /// a companion's local fight goes stale exactly when its owning peer does.</summary>
    private const int CompanionDamageFreshMs = 2000;

    /// <summary>
    /// Number of peer reports currently retained. Internal visibility keeps the bounded-state
    /// invariant observable in tests without exposing transport bookkeeping to callers.
    /// </summary>
    internal int CompanionDamageReportCount
    {
        get
        {
            lock (_gate)
            {
                return _companionDamage.Count;
            }
        }
    }

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
            List<uint>? stale = null;
            foreach (KeyValuePair<uint, CompanionDamageReport> entry in _companionDamage)
            {
                CompanionDamageReport report = entry.Value;

                if (now - report.ReceivedMs > CompanionDamageFreshMs || report.HostLoadSerial != _localWorld.LoadSerial
                    || report.HostNativeEpoch != _localWorld.NativeWorldEpoch)
                {
                    // Freshness already kept an expired report out of the wire result, but
                    // leaving it in this dictionary meant every peer identity ever admitted
                    // occupied a slot for the rest of the process. The cumulative ledger keeps
                    // the peer's baseline separately, so dropping this transport snapshot is
                    // safe: a returning peer replaces it with its next report under the same
                    // stable id, while this map remains bounded by currently active peers.
                    (stale ??= []).Add(entry.Key);
                    continue;
                }

                foreach (CompanionDamageEntry damage in report.Damage)
                {
                    merged.Add(new PeerCompanionDamage(
                        entry.Key, report.Generation, damage.RosterSlotPlusOne, damage.CumulativeDamage, damage.OccupantGeneration, report.Incarnation, report.HostLoadSerial, report.HostNativeEpoch));
                }
            }

            if (stale is not null)
            {
                foreach (uint peerId in stale)
                {
                    _companionDamage.Remove(peerId);
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
            if (report.HostSessionId != _sessionId || report.HostLoadSerial != _localWorld.LoadSerial
                || report.HostNativeEpoch == 0 || report.HostNativeEpoch != _localWorld.NativeWorldEpoch
                || !_peers.TryGetValue(report.PeerId, out PeerConnection? peer) || !peer.EndPoint.Equals(from)
                || peer.DamageIncarnation != report.Incarnation
                || !_companionDamageGenerations.Observe(report.PeerId, report.Incarnation, report.Generation))
            {
                return;
            }

            _companionDamage[report.PeerId] =
                new CompanionDamageReport(report.Generation, report.Damage.Where(damage =>
                {
                    int slot = (int)CompanionDamageSlots.RosterSlotPlusOne(damage.RosterSlotPlusOne) - 1;
                    PartyOccupantIdentity occupant = _partyRoster.OccupantFor(slot);
                    return (uint)slot < 9 && damage.OccupantGeneration == occupant.Generation
                        && (occupant.Generation == 0 || occupant.Template.Length != 0);
                }).ToArray(), now, report.Incarnation, report.HostLoadSerial, report.HostNativeEpoch);
        }
    }
}
