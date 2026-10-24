using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The Steam rich-presence <c>connect</c> codec. Worth testing hard for two reasons that have
/// nothing to do with how simple the format looks: the decoded value becomes a socket
/// connection and a session password, and it arrives from another Steam user's client — so
/// "returns false" rather than "throws" is a behavioural requirement, not a style preference,
/// and every malformed shape below is a case somebody could actually send.
/// </summary>
public class SteamConnectStringRuleTests
{
    [Fact]
    public void AnOpenSessionEncodesToHostAndPort()
    {
        Assert.Equal("kc1/192.168.1.20:27140",
            SteamConnectStringRule.Format("192.168.1.20", 27140, string.Empty));
    }

    [Fact]
    public void ANullPasswordIsTheSameAsAnEmptyOne()
    {
        Assert.Equal(
            SteamConnectStringRule.Format("192.168.1.20", 27140, string.Empty),
            SteamConnectStringRule.Format("192.168.1.20", 27140, null));
    }

    [Theory]
    [InlineData("192.168.1.20", 27140, "")]
    [InlineData("192.168.1.20", 27140, "swordfish")]
    [InlineData("play.example.com", 27015, "a longer pass phrase with spaces")]
    [InlineData("2001:db8::1", 27140, "")]
    [InlineData("2001:db8::1", 27140, "hunter2")]
    [InlineData("::1", 1, "éèê")]      // non-ASCII survives the UTF-8 round trip
    [InlineData("::ffff:127.0.0.1", 27140, "")]
    [InlineData("10.0.0.1", 65535, "/+=")]            // base64's own special characters
    public void EveryEncodedFormRoundTrips(string host, int port, string password)
    {
        string? encoded = SteamConnectStringRule.Format(host, port, password);
        Assert.NotNull(encoded);

        Assert.True(SteamConnectStringRule.TryParse(
            encoded, out string decodedHost, out int decodedPort, out string decodedPassword));

        Assert.Equal(host, decodedHost);
        Assert.Equal(port, decodedPort);
        Assert.Equal(password, decodedPassword);
    }

    [Fact]
    public void AnIpv6LiteralIsBracketedSoThePortColonIsUnambiguous()
    {
        Assert.Equal("kc1/[2001:db8::1]:27140",
            SteamConnectStringRule.Format("2001:db8::1", 27140, string.Empty));
    }

    [Fact]
    public void AnAlreadyBracketedIpv6LiteralIsNotBracketedTwice()
    {
        Assert.Equal("kc1/[2001:db8::1]:27140",
            SteamConnectStringRule.Format("[2001:db8::1]", 27140, string.Empty));
    }

