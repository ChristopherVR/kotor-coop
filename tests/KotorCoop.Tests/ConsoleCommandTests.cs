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

    /// <summary>Roadmap item 24, "join by address robustness": a hostname (not an IP literal)
    /// must resolve through DNS. "localhost" is used because it resolves in every environment
    /// without depending on external network access.</summary>
    [Fact]
    public void ResolvesAPlainHostname()
    {
        CoopCommand c = ConsoleCommand.Parse("connect localhost:9100");
        Assert.True(c.TryResolveEndpoint(7777, out IPEndPoint endpoint));
        Assert.Equal(9100, endpoint.Port);
        Assert.True(IPAddress.IsLoopback(endpoint.Address));
    }

    /// <summary>Roadmap item 24: a bracketed IPv6 literal with an explicit port must resolve —
    /// documented as untested beyond this parse-level check (docs/research/network-topology.md,
    /// "Internet play" section): nothing downstream (the UDP socket, NAT/UPnP) has been
    /// exercised over IPv6.</summary>
    [Fact]
    public void ResolvesABracketedIPv6LiteralWithAPort()
    {
        CoopCommand c = ConsoleCommand.Parse("connect [::1]:9100");
        Assert.True(c.TryResolveEndpoint(7777, out IPEndPoint endpoint));
        Assert.Equal(IPAddress.IPv6Loopback, endpoint.Address);
        Assert.Equal(9100, endpoint.Port);
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

    // ---- staging cheats --------------------------------------------------------

    [Theory]
    [InlineData("god", -1)]
    [InlineData("god on", 1)]
    [InlineData("god ON", 1)]
    [InlineData("god 1", 1)]
    [InlineData("god off", 0)]
    [InlineData("god no", 0)]
    public void GodParsesItsToggle(string line, int amount)
    {
        CoopCommand c = ConsoleCommand.Parse(line);
        Assert.Equal(CoopCommandKind.God, c.Kind);
        Assert.Equal(amount, c.Amount);
    }

    /// <summary>
    /// The rejection that matters: god mode is yours alone, so aiming it at a peer is
    /// refused with the rule rather than silently arming your own.
    /// </summary>
    [Theory]
    [InlineData("god malak")]
    [InlineData("god me")]
    [InlineData("god everyone")]
    public void GodRefusesATarget(string line)
    {
        CoopCommand c = ConsoleCommand.Parse(line);
        Assert.Equal(CoopCommandKind.Invalid, c.Kind);
        Assert.Contains("your own character", c.Error);
    }

    [Fact]
    public void TopUpDefaultsToFullAndTakesACount()
    {
        Assert.Equal(0, ConsoleCommand.Parse("topup").Amount);
        Assert.Equal(25, ConsoleCommand.Parse("topup 25").Amount);
    }

    [Theory]
    [InlineData("topup lots")]
    [InlineData("topup 0")]
    [InlineData("topup -5")]
    public void TopUpRejectsANonCount(string line)
    {
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);
    }

    [Theory]
    [InlineData("standup", 0)]
    [InlineData("getup", 0)]
    [InlineData("standup 30", 30)]
    public void StandUpParses(string line, int amount)
    {
        CoopCommand c = ConsoleCommand.Parse(line);
        Assert.Equal(CoopCommandKind.StandUp, c.Kind);
        Assert.Equal(amount, c.Amount);
    }

    [Fact]
    public void BareSlayMeansTheNearestHostile()
    {
        CoopCommand c = ConsoleCommand.Parse("slay");
        Assert.Equal(CoopCommandKind.Slay, c.Kind);
        Assert.Null(c.Address);
    }

    [Theory]
    [InlineData("slay sith_soldier01")]
    [InlineData("slay 0x2A")]
    [InlineData("slay #42")]
    [InlineData("slay 42")]
    public void SlayAcceptsATagOrAnId(string line)
    {
        Assert.Equal(CoopCommandKind.Slay, ConsoleCommand.Parse(line).Kind);
    }

    /// <summary>A cheat aimed at something the caller does not own: your own character is
    /// never a slay target, and the message says why rather than "not a tag".</summary>
    [Theory]
    [InlineData("slay me")]
    [InlineData("slay self")]
    [InlineData("slay myself")]
    public void SlayRefusesYourself(string line)
    {
        CoopCommand c = ConsoleCommand.Parse(line);
        Assert.Equal(CoopCommandKind.Invalid, c.Kind);
        Assert.Contains("not a target", c.Error);
    }

    [Theory]
    [InlineData("slay some-thing")]
    [InlineData("slay a;b")]
    public void SlayRejectsAnImpossibleToken(string line)
    {
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);
    }

    [Fact]
    public void TeleportParsesCoordinates()
    {
        CoopCommand c = ConsoleCommand.Parse("tp 12.5 -30 2.25");
        Assert.Equal(CoopCommandKind.Teleport, c.Kind);
        Assert.Equal(12.5f, c.X);
        Assert.Equal(-30f, c.Y);
        Assert.Equal(2.25f, c.Z);
    }

    [Fact]
    public void TeleportTakesTwoCoordinatesWithHeightOptional()
    {
        CoopCommand c = ConsoleCommand.Parse("tp 1 2");
        Assert.Equal(CoopCommandKind.Teleport, c.Kind);
        Assert.Equal(0f, c.Z);
    }

    [Theory]
    [InlineData("tp")]
    [InlineData("tp 5")]
    [InlineData("tp here there")]
    [InlineData("tp 1 2 up")]
    public void TeleportRejectsNonCoordinates(string line)
    {
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);
    }

    [Fact]
    public void GoToNeedsAPlayer()
    {
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("goto").Kind);

        CoopCommand c = ConsoleCommand.Parse("goto Malak");
        Assert.Equal(CoopCommandKind.GoTo, c.Kind);
        Assert.Equal("Malak", c.Address);
    }

    [Fact]
    public void DropItemValidatesTheBlueprint()
    {
        CoopCommand c = ConsoleCommand.Parse("dropitem G_W_BlstrPstl001");
        Assert.Equal(CoopCommandKind.DropItem, c.Kind);
        Assert.Equal("g_w_blstrpstl001", c.Address);

        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("dropitem").Kind);
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("dropitem not-a-resref").Kind);
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("dropitem waaaaaaaaaaaaaaaaytoolong").Kind);
    }

    [Fact]
    public void CheatsListsBothHalves()
    {
        Assert.Equal(CoopCommandKind.Cheats, ConsoleCommand.Parse("cheats").Kind);
        Assert.Contains(ConsoleCommand.CheatsText, l => l.Contains("slay"));
        Assert.Contains(ConsoleCommand.CheatsText, l => l.Contains("giveitem"));
    }

    /// <summary>
    /// ⚠ ALL THREE SPELLINGS ARE HEX, and this test is the regression for a live footgun.
    /// Every id this project prints — logs, replies, creatures.ps1 — is hex, so the only
    /// number anyone can type is one they read in hex. Measured 2026-08-15: `slay #35`,
    /// typed to kill the disputed 0x35, silently killed 0x23 because `#35` parsed as
    /// decimal. There is no source anywhere for a decimal object id.
    /// </summary>
    [Theory]
    [InlineData("0x2A", 0x2Au)]
    [InlineData("#42", 0x42u)]
    [InlineData("42", 0x42u)]
    [InlineData("#35", 0x35u)]
    [InlineData("0x35", 0x35u)]
    public void ObjectIdsParseAsHexInAllThreeSpellings(string token, uint expected)
    {
        Assert.True(ConsoleCommand.TryParseObjectId(token, out uint id));
        Assert.Equal(expected, id);
    }

    /// <summary>
    /// Regression for the live 2026-09-02 `movenear 0x11C` report (tar_m10aa): the reply named
    /// a completely different id (0xE) than what was typed, which briefly looked like an id
    /// truncated to a byte or a decimal/hex mixup. It was not — this pins that a 3-digit hex id
    /// above 0xFF parses to its full value in every spelling, with no truncation anywhere in
    /// this layer. The actual bug was downstream, in the plugin's target resolution (see
    /// CheatController.Party.cs's MoveNear).
    /// </summary>
    [Theory]
    [InlineData("0x11C", 0x11Cu)]
    [InlineData("#11C", 0x11Cu)]
    public void ObjectIdsAbove0xFFParseInFull(string token, uint expected)
    {
        Assert.True(ConsoleCommand.TryParseObjectId(token, out uint id));
        Assert.Equal(expected, id);
    }

    /// <summary>
    /// The other half of that rule: a BARE token competes with tags, and plenty of real tags
    /// are valid hex. `slay beef` must aim at the creature tagged "beef", not at object
    /// 0xBEEF — so hex letters need `0x` or `#` to say they are a number.
    /// </summary>
    [Theory]
    [InlineData("beef")]
    [InlineData("cad")]
    [InlineData("end_weld")]
    public void ABareTokenWithLettersIsATagNotAnId(string token)
    {
        Assert.False(ConsoleCommand.TryParseObjectId(token, out _));
    }

    /// <summary>Marked forms may carry hex letters, because the mark says "this is a
    /// number".</summary>
    [Theory]
    [InlineData("#beef", 0xBEEFu)]
    [InlineData("0xbeef", 0xBEEFu)]
    public void AMarkedTokenMayCarryHexLetters(string token, uint expected)
    {
        Assert.True(ConsoleCommand.TryParseObjectId(token, out uint id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("sith_soldier")]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("0xzz")]
    [InlineData("#zz")]
    public void ANonIdIsNotAnObjectId(string token)
    {
        Assert.False(ConsoleCommand.TryParseObjectId(token, out _));
    }

    /// <summary>The in-process resolve question needs an id and refuses without one — it is
    /// a measurement of ONE object, and a blank one would silently measure nothing.</summary>
    [Fact]
    public void ResolveNeedsAnId()
    {
        Assert.Equal(CoopCommandKind.ResolveObject, ConsoleCommand.Parse("resolve 0x35").Kind);
        Assert.Equal("0x35", ConsoleCommand.Parse("resolve 0x35").Address);
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("resolve").Kind);
    }

    /// <summary>
    /// `movenear` follows the exact same shape as `resolve`: the parser only checks an
    /// argument was typed and carries the raw token through unvalidated (the actual
    /// <c>TryParseObjectId</c> call, and the id's fate, live one layer down in the agent's
    /// dispatcher) — so a multi-digit hex id like the live 2026-09-02 report's `0x11C` survives
    /// this layer completely unmangled.
    /// </summary>
    [Fact]
    public void MoveNearNeedsAnId()
    {
        CoopCommand c = ConsoleCommand.Parse("movenear 0x11C");
        Assert.Equal(CoopCommandKind.MoveNear, c.Kind);
        Assert.Equal("0x11C", c.Address);
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("movenear").Kind);
    }

    /// <summary>The census dump has to be reachable from an unattended console line — it is
    /// the only way to ask a running instance what it thinks it is publishing.</summary>
    [Fact]
    public void CensusParsesBare()
    {
        CoopCommand c = ConsoleCommand.Parse("census");
        Assert.Equal(CoopCommandKind.Census, c.Kind);
        Assert.Null(c.Address);
    }

    /// <summary>With an id it becomes a question about ONE creature, which is the form an
    /// argument about a specific id actually needs.</summary>
    [Fact]
    public void CensusCarriesAnOptionalId()
    {
        CoopCommand c = ConsoleCommand.Parse("census 0x35");
        Assert.Equal(CoopCommandKind.Census, c.Kind);
        Assert.Equal("0x35", c.Address);
    }
}
