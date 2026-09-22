using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>The module list docs/research/star-forge-finale.md §3.3/§3.5 verified by reading
/// the real compiled scripts: sta_m45ac (the Bastila-chamber door) and sta_m45ad (Malak's
/// chamber, a continuation of the same forced-solo state).</summary>
public class SoloSegmentCatalogueTests
{
    [Theory]
    [InlineData("sta_m45ac")]
    [InlineData("sta_m45ad")]
    [InlineData("STA_M45AC")] // case-insensitive, matching the engine's own tag comparisons
    public void ContainsTheBaseStarForgeModules(string module)
    {
        SoloSegmentCatalogue catalogue = new();
        Assert.True(catalogue.Contains(module));
    }

    [Theory]
    [InlineData("tar_m02ae")]
    public void ContainsTheScriptBackedDuelRingModules(string module)
    {
        SoloSegmentCatalogue catalogue = new();
        Assert.True(catalogue.Contains(module));
    }

    [Fact]
    public void OnlyTarisIsProvenToPinThePlayerForTheDuelTrigger()
    {
        Assert.True(SoloSegmentCatalogue.ContainsDuelPinModule("tar_m02ae"));
        Assert.False(SoloSegmentCatalogue.ContainsDuelPinModule("korr_m35aa"));
        Assert.False(SoloSegmentCatalogue.ContainsDuelPinModule("end_m01aa"));
    }

    [Fact]
    public void DoesNotContainAnOrdinaryModuleByDefault()
    {
        SoloSegmentCatalogue catalogue = new();

        // sta_m45ab is the droid-horde production floor (§3.4 — full party, no
        // RemovePartyMember anywhere in it), not a solo-gated module. An earlier draft of this
        // feature's brief named it in error; this pins that the catalogue does NOT include it.
        Assert.False(catalogue.Contains("sta_m45ab"));
        Assert.False(catalogue.Contains("tar_m03aa"));
        Assert.False(catalogue.Contains("korr_m35aa")); // exact-script Korriban machine owns this boundary
    }

    [Fact]
    public void EmptyOrNullModuleIsNeverContained()
    {
        SoloSegmentCatalogue catalogue = new();
        Assert.False(catalogue.Contains(string.Empty));
        Assert.False(catalogue.Contains(null!));
    }

    [Fact]
    public void ExtraModulesFromCoopIniAreAddedOnTopOfTheBaseSet()
    {
        SoloSegmentCatalogue catalogue = new(["custom_m01aa"]);

        Assert.True(catalogue.Contains("sta_m45ac"));
        Assert.True(catalogue.Contains("sta_m45ad"));
        Assert.True(catalogue.Contains("custom_m01aa"));
        Assert.False(catalogue.Contains("other_module"));
    }

    [Fact]
    public void ParseExtraModulesSplitsTrimsAndDropsBlankEntries()
    {
        IReadOnlyList<string> parsed = SoloSegmentCatalogue.ParseExtraModules(" foo , bar ,, baz ");
        Assert.Equal(["foo", "bar", "baz"], parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseExtraModulesOfAnAbsentValueIsEmpty(string? raw)
    {
        Assert.Empty(SoloSegmentCatalogue.ParseExtraModules(raw));
    }
}
