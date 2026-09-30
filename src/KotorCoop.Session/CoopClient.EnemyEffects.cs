using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Memory;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — this client's side of the enemy-effect relay (docs/research/enemy-effects.md):
/// a non-damage effect the local player cast on a mirrored enemy, reported reliably to the host
/// (the enemy's owner), which acks it and replays it on the real body.
/// </summary>
public sealed partial class CoopClient
{
    private readonly EnemyEffectSender _enemyEffectSender = new();

    private readonly DamageReportWorldWatch _enemyEffectCaptureWorld = new();
    public EnemyEffectCaptureWorld EnemyEffectCaptureSource
    {
        get
        {
            lock (_gate)
            {
                if (_localPeerId == 0 || _localWorld.NativeWorldEpoch == 0 || _livenessLocalLoadQuarantined
                    || (Capabilities & SessionCapabilities.Enemies) == 0
                    || !_enemyEffectCaptureWorld.Accept(_hostSessionId, _hostWorld.Module, _hostWorld.LoadSerial,
                        _hostWorld.NativeWorldEpoch, _localWorld.Module, _localWorld.NativeWorldEpoch)) return default;
                return new(_hostSessionId,_hostWorld.LoadSerial,_hostWorld.NativeWorldEpoch,_localWorld.NativeWorldEpoch,_localWorld.LoadSerial);
            }
        }
    }

    public void PublishEnemyEffect(string module, uint enemyId, PeerEffectKind kind, uint durationMs, uint magnitude, uint capturedSerial = 0, uint localNativeEpoch = 0, uint localLoadSerial = 0, uint capturedHostSession = 0, uint capturedHostLoad = 0, uint capturedHostEpoch = 0, uint capturedIncarnation = 0)
    {
        lock (_gate)
        {
            if (capturedIncarnation == 0 || capturedIncarnation != _settings.DamageIncarnation || _localPeerId == 0 || capturedSerial == 0 || localNativeEpoch == 0 || _settings.DamageIncarnation == 0
                || capturedHostSession != _hostSessionId || capturedHostLoad != _hostWorld.LoadSerial
                || capturedHostEpoch == 0 || capturedHostEpoch != _hostWorld.NativeWorldEpoch
                || _livenessLocalLoadQuarantined || localNativeEpoch != _localWorld.NativeWorldEpoch
                || localLoadSerial != _localWorld.LoadSerial || _hostSessionId == 0 || _hostWorld.NativeWorldEpoch == 0
                || !string.Equals(module, _localWorld.Module, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(module, _hostWorld.Module, StringComparison.OrdinalIgnoreCase)) return;
            _enemyEffectSender.EnqueueLocal(_localPeerId, module, enemyId, kind, durationMs, magnitude, SessionClock.NowMs,
                capturedSerial, _settings.DamageIncarnation, _hostSessionId, _hostWorld.LoadSerial, _hostWorld.NativeWorldEpoch);
        }
    }

    /// <summary>A client never receives a report: enemy effects flow client to host only.</summary>
    public IReadOnlyList<EnemyEffectPacket> DrainEnemyEffects() => [];

    private void FlushEnemyEffects(long now)
    {
        foreach (EnemyEffectPacket op in _enemyEffectSender.CollectDue(now))
        {
            lock (_gate)
            {
                if (_livenessLocalLoadQuarantined || op.HostSessionId != _hostSessionId || op.HostLoadSerial != _hostWorld.LoadSerial
                    || op.HostNativeEpoch != _hostWorld.NativeWorldEpoch || !string.Equals(op.Module, _hostWorld.Module, StringComparison.OrdinalIgnoreCase)) continue;
            }
            int length = PacketCodec.Write(_sendBuffer, op);
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    private void HandleEnemyEffectAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (from.Equals(_hostEndPoint) && PacketCodec.ReadEnemyEffectAck(span) is { } ack && ack.PeerId == 1)
        {
            _enemyEffectSender.Acknowledge(ack);
        }
    }
}
