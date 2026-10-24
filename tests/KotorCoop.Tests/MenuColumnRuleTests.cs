using KotorCoop.Plugin;
using Xunit;
using Extent = KotorCoop.Plugin.MenuColumnRule.Extent;

namespace KotorCoop.Tests;

/// <summary>The Co-op button's slot is worked out from the menu's live buttons, so it lines up
/// whatever layout a menu mod writes for the player's resolution.</summary>
public sealed class MenuColumnRuleTests
{
    [Fact]
    public void EnhancedMenuLive1024GeometryKeepsCoopBelowQuit()
    {
        Extent[] buttons = [new(592, 364, 235, 24), new(592, 394, 235, 24),
            new(591, 425, 235, 24), new(592, 456, 235, 24), new(592, 486, 235, 24)];
        Assert.True(MenuColumnRule.TryFindSlot(buttons, 1024, 768, out Extent slot));
        Assert.Equal(new Extent(592, 516, 235, 24), slot);
    }

    [Fact]
    public void Enhanced720AssetsInAn800FallbackCannotEstablishAnOnscreenColumn()
    {
        // Captured from the supplied v3 archive on GOG: assets used the requested mode,
        // while the engine fell back. Do not expose an offscreen Co-op hit target.
        Extent[] buttons = [new(848, 340, 235, 24), new(848, 370, 235, 24),
            new(847, 401, 235, 24), new(848, 432, 235, 24), new(848, 462, 235, 24)];
        Assert.False(MenuColumnRule.TryFindSlot(buttons, 800, 600, out _));
        Assert.False(MenuColumnRule.IsAvailable(new(848, 503, 235, 29), buttons, 800, 600));
    }

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
        Assert.True(MenuColumnRule.TryFindSlot(HrMenus1080, 1920, 1080, out Extent slot));
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

        Assert.True(MenuColumnRule.TryFindSlot(buttons, 1920, 1080, out Extent slot));
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

