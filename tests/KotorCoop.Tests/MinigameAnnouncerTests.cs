using KotorCoop.Agent;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The minigame toast: "Malak is in a minigame - waiting" / "Malak is back", posted on the
/// PlayerState.FlagInMinigame edge from the agent side — the plugin-side notice cannot fire
/// under the default minigame_hide_peer=1 (see MinigameAnnouncer's remarks).
/// </summary>
public class MinigameAnnouncerTests
{
    private static RemotePeer Peer(uint id, string name, bool racing) =>
        new(id, name, new PlayerState(
            Vector3f.Zero, 0f, "tar_m03af", 100, 0, Flags: racing ? PlayerState.FlagInMinigame : 0));

    [Fact]
    public void EnteringThenLeavingAnnouncesBothEdgesOnce()
    {
        MinigameAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", racing: false)], lines.Add);
        announcer.Observe([Peer(2, "Malak", racing: false)], lines.Add);
        announcer.Observe([Peer(2, "Malak", racing: true)], lines.Add);
        announcer.Observe([Peer(2, "Malak", racing: true)], lines.Add);
        announcer.Observe([Peer(2, "Malak", racing: false)], lines.Add);

        Assert.Equal(["Malak is in a minigame - waiting", "Malak is back"], lines);
    }

    [Fact]
    public void FirstSightWhileAlreadyRacingIsSilent()
    {
        MinigameAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", racing: true)], lines.Add);

        Assert.Empty(lines);
    }

    [Fact]
    public void LeavingTheRosterMidRaceSaysNothingAndForgetsThePeer()
    {
        MinigameAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", racing: false)], lines.Add);
        announcer.Observe([Peer(2, "Malak", racing: true)], lines.Add);
        announcer.Observe([], lines.Add);
        announcer.Observe([Peer(2, "Malak", racing: false)], lines.Add); // rejoined: baseline again

        Assert.Equal(["Malak is in a minigame - waiting"], lines);
    }

    [Fact]
    public void ResetForgetsEveryone()
    {
        MinigameAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe([Peer(2, "Malak", racing: true)], lines.Add);
        announcer.Reset();
        announcer.Observe([Peer(2, "Malak", racing: true)], lines.Add);
        announcer.Observe([Peer(2, "Malak", racing: false)], lines.Add);

        Assert.Equal(["Malak is back"], lines);
    }
}
