using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — the receiving end of the enemy-effect relay (docs/research/enemy-effects.md): a
/// client's report is authenticated and world-fenced before ACK, deduplicated (a retransmit racing its own ack), bounded, and
/// queued for THIS instance's plugin, which applies it to the real enemy body. Nothing is relayed
/// onward: the other clients see the effect through the ordinary enemy census.
/// </summary>
public sealed partial class CoopHost
{
    /// <summary>Reports the agent has not drained yet. Bounded: a flooding peer costs itself
    /// reports, never unbounded host memory.</summary>
    private const int EnemyEffectInboxCapacity = 64;

    private readonly Queue<EnemyEffectPacket> _enemyEffectInbox = new();

    private readonly EnemyEffectReplayWindow _enemyEffectReplay = new();
    public uint EnemyEffectHostSessionId => _sessionId;
    public bool EnemyEffectWorldCurrent(EnemyEffectPacket effect)
    {
        lock (_gate) return DamageWorldMatches(effect.HostSessionId, effect.HostLoadSerial, effect.HostNativeEpoch, effect.Module);
    }

    /// <summary>The host's own casts already ran against the real enemy, so it reports nothing.</summary>
    public void PublishEnemyEffect(string module, uint enemyId, PeerEffectKind kind, uint durationMs, uint magnitude, uint capturedSerial = 0, uint localNativeEpoch = 0, uint localLoadSerial = 0, uint capturedHostSession = 0, uint capturedHostLoad = 0, uint capturedHostEpoch = 0, uint capturedIncarnation = 0)
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
                EnemyEffectPacket effect = _enemyEffectInbox.Dequeue();
                if (DamageWorldMatches(effect.HostSessionId, effect.HostLoadSerial, effect.HostNativeEpoch, effect.Module)) ready.Add(effect);
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

        lock (_gate)
        {
            if (effect.OriginPeerId == HostPeerId
                || !DamageWorldMatches(effect.HostSessionId, effect.HostLoadSerial, effect.HostNativeEpoch, effect.Module)
                || !_peers.TryGetValue(effect.OriginPeerId, out PeerConnection? peer)
                || !peer.EndPoint.Equals(from) || peer.DamageIncarnation != effect.Incarnation
                || effect.Incarnation == 0 || effect.OpSequence is 0 or > 0xFFFFFF) return;

            // Authenticate and validate the immutable namespace before acknowledging. A duplicate
            // in this namespace is still acknowledged, since the previous ACK may have been lost.
            int length = PacketCodec.Write(_replyBuffer, new EnemyEffectAckPacket(HostPeerId, effect.OriginPeerId,
                effect.OpSequence, effect.Incarnation, effect.HostSessionId, effect.HostLoadSerial, effect.HostNativeEpoch));
            if (length > 0) _channel.Send(from, _replyBuffer.AsSpan(0, length));
            _enemyEffectReplay.Retain(_peers.Keys.Concat(_retired.Values.Select(peer => peer.Id)).ToHashSet());
            if (!_enemyEffectReplay.Observe(effect.OriginPeerId, effect.Incarnation, effect.OpSequence)) return;
            if (_enemyEffectInbox.Count >= EnemyEffectInboxCapacity) _enemyEffectInbox.Dequeue();
            _enemyEffectInbox.Enqueue(effect);
        }
    }
}
