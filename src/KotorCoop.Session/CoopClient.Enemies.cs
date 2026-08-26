using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — The mirrored enemy roster this client's engine displays — a client never owns
/// enemy state, so publishing is a deliberate no-op; only the host's snapshot is
/// applied here, module-gated so stale ids from elsewhere are dropped rather than
/// misapplied.
/// </summary>
public sealed partial class CoopClient
{
    /// <summary>A client never owns enemy state; it mirrors the host's.</summary>
    public bool IsEnemyAuthority => false;

    public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies)
    {
        // Only the host is authoritative, so a client's own view is deliberately dropped.
    }

    public IReadOnlyList<EnemyState> GetEnemies(string module)
    {
        lock (_gate)
        {
            // Ids from another module are not stale, they are WRONG: the same numbers name
            // different creatures here. Dropping the snapshot leaves our own copies alone
            // until we are back in the same place.
            return _enemyModule.Length == 0
                || module.Length == 0
                || string.Equals(_enemyModule, module, StringComparison.OrdinalIgnoreCase)
                    ? _enemies
                    : [];
        }
    }

    private IReadOnlyList<EnemyState> _enemies = [];

    private string _enemyModule = string.Empty;

    public void ReportEnemyDamage(string module, uint generation, IReadOnlyList<EnemyDamageEntry> damage)
    {
        if (_localPeerId == 0 || damage.Count == 0)
        {
            return;
        }

        // Unreliable and re-sent every tick, like the state update it travels beside. Safe
        // precisely because the totals are cumulative: a lost datagram costs one tick of
        // staleness rather than a hit, and a duplicate applies nothing.
        int length = PacketCodec.Write(
            _sendBuffer, new EnemyDamagePacket(_localPeerId, module, generation, damage));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    public IReadOnlyList<PeerEnemyDamage> GetEnemyDamage(string module) => [];

    private void HandleEnemySnapshot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadEnemySnapshot(span) is { } enemySnapshot)
        {
            lock (_gate)
            {
                _enemies = enemySnapshot.Enemies;
                _enemyModule = enemySnapshot.Module;
            }
        }
    }
}