    [Fact]
    public void TheTokenIsUrlSafeBase64WithNoPadding()
    {
        // Not an implementation detail: '+', '/' and '=' are exactly the characters that get
        // mangled by command lines, URLs and log scrapers, and this string travels through all
        // three.
        string? encoded = SteamConnectStringRule.Format("10.0.0.1", 27015, "swordfish");
        Assert.NotNull(encoded);

        string token = encoded.Split('/')[^1];
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('=', token);
        Assert.Equal("c3dvcmRmaXNo", token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.168.1.20:27140")]                  // no version prefix at all
    [InlineData("kc2/192.168.1.20:27140")]              // a FUTURE version: reject, never guess
    [InlineData("KC1/192.168.1.20:27140")]              // the prefix is case-sensitive
    [InlineData("kc1/")]                                // nothing after the prefix
    [InlineData("kc1/192.168.1.20")]                    // no port
    [InlineData("kc1/192.168.1.20:")]                   // empty port
    [InlineData("kc1/:27140")]                          // empty host
    [InlineData("kc1/192.168.1.20:0")]                  // port 0
    [InlineData("kc1/192.168.1.20:65536")]              // past the port range
    [InlineData("kc1/192.168.1.20:-1")]
    [InlineData("kc1/192.168.1.20:notaport")]
    [InlineData("kc1/2001:db8::1:27140")]               // IPv6 without brackets
    [InlineData("kc1/[2001:db8::1]27140")]              // bracketed but no colon
    [InlineData("kc1/[]:27140")]                        // empty brackets
    [InlineData("kc1/192.168.1.20:27140/")]             // an empty token is not an empty password
    [InlineData("kc1/192.168.1.20:27140/a")]            // 1 mod 4 base64: encodes no whole byte
    [InlineData("kc1/192.168.1.20:27140/!!!!")]         // not base64 at all
    [InlineData("kc1/192.168.1.20:27140/aa/bb")]        // a second separator
    [InlineData("kc1/host with spaces:27140")]
    [InlineData("kc1/host\tname:27140")]
    [InlineData("kc1/host\0name:27140")]
    [InlineData("kc1/[example.com]:27140")]
    [InlineData("kc1/[not:ipv6]:27140")]
    [InlineData("kc1/host: +27140")]
    [InlineData("kc1/host:+27140")]
    [InlineData("kc1/host:27140/_w")] // invalid UTF-8 must not change the password to U+FFFD
    [InlineData("kc1/host:27140/Y Q")]
    [InlineData("kc1/host:27140/YQ==")]
    [InlineData("kc1/0.0.0.0:27140")]
    [InlineData("kc1/[::]:27140")]
    [InlineData("kc1/255.255.255.255:27140")]
    [InlineData("kc1/[::ffff:0.0.0.0]:27140")]
    [InlineData("kc1/[::ffff:255.255.255.255]:27140")]
    public void MalformedStringsAreRejectedRatherThanPartlyBelieved(string? value)
    {
        Assert.False(SteamConnectStringRule.TryParse(
            value, out string host, out int port, out string password));

        // A rejected parse must leave nothing usable behind: a caller that ignores the bool
        // and reads the outs anyway must get a value it cannot dial, not a half-decoded one.
        Assert.Equal(string.Empty, host);
        Assert.Equal(0, port);
        Assert.Equal(string.Empty, password);
    }

    [Fact]
    public void ParsingNeverThrows()
    {
        // The one behaviour the whole design leans on. Anything at all, including bytes that
        // are not a string a human would type.
        foreach (string hostile in new[]
        {
            "kc1/" + new string('a', 10_000),
            "kc1/\0:1",
            "kc1/1.2.3.4:1/" + new string('=', 500),
            "\0",
            "kc1//1.2.3.4:1",
        })
        {
            Exception? caught = Record.Exception(
                () => SteamConnectStringRule.TryParse(hostile, out _, out _, out _));
            Assert.Null(caught);
        }
    }

    [Theory]
    [InlineData("", 27140)]
    [InlineData("   ", 27140)]
    [InlineData(null, 27140)]
    [InlineData("192.168.1.20", 0)]
    [InlineData("192.168.1.20", 65536)]
    [InlineData("192.168.1.20", -1)]
    [InlineData("host/with/slashes", 27140)]
    [InlineData("not:an:ipv6", 27140)]
    [InlineData("[example.com]", 27140)]
    [InlineData("host\nname", 27140)]
    [InlineData("host\0name", 27140)]
    [InlineData("0.0.0.0", 27140)]
    [InlineData("::", 27140)]
    [InlineData("255.255.255.255", 27140)]
    [InlineData("::ffff:0.0.0.0", 27140)]
    [InlineData("::ffff:255.255.255.255", 27140)]
    public void UnrepresentableSessionsPublishNothingRatherThanSomethingWrong(string? host, int port)
    {
        Assert.Null(SteamConnectStringRule.Format(host, port, string.Empty));
    }

    [Fact]
    public void APasswordTooLongToPublishIsRefusedRatherThanTruncated()
    {
        // An overlong value cannot fit the native callback buffer and may be refused by
        // SetRichPresence. Do not advertise an invite that cannot reach the joiner intact.
        string huge = new('x', 400);
        Assert.Null(SteamConnectStringRule.Format("192.168.1.20", 27140, huge));

        string? fits = SteamConnectStringRule.Format("192.168.1.20", 27140, "swordfish");
        Assert.NotNull(fits);
        Assert.True(fits.Length <= SteamConnectStringRule.MaxLength);
    }

    [Fact]
    public void AnOverlongStringIsRejectedOnTheWayInToo()
    {
        Assert.False(SteamConnectStringRule.TryParse(
            "kc1/192.168.1.20:27140/" + new string('A', SteamConnectStringRule.MaxLength),
            out _, out _, out _));
    }

    [Fact]
    public void NativeBufferLimitCountsUtf8BytesAndReservesATerminator()
    {
        string host = string.Join('.', Enumerable.Repeat(new string('é', 40), 4));
        string value = $"kc1/{host}:27140";
        Assert.True(value.Length < SteamConnectStringRule.MaxLength);
        Assert.Null(SteamConnectStringRule.Format(host, 27140, null));
        Assert.False(SteamConnectStringRule.TryParse(value, out _, out _, out _));

        string prefix = "kc1/127.0.0.1:27140/";
        string fits = prefix + new string('A', 235); // 255 ASCII bytes, valid token length
        Assert.Equal(255, fits.Length);
        Assert.True(SteamConnectStringRule.TryParse(fits, out _, out _, out _));
        Assert.False(SteamConnectStringRule.TryParse(fits + "A", out _, out _, out _));
    }
}
