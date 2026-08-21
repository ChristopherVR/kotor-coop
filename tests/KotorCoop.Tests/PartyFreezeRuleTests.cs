using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The edge detector behind <c>PartyPresenceController</c>'s roster-freeze guard (roadmap item
/// 9): while the engine has one companion under direct script control (party table +0xF0 != -1
/// — a Leviathan brig beat, the Rakatan temple, any forced-solo sequence), the presence mirror
/// must stop claiming and driving every companion body, not just the scripted one, and hand back
/// whatever faction/health hold it was applying. <see cref="PartyRosterController"/> already
/// backs the roster itself off for the identical reason.
///
/// Every case here checks the same thing the roster controller's own log discipline checks
/// elsewhere in this codebase: a freeze that lasts several seconds must produce exactly two log
/// lines, not one per frame, and the four transitions (steady off, steady on, and the two edges)
/// must never be confused with each other.
/// </summary>
public class PartyFreezeRuleTests
{
    [Fact]
    public void NotFrozenAndWasNotFrozenDrivesNormally()
    {
        Assert.Equal(PartyFreezeAction.Drive, PartyFreezeRule.Decide(frozenNow: false, frozenBefore: false));
    }

    [Fact]
    public void TheFreezingEdgeReleasesAndLogs()
    {
        Assert.Equal(
            PartyFreezeAction.ReleaseAndLog,
            PartyFreezeRule.Decide(frozenNow: true, frozenBefore: false));
    }

    [Fact]
    public void StayingFrozenStaysOffSilently()
    {
        // The freeze already reported itself on the rising edge; a scripted sequence that runs
        // for several seconds must not repeat that line every frame it lasts.
        Assert.Equal(PartyFreezeAction.StayOff, PartyFreezeRule.Decide(frozenNow: true, frozenBefore: true));
    }

    [Fact]
    public void TheThawingEdgeResumesAndLogs()
    {
        Assert.Equal(
            PartyFreezeAction.ResumeAndLog,
            PartyFreezeRule.Decide(frozenNow: false, frozenBefore: true));
    }

    [Fact]
    public void EveryFrameOfARealisticFreezeProducesExactlyTwoEdges()
    {
        bool frozenBefore = false;
        int releases = 0, resumes = 0, driven = 0, silent = 0;

        // A realistic sequence: five unfrozen frames, then the engine takes a companion for
        // roughly two seconds (thirty frames at the plugin's 15 Hz-ish party pass), then five
        // more unfrozen frames.
        bool[] frozenReadings =
        [
            false, false, false, false, false,
            .. Enumerable.Repeat(true, 30),
            false, false, false, false, false,
        ];

        foreach (bool frozenNow in frozenReadings)
        {
            PartyFreezeAction action = PartyFreezeRule.Decide(frozenNow, frozenBefore);
            frozenBefore = frozenNow;

            switch (action)
            {
                case PartyFreezeAction.ReleaseAndLog: releases++; break;
                case PartyFreezeAction.ResumeAndLog: resumes++; break;
                case PartyFreezeAction.Drive: driven++; break;
                case PartyFreezeAction.StayOff: silent++; break;
            }
        }

        Assert.Equal(1, releases);
        Assert.Equal(1, resumes);
        Assert.Equal(9, driven); // the ten unfrozen frames, minus the one that is the resume edge
        Assert.Equal(29, silent); // the thirty frozen frames, minus the one that is the release edge
    }
}
