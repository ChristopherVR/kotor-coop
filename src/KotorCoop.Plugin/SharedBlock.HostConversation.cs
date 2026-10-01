namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- host-start conversation relay (version 97, region 0x5C50..0x5CB0) ----------------
    //
    // docs/research/dialogue-sync.md, host-start relay: a joiner whose engine refuses a
    // host-only conversation start (HostLedConversationRule, enforced by DialogueGateHook)
    // publishes the target's tag and the dialogue resref here; the agent relays it to the host
    // as HostConversationRequestPacket, and the HOST's agent writes it into the host half below
    // for HostConversationRequestController to start for the host's own player.
    //
    // Two independent halves (one instance only plays one role, both exist in every build):
    //   plugin -> agent  0x5C50..0x5C80  "my engine just refused this host-only start"
    //   agent  -> plugin 0x5C80..0x5CB0  "start this conversation" (fire-and-forget, no ack)
    // Writers fill the strings first and bump the serial LAST. No TotalSize growth: the region
    // sits in the free 0x5C50..0x6000 tail after SharedBlock.PartySelect.cs.

    public const int HostConversationTagLength = 24;
    public const int HostConversationResrefLength = 16;

    /// <summary>Plugin -&gt; agent. Bumped per published capture. 0 = never.</summary>
    public const int OffsetHostConversationClientSerial = 0x5C50;

    /// <summary>Plugin -&gt; agent. The refused target's tag, NUL-padded ASCII.</summary>
    public const int OffsetHostConversationClientTag = 0x5C54;

    /// <summary>Plugin -&gt; agent. The dialogue resref the engine was asked to run (empty for
    /// the object's default conversation). Ends at 0x5C7C; 0x5C7C..0x5C80 is padding.</summary>
    public const int OffsetHostConversationClientResref = 0x5C6C;

    /// <summary>Agent -&gt; plugin (HOST only). A request is pending while this is ahead of the
    /// controller's own applied count. Written LAST.</summary>
    public const int OffsetHostConversationHostSerial = 0x5C80;

    /// <summary>Agent -&gt; plugin. The requesting peer's id, for the log line only.</summary>
    public const int OffsetHostConversationHostPeerId = 0x5C84;

    public const int OffsetHostConversationHostTag = 0x5C88;

    /// <summary>Region ends at 0x5CB0; 0x5CB0..0x6000 is free.</summary>
    public const int OffsetHostConversationHostResref = 0x5CA0;
}
