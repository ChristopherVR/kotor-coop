namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- party-select relay (version 81, region 0x5C00..0x5C48) --------------------------
    //
    // docs/research/galaxy-map-and-party-select.md's 2026-09-25 relay section: a client's own
    // copy of CSWGuiPartySelection is suppressed outright (PartySelectSuppressionController),
    // so a client-raised screen used to just vanish with nobody able to pick — the host never
    // even learned it happened. This region turns that into a request: the client captures
    // what the panel would have shown (exit_script, forced NPCs) before sweeping it, the
    // request crosses the wire as PartySelectRequestPacket, and the HOST's own plugin opens
    // its OWN unmodified copy of the same screen with the same parameters through the native
    // ShowPartySelection 0x0062DD20 the retail nwscript action already calls.
    //
    // Two independent halves, the same shape SharedBlock.GalaxyDeparture.cs uses for an
    // identical reason (one instance only ever plays one role at a time in practice, but both
    // halves exist in every build):
    //   plugin -> agent  0x5C00..0x5C1C  "this instance's own client panel was just captured"
    //   agent  -> plugin 0x5C1C..0x5C48  "open party-select with these params" + the ack +
    //                                     the eventual confirmed/cancelled outcome
    //
    // Starts cleanly at 0x5C00: SharedBlock.cs's own "0x5C00..0x6000 is deliberately left
    // alone here — reserved for other in-flight work" note names exactly this gap, and
    // SharedBlock.PlacedMines.cs (version 78) already claimed 0x5A00..0x5BBC below it. No
    // TotalSize growth needed — 0x5C48..0x6000 remains free.

    /// <summary>Plugin -&gt; agent. Bumped when THIS instance's own client copy of
    /// CSWGuiPartySelection is captured (about to be suppressed) —
    /// <see cref="PartySelectSuppressionController"/>. 0 = never captured.</summary>
    public const int OffsetPartySelectClientSerial = 0x5C00;

    /// <summary>Plugin -&gt; agent. The panel's <c>exit_script</c> field
    /// (<c>CSWGuiPartySelection+0x074</c>, a <c>CExoString</c>) at the moment of capture — a
    /// resref-length NUL-padded ASCII name, <see cref="PartySelectScriptLength"/> bytes.
    /// Empty when the panel carried no exit script.</summary>
    public const int OffsetPartySelectClientExitScript = 0x5C04;

    public const int PartySelectScriptLength = 16;

    /// <summary>Plugin -&gt; agent. The first forced NPC roster slot the panel was showing
    /// (read back from <c>party_data[i]+0x448</c>'s forced-selection bit —
    /// <c>CSWGuiPartySelection::SetForcedNPC 0x006BE310</c>), or -1 when none.</summary>
    public const int OffsetPartySelectClientForceNpc1 = 0x5C14;

    /// <summary>Plugin -&gt; agent. The second forced NPC roster slot, or -1.</summary>
    public const int OffsetPartySelectClientForceNpc2 = 0x5C18;

    /// <summary>Agent -&gt; plugin (HOST instance only). A request is pending while this is
    /// ahead of <see cref="OffsetPartySelectHostApplied"/>. Written LAST, after the script and
    /// forced-NPC fields below.</summary>
    public const int OffsetPartySelectHostRequestSerial = 0x5C1C;

    /// <summary>Agent -&gt; plugin. The exit script to open with — copied verbatim from the
    /// requester's <see cref="OffsetPartySelectClientExitScript"/>.</summary>
    public const int OffsetPartySelectHostExitScript = 0x5C20;

    /// <summary>Agent -&gt; plugin. The forced NPC slots to open with, or -1.</summary>
    public const int OffsetPartySelectHostForceNpc1 = 0x5C30;

    public const int OffsetPartySelectHostForceNpc2 = 0x5C34;

    /// <summary>Plugin -&gt; agent. Echoes <see cref="OffsetPartySelectHostRequestSerial"/>
    /// once the open attempt has been acted on (opened, or refused). Written AFTER the status
    /// below, so a torn read only ever shows "not handled yet".</summary>
    public const int OffsetPartySelectHostApplied = 0x5C38;

    /// <summary>Plugin -&gt; agent. <see cref="PartySelectHostStatusCode"/> for the last
    /// handled open request.</summary>
    public const int OffsetPartySelectHostStatus = 0x5C3C;

    /// <summary>Plugin -&gt; agent. Bumped once the screen this instance opened for a request
    /// (status <see cref="PartySelectHostStatusCode.Opened"/>) is done — confirmed or
    /// cancelled, see <see cref="OffsetPartySelectHostOutcome"/>. Independent of
    /// <see cref="OffsetPartySelectHostApplied"/> because "opened" and "finished" are two
    /// different moments, sometimes minutes apart (a human has to actually choose).</summary>
    public const int OffsetPartySelectHostOutcomeSerial = 0x5C40;

    /// <summary>Plugin -&gt; agent. <see cref="PartySelectHostOutcomeCode"/> for the last
    /// finished screen. Region ends at 0x5C48; 0x5C48..0x6000 is free.</summary>
    public const int OffsetPartySelectHostOutcome = 0x5C44;
}
