namespace KotorCoop.Plugin;

// Host-start conversation relay (shared block version 97, region 0x5C50..0x5CB0). See
// SharedBlock.HostConversation.cs for the layout.
internal sealed unsafe partial class SharedBlockView
{
    /// <summary>Plugin -&gt; agent. Publishes a refused host-only start: tag and resref first,
    /// the serial LAST, so the agent never pairs a new serial with the previous capture's text.</summary>
    public void PublishHostConversationCapture(string tag, string resref)
    {
        WriteAsciiField(SharedBlock.OffsetHostConversationClientTag, tag, SharedBlock.HostConversationTagLength);
        WriteAsciiField(SharedBlock.OffsetHostConversationClientResref, resref, SharedBlock.HostConversationResrefLength);
        *U32(SharedBlock.OffsetHostConversationClientSerial) = *U32(SharedBlock.OffsetHostConversationClientSerial) + 1;
    }

    /// <summary>Agent -&gt; plugin (HOST only). Bumped by the agent once the fields are written.</summary>
    public uint HostConversationHostSerial => *U32(SharedBlock.OffsetHostConversationHostSerial);

    public uint HostConversationHostPeerId => *U32(SharedBlock.OffsetHostConversationHostPeerId);

    public string HostConversationHostTag => ReadAsciiField(
        SharedBlock.OffsetHostConversationHostTag, SharedBlock.HostConversationTagLength);

    public string HostConversationHostResref => ReadAsciiField(
        SharedBlock.OffsetHostConversationHostResref, SharedBlock.HostConversationResrefLength);
}
