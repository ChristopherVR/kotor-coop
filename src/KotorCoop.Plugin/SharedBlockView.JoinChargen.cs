namespace KotorCoop.Plugin;

internal sealed unsafe partial class SharedBlockView
{
    /// <summary>Agent -&gt; plugin. Bumped to arm the new-character capture. See
    /// <see cref="SharedBlock.OffsetJoinChargenRequest"/>.</summary>
    public uint JoinChargenRequest => *U32(SharedBlock.OffsetJoinChargenRequest);

    /// <summary>Plugin -&gt; agent. The request serial the plugin has acted on.</summary>
    public uint JoinChargenApplied
    {
        get => *U32(SharedBlock.OffsetJoinChargenApplied);
        set => *U32(SharedBlock.OffsetJoinChargenApplied) = value;
    }

    /// <summary>Plugin -&gt; agent. One of <c>SharedBlock.JoinChargenState*</c>.</summary>
    public uint JoinChargenState
    {
        get => *U32(SharedBlock.OffsetJoinChargenState);
        set => *U32(SharedBlock.OffsetJoinChargenState) = value;
    }

    /// <summary>Plugin -&gt; agent. Size of the captured character file, in bytes.</summary>
    public uint JoinChargenBytes
    {
        get => *U32(SharedBlock.OffsetJoinChargenBytes);
        set => *U32(SharedBlock.OffsetJoinChargenBytes) = value;
    }
}
