using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The <c>showgui equip</c> live-test verb (docs/research/companion-state-sync.md's dated
/// follow-up to §14). Parsing only — the engine call itself
/// (<c>Odyssey.ShowEquipScreenForPartyMember</c>) needs a live process and is NEEDS-LIVE.
/// </summary>
public class LiveShowGuiEquipTests
{
    [Fact]
    public void ShowGuiEquipDefaultsToPartyIndexZero()
    {
        CoopCommand command = ConsoleCommand.Parse("showgui equip");

        Assert.Equal(CoopCommandKind.ShowGuiEquip, command.Kind);
        Assert.Equal(0, command.Amount);
    }

    [Theory]
    [InlineData("showgui equip 0", 0)]
    [InlineData("showgui equip 1", 1)]
    [InlineData("showgui equip 2", 2)]
    public void ShowGuiEquipParsesThePartyIndex(string line, int expected)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.ShowGuiEquip, command.Kind);
        Assert.Equal(expected, command.Amount);
    }

    [Fact]
    public void ShowGuiEquipIsCaseInsensitiveOnTheSubcommand()
    {
        CoopCommand command = ConsoleCommand.Parse("showgui EQUIP 1");

        Assert.Equal(CoopCommandKind.ShowGuiEquip, command.Kind);
        Assert.Equal(1, command.Amount);
    }

    [Theory]
    [InlineData("showgui")]
    [InlineData("showgui inventory")]
    [InlineData("showgui equip 3")]
    [InlineData("showgui equip -1")]
    [InlineData("showgui equip notanumber")]
    [InlineData("showgui equip 1 extra")]
    public void ShowGuiEquipRefusesBadInput(string line) =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);
}
