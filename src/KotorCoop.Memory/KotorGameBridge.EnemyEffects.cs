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

        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectOutHead, out uint head)
            || EnemyEffectMailbox.SerialOf(head) == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectOutArgs, out uint args)
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectOutEnemy, out uint enemy)
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectOutHead, out uint headAgain)
            || headAgain != head)
        {
            // Head is written LAST by the plugin; a head that moved under the read is a torn pair.
            return false;
        }

        sample = new EnemyEffectSample(
            EnemyEffectMailbox.SerialOf(head), enemy, EnemyEffectMailbox.KindOf(head),
            EnemyEffectMailbox.DurationMsOf(args), EnemyEffectMailbox.MagnitudeOf(args));
        return true;
    }

    /// <inheritdoc />
    public bool TryAckEnemyEffectOut(uint serial) =>
        _pluginBlock != 0 && _memory.TryWrite(_pluginBlock + BlockEnemyEffectOutAck, serial);

    /// <inheritdoc />
    public bool TryWriteEnemyEffectIn(in EnemyEffectSample sample)
    {
        if (_pluginBlock == 0 || sample.Serial == 0)
        {
            return false;
        }

        // Head (which carries the serial) LAST, so the plugin never pairs a new serial with the
        // previous effect's enemy or arguments.
        bool ok = _memory.TryWrite(_pluginBlock + BlockEnemyEffectInEnemy, sample.EnemyId);
        ok &= _memory.TryWrite(
            _pluginBlock + BlockEnemyEffectInArgs,
            EnemyEffectMailbox.PackArgs(sample.DurationMs, sample.Magnitude));
        ok &= _memory.TryWrite(
            _pluginBlock + BlockEnemyEffectInHead,
            EnemyEffectMailbox.PackHead(sample.Serial, sample.Kind));
        return ok;
    }

    /// <inheritdoc />
    public bool TryReadEnemyEffectApplied(out uint serial)
    {
        serial = 0;
        return _pluginBlock != 0 && _memory.TryReadUInt32(_pluginBlock + BlockEnemyEffectInApplied, out serial);
    }
}
