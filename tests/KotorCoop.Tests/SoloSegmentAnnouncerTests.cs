using KotorCoop.Agent;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The solo-segment toast: "Malak is fighting alone - spectating" / "Malak's fight is over",
/// posted on the PlayerState.FlagSoloSegment edge — the same diff-per-tick shape
/// <see cref="MinigameAnnouncerTests"/> already pins for <see cref="MinigameAnnouncer"/>.
/// </summary>
public class SoloSegmentAnnouncerTests
{
    private static RemotePeer Peer(uint id, string name, bool inSegment) =>
        new(id, name, new PlayerState(
            Vector3f.Zero, 0f, "sta_m45ac", 100, 0, Flags: inSegment ? PlayerState.FlagSoloSegment : 0));

    [Fact]
    public void EnteringThenLeavingAnnouncesBothEdgesOnce()
    {
        SoloSegmentAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", inSegment: false)], lines.Add);
        announcer.Observe([Peer(2, "Malak", inSegment: false)], lines.Add);
        announcer.Observe([Peer(2, "Malak", inSegment: true)], lines.Add);
        announcer.Observe([Peer(2, "Malak", inSegment: true)], lines.Add);
        announcer.Observe([Peer(2, "Malak", inSegment: false)], lines.Add);

        Assert.Equal(["Malak is fighting alone - spectating", "Malak's fight is over"], lines);
    }

    [Fact]
    public void FirstSightWhileAlreadyInASegmentIsSilent()
    {
        SoloSegmentAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", inSegment: true)], lines.Add);

        Assert.Empty(lines);
    }

    [Fact]
    public void LeavingTheRosterMidSegmentSaysNothingAndForgetsThePeer()
    {
        SoloSegmentAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", inSegment: false)], lines.Add);
        announcer.Observe([Peer(2, "Malak", inSegment: true)], lines.Add);
        announcer.Observe([], lines.Add);
        announcer.Observe([Peer(2, "Malak", inSegment: false)], lines.Add); // rejoined: baseline again

        Assert.Equal(["Malak is fighting alone - spectating"], lines);
    }

    [Fact]
    public void ResetForgetsEveryone()
    {
        SoloSegmentAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", inSegment: true)], lines.Add);
        announcer.Reset();
        announcer.Observe([Peer(2, "Malak", inSegment: true)], lines.Add);
        announcer.Observe([Peer(2, "Malak", inSegment: false)], lines.Add);

        Assert.Equal(["Malak's fight is over"], lines);
    }
}
