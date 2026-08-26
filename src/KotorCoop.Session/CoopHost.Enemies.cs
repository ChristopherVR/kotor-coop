using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — The authoritative enemy roster this host's engine currently knows about, plus
/// the peer damage-report ledger merged into it. Unreliable and resent every tick:
/// death is a level in this state, not an event, so a lost datagram costs staleness,
/// never a missed kill.
/// </summary>
public sealed partial class CoopHost
{
    /// <summary>The host is the enemy authority: its fights are the ones that count.</summary>
    public bool IsEnemyAuthority => true;

    public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies)
    {
        lock (_gate)
        {
            if (module.Length > 0
                && !string.Equals(module, _enemyModule, StringComparison.OrdinalIgnoreCase))
            {
                // Owner enemy ids are reassigned per module load
                // (docs/research/enemy-materialisation.md §6): a resync history keyed on them
                // from the last module names a different creature now, or nothing, so it must
                // not survive into the next one. `_enemyModule` starts empty, so the very
                // first legitimate publish also clears an already-empty history — harmless.
                _materialisedLootHistory.Clear();
            }

            _enemyModule = module;
            _enemies = enemies;
        }
    }

    public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

    private IReadOnlyList<EnemyState> _enemies = [];

    /// <summary>The module the published ids were read in; it travels with them so a peer
    /// somewhere else can tell that they mean nothing to it.</summary>
    private string _enemyModule = string.Empty;

    /// <summary>Latest damage report per client, so a client's hits become real here.</summary>
    private sealed record EnemyDamageReport(
        string Module, uint Generation, IReadOnlyList<EnemyDamageEntry> Damage, long ReceivedMs);

    private readonly Dictionary<uint, EnemyDamageReport> _enemyDamage = [];

    /// <summary>Reports older than this are stale — the client left, or changed module
    /// and its ids name strangers now.</summary>
    private const int EnemyReportFreshMs = 2000;

    public void ReportEnemyDamage(string module, uint generation, IReadOnlyList<EnemyDamageEntry> damage)
    {
        // The host does not report to itself; its own engine is the authority.
    }

    public IReadOnlyList<PeerEnemyDamage> GetEnemyDamage(string module)
    {
        long now = SessionClock.NowMs;
        List<PeerEnemyDamage> merged = [];

        lock (_gate)
        {
            foreach (KeyValuePair<uint, EnemyDamageReport> entry in _enemyDamage)
            {
                EnemyDamageReport report = entry.Value;

                if (now - report.ReceivedMs > EnemyReportFreshMs
                    || !string.Equals(report.Module, module, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (EnemyDamageEntry damage in report.Damage)
                {
                    merged.Add(new PeerEnemyDamage(
                        entry.Key, report.Generation, damage.ObjectId, damage.CumulativeDamage));
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// Sends the authoritative enemy list. Unreliable and repeated every tick, like the
    /// world snapshot: a lost packet costs one tick of staleness, and the next one carries
    /// the full truth anyway. Death is a level in that state, not an event, so it cannot
    /// be missed the way a one-shot "it died" message could.
    /// </summary>
    private void BroadcastEnemies(long now, List<PeerConnection> recipients)
    {
        IReadOnlyList<EnemyState> enemies;
        string module;
        lock (_gate)
        {
            enemies = _enemies;
            module = _enemyModule;
        }

        if (enemies.Count == 0 || recipients.Count == 0)
        {
            return;
        }

        int length = PacketCodec.Write(_sendBuffer, new EnemySnapshotPacket(now, module, enemies));
        if (length == 0)
        {
            return;
        }

        foreach (PeerConnection peer in recipients)
        {
            _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void HandleEnemyDamage(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadEnemyDamage(span) is not { } report)
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

            _enemyDamage[report.PeerId] =
                new EnemyDamageReport(report.Module, report.Generation, report.Damage, now);
        }
    }
}
