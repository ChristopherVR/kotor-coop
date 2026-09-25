using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

public sealed class CoopSaveSlotTests
{
    [Theory]
    [InlineData("Yasuko Kaar", "Yasuko Kaar")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "abcdefghij")]
    [InlineData("", "Co-op")]
    [InlineData("   ", "Co-op")]
    [InlineData("///", "Co-op")]
    public void NamesAreReducedToFolderSafeTextOrFallToTheLabel(string raw, string expected)
    {
        Assert.Equal(expected, CoopSaveSlot.SanitizeName(raw));
    }

    [Fact]
    public void ALongNameIsCutAndTrimmed()
    {
        string name = CoopSaveSlot.SanitizeName(new string('a', 30) + " " + new string('b', 30));

        Assert.True(name.Length <= CoopSaveSlot.MaxNameLength);
        Assert.False(name.EndsWith(' '));
    }

    [Theory]
    [InlineData("000002 - Co-op", true)]
    [InlineData("000123 - Co-op", true)]
    [InlineData("000123 - co-op", true)]
    [InlineData("000123 - CO-OP", true)]
    [InlineData("000000 - QUICKSAVE", false)]
    [InlineData("000001 - AUTOSAVE", false)]
    [InlineData("000004 - Co-op run", false)]
    [InlineData("Co-op", true)]
    [InlineData("nonsense", false)]
    public void IsCoopSlotMatchesOnlyTheExactLabel(string folder, bool expected)
    {
        Assert.Equal(expected, CoopSaveSlot.IsCoopSlot(folder));
    }

    [Fact]
    public void TheNextIndexIsOneAboveTheHighestAndNeverZero()
    {
        Assert.Equal(1, CoopSaveSlot.NextIndex([]));
        Assert.Equal(2, CoopSaveSlot.NextIndex(["000000 - QUICKSAVE", "000001 - AUTOSAVE"]));
        Assert.Equal(4, CoopSaveSlot.NextIndex(["000000 - QUICKSAVE", "000003 - zzz_swoop", "000002 - Game1"]));
    }

    [Fact]
    public void AFreshInstallAllocatesTheNextFreeNumberAfterAutosave()
    {
        string folder = CoopSaveSlot.ResolveFolderName(["000000 - QUICKSAVE", "000001 - AUTOSAVE"]);

        Assert.Equal("000002 - Co-op", folder);
        Assert.True(CoopSaveSlot.IsCoopSlot(folder));
    }

    [Fact]
    public void AnExistingCoopSlotKeepsItsOwnNumberForever()
    {
        string[] existing =
        [
            "000000 - QUICKSAVE",
            "000001 - AUTOSAVE",
            "000002 - Co-op",
            "000003 - my named save",
        ];

        Assert.Equal("000002 - Co-op", CoopSaveSlot.ResolveFolderName(existing));
    }

    [Fact]
    public void ANumberAlreadyTakenByAPlayerNamedSaveIsNeverReused()
    {
        // The install already has a manually-named save sitting at the number a fresh
        // ResolveFolderName would otherwise pick first.
        string[] existing = ["000000 - QUICKSAVE", "000001 - AUTOSAVE", "000002 - my manaan run"];

        string folder = CoopSaveSlot.ResolveFolderName(existing);

        Assert.Equal("000003 - Co-op", folder);
        Assert.DoesNotContain(folder, existing);
    }

    [Fact]
    public void ResolveFolderNameIsDeterministicAcrossRepeatedCalls()
    {
        string[] existing = ["000000 - QUICKSAVE", "000001 - AUTOSAVE"];

        string first = CoopSaveSlot.ResolveFolderName(existing);
        string second = CoopSaveSlot.ResolveFolderName([.. existing, first]);

        Assert.Equal(first, second);
    }
}
