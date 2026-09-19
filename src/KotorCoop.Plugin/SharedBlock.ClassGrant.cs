namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- class-grant relay (version 68, region 0x4C20..0x4C38, no TotalSize growth) ------
    //
    // docs/research/class-and-power-grants.md, coop.ini class_grant_sync (default OFF,
    // NEEDS-LIVE): Master Dorak's path choice on Dantooine (danm13_s.rim:k_pdan_makejedi) runs
    // AddMultiClass(nJediClass, GetPC()) on the engine that owns the conversation only, so the
    // other player's character never became a Jedi (docs/review-seamless-2026-09-18.md
    // blocker #1). AddMultiClassHook captures the call on the driver at the VM handler; this
    // region hands it to the agent, which relays it (ClassGrantPacket, reliable) and asks the
    // peer's plugin to run the SAME VM handler on its own player (Odyssey.AddMultiClass).
    //
    // Same three-part, serial-edged shape as the galaxy-map departure relay directly before it
    // (0x4C00..0x4C18):
    //   plugin -> agent  OffsetClassGrantSerial/Class          "my player just gained a class"
    //   agent  -> plugin OffsetClassGrantRequestSerial/Class   "add this class to your player"
    //   plugin -> agent  OffsetClassGrantApplied/Status         the ack for the request
    //
    // Fits the galaxy region's own free tail (0x4C18..0x4D00), so TotalSize stays 0x4D00.
    // One slot, not a ring: the engine caps a creature at two classes and a vanilla
    // playthrough adds exactly one, so two grants inside one agent tick cannot happen
    // (the addclass verb aside, and a burst there only loses the older of two test grants).

    /// <summary>Plugin -&gt; agent. Bumped LAST by <see cref="ClassGrantController"/>, after
    /// <see cref="OffsetClassGrantClass"/> is written, once per <c>AddMultiClass</c> a SCRIPT
    /// ran against this instance's own player (a replay of a peer's grant never bumps it —
    /// <c>AddMultiClassHook.NextGrantIsReplay</c>). 0 = never.</summary>
    public const int OffsetClassGrantSerial = 0x4C20;

    /// <summary>Plugin -&gt; agent. The <c>classes.2da</c> row the script added.</summary>
    public const int OffsetClassGrantClass = 0x4C24;

    /// <summary>Agent -&gt; plugin. A request is pending while this is ahead of
    /// <see cref="OffsetClassGrantApplied"/>. Written LAST, after the class.</summary>
    public const int OffsetClassGrantRequestSerial = 0x4C28;

    /// <summary>Agent -&gt; plugin. The <c>classes.2da</c> row to add to the local player.</summary>
    public const int OffsetClassGrantRequestClass = 0x4C2C;

    /// <summary>Plugin -&gt; agent. Echoes <see cref="OffsetClassGrantRequestSerial"/> once the
    /// request has been acted on (or refused). Written AFTER the status.</summary>
    public const int OffsetClassGrantApplied = 0x4C30;

    /// <summary>Plugin -&gt; agent. <see cref="ClassGrantStatusCode"/> for the last handled
    /// request. Region ends at 0x4C38; 0x4C38..0x4D00 is free.</summary>
    public const int OffsetClassGrantStatus = 0x4C34;
}
