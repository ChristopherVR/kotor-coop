using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <c>steamstatus</c>/<c>steaminvite</c> parsing (<c>ConsoleCommand.Steam.cs</c>). The
/// SteamID64 in <c>steaminvite</c> is UNTRUSTED-shaped input in the same sense a typed IP is —
/// a human typed it, possibly wrong — so every malformed case must return
/// <see cref="CoopCommandKind.Invalid"/> with a message rather than let a garbage value reach
/// <c>SteamNative.InviteUserToGame</c>.
/// </summary>
public class ConsoleCommandSteamTests
{
    [Fact]
    public void SteamStatusNeedsNoArguments()
    {
        CoopCommand parsed = ConsoleCommand.Parse("steamstatus");
        Assert.Equal(CoopCommandKind.SteamStatus, parsed.Kind);
    }

    [Theory]
    [InlineData("steamstatus")]
    [InlineData("steam_status")]
    public void BothSteamStatusSpellingsParse(string line)
    {
        Assert.Equal(CoopCommandKind.SteamStatus, ConsoleCommand.Parse(line).Kind);
    }

    [Fact]
    public void AValidSteamId64ParsesIntoAddress()
    {
        CoopCommand parsed = ConsoleCommand.Parse("steaminvite 76561198000000000");
        Assert.Equal(CoopCommandKind.SteamInvite, parsed.Kind);
        Assert.Equal("76561198000000000", parsed.Address);
    }

    [Theory]
    [InlineData("steam_invite")]
    public void TheUnderscoreSpellingAlsoParses(string line)
    {
        Assert.Equal(CoopCommandKind.SteamInvite,
            ConsoleCommand.Parse(line + " 76561198000000000").Kind);
    }

    [Fact]
    public void NoArgumentIsInvalidWithAHelpfulMessage()
    {
        CoopCommand parsed = ConsoleCommand.Parse("steaminvite");
        Assert.Equal(CoopCommandKind.Invalid, parsed.Kind);
        Assert.Contains("SteamID64", parsed.Error);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-76561198000000000")]   // negative: not a valid ulong
    [InlineData("12.5")]                 // not an integer
    [InlineData("0")]                    // zero is never a real SteamID
    public void MalformedSteamIdsAreInvalidNotThrown(string typed)
    {
        CoopCommand parsed = ConsoleCommand.Parse($"steaminvite {typed}");
        Assert.Equal(CoopCommandKind.Invalid, parsed.Kind);
        Assert.NotEmpty(parsed.Error);
    }

    [Fact]
    public void OnlyTheFirstTokenIsTakenAsTheSteamId()
    {
        // Mirrors `say`'s own "extra words are not silently folded in" expectation - a second
        // token here is far more likely to be a typo than a second argument, so it must fail
        // loudly (ulong.TryParse on "123 456" fails) rather than quietly using "123".
        CoopCommand parsed = ConsoleCommand.Parse("steaminvite 76561198000000000 76561198000000001");
        Assert.Equal(CoopCommandKind.SteamInvite, parsed.Kind);
        Assert.Equal("76561198000000000", parsed.Address);
    }
}
