namespace KotorCoop.Plugin;

// Party-select relay (shared block version 81, region 0x5C00..0x5C48). See
// SharedBlock.PartySelect.cs for the layout and docs/research/galaxy-map-and-party-select.md's
// 2026-09-25 relay section for the design.
internal sealed unsafe partial class SharedBlockView
{
    // ---- client half: this instance's own captured panel ---------------------------------

    /// <summary>Plugin -&gt; agent. Publishes "this instance's own client copy of the
    /// party-select screen was just captured" — script and forced NPCs first, the serial
    /// LAST, so the agent never pairs a new serial with the previous capture's params.</summary>
    public void PublishPartySelectClientCapture(string exitScript, int forceNpc1, int forceNpc2)
    {
        WriteAsciiField(SharedBlock.OffsetPartySelectClientExitScript, exitScript, SharedBlock.PartySelectScriptLength);
        *I32(SharedBlock.OffsetPartySelectClientForceNpc1) = forceNpc1;
        *I32(SharedBlock.OffsetPartySelectClientForceNpc2) = forceNpc2;
        *U32(SharedBlock.OffsetPartySelectClientSerial) = *U32(SharedBlock.OffsetPartySelectClientSerial) + 1;
    }

    public uint PartySelectClientSerial => *U32(SharedBlock.OffsetPartySelectClientSerial);

    public string PartySelectClientExitScript => ReadAsciiField(
        SharedBlock.OffsetPartySelectClientExitScript, SharedBlock.PartySelectScriptLength);

    public int PartySelectClientForceNpc1 => *I32(SharedBlock.OffsetPartySelectClientForceNpc1);

    public int PartySelectClientForceNpc2 => *I32(SharedBlock.OffsetPartySelectClientForceNpc2);

    // ---- host half: open a peer's requested screen here -----------------------------------

    /// <summary>Agent -&gt; plugin (HOST instance only). A request is pending while this is
    /// ahead of <see cref="PartySelectHostApplied"/>.</summary>
    public uint PartySelectHostRequestSerial => *U32(SharedBlock.OffsetPartySelectHostRequestSerial);

    public string PartySelectHostExitScript => ReadAsciiField(
        SharedBlock.OffsetPartySelectHostExitScript, SharedBlock.PartySelectScriptLength);

    public int PartySelectHostForceNpc1 => *I32(SharedBlock.OffsetPartySelectHostForceNpc1);

    public int PartySelectHostForceNpc2 => *I32(SharedBlock.OffsetPartySelectHostForceNpc2);

    /// <summary>Agent -&gt; plugin. Writes a new open request — script and forced NPCs first,
    /// the serial last, continuing from whatever is already there (the same convention
    /// <c>TryRequestGalaxyDepart</c> uses).</summary>
    public void RequestPartySelectHostOpen(string exitScript, int forceNpc1, int forceNpc2)
    {
        WriteAsciiField(SharedBlock.OffsetPartySelectHostExitScript, exitScript, SharedBlock.PartySelectScriptLength);
        *I32(SharedBlock.OffsetPartySelectHostForceNpc1) = forceNpc1;
        *I32(SharedBlock.OffsetPartySelectHostForceNpc2) = forceNpc2;
        *U32(SharedBlock.OffsetPartySelectHostRequestSerial) = *U32(SharedBlock.OffsetPartySelectHostRequestSerial) + 1;
    }

    /// <summary>Plugin -&gt; agent. Echoes <see cref="PartySelectHostRequestSerial"/> once
    /// acted on.</summary>
    public uint PartySelectHostApplied
    {
        get => *U32(SharedBlock.OffsetPartySelectHostApplied);
        set => *U32(SharedBlock.OffsetPartySelectHostApplied) = value;
    }

    /// <summary>Plugin -&gt; agent. <see cref="PartySelectHostStatusCode"/> of the last
    /// handled open request.</summary>
    public uint PartySelectHostStatus
    {
        get => *U32(SharedBlock.OffsetPartySelectHostStatus);
        set => *U32(SharedBlock.OffsetPartySelectHostStatus) = value;
    }

    /// <summary>Plugin -&gt; agent. Bumps once a screen this instance opened finishes.</summary>
    public uint PartySelectHostOutcomeSerial
    {
        get => *U32(SharedBlock.OffsetPartySelectHostOutcomeSerial);
        set => *U32(SharedBlock.OffsetPartySelectHostOutcomeSerial) = value;
    }

    /// <summary>Plugin -&gt; agent. <see cref="PartySelectHostOutcomeCode"/> of the last
    /// finished screen.</summary>
    public uint PartySelectHostOutcome
    {
        get => *U32(SharedBlock.OffsetPartySelectHostOutcome);
        set => *U32(SharedBlock.OffsetPartySelectHostOutcome) = value;
    }

    // ---- shared helpers --------------------------------------------------------------------

    private void WriteAsciiField(int offset, string text, int capacity)
    {
        byte* field = baseAddress + offset;
        int count = Math.Min(text?.Length ?? 0, capacity - 1);
        for (int i = 0; i < count; i++)
        {
            char c = text![i];
            field[i] = (byte)(c < 0x20 || c > 0x7E ? (byte)0 : (byte)c);
        }

        for (int i = count; i < capacity; i++)
        {
            field[i] = 0;
        }
    }

    private string ReadAsciiField(int offset, int capacity)
    {
        byte* field = baseAddress + offset;
        int length = 0;
        while (length < capacity && field[length] != 0)
        {
            length++;
        }

        return length == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(field, length);
    }
}
