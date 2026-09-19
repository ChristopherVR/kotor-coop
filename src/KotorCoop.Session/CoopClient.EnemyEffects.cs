using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — this client's side of the enemy-effect relay (docs/research/enemy-effects.md):
/// a non-damage effect the local player cast on a mirrored enemy, reported reliably to the host
/// (the enemy's owner), which acks it and replays it on the real body.
/// </summary>
public sealed partial class CoopClient
{
    private readonly EnemyEffectSender _enemyEffectSender = new();

    public void PublishEnemyEffect(string module, uint enemyId, PeerEffectKind kind, uint durationMs, uint magnitude)
    {
        if (_sessionPeerId == 0)
        {
            return;
        }

        _enemyEffectSender.EnqueueLocal(_sessionPeerId, module, enemyId, kind, durationMs, magnitude, SessionClock.NowMs);
    }

    /// <summary>A client never receives a report: enemy effects flow client to host only.</summary>
    public IReadOnlyList<EnemyEffectPacket> DrainEnemyEffects() => [];

    private void FlushEnemyEffects(long now)
    {
        foreach (EnemyEffectPacket op in _enemyEffectSender.CollectDue(now))
        {
            int length = PacketCodec.Write(_sendBuffer, op);
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    private void HandleEnemyEffectAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadEnemyEffectAck(span) is { } ack)
        {
            _enemyEffectSender.Acknowledge(ack.OriginPeerId, ack.OpSequence);
        }
    }
}
