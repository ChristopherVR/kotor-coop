using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The <c>attack</c> live-test verb (docs/research/combat-participation.md §15). Parsing only
/// — the engine call itself (<c>Odyssey.AttackTarget</c>) needs a live process and is
/// NEEDS-LIVE.
/// </summary>
public class LiveAttackTests
{
    [Theory]
    [InlineData("attack ranged 0x35")]
    [InlineData("attack RANGED 0x35")]
    public void RangedAttackCarriesUnstagedModeAndExactTarget(string line)
    {
        CoopCommand command = ConsoleCommand.Parse(line);
        Assert.Equal(CoopCommandKind.Attack, command.Kind);
        Assert.Equal("0x35", command.Address);
        Assert.Equal(1, command.Amount);
        Assert.Equal(0, ConsoleCommand.Parse("attack 0x35").Amount);
        Assert.Equal(0, ConsoleCommand.Parse("attack stop").Amount);
    }

    [Theory]
    [InlineData("attack ranged stop")]
    [InlineData("attack ranged 0x0")]
    [InlineData("attack ranged 0x35 extra")]
    public void RangedAttackRefusesInvalidTargetsAndExtraArguments(string line) =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);

    [Fact]
    public void AttackParsesAnObjectId()
    {
        CoopCommand command = ConsoleCommand.Parse("attack 0x35");

        Assert.Equal(CoopCommandKind.Attack, command.Kind);
        Assert.Equal("0x35", command.Address);
    }

    [Fact]
    public void AttackStopParsesToTheLiteralStopToken()
    {
        CoopCommand command = ConsoleCommand.Parse("attack stop");

        Assert.Equal(CoopCommandKind.Attack, command.Kind);
        Assert.Equal("stop", command.Address);
    }

    [Fact]
    public void AttackStopIsCaseInsensitive()
    {
        CoopCommand command = ConsoleCommand.Parse("attack STOP");

        Assert.Equal(CoopCommandKind.Attack, command.Kind);
        Assert.Equal("stop", command.Address);
    }

    [Fact]
    public void AttackRefusesATargetThatIsNotAnObjectId() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("attack end_weld").Kind);

    [Fact]
    public void AttackRefusesTheZeroObjectId() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("attack 0x0").Kind);

    [Theory]
    [InlineData("attack")]
    [InlineData("attack 0x35 extra")]
    public void AttackRefusesTheWrongArgumentCount(string line) =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);
}
