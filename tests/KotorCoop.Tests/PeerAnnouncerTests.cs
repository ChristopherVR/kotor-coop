using KotorCoop.Agent;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The join/leave announcements. The host's missing half of a join: it admitted the peer,
/// its readout ticked to "peers 1", and nothing on its screen said a word.
/// </summary>
public class PeerAnnouncerTests
{
    private static RemotePeer Peer(uint id, string name) =>
        new(id, name, new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 100, 0));

    [Fact]
    public void AJoinIsAnnouncedOnceAndALeaveOnce()
    {
        PeerAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak")], lines.Add);
        announcer.Observe([Peer(2, "Malak")], lines.Add); // steady state: silence
        announcer.Observe([], lines.Add);
        announcer.Observe([], lines.Add);                 // still gone: silence

        Assert.Equal(["Malak joined the game", "Malak left the game"], lines);
    }

    [Fact]
    public void TwoPeersAnnounceIndependently()
    {
        PeerAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak")], lines.Add);
        announcer.Observe([Peer(2, "Malak"), Peer(3, "Bastila")], lines.Add);
        announcer.Observe([Peer(3, "Bastila")], lines.Add);

        Assert.Equal(
            ["Malak joined the game", "Bastila joined the game", "Malak left the game"],
            lines);
    }

    [Fact]
    public void ARejoinUnderANewIdIsARealJoin()
    {
        // The same player dropping and reconnecting gets a fresh peer id; that IS a join
        // worth telling the host about, not a duplicate to suppress.
        PeerAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak")], lines.Add);
        announcer.Observe([], lines.Add);
        announcer.Observe([Peer(4, "Malak")], lines.Add);

        Assert.Equal(
            ["Malak joined the game", "Malak left the game", "Malak joined the game"],
            lines);
    }

    [Fact]
    public void ResetForgetsEveryoneSoAFreshSessionReAnnounces()
    {
        PeerAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak")], lines.Add);
        announcer.Reset();
        announcer.Observe([Peer(2, "Malak")], lines.Add);

        Assert.Equal(["Malak joined the game", "Malak joined the game"], lines);
    }

    [Fact]
    public void TwoPlayersSharingANameAreTrackedByIdNotName()
    {
        PeerAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Revan"), Peer(3, "Revan")], lines.Add);
        announcer.Observe([Peer(3, "Revan")], lines.Add); // peer 2 left; peer 3 stays

        Assert.Equal(
            ["Revan joined the game", "Revan joined the game", "Revan left the game"],
            lines);
    }
}
