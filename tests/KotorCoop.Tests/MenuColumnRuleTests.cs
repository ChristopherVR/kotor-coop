using KotorCoop.Plugin;
using Xunit;
using Extent = KotorCoop.Plugin.MenuColumnRule.Extent;

namespace KotorCoop.Tests;

/// <summary>The Co-op button's slot is worked out from the menu's live buttons, so it lines up
/// whatever layout a menu mod writes for the player's resolution.</summary>
public sealed class MenuColumnRuleTests
{
    // HR Menus' mainmenu8x6.gui as the K1 Full build installs it for 1920x1080 (BTN_WARP was left
    // at 368,443, far left of the column).
    private static readonly Extent[] HrMenus1080 =
    [
        new(954, 504, 423, 43), // New Game
        new(954, 558, 423, 43), // Load Game
        new(952, 614, 423, 43), // Movies (two pixels off)
        new(954, 670, 423, 43), // Options
        new(954, 724, 423, 43), // Quit
    ];

    [Fact]
    public void HrMenusPutsTheButtonOneRowUnderQuit()
    {
        Assert.True(MenuColumnRule.TrySlotBelowColumn(HrMenus1080, 1080, out Extent slot));
        Assert.Equal(new Extent(954, 778, 423, 43), slot);
    }

    [Fact]
    public void ATallerQuitButtonDoesNotStretchTheRow()
    {
        // HR Menus' mainmenu.gui draws Quit 61 high under 50-high buttons spaced 54 apart.
        Extent[] buttons =
        [
            new(934, 500, 473, 50), new(934, 554, 473, 50), new(934, 608, 473, 50),
            new(934, 662, 473, 50), new(934, 716, 473, 61),
        ];

        Assert.True(MenuColumnRule.TrySlotBelowColumn(buttons, 1080, out Extent slot));
        Assert.Equal(new Extent(934, 781, 473, 50), slot);
    }

    [Fact]
    public void TheStockLayoutKeepsTheGuiSlot()
    {
        // The stock 800x600 menu: BTN_WARP's own .gui extent is (368, 443, 235, 29), one row below
        // Quit at (368, 402); the rule must land on the same rectangle.
        Extent[] buttons =
        [
            new(368, 238, 235, 29), new(368, 279, 235, 29), new(368, 320, 235, 29),
            new(368, 361, 235, 29), new(368, 402, 235, 29),
        ];

        Assert.True(MenuColumnRule.TrySlotBelowColumn(buttons, 600, out Extent slot));
        Assert.Equal(new Extent(368, 443, 235, 29), slot);
    }

    [Fact]
    public void ButtonsOutsideTheColumnAreIgnored()
    {
        Extent[] buttons = [.. HrMenus1080, new(188, 48, 270, 30), new(1500, 900, 100, 20)];

        Assert.True(MenuColumnRule.TrySlotBelowColumn(buttons, 1080, out Extent slot));
        Assert.Equal(954, slot.Left);
        Assert.Equal(778, slot.Top);
    }

    [Fact]
    public void NoColumnLeavesTheButtonAlone()
    {
        Extent[] buttons = [new(100, 100, 200, 40), new(500, 300, 200, 40)];
        Assert.False(MenuColumnRule.TrySlotBelowColumn(buttons, 1080, out _));
        Assert.False(MenuColumnRule.TrySlotBelowColumn([], 1080, out _));
    }

    [Fact]
    public void ASlotOffTheBottomOfTheScreenLeavesTheButtonAlone()
    {
        Assert.False(MenuColumnRule.TrySlotBelowColumn(HrMenus1080, 800, out _));
    }

    [Fact]
    public void HiddenOrEmptyRectanglesDoNotCount()
    {
        Extent[] buttons = [.. HrMenus1080, new(954, 0, 0, 0), new(954, 2000, 423, 0)];

        Assert.True(MenuColumnRule.TrySlotBelowColumn(buttons, 1080, out Extent slot));
        Assert.Equal(778, slot.Top);
    }
}
