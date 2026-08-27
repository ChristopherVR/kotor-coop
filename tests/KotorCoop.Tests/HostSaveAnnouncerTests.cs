using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins the "host with no save yet is a silent no-op" fix: a host who presses Host before
/// ever saving should see a clear line explaining why nobody can join, and that line should
/// update itself the moment a save appears — no tick loop, no session, no filesystem.
/// </summary>
public class HostSaveAnnouncerTests
{
    // ---- the pure function: Line -------------------------------------------------

    [Fact]
    public void NoSaveProducesTheActionableExplanation()
    {
        string line = HostSaveAnnouncer.Line(hasSave: false, saveName: null);

        Assert.Equal(
            "co-op: no save to share yet - save the game once (or reach the first autosave), then host",
            line);
    }

    [Fact]
    public void HavingASaveNamesIt()
    {
        string line = HostSaveAnnouncer.Line(hasSave: true, saveName: "000001 - AUTOSAVE");

        Assert.Equal("co-op: now sharing '000001 - AUTOSAVE'", line);
    }

    // ---- diffing: Observe ----------------------------------------------------------

    [Fact]
    public void NoSaveIsAnnouncedOnceOnly()
    {
        HostSaveAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(false, null, lines.Add);
        announcer.Observe(false, null, lines.Add);
        announcer.Observe(false, null, lines.Add);

        Assert.Single(lines);
        Assert.Contains("no save to share yet", lines[0]);
    }

    [Fact]
    public void ASaveAppearingWhileHostingAnnouncesTheTransition()
    {
        HostSaveAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(false, null, lines.Add);
        announcer.Observe(true, "000001 - AUTOSAVE", lines.Add);

        Assert.Equal(
            [
                "co-op: no save to share yet - save the game once (or reach the first autosave), then host",
                "co-op: now sharing '000001 - AUTOSAVE'",
            ],
            lines);
    }

    [Fact]
    public void AlreadyHavingASaveAnnouncesOnceAndDoesNotRepeat()
    {
        HostSaveAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(true, "quicksave", lines.Add);
        announcer.Observe(true, "quicksave", lines.Add);

        Assert.Single(lines);
    }

    [Fact]
    public void ResetForgetsTheLastStateSoHostingAgainAnnouncesFresh()
    {
        HostSaveAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(true, "quicksave", lines.Add);
        announcer.Reset();
        announcer.Observe(true, "quicksave", lines.Add);

        // Without Reset the second Observe would be a no-op (identical to the last state seen).
        Assert.Equal(2, lines.Count);
    }
}
