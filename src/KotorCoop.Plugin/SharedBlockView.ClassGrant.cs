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
    /// <see cref="ClassGrantApplied"/>. Settable so the local <c>jedichoice</c> dev verb
    /// (CheatController.JediChoice.cs) can manufacture a request without a network round trip,
    /// exactly the shape a peer's real relay writes.</summary>
    public uint ClassGrantRequestSerial
    {
        get => *U32(SharedBlock.OffsetClassGrantRequestSerial);
        set => *U32(SharedBlock.OffsetClassGrantRequestSerial) = value;
    }

    /// <summary>Agent -&gt; plugin. Vestigial since 2026-09-25 (the receiving side no longer
    /// forces this exact class onto the local player — see
    /// <see cref="JediChoicePromptController"/>); kept only so the wire's field shape needs no
    /// change and a stale peer sending it does nothing harmful.</summary>
    public int ClassGrantRequestClass
    {
        get => *I32(SharedBlock.OffsetClassGrantRequestClass);
        set => *I32(SharedBlock.OffsetClassGrantRequestClass) = value;
    }

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
