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
}
