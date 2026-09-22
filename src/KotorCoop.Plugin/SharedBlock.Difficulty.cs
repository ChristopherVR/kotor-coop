using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- runtime difficulty agreement (version 72, free tail at 0x4E40) --------------
    // The container-loot mailbox ends at 0x4E3C. The difficulty observer uses two bytes in
    // the remaining tail; TotalSize stays 0x4F00.

    /// <summary>Plugin -&gt; agent. CClientOptions difficulty index, 0..2.</summary>
    public const int OffsetDifficultyLevel = 0x4E40;

    /// <summary>Plugin -&gt; agent. 1 after a valid difficulty level was written.</summary>
    public const int OffsetDifficultyKnown = 0x4E41;

    [FieldOffset(OffsetDifficultyLevel)] public byte DifficultyLevel;
    [FieldOffset(OffsetDifficultyKnown)] public byte DifficultyKnown;
}
