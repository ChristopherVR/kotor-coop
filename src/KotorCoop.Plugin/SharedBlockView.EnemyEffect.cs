namespace KotorCoop.Plugin;

// Client-cast enemy-effect relay (shared block version 70, region 0x4C60..0x4C80). See
// SharedBlock.EnemyEffect.cs for the layout and docs/research/enemy-effects.md.
internal sealed unsafe partial class SharedBlockView
{
    /// <summary>Plugin -&gt; agent. Offers one captured effect: enemy and args first, the head
    /// (which carries the serial) LAST, so the agent never pairs a new serial with the previous
    /// effect's fields.</summary>
    public void PublishEnemyEffectOut(uint enemyId, uint head, uint args, uint epoch, uint load, uint hostSession, uint hostLoad, uint hostEpoch)
    {
        *U32(SharedBlock.OffsetEnemyEffectOutHead) = 0;
        *U32(SharedBlock.OffsetEnemyEffectOutHostSession) = hostSession;
        *U32(SharedBlock.OffsetEnemyEffectOutHostLoad) = hostLoad;
        *U32(SharedBlock.OffsetEnemyEffectOutHostEpoch) = hostEpoch;
        *U32(SharedBlock.OffsetEnemyEffectOutEpoch) = epoch;
        *U32(SharedBlock.OffsetEnemyEffectOutLoad) = load;
        *U32(SharedBlock.OffsetEnemyEffectOutEnemy) = enemyId;
        *U32(SharedBlock.OffsetEnemyEffectOutArgs) = args;
        *U32(SharedBlock.OffsetEnemyEffectOutHead) = head;
    }

    /// <summary>Agent -&gt; plugin. The OUT serial the agent has taken.</summary>
    public uint EnemyEffectOutAck => *U32(SharedBlock.OffsetEnemyEffectOutAck);

    /// <summary>The head currently published (0 = empty).</summary>
    public uint EnemyEffectOutHead => *U32(SharedBlock.OffsetEnemyEffectOutHead);

    /// <summary>Agent -&gt; plugin. One reported effect to apply: (enemy id, head, args). Read the
    /// head FIRST and re-check it after the other two — the agent writes it last.</summary>
    public (uint EnemyId, uint Head, uint Args, uint Sequence) GetEnemyEffectIn()
    {
        uint sequence = *U32(SharedBlock.OffsetEnemyEffectInSequence);
        if (sequence == 0 || (sequence & 1) != 0) return (0,0,0,0);
        uint head = *U32(SharedBlock.OffsetEnemyEffectInHead);
        uint enemy = *U32(SharedBlock.OffsetEnemyEffectInEnemy);
        uint args = *U32(SharedBlock.OffsetEnemyEffectInArgs);
        return sequence == *U32(SharedBlock.OffsetEnemyEffectInSequence) && head == *U32(SharedBlock.OffsetEnemyEffectInHead) ? (enemy, head, args, sequence) : (0, 0, 0, 0);
    }

    public bool EnemyEffectInWorldCurrent(uint expectedSequence)
    {
        uint sequence = *U32(SharedBlock.OffsetEnemyEffectInSequence);
        if (sequence != expectedSequence || sequence == 0 || (sequence & 1) != 0) return false;
        uint epoch = *U32(SharedBlock.OffsetEnemyEffectInEpoch), load = *U32(SharedBlock.OffsetEnemyEffectInLoad);
        uint session = *U32(SharedBlock.OffsetEnemyEffectInSession), active = *U32(SharedBlock.OffsetEnemyEffectActiveSession);
        System.Threading.Thread.MemoryBarrier();
        return EnemyEffectNamespaceRule.Matches(sequence, *U32(SharedBlock.OffsetEnemyEffectInSequence),
            session, active, epoch, LivenessNativeEpoch, load, WorldLoadSerial);
    }

    /// <summary>Plugin -&gt; agent. Echoes the IN serial once handled (applied OR refused).</summary>
    public uint EnemyEffectInApplied
    {
        get => *U32(SharedBlock.OffsetEnemyEffectInApplied);
        set => *U32(SharedBlock.OffsetEnemyEffectInApplied) = value;
    }

    public void ClearEnemyEffectSession()
    {
        *U32(SharedBlock.OffsetEnemyEffectSourceSequence) = 0;
        *U32(SharedBlock.OffsetEnemyEffectActiveSession) = 0;
        *U32(SharedBlock.OffsetEnemyEffectInSequence) = 0;
    }

    /// <summary>Resets the plugin-owned words at a world edge (the agent's are its own).</summary>
    public void ClearEnemyEffectOut()
    {
        *U32(SharedBlock.OffsetEnemyEffectOutHead) = 0;
        *U32(SharedBlock.OffsetEnemyEffectOutEnemy) = 0;
        *U32(SharedBlock.OffsetEnemyEffectOutArgs) = 0;
    }
}
