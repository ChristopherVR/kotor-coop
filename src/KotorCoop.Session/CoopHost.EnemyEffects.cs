using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — the receiving end of the enemy-effect relay (docs/research/enemy-effects.md): a
/// client's report is acked at once, deduplicated (a retransmit racing its own ack), bounded, and
/// queued for THIS instance's plugin, which applies it to the real enemy body. Nothing is relayed
/// onward: the other clients see the effect through the ordinary enemy census.
/// </summary>
public sealed partial class CoopHost
{
    /// <summary>Reports the agent has not drained yet. Bounded: a flooding peer costs itself
    /// reports, never unbounded host memory.</summary>
    private const int EnemyEffectInboxCapacity = 64;

    private readonly Queue<EnemyEffectPacket> _enemyEffectInbox = new();

    private readonly HashSet<(uint Origin, uint Sequence)> _enemyEffectSeen = [];
    private readonly Queue<(uint Origin, uint Sequence)> _enemyEffectSeenOrder = new();
    private const int EnemyEffectSeenCapacity = 512;

    /// <summary>The host's own casts already ran against the real enemy, so it reports nothing.</summary>
    public void PublishEnemyEffect(string module, uint enemyId, PeerEffectKind kind, uint durationMs, uint magnitude)
    {
    }

    public IReadOnlyList<EnemyEffectPacket> DrainEnemyEffects()
    {
        lock (_gate)
        {
            if (_enemyEffectInbox.Count == 0)
            {
                return [];
            }

            List<EnemyEffectPacket> ready = [];
            while (_enemyEffectInbox.Count > 0)
            {
                ready.Add(_enemyEffectInbox.Dequeue());
            }

            return ready;
        }
    }

    private void HandleEnemyEffect(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadEnemyEffect(span) is not { } effect)
        {
            _channel.NoteMalformed();
            return;
        }

        // Ack first and unconditionally: a duplicate means our previous ack was lost.
        int length = PacketCodec.Write(
            _replyBuffer, new EnemyEffectAckPacket(HostPeerId, effect.OriginPeerId, effect.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }

        if (effect.OriginPeerId == HostPeerId)
        {
            return; // the host never reports to itself; a spoofed origin is not a peer's effect
        }

        lock (_gate)
        {
            if (!_peers.ContainsKey(effect.OriginPeerId))
            {
                return; // only a known peer
            }

            if (!_enemyEffectSeen.Add((effect.OriginPeerId, effect.OpSequence)))
            {
                return; // a retransmit that raced its ack
            }

            _enemyEffectSeenOrder.Enqueue((effect.OriginPeerId, effect.OpSequence));
            while (_enemyEffectSeenOrder.Count > EnemyEffectSeenCapacity)
            {
                _enemyEffectSeen.Remove(_enemyEffectSeenOrder.Dequeue());
            }

            if (_enemyEffectInbox.Count >= EnemyEffectInboxCapacity)
            {
                _enemyEffectInbox.Dequeue(); // an effect is only worth its freshest replay
            }

            _enemyEffectInbox.Enqueue(effect);
        }
    }
}
