using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The live-cast staging verbs (docs/research/effect-sync.md §14): <c>cast</c> and
/// <c>grantpower</c>. Parsing/validation only — the engine call itself
/// (<c>Odyssey.CastSpellAtObject</c>/<c>Odyssey.TryGrantKnownSpell</c>) needs a live process
/// and is NEEDS-LIVE.
/// </summary>
public class LiveCastTests
{
    // ---- cast ---------------------------------------------------------------------------

    [Fact]
    public void CastParsesANamedSpellAtAnExplicitTarget()
    {
        CoopCommand command = ConsoleCommand.Parse("cast heal 0x35");

        Assert.Equal(CoopCommandKind.Cast, command.Kind);
        Assert.Equal(28, command.Amount); // spells.2da row 28, FORCE_POWER_HEAL
        Assert.Equal("0x35", command.Address);
        Assert.Equal(0u, command.EffectMagnitude);
    }

    [Fact]
    public void CastAcceptsABareSpellRowNumber()
    {
        CoopCommand command = ConsoleCommand.Parse("cast 46 0x35"); // FORCE_POWER_STUN
        Assert.Equal(CoopCommandKind.Cast, command.Kind);
        Assert.Equal(46, command.Amount);
    }

    [Theory]
    [InlineData("heal", 28)]
    [InlineData("cure", 10)]
    [InlineData("valor", 22)]
    [InlineData("forcevalor", 22)]
    [InlineData("stun", 46)]
    [InlineData("fear", 16)]
    [InlineData("forcepush", 23)]
    [InlineData("push", 23)]
    [InlineData("stasis", 29)]
    public void EveryDocumentedAliasResolvesToItsVerifiedSpellsRow(string alias, int expectedRow)
    {
        CoopCommand command = ConsoleCommand.Parse($"cast {alias} me");
        Assert.Equal(CoopCommandKind.Cast, command.Kind);
        Assert.Equal(expectedRow, command.Amount);
    }

    [Fact]
    public void CastResolvesMeToTheLocalPlayerObjectId()
    {
        CoopCommand command = ConsoleCommand.Parse("cast heal me");
        Assert.Equal(CoopCommandKind.Cast, command.Kind);
        Assert.Equal("0x7FFFFFFF", command.Address);
    }

    [Fact]
    public void CastAcceptsAnOptionalMetaMagicBitmask()
    {
        CoopCommand command = ConsoleCommand.Parse("cast heal 0x35 18"); // extend|quicken
        Assert.Equal(CoopCommandKind.Cast, command.Kind);
        Assert.Equal(18u, command.EffectMagnitude);
    }

    [Theory]
    [InlineData("cast")]
    [InlineData("cast heal")]
    [InlineData("cast heal 0x35 1 2")]
    public void CastRefusesTheWrongArgumentCount(string line) =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);

    [Fact]
    public void CastRefusesAnUnknownSpellName() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("cast notaspell 0x35").Kind);

    [Fact]
    public void CastRefusesASpellRowPastTheVerifiedTable() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("cast 9999 0x35").Kind);

    [Fact]
    public void CastRefusesANegativeSpellRow() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("cast -1 0x35").Kind);

    [Fact]
    public void CastRefusesATargetThatIsNotAnObjectId() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("cast heal end_weld").Kind);

    [Fact]
    public void CastRefusesAMetaMagicBitmaskOutOfRange() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("cast heal 0x35 127").Kind);

    [Fact]
    public void CastAcceptsTheHighestValidMetaMagicBitmask()
    {
        CoopCommand command = ConsoleCommand.Parse("cast heal 0x35 126");
        Assert.Equal(CoopCommandKind.Cast, command.Kind);
        Assert.Equal(126u, command.EffectMagnitude);
    }

    // ---- grantpower -----------------------------------------------------------------------

    [Fact]
    public void GrantPowerParsesANamedSpellWithTheDefaultClassSlot()
    {
        CoopCommand command = ConsoleCommand.Parse("grantpower heal");
        Assert.Equal(CoopCommandKind.GrantPower, command.Kind);
        Assert.Equal(28, command.Amount);
        Assert.Equal(0u, command.EffectMagnitude);
    }

    [Fact]
    public void GrantPowerAcceptsAnExplicitClassSlot()
    {
        CoopCommand command = ConsoleCommand.Parse("grantpower stun 1");
        Assert.Equal(CoopCommandKind.GrantPower, command.Kind);
        Assert.Equal(46, command.Amount);
        Assert.Equal(1u, command.EffectMagnitude);
    }

    [Fact]
    public void GrantPowerRefusesAClassSlotOutsideZeroOrOne() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("grantpower heal 2").Kind);

    [Fact]
    public void GrantPowerRefusesAnUnknownSpellName() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("grantpower notaspell").Kind);

    [Theory]
    [InlineData("grantpower")]
    [InlineData("grantpower heal 0 extra")]
    public void GrantPowerRefusesTheWrongArgumentCount(string line) =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);
}
