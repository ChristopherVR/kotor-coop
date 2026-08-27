using KotorCoop.Agent;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The away toast: "Malak left the world (main menu / loading)" / "Malak is back", posted on
/// the PlayerState.FlagAway edge — a separate diff from PeerAnnouncer's join/leave (see
/// AwayAnnouncer's remarks for why a presence change and a roster change answer different
/// questions).
/// </summary>
public class AwayAnnouncerTests
{
    private static RemotePeer Peer(uint id, string name, bool away) =>
        new(id, name, new PlayerState(
            Vector3f.Zero, 0f, "end_m01aa", 100, 0, Flags: away ? PlayerState.FlagAway : 0));

    [Fact]
    public void GoingAwayThenComingBackAnnouncesBothEdgesOnce()
    {
        AwayAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", away: false)], lines.Add); // baseline: silent
        announcer.Observe([Peer(2, "Malak", away: false)], lines.Add); // steady state: silent
        announcer.Observe([Peer(2, "Malak", away: true)], lines.Add);
        announcer.Observe([Peer(2, "Malak", away: true)], lines.Add);  // steady state: silent
        announcer.Observe([Peer(2, "Malak", away: false)], lines.Add);

        Assert.Equal(
            ["Malak left the world (main menu / loading)", "Malak is back"],
            lines);
    }

    [Fact]
    public void APeerAlreadyAwayOnFirstObservationAnnouncesNothing()
    {
        // No edge exists yet to fire on — a player who joins already at the main menu (or
        // whose first snapshot simply arrives after the away flag latched) must not toast on
        // tick one for no transition at all.
        AwayAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", away: true)], lines.Add);
        announcer.Observe([Peer(2, "Malak", away: true)], lines.Add);

        Assert.Empty(lines);
    }

    [Fact]
    public void APeerLeavingTheRosterWhileAwayAnnouncesNothingOfItsOwn()
    {
        // PeerAnnouncer already says "Malak left the game"; this channel firing "is back" on
        // the way out would read backwards.
        AwayAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", away: false)], lines.Add);
        announcer.Observe([Peer(2, "Malak", away: true)], lines.Add);
        announcer.Observe([], lines.Add);

        Assert.Equal(["Malak left the world (main menu / loading)"], lines);
    }

    [Fact]
    public void ARejoinUnderANewIdStartsFreshRatherThanInheritingTheOldHoldersAwayState()
    {
        AwayAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", away: true)], lines.Add);
        announcer.Observe([], lines.Add);
        announcer.Observe([Peer(4, "Malak", away: true)], lines.Add); // fresh id: baseline, silent

        Assert.Empty(lines);
    }

    [Fact]
    public void TwoPeersAreTrackedIndependently()
    {
        AwayAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", away: false), Peer(3, "Bastila", away: false)], lines.Add);
        announcer.Observe([Peer(2, "Malak", away: true), Peer(3, "Bastila", away: false)], lines.Add);
        announcer.Observe([Peer(2, "Malak", away: true), Peer(3, "Bastila", away: true)], lines.Add);

        Assert.Equal(
            [
                "Malak left the world (main menu / loading)",
                "Bastila left the world (main menu / loading)",
            ],
            lines);
    }

    [Fact]
    public void ResetForgetsEveryoneSoAFreshSessionTracksFromScratch()
    {
        AwayAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", away: false)], lines.Add);
        announcer.Observe([Peer(2, "Malak", away: true)], lines.Add);
        announcer.Reset();

        // Post-reset, the very next observation is a fresh baseline — even though the peer is
        // still away, there is no remembered prior state to edge away from.
        announcer.Observe([Peer(2, "Malak", away: true)], lines.Add);

        Assert.Equal(["Malak left the world (main menu / loading)"], lines);
    }
}
