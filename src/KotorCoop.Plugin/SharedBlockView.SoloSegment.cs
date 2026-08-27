namespace KotorCoop.Plugin;

internal sealed unsafe partial class SharedBlockView
{
    /// <summary>Plugin -&gt; agent. Nonzero while this instance's own local player is the
    /// active owner of a story-forced solo segment. See
    /// <see cref="SharedBlock.OffsetLocalInSoloSegment"/>.</summary>
    public uint LocalInSoloSegment
    {
        get => *U32(SharedBlock.OffsetLocalInSoloSegment);
        set => *U32(SharedBlock.OffsetLocalInSoloSegment) = value;
    }
}
