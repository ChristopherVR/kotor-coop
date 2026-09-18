using System.Net;
using System.Text;
using KotorCoop.Plugin;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The browser's address box: what the player types becomes the console line
/// <c>connect host:port</c>. These pin the grammar, the CExoString length trap, the
/// "empty is a cancel, garbage is a rejection" split, and — because the plugin cannot call the
/// agent's parsers — that the emitted shape is exactly what the agent's own resolver and the
/// Steam connect-string codec read back.
/// </summary>
public class ConnectAddressRuleTests
{
    private static string Normalize(string typed)
    {
        Assert.True(ConnectAddressRule.TryNormalize(typed, out string address, out string why), why);
        return address;
    }

    private static string Reject(string typed)
    {
        Assert.False(ConnectAddressRule.TryNormalize(typed, out _, out string why));
        Assert.NotEmpty(why);
        return why;
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static string ComposeToString(byte[] typed, out string? rejection, int destinationSize = 96)
    {
        byte[] destination = new byte[destinationSize];
        int n = ConnectAddressRule.Compose(typed, destination, out rejection);
        return Encoding.ASCII.GetString(destination, 0, n);
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20:27140")]
    [InlineData("192.168.1.20:7777", "192.168.1.20:7777")]
    [InlineData("  play.example.com  ", "play.example.com:27140")]
    [InlineData("play.example.com:27015", "play.example.com:27015")]
    [InlineData("my-host_1.local", "my-host_1.local:27140")]
    [InlineData("[2001:db8::1]:27140", "[2001:db8::1]:27140")]
    [InlineData("[2001:db8::1]", "[2001:db8::1]:27140")]
    [InlineData("2001:db8::1", "[2001:db8::1]:27140")]
    [InlineData("::1", "[::1]:27140")]
    [InlineData("connect 10.0.0.1:1", "10.0.0.1:1")]
    [InlineData("CONNECT 10.0.0.1", "10.0.0.1:27140")]
    public void AcceptedShapesAlwaysCarryAnExplicitPort(string typed, string expected)
    {
        Assert.Equal(expected, Normalize(typed));
    }

    [Fact]
    public void AnUnbracketedV6LiteralWithATrailingNumberIsTheLiteralNotAPort()
    {
        // Rule 5: "2001:db8::1:27140" is a valid IPv6 literal on its own; the last group is
        // read as part of the address. Brackets are how a port is attached.
        Assert.Equal("[2001:db8::1:27140]:27140", Normalize("2001:db8::1:27140"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("connect")]
    [InlineData("connect   ")]
    public void EmptyTextIsRejectedWithAPrompt(string typed)
    {
        Assert.Equal("Type the host's address", Reject(typed));
    }

    [Theory]
    [InlineData("192.168.1.20 27140")]
    [InlineData("my host")]
    public void SpacesInsideAreRejected(string typed)
    {
        Assert.Equal("No spaces in an address", Reject(typed));
    }

    [Theory]
    [InlineData("http://example.com:27140")]
    [InlineData("example.com/path")]
    public void UrlsAndPathsAreRejected(string typed)
    {
        Assert.Equal("Just host:port, no http:// or path", Reject(typed));
    }

    [Theory]
    [InlineData("host:")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("host:abc")]
    [InlineData("host:-1")]
    [InlineData("[::1]:99999")]
    public void PortsOutsideOneToSixtyFiveThousandAreRejected(string typed)
    {
        Assert.Equal("Port must be 1-65535, e.g. host:27140", Reject(typed));
    }

    [Theory]
    [InlineData("[")]
    [InlineData("[]")]
    [InlineData("[]:27140")]
    [InlineData("[::1]27140")]
    [InlineData("[::1]:")]
    public void MalformedBracketsAreRejected(string typed)
    {
        Reject(typed);
    }

    [Fact]
    public void BracketsAroundAHostNameAreRejected()
    {
        Assert.Equal("Brackets are for IPv6 only", Reject("[example.com]:27140"));
    }

    [Theory]
    [InlineData("host$name")]
    [InlineData("héllo.example")]
    [InlineData(":27140")]
    public void HostNamesOutsideTheDnsAlphabetAreRejected(string typed)
    {
        Reject(typed);
    }

    [Fact]
    public void AHostLongerThanADnsNameIsRejected()
    {
        Assert.Equal("That address is too long", Reject(new string('a', 254)));
    }

    // ---- Compose: the engine-side bytes ----------------------------------------------

    [Fact]
    public void ComposesTheExactConsoleLine()
    {
        Assert.Equal("connect 10.5.0.2:27140", ComposeToString(Ascii("10.5.0.2"), out string? why));
        Assert.Null(why);
    }

    [Fact]
    public void CutsAtTheFirstNullWhateverFollowsIt()
    {
        // The raw engine read: "10.5.0.2\0" (length-includes-null) plus stale buffer bytes.
        Assert.Equal("connect 10.5.0.2:27140", ComposeToString(Ascii("10.5.0.2\0garbage:1"), out _));
    }

    [Fact]
    public void EmptyTypedTextIsACancelNotARejection()
    {
        Assert.Equal(0, ConnectAddressRule.Compose(Ascii(""), new byte[96], out string? why));
        Assert.Null(why);
        Assert.Equal(0, ConnectAddressRule.Compose(Ascii("\0"), new byte[96], out why));
        Assert.Null(why);
        Assert.Equal(0, ConnectAddressRule.Compose(Ascii("\0host"), new byte[96], out why));
        Assert.Null(why);
    }

    [Fact]
    public void GarbageIsARejectionThatComposesNothing()
    {
        byte[] destination = new byte[96];
        Assert.Equal(0, ConnectAddressRule.Compose(Ascii("not an address"), destination, out string? why));
        Assert.Equal("No spaces in an address", why);
        Assert.All(destination, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ALineThatWouldNotFitTheConsoleChannelIsRejected()
    {
        // 96-byte channel: 95 usable, "connect " is 8, so 87 for the address. An 84-character
        // host plus ":27140" is 90 — an address in its own right, but not one the block carries.
        string host = new string('a', 84);
        Assert.True(ConnectAddressRule.TryNormalize(host, out _, out _));
        Assert.Equal(0, ConnectAddressRule.Compose(Ascii(host), new byte[96], out string? why));
        Assert.Equal("That address is too long", why);
    }

    [Fact]
    public void AddressOfReadsBackWhatComposeWrote()
    {
        byte[] line = new byte[96];
        int n = ConnectAddressRule.Compose(Ascii("[2001:db8::1]:7"), line, out _);
        Assert.Equal("[2001:db8::1]:7", ConnectAddressRule.AddressOf(line.AsSpan(0, n)));
        Assert.Equal("[2001:db8::1]:7", ConnectAddressRule.AddressOf(line)); // null-terminated read
        Assert.Equal(string.Empty, ConnectAddressRule.AddressOf(Ascii("password hoth")));
        Assert.Equal(string.Empty, ConnectAddressRule.AddressOf(Ascii("connect ")));
    }

    // ---- the coop.ini prefill -------------------------------------------------------------

    [Theory]
    [InlineData("192.168.1.20", "27140", "192.168.1.20:27140")]
    [InlineData("192.168.1.20", "", "192.168.1.20:27140")]
    [InlineData("192.168.1.20", null, "192.168.1.20:27140")]
    [InlineData("192.168.1.20", "7777", "192.168.1.20:7777")]
    [InlineData("2001:db8::1", "7777", "[2001:db8::1]:7777")]
    [InlineData("[2001:db8::1]", "7777", "[2001:db8::1]:7777")]
    [InlineData("", "27140", "")]
    [InlineData(null, null, "")]
    [InlineData("not a host", "27140", "")]
    [InlineData("example.com", "junk", "")]
    public void PrefillFoldsTheHostAndPortKeys(string? host, string? port, string expected)
    {
        Assert.Equal(expected, ConnectAddressRule.PrefillFromConfig(host, port));
    }

    // ---- agreement with the agent's own parsers -------------------------------------------

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", 27140)]
    [InlineData("192.168.1.20:7777", "192.168.1.20", 7777)]
    [InlineData("[2001:db8::1]:27140", "2001:db8::1", 27140)]
    [InlineData("2001:db8::1", "2001:db8::1", 27140)]
    [InlineData("[::1]", "::1", 27140)]
    public void TheAgentResolvesEveryEmittedLiteralToTheSameEndpoint(string typed, string ip, int port)
    {
        // CoopCommand.TryResolveEndpoint is the agent's reader for `connect <address>`; for an
        // IP literal it does no DNS, so this is a pure check that the two grammars agree.
        string address = Normalize(typed);
        CoopCommand command = new(CoopCommandKind.Connect, Address: address);
        Assert.True(command.TryResolveEndpoint(ConnectAddressRule.DefaultPort, out IPEndPoint endpoint));
        Assert.Equal(new IPEndPoint(IPAddress.Parse(ip), port), endpoint);
    }

    [Theory]
    [InlineData("192.168.1.20:7777")]
    [InlineData("play.example.com")]
    [InlineData("[2001:db8::1]:27140")]
    [InlineData("2001:db8::1")]
    public void TheSteamCodecReadsTheSameEndpointBack(string typed)
    {
        // The Steam connect string uses the same endpoint grammar; the normalised address must
        // be the exact body it would carry, with the same host/port coming back out.
        string address = Normalize(typed);
        Assert.True(SteamConnectStringRule.TryParse(SteamConnectStringRule.Prefix + address,
            out string host, out int port, out _));
        Assert.Equal(address, host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}");
    }

    [Fact]
    public void TheConsoleParserAcceptsTheComposedLineAsAConnect()
    {
        string line = ComposeToString(Ascii("play.example.com:27015"), out _);
        CoopCommand parsed = ConsoleCommand.Parse(line);
        Assert.Equal(CoopCommandKind.Connect, parsed.Kind);
        Assert.Equal("play.example.com:27015", parsed.Address);
        Assert.Null(parsed.Password);
    }
}