        Assert.True(MenuColumnRule.TryFindSlot(buttons, 800, 600, out Extent slot));
        Assert.Equal(new Extent(368, 443, 235, 29), slot);
    }

    [Fact]
    public void LiveStockColumnUsesItsActualHeightAndPitch()
    {
        // Read from the 800x600 menu with page heap enabled, 2026-10-24. BTN_WARP's
        // original 235x29 extent differs from the real buttons' 235x24 extents.
        Extent[] buttons = [new(368, 280, 235, 24), new(368, 310, 235, 24),
            new(368, 341, 235, 24), new(368, 372, 235, 24), new(368, 402, 235, 24), new(0, 0, 0, 0)];
        Assert.True(MenuColumnRule.TryFindSlot(buttons, 800, 600, out Extent slot));
        Assert.Equal(new Extent(368, 432, 235, 24), slot);
    }

    [Fact]
    public void ButtonsOutsideTheColumnAreIgnored()
    {
        Extent[] buttons = [.. HrMenus1080, new(188, 48, 270, 30), new(1500, 900, 100, 20)];

        Assert.True(MenuColumnRule.TryFindSlot(buttons, 1920, 1080, out Extent slot));
        Assert.Equal(954, slot.Left);
        Assert.Equal(778, slot.Top);
    }

    [Fact]
    public void NoColumnLeavesTheButtonAlone()
    {
        Extent[] buttons = [new(100, 100, 200, 40), new(500, 300, 200, 40)];
        Assert.False(MenuColumnRule.TryFindSlot(buttons, 1920, 1080, out _));
        Assert.False(MenuColumnRule.TryFindSlot([], 1920, 1080, out _));
    }

    [Fact]
    public void ASlotOffTheBottomMovesAboveTheColumn()
    {
        Assert.True(MenuColumnRule.TryFindSlot(HrMenus1080, 1920, 800, out Extent slot));
        Assert.Equal(new Extent(954, 450, 423, 43), slot);
    }

    [Fact]
    public void HiddenOrEmptyRectanglesDoNotCount()
    {
        Extent[] buttons = [.. HrMenus1080, new(954, 0, 0, 0), new(954, 2000, 423, 0)];

        Assert.True(MenuColumnRule.TryFindSlot(buttons, 1920, 1080, out Extent slot));
        Assert.Equal(778, slot.Top);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3440, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(5120, 1440)]
    public void ScaledColumnsStayOnScreen(int width, int height)
    {
        // Synthetic geometry coverage, not evidence that the engine accepted these modes.
        int buttonWidth = width / 4, buttonHeight = height / 20, pitch = height / 12;
        Extent[] buttons = Enumerable.Range(0, 5)
            .Select(i => new Extent(width / 2, height / 3 + pitch * i, buttonWidth, buttonHeight)).ToArray();
        Assert.True(MenuColumnRule.TryFindSlot(buttons, width, height, out Extent slot));
        Assert.Equal(buttons[^1].Top + pitch, slot.Top);
        Assert.True(MenuColumnRule.IsAvailable(slot, buttons, width, height));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferingWidthsCanShareACentreOrRightEdge(bool rightAligned)
    {
        int[] widths = [200, 240, 280, 240, 240];
        Extent[] buttons = widths.Select((w, i) =>
            new Extent(rightAligned ? 1000 - w : 800 - w / 2, 200 + i * 60, w, 40)).ToArray();
        Assert.True(MenuColumnRule.TryFindSlot(buttons, 1280, 720, out Extent slot));
        Assert.Equal(new Extent(rightAligned ? 760 : 680, 500, 240, 40), slot);
    }

    [Fact]
    public void FullHeightColumnUsesSideSlot()
    {
        Extent[] buttons = [new(400, 0, 200, 200), new(400, 200, 200, 200), new(400, 400, 200, 200)];
        Assert.True(MenuColumnRule.TryFindSlot(buttons, 800, 600, out Extent slot));
        Assert.Equal(new Extent(196, 400, 200, 200), slot); // right side would be offscreen
    }

    [Fact]
    public void AnotherColumnCannotBeCoveredByTheNewButton()
    {
        Extent[] buttons = [.. HrMenus1080, new(1000, 778, 400, 43)];
        Assert.True(MenuColumnRule.TryFindSlot(buttons, 1920, 1080, out Extent slot));
        Assert.Equal(450, slot.Top);
    }

    [Fact]
    public void NoFreeSlotFailsInsteadOfOverlappingOrLeavingTheScreen()
    {
        Extent[] buttons = [new(0, 0, 800, 200), new(0, 200, 800, 200), new(0, 400, 800, 200)];
        Assert.False(MenuColumnRule.TryFindSlot(buttons, 800, 600, out _));
        Assert.False(MenuColumnRule.IsAvailable(new(0, 500, 800, 100), buttons, 800, 600));
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(800, 0)]
    [InlineData(-1, 600)]
    [InlineData(800, -1)]
    public void UnknownViewportCannotProduceASlot(int width, int height) =>
        Assert.False(MenuColumnRule.TryFindSlot(HrMenus1080, width, height, out _));

    [Fact]
    public void InvalidAndDuplicateRectanglesCannotManufactureAColumn()
    {
        Extent duplicate = new(100, 100, 200, 40);
        Extent[] buttons = [duplicate, duplicate, duplicate, new(-100, 200, 200, 40),
            new(int.MaxValue, 300, int.MaxValue, 40), new(100, int.MaxValue, 200, int.MaxValue)];
        Assert.False(MenuColumnRule.TryFindSlot(buttons, 800, 600, out _));
    }

    [Fact]
    public void ACustomExistingSlotMustBeOnScreenAndClear()
    {
        Assert.True(MenuColumnRule.IsAvailable(new(100, 100, 235, 29), HrMenus1080, 1920, 1080));
        Assert.False(MenuColumnRule.IsAvailable(new(1800, 100, 235, 29), HrMenus1080, 1920, 1080));
        Assert.False(MenuColumnRule.IsAvailable(new(954, 510, 235, 29), HrMenus1080, 1920, 1080));
        Assert.False(MenuColumnRule.IsAvailable(new(0, 0, 0, 0), [], 1920, 1080));
    }
}
