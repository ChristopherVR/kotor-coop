using KotorCoop.Agent;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The half of the compatibility feature the player can actually see. A degradation is
/// invisible by design — the session keeps running — so the only thing separating "we turned
/// enemy sync off for your safety" from "enemy sync is broken" is the line that says so.
/// These pin: it says it, it says it once, and it says it again when the channels come back.
/// </summary>
public class CompatibilityAnnouncerTests
{
    private static CompatibilityVerdict Verdict(
        SessionCapabilities allowed, params CompatibilityNote[] notes) =>
        new(RefusalReason.None, allowed, notes);

    private static CompatibilityNote GitNote(string module) => new(
        CompatibilityCode.ModuleGit,
        ContentCompatibility.ObjectIdKeyed,
        $"module {module} is modded differently on the two installs");

    [Fact]
    public void ADegradationIsAnnouncedOnceWithItsCode()
    {
        CompatibilityAnnouncer announcer = new();
        List<string> lines = [];

        CompatibilityVerdict verdict = Verdict(
            SessionCapabilities.All & ~ContentCompatibility.ObjectIdKeyed, GitNote("tar_m03aa"));

        announcer.Observe(verdict, lines.Add);
        announcer.Observe(verdict, lines.Add);
        announcer.Observe(verdict, lines.Add);

        string line = Assert.Single(lines);
        Assert.Contains("W_GIT", line, StringComparison.Ordinal);
        Assert.Contains("tar_m03aa", line, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDifficultyWarningIsRepeatableAfterTheSettingsAgreeAgain()
    {
        CompatibilityAnnouncer announcer = new();
        List<string> lines = [];
        CompatibilityVerdict warning = Verdict(SessionCapabilities.All,
            new CompatibilityNote(CompatibilityCode.Difficulty, SessionCapabilities.None,
                "live difficulty settings differ; PC and party damage scaling may differ, but co-op remains enabled"));

        announcer.Observe(warning, lines.Add);
        announcer.Observe(warning, lines.Add);
        Assert.Single(lines);
        Assert.Contains("W_DIFFICULTY", lines[0], StringComparison.Ordinal);

        announcer.Observe(Verdict(SessionCapabilities.All), lines.Add);
        lines.Clear();
        announcer.Observe(warning, lines.Add);

        Assert.Single(lines);
        Assert.Contains("W_DIFFICULTY", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelsComingBackIsNewsToo()
    {
        // Walking out of a mod-edited module into one both installs agree on. A player told
        // "enemies are off here" must be told when "here" ends, or they will believe the
        // session is still degraded for the rest of the night.
        CompatibilityAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(
            Verdict(SessionCapabilities.All & ~ContentCompatibility.ObjectIdKeyed, GitNote("tar_m03aa")),
            lines.Add);
        lines.Clear();

        announcer.Observe(Verdict(SessionCapabilities.All), lines.Add);

        string line = Assert.Single(lines);
        Assert.Contains("shared again", line, StringComparison.Ordinal);
        Assert.Contains("enemies", line, StringComparison.Ordinal);

        // And a second mod-edited module warns again, rather than staying silent because a
        // GIT mismatch was "already announced".
        lines.Clear();
        announcer.Observe(
            Verdict(SessionCapabilities.All & ~ContentCompatibility.ObjectIdKeyed, GitNote("tat_m17aa")),
            lines.Add);

        Assert.Contains(lines, l => l.Contains("tat_m17aa", StringComparison.Ordinal));
    }

    [Fact]
    public void ARefusalIsAnnouncedWithItsMachineReadableCode()
    {
        CompatibilityAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(new CompatibilityVerdict(
            RefusalReason.SaveGame, SessionCapabilities.None, []), lines.Add);

        Assert.Contains(lines, l => l.Contains("E_SAVE", StringComparison.Ordinal));
    }

    [Fact]
    public void NothingIsSaidWhenThereIsNothingToSay()
    {
        CompatibilityAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(null, lines.Add);
        announcer.Observe(Verdict(SessionCapabilities.All), lines.Add);

        Assert.Empty(lines);
    }

    [Fact]
    public void AReconnectReAnnouncesItsVerdict()
    {
        CompatibilityAnnouncer announcer = new();
        List<string> lines = [];

        CompatibilityVerdict verdict = Verdict(
            SessionCapabilities.All & ~ContentCompatibility.ObjectIdKeyed, GitNote("tar_m03aa"));

        announcer.Observe(verdict, lines.Add);
        announcer.Reset();
        lines.Clear();

        announcer.Observe(verdict, lines.Add);

        Assert.Single(lines);
    }

    [Fact]
    public void ChannelsAreNamedTheWayAPlayerThinksOfThem()
    {
        Assert.Equal(
            "enemies, doors, items and party",
            CompatibilityAnnouncer.Describe(ContentCompatibility.ObjectIdKeyed));

        Assert.Equal("enemies", CompatibilityAnnouncer.Describe(SessionCapabilities.Enemies));
        Assert.Equal("nothing", CompatibilityAnnouncer.Describe(SessionCapabilities.None));
    }
}
