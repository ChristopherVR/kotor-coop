using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The <c>loottake</c> staging verb's parse layer (docs/research/dialogue-consequences.md's
/// script-item-sync round, Controls 2/3 live-test primitive): same target shape as
/// <c>useplaceable</c> (blank = nearest container, or a tag/object id), see <c>DoorUseTests</c>
/// for the door-verb sibling this mirrors.
/// </summary>
public class LootTakeParseTests
{
    [Fact]
    public void ABareLootTakeMeansTheNearestContainer()
    {
        CoopCommand command = ConsoleCommand.Parse("loottake");

        Assert.Equal(CoopCommandKind.LootTake, command.Kind);
        Assert.Null(command.Address);
    }

    [Theory]
    [InlineData("loottake end_chest01", "end_chest01")]
    [InlineData("loottake 0x1F", "0x1F")]
    [InlineData("loottake #31", "#31")]
    public void ANamedContainerTravelsAsTyped(string line, string token)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.LootTake, command.Kind);
        Assert.Equal(token, command.Address);
    }

    [Theory]
    [InlineData("loottake end chest 01")]      // more than one token
    [InlineData("loottake end-chest-01")]      // not a tag and not an id
    public void AMalformedLootTakeLineIsRefusedWithAReason(string line)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.Invalid, command.Kind);
        Assert.NotEqual(string.Empty, command.Error);
    }
}
