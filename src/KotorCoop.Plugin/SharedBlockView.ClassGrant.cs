namespace KotorCoop.Plugin;

// Class-grant relay (shared block version 68, region 0x4C20..0x4C38). See
// SharedBlock.ClassGrant.cs for the layout and docs/research/class-and-power-grants.md for
// why a class added by a script on one instance has to be replayed on the other.
internal sealed unsafe partial class SharedBlockView
{
    /// <summary>
    /// Plugin -&gt; agent. Publishes "a script just added <paramref name="classId"/> to this
    /// instance's own player" — the class first, the serial LAST, so the agent never pairs a
    /// new serial with the previous grant's class. Called from
    /// <see cref="ClassGrantController"/> only.
    /// </summary>
    public void PublishClassGrant(int classId)
    {
        *I32(SharedBlock.OffsetClassGrantClass) = classId;
        *U32(SharedBlock.OffsetClassGrantSerial) = *U32(SharedBlock.OffsetClassGrantSerial) + 1;
    }

    /// <summary>Plugin -&gt; agent. The raw grant counter (diagnostics, the <c>addclass</c> verb).</summary>
    public uint ClassGrantSerial => *U32(SharedBlock.OffsetClassGrantSerial);

    /// <summary>Agent -&gt; plugin. A request is pending while this is ahead of
    /// <see cref="ClassGrantApplied"/>.</summary>
    public uint ClassGrantRequestSerial => *U32(SharedBlock.OffsetClassGrantRequestSerial);

    /// <summary>Agent -&gt; plugin. The class the pending request asks this instance to add.</summary>
    public int ClassGrantRequestClass => *I32(SharedBlock.OffsetClassGrantRequestClass);

    /// <summary>Plugin -&gt; agent. Echoes <see cref="ClassGrantRequestSerial"/> once the request
    /// has been acted on. Set AFTER <see cref="ClassGrantStatus"/>.</summary>
    public uint ClassGrantApplied
    {
        get => *U32(SharedBlock.OffsetClassGrantApplied);
        set => *U32(SharedBlock.OffsetClassGrantApplied) = value;
    }

    /// <summary>Plugin -&gt; agent. <see cref="ClassGrantStatusCode"/> of the last request.</summary>
    public uint ClassGrantStatus
    {
        get => *U32(SharedBlock.OffsetClassGrantStatus);
        set => *U32(SharedBlock.OffsetClassGrantStatus) = value;
    }
}
