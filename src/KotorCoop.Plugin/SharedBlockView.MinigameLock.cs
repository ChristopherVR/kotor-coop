namespace KotorCoop.Plugin;

internal sealed unsafe partial class SharedBlockView
{
    /// <summary>Agent -&gt; plugin. Nonzero while a PEER is inside a minigame and this
    /// instance's player must therefore be held still. See
    /// <see cref="SharedBlock.OffsetMinigameLockActive"/>.</summary>
    public uint MinigameLockActive
    {
        get => *U32(SharedBlock.OffsetMinigameLockActive);
        set => *U32(SharedBlock.OffsetMinigameLockActive) = value;
    }

    /// <summary>The racing peer's name, as null-terminated ASCII inside the block. Read only
    /// while <see cref="MinigameLockActive"/> is set; the same raw-pointer shape
    /// <see cref="GetRemoteName"/> hands the notice drawers.</summary>
    public byte* MinigameLockName => baseAddress + SharedBlock.OffsetMinigameLockName;
}
