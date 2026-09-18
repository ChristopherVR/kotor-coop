using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// docs/review-seamless-2026-09-18.md blocker #7: a manual save bakes co-op stand-ins and
/// map pins into the module; the load-time sweep must recognise exactly those and nothing else.
/// </summary>
public sealed class GhostSweepRuleTests
{
    [Theory]
    [InlineData("coop_phantom")]
    [InlineData("COOP_PHANTOM")]
    public void TheStandInTagIsAGhost(string tag)
    {
        Assert.True(GhostSweepRule.IsGhostCreatureTag(tag));
    }

    [Theory]
    [InlineData("carth")]
    [InlineData("end_sith2")]
    [InlineData("")]
    [InlineData("coop_phantom2")]
    public void EveryOtherCreatureIsLeftAlone(string tag)
    {
        Assert.False(GhostSweepRule.IsGhostCreatureTag(tag));
    }

    [Fact]
    public void ATaggedPeerPinIsAGhostWhateverItsNoteState()
    {
        Assert.True(GhostSweepRule.IsGhostMapMarker("coop_mapnote", hasMapNote: true, mapNoteEnabled: true));
        Assert.True(GhostSweepRule.IsGhostMapMarker("COOP_MAPNOTE", hasMapNote: false, mapNoteEnabled: false));
    }

    [Fact]
    public void AnUntaggedEnabledMapNoteIsALegacyPeerPin()
    {
        // Pins written by builds before the tag existed: an enabled map note with no tag.
        // Measured: every shipped map note carries a tag (sw_waypoint001 on the Endar Spire).
        Assert.True(GhostSweepRule.IsGhostMapMarker("", hasMapNote: true, mapNoteEnabled: true));
    }

    [Theory]
    [InlineData("sw_waypoint001", true, true)]
    [InlineData("", false, false)]
    [InlineData("", true, false)]
    [InlineData("", false, true)]
    [InlineData("wp_spawn", false, false)]
    public void ShippedWaypointsAreLeftAlone(string tag, bool hasMapNote, bool mapNoteEnabled)
    {
        Assert.False(GhostSweepRule.IsGhostMapMarker(tag, hasMapNote, mapNoteEnabled));
    }
}
