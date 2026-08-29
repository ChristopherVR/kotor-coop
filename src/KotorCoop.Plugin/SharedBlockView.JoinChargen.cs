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

    /// <summary>Agent -&gt; plugin. Bumped to run a chargen-drive verb.</summary>
    public uint ChargenDriveRequest => *U32(SharedBlock.OffsetChargenDriveRequest);

    /// <summary>Agent -&gt; plugin. Low 16 bits: one of <c>SharedBlock.ChargenDrive*</c>. High 16
    /// bits: the class index, for <c>ChargenDriveQuick</c>.</summary>
    public uint ChargenDriveKind => *U32(SharedBlock.OffsetChargenDriveKind);

    /// <summary>Plugin -&gt; agent. The serial acted on.</summary>
    public uint ChargenDriveApplied
    {
        get => *U32(SharedBlock.OffsetChargenDriveApplied);
        set => *U32(SharedBlock.OffsetChargenDriveApplied) = value;
    }

    /// <summary>Plugin -&gt; agent. 0 done, nonzero a refusal code.</summary>
    public uint ChargenDriveStatus
    {
        get => *U32(SharedBlock.OffsetChargenDriveStatus);
        set => *U32(SharedBlock.OffsetChargenDriveStatus) = value;
    }
}
