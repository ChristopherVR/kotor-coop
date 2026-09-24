namespace KotorCoop.Plugin;

// Exact enemy-health baseline persistence (version 76, region 0x5400..0x5800, TotalSize grown
// to 0x5800). The full layout and codec are KotorCoop.Protocol.EnemyScalingBaselineChannel,
// compiled into this plugin; these literal mirrors exist so SharedBlockNewRegionOffsetMirrorTests
// can compare them with KotorGameBridge.EnemyScalingBaseline.cs, and
// EnemyScalingBaselineChannelTests pins both to the channel's own constants.
// docs/research/enemy-health-persistence.md.
internal partial struct SharedBlock
{
    /// <summary>Plugin -&gt; agent. Seqlock of the world/export page (0x5404..0x5640).</summary>
    public const int OffsetEnemyBaselineExportSequence = 0x5400;

    /// <summary>Agent -&gt; plugin. The export sequence the agent consumed.</summary>
    public const int OffsetEnemyBaselineExportAck = 0x5640;

    /// <summary>Agent -&gt; plugin. Seqlock of the verdict/import page (0x564C..0x5760).</summary>
    public const int OffsetEnemyBaselineImportSequence = 0x5648;

    /// <summary>Plugin -&gt; agent. The import sequence the plugin consumed.</summary>
    public const int OffsetEnemyBaselineImportAck = 0x5760;

    /// <summary>Plugin -&gt; agent. Seqlock of the native save-request mailbox (0x576C..0x57C0).</summary>
    public const int OffsetEnemyBaselineSaveSequence = 0x5768;
}
