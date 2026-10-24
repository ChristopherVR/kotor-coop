namespace KotorCoop.Plugin;

/// <summary>Geometry for resizing a captured Enhanced v3 menu without rewriting its GUI assets.
/// Callers retain original rectangles and always derive the next mode from that baseline.</summary>
internal static class EnhancedMenuLayoutRule
{
    /// <summary>Enhanced's main-menu size tiers (prepareMainMenu, vendor RVA 0x6E50).
    /// Positive [Enhanced] MainMenuScalePercent replaces the automatic scale candidate;
    /// the vendor still applies its discrete tiers and minimum viewport sizes.</summary>
    public static int TargetPercent(int width, int height, int manualScalePercent = 0)
    {
        double candidate = (manualScalePercent > 0
            ? manualScalePercent : Math.Max(100, height * (100.0 / 720))) + 0.01;
        if (candidate >= 300 && width >= 2400 && height >= 1800) return 300;
        if (candidate >= 250 && width >= 2000 && height >= 1500) return 250;
        if (candidate >= 200 && width >= 1600 && height >= 1200) return 200;
        if (candidate >= 175 && width >= 1400 && height >= 1050) return 175;
        if (candidate >= 150 && width >= 1200 && height >= 900) return 150;
        if (candidate >= 125 && width >= 1000 && height >= 750) return 125;
        return 100;
    }

    /// <summary>Resize the main menu's right-anchored buttons and decorations. Enhanced places
    /// BTN_*, LBL_GAMELOGO, LBL_MENUBG, LBL_LUCAS and LBL_BW this way; other decorations need
    /// their own centering or full-canvas rule.</summary>
    public static MenuColumnRule.Extent ResizeMainControl(MenuColumnRule.Extent e,
        int sourceWidth, int sourceHeight, int sourcePercent,
        int targetWidth, int targetHeight, int targetPercent, bool rightAnchored = true)
    {
        if (!ValidCanvas(sourceWidth, sourceHeight, targetWidth, targetHeight)
            || sourcePercent <= 0 || targetPercent <= 0) return e;
        double ratio = (double)targetPercent / sourcePercent;
        return new(
            rightAnchored
                ? Coordinate(targetWidth - ((double)sourceWidth - e.Left) * ratio)
                : Coordinate(targetWidth / 2.0 + (e.Left - sourceWidth / 2.0) * ratio),
            Coordinate(targetHeight / 2.0 + (e.Top - sourceHeight / 2.0) * ratio),
            Size(e.Width * ratio), Size(e.Height * ratio));
    }

    /// <summary>Resize a full-canvas menu's local controls with the vendor's uniform scale
    /// and centered 640x480 content canvas. Full-screen root extents track the viewport.</summary>
    public static MenuColumnRule.Extent ResizeFullCanvasControl(MenuColumnRule.Extent e,
        int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (!ValidCanvas(sourceWidth, sourceHeight, targetWidth, targetHeight)) return e;
        if (e == new MenuColumnRule.Extent(0, 0, sourceWidth, sourceHeight))
            return new(0, 0, targetWidth, targetHeight);
        int sourceScale = CanvasPercent(sourceWidth, sourceHeight);
        int targetScale = CanvasPercent(targetWidth, targetHeight);
        double ratio = (double)targetScale / sourceScale;
        int sourceX = (sourceWidth - Size(640.0 * sourceScale / 100)) / 2;
        int sourceY = (sourceHeight - Size(480.0 * sourceScale / 100)) / 2;
        int targetX = (targetWidth - Size(640.0 * targetScale / 100)) / 2;
        int targetY = (targetHeight - Size(480.0 * targetScale / 100)) / 2;
        return new(Coordinate(targetX + ((double)e.Left - sourceX) * ratio),
            Coordinate(targetY + ((double)e.Top - sourceY) * ratio),
            Size(e.Width * ratio), Size(e.Height * ratio));
    }

    /// <summary>Scale a dialog's local rectangles; its native panel flags retain screen centering.</summary>
    public static MenuColumnRule.Extent ResizeDialogControl(MenuColumnRule.Extent e,
        int sourcePercent, int targetPercent)
    {
        if (sourcePercent <= 0 || targetPercent <= 0) return e;
        // At >=100%, the vendor's rounded integer source coordinates uniquely identify
        // their integer template coordinates. Recover those before scaling again: a root
        // width of 363 at 125% came from 290, and must become 435 (not 436) at 150%.
        // Exact root sizes matter to Enhanced's managed-panel hit-test classifier.
        double Resize(int value)
        {
            if (sourcePercent < 100) return (double)value * targetPercent / sourcePercent;
            double original = Math.Round((double)value * 100 / sourcePercent, MidpointRounding.AwayFromZero);
            return original * targetPercent / 100;
        }
        return new(Coordinate(Resize(e.Left)), Coordinate(Resize(e.Top)),
            Size(Resize(e.Width)), Size(Resize(e.Height)));
    }

    private static bool ValidCanvas(int sw, int sh, int tw, int th) => sw > 0 && sh > 0 && tw > 0 && th > 0;

    private static int CanvasPercent(int width, int height) => (int)Math.Clamp(
        Math.Min((long)width * 100 / 640, (long)height * 100 / 480), 100, 450);

    // roundf is the vendor's rounding rule; preserve negative off-canvas positions, and
    // saturate before conversion so malformed rectangles cannot overflow into hit targets.
    private static int Coordinate(double value) => (int)Math.Clamp(
        Math.Round(value, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);

    private static int Size(double value) => (int)Math.Clamp(
        Math.Round(value, MidpointRounding.AwayFromZero), 0, int.MaxValue);
}
