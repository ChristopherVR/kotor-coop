using System.Net;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class ConsoleCommandTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankIsNone(string? line)
    {
        Assert.Equal(CoopCommandKind.None, ConsoleCommand.Parse(line).Kind);
    }

    [Fact]
    public void ConnectWithAddressOnly()
    {
        CoopCommand c = ConsoleCommand.Parse("connect 10.0.0.5");
        Assert.Equal(CoopCommandKind.Connect, c.Kind);
        Assert.Equal("10.0.0.5", c.Address);
        Assert.Null(c.Password);
    }

    [Fact]
    public void ConnectWithAddressAndPassword()
    {
        CoopCommand c = ConsoleCommand.Parse("connect 10.0.0.5:7777 hunter2");
        Assert.Equal(CoopCommandKind.Connect, c.Kind);
        Assert.Equal("10.0.0.5:7777", c.Address);
        Assert.Equal("hunter2", c.Password);
    }

    [Fact]
    public void ConnectWithoutAddressIsInvalid()
    {
        CoopCommand c = ConsoleCommand.Parse("connect");
        Assert.Equal(CoopCommandKind.Invalid, c.Kind);
        Assert.Contains("host", c.Error);
    }

    [Fact]
    public void JoinIsAnAliasForConnect()
    {
        Assert.Equal(CoopCommandKind.Connect, ConsoleCommand.Parse("join 1.2.3.4").Kind);
    }

    [Fact]
    public void HostWithOptionalPassword()
    {
        Assert.Equal(CoopCommandKind.Host, ConsoleCommand.Parse("host").Kind);

        CoopCommand withPw = ConsoleCommand.Parse("host swordfish");
        Assert.Equal(CoopCommandKind.Host, withPw.Kind);
        Assert.Equal("swordfish", withPw.Password);
    }

    [Theory]
    [InlineData("servers", CoopCommandKind.Servers)]
    [InlineData("browse", CoopCommandKind.Servers)]
    [InlineData("clear", CoopCommandKind.Clear)]
    [InlineData("cls", CoopCommandKind.Clear)]
    [InlineData("disconnect", CoopCommandKind.Disconnect)]
    [InlineData("dc", CoopCommandKind.Disconnect)]
    [InlineData("leave", CoopCommandKind.Disconnect)]
    [InlineData("reconnect", CoopCommandKind.Reconnect)]
    [InlineData("rc", CoopCommandKind.Reconnect)]
    [InlineData("status", CoopCommandKind.Status)]
    [InlineData("help", CoopCommandKind.Help)]
    [InlineData("?", CoopCommandKind.Help)]
    public void VerbsMapToKinds(string line, CoopCommandKind expected)
    {
        Assert.Equal(expected, ConsoleCommand.Parse(line).Kind);
    }

    [Fact]
    public void IsCaseAndWhitespaceInsensitive()
    {
        CoopCommand c = ConsoleCommand.Parse("  CONNECT   1.2.3.4   secret  ");
        Assert.Equal(CoopCommandKind.Connect, c.Kind);
        Assert.Equal("1.2.3.4", c.Address);
        Assert.Equal("secret", c.Password);
    }

    [Fact]
    public void CoopPrefixIsStripped()
    {
        Assert.Equal(CoopCommandKind.Disconnect, ConsoleCommand.Parse("coop disconnect").Kind);
        Assert.Equal(CoopCommandKind.Connect, ConsoleCommand.Parse("/coop connect 1.2.3.4").Kind);
        Assert.Equal(CoopCommandKind.Help, ConsoleCommand.Parse("coop").Kind);
    }

    [Fact]
    public void UnrelatedVerbIsUnknown()
    {
        Assert.Equal(CoopCommandKind.Unknown, ConsoleCommand.Parse("giveitem g_w_lghtsbr01").Kind);
    }

    [Fact]
    public void PasswordCarriesOnlyTheSecret()
    {
        // The answer to a prompt. It deliberately has NO address: the player picked a row in
        // the browser panel and never saw an IP, so the join being retried is the one that
        // asked, not one they have to re-identify.
        CoopCommand c = ConsoleCommand.Parse("password hoth");
        Assert.Equal(CoopCommandKind.Password, c.Kind);
        Assert.Equal("hoth", c.Password);
        Assert.Null(c.Address);

        Assert.Equal(CoopCommandKind.Password, ConsoleCommand.Parse("pw hoth").Kind);
    }

    [Fact]
    public void APassphraseKeepsItsSpaces()
    {
        // Keeping only the first word would be indistinguishable from a wrong password.
        Assert.Equal("two words", ConsoleCommand.Parse("password two words").Password);
    }

    [Fact]
    public void PasswordWithNothingToSayIsInvalid()
    {
        CoopCommand c = ConsoleCommand.Parse("password");
        Assert.Equal(CoopCommandKind.Invalid, c.Kind);
        Assert.Contains("password", c.Error);
    }

    [Fact]
    public void SayCapturesTheWholeMessage()
    {
        CoopCommand c = ConsoleCommand.Parse("say on my way to the bridge");
        Assert.Equal(CoopCommandKind.Say, c.Kind);
        Assert.Equal("on my way to the bridge", c.Address);
    }

    [Fact]
    public void SayWithoutAMessageIsInvalid()
    {
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("say").Kind);
    }

    [Theory]
    [InlineData("chat hello")]
    [InlineData("msg hello")]
    public void ChatAndMsgAreAliasesForSay(string line)
    {
        Assert.Equal(CoopCommandKind.Say, ConsoleCommand.Parse(line).Kind);
    }

    [Fact]
    public void ResolvesLiteralAddressWithDefaultPort()
    {
        CoopCommand c = ConsoleCommand.Parse("connect 10.0.0.5");
        Assert.True(c.TryResolveEndpoint(7777, out IPEndPoint endpoint));
        Assert.Equal(IPAddress.Parse("10.0.0.5"), endpoint.Address);
        Assert.Equal(7777, endpoint.Port);
    }

    [Fact]
    public void ExplicitPortOverridesDefault()
    {
        CoopCommand c = ConsoleCommand.Parse("connect 10.0.0.5:9000");
        Assert.True(c.TryResolveEndpoint(7777, out IPEndPoint endpoint));
        Assert.Equal(9000, endpoint.Port);
    }

    [Fact]
    public void HostCommandHasNoEndpoint()
    {
        CoopCommand c = ConsoleCommand.Parse("host");
        Assert.False(c.TryResolveEndpoint(7777, out _));
    }

    [Fact]
    public void WarpCarriesTheModuleLowercased()
    {
        CoopCommand c = ConsoleCommand.Parse("warp DANM13");
        Assert.Equal(CoopCommandKind.Warp, c.Kind);
        Assert.Equal("danm13", c.Address);
        Assert.Null(c.Password);
    }

    [Fact]
    public void WarpAcceptsAnOptionalWaypointTag()
    {
        CoopCommand c = ConsoleCommand.Parse("warp danm13 wp_entry_01");
        Assert.Equal(CoopCommandKind.Warp, c.Kind);
        Assert.Equal("danm13", c.Address);
        Assert.Equal("wp_entry_01", c.Password);
    }

    [Fact]
    public void WarpWithoutAModuleIsInvalid()
    {
        CoopCommand c = ConsoleCommand.Parse("warp");
        Assert.Equal(CoopCommandKind.Invalid, c.Kind);
        Assert.Contains("module", c.Error);
    }

    [Theory]
    [InlineData("warp bad-name")]              // '-' is not resref-safe
    [InlineData("warp danm13x_reallytoolong")] // > 16 chars
    [InlineData("warp dan m13; drop")]         // shell-ish junk in the waypoint slot
    public void WarpRejectsMalformedResrefs(string line)
    {
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);
    }

    [Theory]
    [InlineData("who", CoopCommandKind.Who)]
    [InlineData("peers", CoopCommandKind.Who)]
    [InlineData("where", CoopCommandKind.Where)]
    [InlineData("coop who", CoopCommandKind.Who)]
    public void RosterAndPositionVerbsMapToKinds(string line, CoopCommandKind expected)
    {
        Assert.Equal(expected, ConsoleCommand.Parse(line).Kind);
    }
}
