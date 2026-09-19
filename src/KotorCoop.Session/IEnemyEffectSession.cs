// A non-damage effect a client cast on a mirrored enemy, relayed to the host so it lands on the
// real body (docs/research/enemy-effects.md).
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IEnemyEffectSession
{
    /// <summary>
    /// Client side: reports that a non-damage effect THIS player cast just landed on enemy
    /// <paramref name="enemyId"/> (in the host's id space, in <paramref name="module"/>),
    /// delivered reliably to the host. A no-op on the host — its own engine already ran the
    /// cast against the real enemy — and for any kind the enemy relay does not forward.
    /// </summary>
    void PublishEnemyEffect(string module, uint enemyId, PeerEffectKind kind, uint durationMs, uint magnitude) { }

    /// <summary>
    /// Host side: every enemy-effect report received from a client since the last drain, oldest
    /// first, for the agent to hand to the plugin. Always empty on a client.
    /// </summary>
    IReadOnlyList<EnemyEffectPacket> DrainEnemyEffects() => [];
}
