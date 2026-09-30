// The agent's half of the client-cast enemy-effect relay (KotorCoop.Plugin's
// SharedBlock.EnemyEffect.cs, shared block version 70, region 0x4C60..0x4C80): read the effect the
// plugin captured on a mirrored enemy, ack it so the plugin offers the next, and (host side) hand
// the plugin a client's reported effect to apply on the real enemy. docs/research/enemy-effects.md.
namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    /// <inheritdoc />
    public bool TryReadEnemyEffectOut(out EnemyEffectSample sample)
    {
        sample = default;
        uint incarnation = ReadDamageDealtIncarnation();

        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectOutHead, out uint head)
            || EnemyEffectMailbox.SerialOf(head) == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectOutArgs, out uint args)
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectOutEnemy, out uint enemy)
            || incarnation == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectOutHostSession, out uint hostSession) || hostSession == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectOutHostLoad, out uint hostLoad)
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectOutHostEpoch, out uint hostEpoch) || hostEpoch == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectOutEpoch, out uint epoch) || epoch == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectOutLoad, out uint load)
            || !TryReadLivenessEpoch(out uint currentEpoch) || currentEpoch != epoch
            || !TryReadWorldLoadSerial(out uint currentLoad) || currentLoad != load
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectOutHead, out uint headAgain)
            || headAgain != head)
        {
            // Head is written LAST by the plugin; a head that moved under the read is a torn pair.
            return false;
        }

        sample = new EnemyEffectSample(
            EnemyEffectMailbox.SerialOf(head), enemy, EnemyEffectMailbox.KindOf(head),
            EnemyEffectMailbox.DurationMsOf(args), EnemyEffectMailbox.MagnitudeOf(args), epoch, load, hostSession, hostLoad, hostEpoch, incarnation);
        return true;
    }

    /// <inheritdoc />
    public bool TryAckEnemyEffectOut(uint serial) =>
        _pluginBlock != 0 && _memory.TryWrite(_pluginBlock + BlockEnemyEffectOutAck, serial);

    /// <inheritdoc />
    public bool TryWriteEnemyEffectIn(in EnemyEffectSample sample)
    {
        if (_pluginBlock == 0 || sample.Serial == 0 || sample.NativeEpoch == 0 || sample.HostSessionId == 0
            || !TryReadLivenessEpoch(out uint epoch) || epoch != sample.NativeEpoch
            || !TryReadWorldLoadSerial(out uint load) || load != sample.LoadSerial
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectInSequence, out uint sequence)) return false;
        uint odd = (sequence + 1) | 1;
        if (!_memory.TryWrite(_pluginBlock + BlockEnemyEffectInSequence, odd)) return false;
        // Failure leaves the bank odd; never publish a partly written effect.
        if (!_memory.TryWrite(_pluginBlock + BlockEnemyEffectInEpoch, sample.NativeEpoch)
            || !_memory.TryWrite(_pluginBlock + BlockEnemyEffectInLoad, sample.LoadSerial)
            || !_memory.TryWrite(_pluginBlock + BlockEnemyEffectInSession, sample.HostSessionId)
            || !_memory.TryWrite(_pluginBlock + BlockEnemyEffectInEnemy, sample.EnemyId)
            || !_memory.TryWrite(_pluginBlock + BlockEnemyEffectInArgs, EnemyEffectMailbox.PackArgs(sample.DurationMs,sample.Magnitude))
            || !_memory.TryWrite(_pluginBlock + BlockEnemyEffectInHead, EnemyEffectMailbox.PackHead(sample.Serial,sample.Kind))
            || !TryReadLivenessEpoch(out uint afterEpoch) || afterEpoch != epoch
            || !TryReadWorldLoadSerial(out uint afterLoad) || afterLoad != load) return false;
        return _memory.TryWrite(_pluginBlock + BlockEnemyEffectInSequence, (odd + 1) & ~1u);
    }

    public bool TrySetEnemyEffectHostSession(uint session) => _pluginBlock != 0
        && _memory.TryWrite(_pluginBlock + BlockEnemyEffectActiveSession, session);

    /// <inheritdoc />
    public bool TryReadEnemyEffectApplied(out uint serial)
    {
        serial = 0;
        return _pluginBlock != 0 && _memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectInApplied, out serial);
    }
    public bool TryPublishEnemyEffectCaptureWorld(in EnemyEffectCaptureWorld world)
    {
        if (_pluginBlock == 0 || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectSourceSequence,out uint sequence)) return false;
        uint odd=(sequence+1)|1u;
        if (!_memory.TryWrite(_pluginBlock+BlockEnemyEffectSourceSequence,odd)) return false;
        if (!_memory.TryWrite(_pluginBlock+BlockEnemyEffectSourceHostSession,world.HostSession)
            || !_memory.TryWrite(_pluginBlock+BlockEnemyEffectSourceHostLoad,world.HostLoad)
            || !_memory.TryWrite(_pluginBlock+BlockEnemyEffectSourceHostEpoch,world.HostEpoch)
            || !_memory.TryWrite(_pluginBlock+BlockEnemyEffectSourceLocalEpoch,world.LocalEpoch)
            || !_memory.TryWrite(_pluginBlock+BlockEnemyEffectSourceLocalLoad,world.LocalLoad)) return false;
        return _memory.TryWrite(_pluginBlock+BlockEnemyEffectSourceSequence,(odd+1)&~1u);
    }
}
