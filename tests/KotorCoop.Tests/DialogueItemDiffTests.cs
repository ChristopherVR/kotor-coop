using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pure coverage for <see cref="DialogueItemDiff"/> — the detection half of
/// docs/research/dialogue-consequences.md §3.2's item-grant channel. No bridge, no session, no
/// game: just two snapshots of possessed-item rows in, the net-new rows out.
/// </summary>
public class DialogueItemDiffTests
{
    private static (string Resref, ItemIdentity Identity) Row(
        string resref, ushort stack = 1, byte charges = 0, byte maxCharges = 0,
        uint upgrades = 0, byte flags = 0) =>
        (resref, new ItemIdentity(stack, charges, maxCharges, upgrades, flags));

    [Fact]
    public void NoChangeYieldsNothing()
    {
        List<(string, ItemIdentity)> before = [Row("g_w_blstrpstl001")];
        List<(string, ItemIdentity)> after = [Row("g_w_blstrpstl001")];

        Assert.Empty(DialogueItemDiff.Compute(before, after));
    }

    [Fact]
    public void ANewResrefIsReportedWithItsFullIdentity()
    {
        List<(string, ItemIdentity)> before = [];
        List<(string, ItemIdentity)> after = [Row("g_w_lghtsbr01", stack: 1, charges: 3, upgrades: 0x5)];

        List<MaterialisedLootItem> gained = DialogueItemDiff.Compute(before, after);

        MaterialisedLootItem item = Assert.Single(gained);
        Assert.Equal("g_w_lghtsbr01", item.Resref);
        Assert.Equal(3, item.Identity.Charges);
        Assert.Equal(0x5u, item.Identity.Upgrades);
    }

    /// <summary>The whole reason the diff counts ROWS per resref rather than comparing
    /// identity: a stack GROWING is the same row, not a new one, so it must never be reported
    /// as a grant — "ignore stack increases of existing stackables".</summary>
    [Fact]
    public void AStackGrowingOnAnExistingItemIsNotAGrant()
    {
        List<(string, ItemIdentity)> before = [Row("g_i_medeqpmnt01", stack: 5)];
        List<(string, ItemIdentity)> after = [Row("g_i_medeqpmnt01", stack: 8)];

        Assert.Empty(DialogueItemDiff.Compute(before, after));
    }

    /// <summary>A stack SHRINKING (the PC spent one) must not read as a grant either — the diff
    /// only ever reports the AFTER side's extra rows, never "missing" ones.</summary>
    [Fact]
    public void AStackShrinkingIsNotAGrant()
    {
        List<(string, ItemIdentity)> before = [Row("g_i_medeqpmnt01", stack: 5)];
        List<(string, ItemIdentity)> after = [Row("g_i_medeqpmnt01", stack: 2)];

        Assert.Empty(DialogueItemDiff.Compute(before, after));
    }

    /// <summary>A second, genuinely new copy of a resref the PC already carried (a
    /// non-stacking item, or a second stack) raises the row count and is reported.</summary>
    [Fact]
    public void AnExtraCopyOfAnAlreadyHeldResrefIsAGrant()
    {
        List<(string, ItemIdentity)> before = [Row("g_w_blstrpstl001")];
        List<(string, ItemIdentity)> after = [Row("g_w_blstrpstl001"), Row("g_w_blstrpstl001")];

        MaterialisedLootItem item = Assert.Single(DialogueItemDiff.Compute(before, after));
        Assert.Equal("g_w_blstrpstl001", item.Resref);
    }

    [Fact]
    public void MultipleDistinctNewItemsAreAllReported()
    {
        List<(string, ItemIdentity)> before = [Row("g_w_blstrpstl001")];
        List<(string, ItemIdentity)> after =
            [Row("g_w_blstrpstl001"), Row("g_i_medeqpmnt01"), Row("g_w_lghtsbr01")];

        List<MaterialisedLootItem> gained = DialogueItemDiff.Compute(before, after);

        Assert.Equal(2, gained.Count);
        Assert.Contains(gained, i => i.Resref == "g_i_medeqpmnt01");
        Assert.Contains(gained, i => i.Resref == "g_w_lghtsbr01");
    }

    [Fact]
    public void EmptyResrefRowsAreIgnoredOnBothSides()
    {
        List<(string, ItemIdentity)> before = [Row("")];
        List<(string, ItemIdentity)> after = [Row(""), Row("")];

        Assert.Empty(DialogueItemDiff.Compute(before, after));
    }

    [Fact]
    public void EmptyBeforeAndAfterYieldsNothing()
    {
        Assert.Empty(DialogueItemDiff.Compute([], []));
    }
}
