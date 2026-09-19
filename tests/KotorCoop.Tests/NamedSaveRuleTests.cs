using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class NamedSaveRuleTests
{
    private static readonly DateTime T0 = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("manaan", "manaan")]
    [InlineData("  Manaan   Rift  ", "Manaan Rift")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "abcdefghij")]
    [InlineData("under_score-dash 2", "under_score-dash 2")]
    [InlineData("../../evil", "evil")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("///", "")]
    public void NamesAreReducedToFolderSafeText(string raw, string expected)
    {
        Assert.Equal(expected, NamedSaveRule.SanitizeName(raw));
    }

    [Fact]
    public void ALongNameIsCutAndTrimmed()
    {
        string name = NamedSaveRule.SanitizeName(new string('a', 30) + " " + new string('b', 30));

        Assert.True(name.Length <= NamedSaveRule.MaxNameLength);
        Assert.False(name.EndsWith(' '));
    }

    [Fact]
    public void TheNextIndexIsOneAboveTheHighestAndNeverZero()
    {
        Assert.Equal(1, NamedSaveRule.NextIndex([]));
        Assert.Equal(2, NamedSaveRule.NextIndex(["000000 - QUICKSAVE", "000001 - AUTOSAVE"]));
        Assert.Equal(4, NamedSaveRule.NextIndex(["000000 - QUICKSAVE", "000003 - zzz_swoop", "000002 - Game1"]));
        Assert.Equal(1, NamedSaveRule.NextIndex(["nonsense", "no digits here"]));
    }

    [Fact]
    public void FolderNamesAreSixDigitsDashName()
    {
        Assert.Equal("000012 - manaan", NamedSaveRule.FolderName(12, "manaan"));
        Assert.Equal("manaan", NamedSaveRule.NameOf("000012 - manaan"));
        Assert.Equal("weird", NamedSaveRule.NameOf("weird"));
        Assert.Equal("a - b", NamedSaveRule.NameOf("000001 - a - b"));
    }

    private static (string, DateTime)[] Saves() =>
    [
        ("000000 - QUICKSAVE", T0.AddMinutes(10)),
        ("000001 - AUTOSAVE", T0.AddMinutes(30)), // newest of all, must not take a slot
        ("000002 - Game1", T0),
        ("000004 - manaan rift", T0.AddMinutes(5)),
        ("000005 - manaan station", T0.AddMinutes(2)),
        ("000003 - zzz_swoop", T0.AddMinutes(-5)),
    ];

    [Fact]
    public void SlotsFollowNewestFirstWithTheAutosaveLeftOut()
    {
        // order: QUICKSAVE(+10), manaan rift(+5), manaan station(+2), Game1(0), zzz_swoop(-5)
        Assert.Equal(NamedSaveRule.LookupResult.Found, NamedSaveRule.FindSlot(Saves(), "quicksave", out int slot, out string folder));
        Assert.Equal(0, slot);
        Assert.Equal("000000 - QUICKSAVE", folder);

        Assert.Equal(NamedSaveRule.LookupResult.Found, NamedSaveRule.FindSlot(Saves(), "Game1", out slot, out _));
        Assert.Equal(3, slot);

        Assert.Equal(NamedSaveRule.LookupResult.Found, NamedSaveRule.FindSlot(Saves(), "zzz_swoop", out slot, out _));
        Assert.Equal(4, slot);
    }

    [Fact]
    public void AnExactNameBeatsASubstringAndAFullFolderNameMatches()
    {
        Assert.Equal(NamedSaveRule.LookupResult.Found, NamedSaveRule.FindSlot(Saves(), "manaan rift", out int slot, out _));
        Assert.Equal(1, slot);

        Assert.Equal(NamedSaveRule.LookupResult.Found, NamedSaveRule.FindSlot(Saves(), "000005 - manaan station", out slot, out _));
        Assert.Equal(2, slot);
    }

    [Fact]
    public void ASharedSubstringIsAmbiguousAndAUniqueOneResolves()
    {
        Assert.Equal(NamedSaveRule.LookupResult.Ambiguous, NamedSaveRule.FindSlot(Saves(), "manaan", out int slot, out _));
        Assert.Equal(-1, slot);

        Assert.Equal(NamedSaveRule.LookupResult.Found, NamedSaveRule.FindSlot(Saves(), "station", out slot, out _));
        Assert.Equal(2, slot);
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("")]
    [InlineData("autosave")] // the autosave is excluded, so it cannot be named
    public void UnknownOrEmptyNamesAreNotFound(string wanted)
    {
        Assert.Equal(NamedSaveRule.LookupResult.NotFound, NamedSaveRule.FindSlot(Saves(), wanted, out int slot, out _));
        Assert.Equal(-1, slot);
    }
}
