using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The make-a-real-Jedi staging verbs (docs/research/class-and-power-grants.md §9):
/// <c>levelup</c> and <c>stats</c>. Parsing/validation only — the engine calls themselves
/// (<c>Odyssey.TryAutoLevelUp</c>, the <c>Odyssey.LevelUp.cs</c> reads) need a live process and
/// are NEEDS-LIVE.
/// </summary>
public class LiveLevelUpTests
{
    // ---- levelup --------------------------------------------------------------------------

    [Fact]
    public void LevelUpParsesAClassSlotWithTheDefaultCount()
    {
        CoopCommand command = ConsoleCommand.Parse("levelup 1");

        Assert.Equal(CoopCommandKind.LevelUp, command.Kind);
        Assert.Equal(1, command.Amount);
        Assert.Equal(1u, command.EffectMagnitude);
    }

    [Fact]
    public void LevelUpAcceptsAnExplicitCount()
    {
        CoopCommand command = ConsoleCommand.Parse("levelup 1 4");

        Assert.Equal(CoopCommandKind.LevelUp, command.Kind);
        Assert.Equal(1, command.Amount);
        Assert.Equal(4u, command.EffectMagnitude);
    }

    [Fact]
    public void LevelUpAcceptsAClassesTwoDaRowId()
    {
        // addclass 3 (Guardian) is a slot 0/1 index everywhere ELSE in this table, but levelup
        // also accepts the raw classes.2da id the way addclass itself does.
        CoopCommand command = ConsoleCommand.Parse("levelup 3");

        Assert.Equal(CoopCommandKind.LevelUp, command.Kind);
        Assert.Equal(3, command.Amount);
    }

    [Fact]
    public void LevelUpAcceptsTheHighestValidCount()
    {
        CoopCommand command = ConsoleCommand.Parse("levelup 0 40");

        Assert.Equal(CoopCommandKind.LevelUp, command.Kind);
        Assert.Equal(40u, command.EffectMagnitude);
    }

    [Theory]
    [InlineData("levelup")]
    [InlineData("levelup 1 4 extra")]
    public void LevelUpRefusesTheWrongArgumentCount(string line) =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);

    [Fact]
    public void LevelUpRefusesAClassArgumentThatIsNotAnInteger() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("levelup guardian").Kind);

    [Fact]
    public void LevelUpRefusesANegativeClassArgument() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("levelup -1").Kind);

    [Fact]
    public void LevelUpRefusesAClassArgumentPastTheClassesTable() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("levelup 9").Kind);

    [Fact]
    public void LevelUpRefusesACountOfZero() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("levelup 1 0").Kind);

    [Fact]
    public void LevelUpRefusesACountPastTheCap() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("levelup 1 41").Kind);

    // ---- stats ------------------------------------------------------------------------------

    [Fact]
    public void StatsParsesWithNoArguments()
    {
        CoopCommand command = ConsoleCommand.Parse("stats");
        Assert.Equal(CoopCommandKind.Stats, command.Kind);
    }

    [Fact]
    public void StatsRefusesAnyArgument() =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("stats me").Kind);
}
