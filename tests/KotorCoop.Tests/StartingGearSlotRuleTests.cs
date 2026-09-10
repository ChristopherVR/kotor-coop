using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure classification half of <c>Odyssey.EquipUnequippedStartingGear</c> — the
/// join_mode = newcharacter repair added 2026-09-10 (docs/research/world-adoption.md's
/// addendum) for a first-time joiner's starting gear sitting unequipped at capture time.
/// </summary>
public class StartingGearSlotRuleTests
{
    [Theory]
    [InlineData("g_a_clothes01", StartingGearSlotRule.SlotBody)]
    [InlineData("G_A_CLASS4001", StartingGearSlotRule.SlotBody)]
    [InlineData("g_w_blstrpstl001", StartingGearSlotRule.SlotRightWeapon)]
    [InlineData("g_w_shortswrd01", StartingGearSlotRule.SlotRightWeapon)]
    public void Classify_recognises_the_stock_prefixes(string tag, int expectedSlot)
    {
        Assert.Equal(expectedSlot, StartingGearSlotRule.Classify(tag));
    }

    [Theory]
    [InlineData("g_i_medeqpmnt01")] // a misc item, not equippable in either recognised slot
    [InlineData("k_computer_spike")] // a quest item whose tag does not follow the g_*_ convention
    [InlineData("")]
    [InlineData("g_a")] // shorter than the 4-character prefix this rule reads
    public void Classify_leaves_an_unrecognised_tag_alone(string tag)
    {
        Assert.Equal(-1, StartingGearSlotRule.Classify(tag));
    }
}
