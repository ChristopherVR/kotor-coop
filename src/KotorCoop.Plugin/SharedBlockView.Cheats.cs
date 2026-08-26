namespace KotorCoop.Plugin;

// The co-op cheat-channel accessors (request fields the agent writes, status/detail fields
// the plugin answers on). See SharedBlockView.cs for the class declaration and shared
// helpers.
internal sealed unsafe partial class SharedBlockView
{
    // ---- co-op cheats -----------------------------------------------------------

    /// <summary>Agent -&gt; plugin. Ahead of <see cref="CheatApplied"/> while a request waits.</summary>
    public uint CheatSerial => *U32(SharedBlock.OffsetCheatSerial);

    public uint CheatKind => *U32(SharedBlock.OffsetCheatKind);

    public uint CheatTarget => *U32(SharedBlock.OffsetCheatTarget);

    public int CheatAmount => *I32(SharedBlock.OffsetCheatAmount);

    public Vec3 CheatPosition => new()
    {
        X = *F32(SharedBlock.OffsetCheatX),
        Y = *F32(SharedBlock.OffsetCheatY),
        Z = *F32(SharedBlock.OffsetCheatZ),
    };

    /// <summary>Pointer to the null-terminated tag/resref argument (32 bytes).</summary>
    public byte* CheatText => baseAddress + SharedBlock.OffsetCheatText;

    /// <summary>Agent -&gt; plugin, <see cref="SharedBlock.CheatKindSetItem"/> only.
    /// <see cref="SharedBlock.CheatFieldUnchanged"/> means "leave this field as it is".</summary>
    public uint CheatItemCharges => *U32(SharedBlock.OffsetCheatCharges);

    public uint CheatItemMaxCharges => *U32(SharedBlock.OffsetCheatMaxCharges);

    public uint CheatItemUpgrades => *U32(SharedBlock.OffsetCheatUpgrades);

    public uint CheatItemFlags => *U32(SharedBlock.OffsetCheatItemFlags);

    public uint CheatApplied
    {
        get => *U32(SharedBlock.OffsetCheatApplied);
        set => *U32(SharedBlock.OffsetCheatApplied) = value;
    }

    public int CheatStatus
    {
        get => *I32(SharedBlock.OffsetCheatStatus);
        set => *I32(SharedBlock.OffsetCheatStatus) = value;
    }

    /// <summary>
    /// Writes the sentence the agent will print and the log will carry. ASCII only and
    /// truncated to the field, which is why the plugin composes short, factual detail —
    /// "phantom 0x2A belongs to Malak" rather than a paragraph.
    /// </summary>
    public void SetCheatDetail(string detail)
    {
        byte* p = baseAddress + SharedBlock.OffsetCheatDetail;
        int count = Math.Min(detail.Length, SharedBlock.CheatDetailLength - 1);
        for (int i = 0; i < count; i++)
        {
            char c = detail[i];
            p[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }

        p[count] = 0;
    }

}
