using KotorCoop.Agent;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <c>--steam-join</c> and the <c>steam_invites</c> config key — the two halves of Steam
/// support that are exercisable with no Steam client, no native library and no game. That is
/// the point of the split: everything that decides *what* a Steam invite means is pure and
/// tested here, and only the ~8 native calls that carry it are left NEEDS-LIVE
/// (docs/research/steam-support.md §16).
/// </summary>
public class SteamJoinOptionTests
{
    [Fact]
    public void SteamJoinBecomesAnOrdinaryJoin()
    {
        // The whole design goal in one assertion: after parsing, nothing downstream can tell
        // this apart from a hand-typed --join, so no other code path has to know Steam exists.
        AgentOptions options = AgentOptions.Parse(
            ["run", "--steam-join", "kc1/127.0.0.1:27140"]);

        Assert.Equal(AgentCommand.Run, options.Command);
        Assert.Equal(CoopMode.Join, options.Mode);
        Assert.NotNull(options.HostEndPoint);
        Assert.Equal("127.0.0.1", options.HostEndPoint.Address.ToString());
        Assert.Equal(27140, options.HostEndPoint.Port);
        Assert.Equal(string.Empty, options.Password);
        Assert.Equal("kc1/127.0.0.1:27140", options.SteamConnect);
    }

    [Fact]
    public void ThePasswordRidesAlongSoTheInviteeIsNeverAskedForOne()
    {
        string connect = SteamConnectStringRule.Format("127.0.0.1", 27140, "swordfish")!;

        AgentOptions options = AgentOptions.Parse(["run", "--steam-join", connect]);

        Assert.Equal(CoopMode.Join, options.Mode);
        Assert.Equal("swordfish", options.Password);
    }

    [Fact]
    public void AnExplicitPasswordAfterTheInviteStillWins()
    {
        // Ordering is the ordinary "last flag wins", which matters because a player pasting a
        // stale invite and typing the real password by hand must not be silently overridden.
        string connect = SteamConnectStringRule.Format("127.0.0.1", 27140, "old")!;

        AgentOptions options = AgentOptions.Parse(
            ["run", "--steam-join", connect, "--password", "new"]);

        Assert.Equal("new", options.Password);
    }

    [Theory]
    [InlineData("not-a-connect-string")]
    [InlineData("kc2/127.0.0.1:27140")]
    [InlineData("kc1/127.0.0.1:0")]
    [InlineData("kc1/")]
    public void AMalformedInviteFallsBackToHelpRatherThanDiallingSomething(string connect)
    {
        AgentOptions options = AgentOptions.Parse(["run", "--steam-join", connect]);

        Assert.Equal(AgentCommand.Help, options.Command);
        Assert.Equal(CoopMode.None, options.Mode);
        Assert.Null(options.HostEndPoint);
    }

    [Fact]
    public void AMissingArgumentIsHelpRatherThanACrash()
    {
        Assert.Equal(AgentCommand.Help, AgentOptions.Parse(["run", "--steam-join"]).Command);
    }

    [Fact]
    public void HelpMentionsTheFlag()
    {
        Assert.Contains("--steam-join", AgentOptions.HelpText);
    }

    // ---- steam_invites -------------------------------------------------------------------

    [Fact]
    public void SteamInvitesIsOnWhenTheKeyIsAbsent()
    {
        // The ON-by-default idiom, and the reason it is safe: every layer below is inert
        // without a player-supplied steam_api64.dll, so an existing coop.ini that predates
        // this key must keep behaving identically while still getting the feature for free
        // if the player ever adds the DLL.
        Assert.True(Load("mode = join").SteamInvites);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("on")]
    public void SteamInvitesAcceptsEveryTruthySpelling(string value)
    {
        Assert.True(Load($"steam_invites = {value}").SteamInvites);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("")]
    [InlineData("nonsense")]
    public void SteamInvitesIsOffForAnythingThatIsNotTruthy(string value)
    {
        Assert.False(Load($"steam_invites = {value}").SteamInvites);
    }

    private static CoopConfig Load(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kotorcoop-steam-{Guid.NewGuid():N}.ini");

        try
        {
            File.WriteAllText(path, contents);
            CoopConfig? config = CoopConfig.TryLoad(path);
            Assert.NotNull(config);
            return config;
        }
        finally
        {
            File.Delete(path);
        }
    }
}
