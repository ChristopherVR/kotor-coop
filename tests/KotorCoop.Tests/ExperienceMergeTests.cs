using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The host's per-peer XP baseline (<see cref="ExperienceMerge"/>, 2026-09-18). The rule under
/// test: a peer's FIRST report after it joins never raises the host; only what it earns after
/// that is folded in by max; a peer at or below the host only re-baselines; and a reload
/// (two consecutive lower reports) re-baselines too. <see cref="ProgressionPumpTests"/> covers
/// the same rule through the real pump.
/// </summary>
public class ExperienceMergeTests
{
    [Fact]
    public void AVeteranJoiningAnEarlierSaveDoesNotLiftTheHost()
    {
        // docs/review-seamless-2026-09-18.md §2: 5,000 XP joins a 500 XP save.
        ExperienceMerge merge = new();

        Assert.Equal(500u, merge.Fold(500, peerId: 2, generation: 7, peerExperience: 5000));
        Assert.Equal(500u, merge.Fold(500, 2, 7, 5000));
    }

    [Fact]
    public void WhatThePeerEarnsAfterItsBaselineIsCarried()
    {
        // A conversation reward only the client ran: +200 on the peer, nothing on the host.
        ExperienceMerge merge = new();
        merge.Fold(500, 2, 7, 5000);

        Assert.Equal(700u, merge.Fold(500, 2, 7, 5200));
    }

    [Fact]
    public void ASharedKillIsNotCountedTwice()
    {
        // Both engines grant the same kill independently (+100 each); the candidate tracks
        // the host exactly and lifts it by nothing.
        ExperienceMerge merge = new();
        merge.Fold(500, 2, 7, 5000);

        Assert.Equal(600u, merge.Fold(600, 2, 7, 5100));
    }

    [Fact]
    public void HostOnlyProgressIsNeverPulledDown()
    {
        ExperienceMerge merge = new();
        merge.Fold(500, 2, 7, 5000);

        // The host earned 300 on its own; the peer's unchanged report is below that.
        Assert.Equal(800u, merge.Fold(800, 2, 7, 5000));
    }

    [Fact]
    public void AFreshCharacterAdoptingTheHostsTotalIsNotEarnings()
    {
        // A 0 XP character joins a 5,000 XP host; the client's own Math.Max write lifts it to
        // 5,000, which it then reports. Read as a delta that would be +5,000 on the host.
        ExperienceMerge merge = new();
        merge.Fold(5000, 2, 7, 0);

        Assert.Equal(5000u, merge.Fold(5000, 2, 7, 5000));

        // From there, a client-only grant does carry.
        Assert.Equal(5200u, merge.Fold(5000, 2, 7, 5200));
    }

    [Fact]
    public void APeerBehindTheHostNeverLiftsItUntilItPasses()
    {
        // The pre-existing property of the bare max, kept: 0 -> 50 on a peer below a 5,000
        // host is nothing to the party until the peer is actually ahead.
        ExperienceMerge merge = new();
        merge.Fold(5000, 2, 7, 0);

        Assert.Equal(5000u, merge.Fold(5000, 2, 7, 50));
        Assert.Equal(5000u, merge.Fold(5000, 2, 7, 4999));

        // Each report at or below the host re-baselined it there, so once it passes, only the
        // rise SINCE the last such report (4999 -> 5001) is what the host gains.
        Assert.Equal(5002u, merge.Fold(5000, 2, 7, 5001));
    }

    [Fact]
    public void ANewGenerationIsANewBaseline()
    {
        // The peer's agent restarted: its first report under the new generation is a
        // baseline again, whatever it says.
        ExperienceMerge merge = new();
        merge.Fold(500, 2, 7, 5000);
        Assert.Equal(700u, merge.Fold(500, 2, 7, 5200));

        Assert.Equal(700u, merge.Fold(700, 2, 8, 9000));
        Assert.Equal(750u, merge.Fold(700, 2, 8, 9050));
    }

    [Fact]
    public void OneLowerReportIsAReorderAndIsIgnored()
    {
        ExperienceMerge merge = new();
        merge.Fold(500, 2, 7, 5000);
        merge.Fold(500, 2, 7, 5100);

        // A stale datagram from before the 5,100 one.
        Assert.Equal(600u, merge.Fold(600, 2, 7, 5000));

        // The next fresh report is measured against the ORIGINAL baseline, so the 100 the
        // reorder straddled is not credited a second time.
        Assert.Equal(600u, merge.Fold(600, 2, 7, 5100));
    }

    [Fact]
    public void TwoLowerReportsInARowAreAReloadAndRebaseline()
    {
        // A rejoin keeps the peer id and generation but loads a save from before the last
        // door; whatever it earns from THERE should carry.
        ExperienceMerge merge = new();
        merge.Fold(500, 2, 7, 5000);

        Assert.Equal(500u, merge.Fold(500, 2, 7, 4800));
        Assert.Equal(500u, merge.Fold(500, 2, 7, 4800));

        Assert.Equal(550u, merge.Fold(500, 2, 7, 4850));
    }

    [Fact]
    public void ThreePeersAreFoldedByMaxInOnePass()
    {
        ExperienceMerge merge = new();
        uint merged = 200;
        merged = merge.Fold(merged, 2, 7, 500);
        merged = merge.Fold(merged, 3, 9, 1200);
        merged = merge.Fold(merged, 4, 11, 800);
        Assert.Equal(200u, merged);

        // Next pass: peer 3 earned 30 alone, peer 4 earned 10 alone; the highest carries.
        merged = 200;
        merged = merge.Fold(merged, 2, 7, 500);
        merged = merge.Fold(merged, 3, 9, 1230);
        merged = merge.Fold(merged, 4, 11, 810);
        Assert.Equal(230u, merged);
    }

    [Fact]
    public void ACandidateSaturatesRatherThanWrapping()
    {
        ExperienceMerge merge = new();
        // The engine field is a signed int, so the ceiling is int.MaxValue.
        merge.Fold(int.MaxValue - 10, 2, 7, 100);

        Assert.Equal((uint)int.MaxValue, merge.Fold(int.MaxValue - 10, 2, 7, (uint)int.MaxValue));
    }

    [Fact]
    public void ANegativeExperienceIsBehindZeroNotAHugeMaximum()
    {
        // The engine's field is a signed int: -10 is 0xFFFFFFF6. Read as unsigned it beat every
        // real total (live 2026-09-30: a client at -10 pulled the host back to -10 after each load).
        ExperienceMerge merge = new();
        uint minusTen = unchecked((uint)-10);

        Assert.Equal(0u, merge.Fold(0, peerId: 2, generation: 7, peerExperience: minusTen));
        Assert.Equal(0u, merge.Fold(0, 2, 7, minusTen));
        // The peer earns 25 from -10 (now 15): that is +25 earned, so the host carries 25.
        Assert.Equal(25u, merge.Fold(0, 2, 7, 15));
    }

    [Fact]
    public void ANegativeHostIsRaisedByAPeerWithRealExperience()
    {
        ExperienceMerge merge = new();
        uint minusTwenty = unchecked((uint)-20);

        merge.Fold(minusTwenty, 2, 7, minusTwenty);
        // Peer at +5 after being level with the host at -20: earned 25, host -20 + 25 = 5.
        Assert.Equal(5u, merge.Fold(minusTwenty, 2, 7, 5));
    }
}
