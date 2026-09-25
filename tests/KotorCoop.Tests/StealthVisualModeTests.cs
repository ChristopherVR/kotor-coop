using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure parsing/migration behind coop.ini <c>stealth_partner_visual</c> (replaces the old
/// boolean <c>stealth_visual_sync</c>) — see docs/research/effect-sync.md §12.10.
/// </summary>
public class StealthVisualModeTests
{
    // ---- TryParse ---------------------------------------------------------------------------

    [Theory]
    [InlineData("translucent", 0)]
    [InlineData("TRANSLUCENT", 0)]
    [InlineData("  translucent  ", 0)]
    [InlineData("hidden", 1)]
    [InlineData("Hidden", 1)]
    [InlineData("off", 2)]
    [InlineData("OFF", 2)]
    [InlineData("none", 2)]
    public void TryParseAcceptsTheThreeSpellingsCaseAndSpaceInsensitively(string raw, int expected)
    {
        Assert.True(StealthVisualMode.TryParse(raw, out StealthVisualMode.Kind kind));
        Assert.Equal((StealthVisualMode.Kind)expected, kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("shimmer")]
    [InlineData("1")]
    [InlineData("hiddne")]
    public void TryParseRejectsAnythingElseAndDefaultsTheOutParamToTranslucent(string? raw)
    {
        Assert.False(StealthVisualMode.TryParse(raw, out StealthVisualMode.Kind kind));
        Assert.Equal(StealthVisualMode.Kind.Translucent, kind);
    }

    // ---- Resolve: the new key wins outright when present ------------------------------------

    [Theory]
    [InlineData("translucent", 0)]
    [InlineData("hidden", 1)]
    [InlineData("off", 2)]
    public void ResolveUsesTheNewKeyWhenPresentRegardlessOfTheLegacyKey(string newRaw, int expected)
    {
        var want = (StealthVisualMode.Kind)expected;
        Assert.Equal(want, StealthVisualMode.Resolve(newRaw, legacyRaw: "1"));
        Assert.Equal(want, StealthVisualMode.Resolve(newRaw, legacyRaw: "0"));
        Assert.Equal(want, StealthVisualMode.Resolve(newRaw, legacyRaw: null));
    }

    [Fact]
    public void AMisspelledNewKeyBecomesTranslucentNotTheLegacyMeaning()
    {
        // A typo in stealth_partner_visual must not silently resurrect stealth_visual_sync's
        // meaning - it becomes the new default, same as TryParse's own fallback.
        Assert.Equal(
            StealthVisualMode.Kind.Translucent,
            StealthVisualMode.Resolve(newRaw: "shimmer", legacyRaw: "1"));
    }

    // ---- Resolve: the legacy key, only when the new key is absent ---------------------------

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("on")]
    public void AnExplicitTruthyLegacyKeyMapsToHiddenWhenTheNewKeyIsAbsent(string legacyRaw)
    {
        Assert.Equal(StealthVisualMode.Kind.Hidden, StealthVisualMode.Resolve(newRaw: null, legacyRaw));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("off")]
    public void AnExplicitFalseLegacyKeyMapsToOffWhenTheNewKeyIsAbsent(string legacyRaw)
    {
        // This is what stealth_visual_sync = 0 already meant: no engine call at all.
        Assert.Equal(StealthVisualMode.Kind.Off, StealthVisualMode.Resolve(newRaw: null, legacyRaw));
    }

    // ---- Resolve: neither key present -> the new product default ----------------------------

    [Fact]
    public void NeitherKeyPresentDefaultsToTranslucent()
    {
        Assert.Equal(StealthVisualMode.Kind.Translucent, StealthVisualMode.Resolve(newRaw: null, legacyRaw: null));
    }

    // ---- An existing coop.ini keeps behaving identically after the upgrade ------------------

    [Fact]
    public void AnUpgradedInstallThatNeverSetEitherKeyGainsTheNewShimmerLook()
    {
        // Before this change: stealth_visual_sync defaulted to false/OFF -> no visual change.
        // After: neither key present -> Translucent. This is the deliberate 2026-09-25 product
        // decision (prefer the retail-shaped look over the old cautious default), pinned here
        // so a future "restore the old cautious default" change has to touch this test, not
        // silently drift.
        Assert.Equal(StealthVisualMode.Kind.Translucent, StealthVisualMode.Resolve(null, null));
    }

    [Fact]
    public void AnInstallThatExplicitlySetStealthVisualSyncOnKeepsMeaningHiddenAfterUpgrade()
    {
        Assert.Equal(StealthVisualMode.Kind.Hidden, StealthVisualMode.Resolve(null, "1"));
    }

    [Fact]
    public void AnInstallThatExplicitlySetStealthVisualSyncOffKeepsMeaningOffAfterUpgrade()
    {
        Assert.Equal(StealthVisualMode.Kind.Off, StealthVisualMode.Resolve(null, "0"));
    }
}
