using KotorCoop.Agent;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="BehaviourAnnouncer"/>: the W_CFG line every difference gets, the SECOND
/// "adopted host's X" line a host-authoritative difference also gets (the fix that replaced
/// <c>CoopClient.ApplyBehaviourComparison</c>'s <c>Console.Error.WriteLine</c>, which reached
/// neither <c>--log</c> nor the chat overlay), dedup per key, and <see cref="BehaviourAnnouncer.Reset"/>.
/// </summary>
public class BehaviourAnnouncerTests
{
    private static BehaviourDifference HostAuthoritative(string key, string hostValue = "1", string yourValue = "0") =>
        new(key, hostValue, yourValue, HostAuthoritative: true);

    private static BehaviourDifference WarnOnly(string key, string hostValue = "1", string yourValue = "0") =>
        new(key, hostValue, yourValue, HostAuthoritative: false);

    [Fact]
    public void NullResultAnnouncesNothing()
    {
        BehaviourAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(null, lines.Add);

        Assert.Empty(lines);
    }

    [Fact]
    public void AWarnOnlyDifferenceGetsOnlyTheWCfgLine()
    {
        BehaviourAnnouncer announcer = new();
        List<string> lines = [];
        BehaviourComparisonResult result = new([WarnOnly("local_combat")]);

        announcer.Observe(result, lines.Add);

        string line = Assert.Single(lines);
        Assert.StartsWith("co-op: Your co-op setting 'local_combat' differs", line);
    }

    [Fact]
    public void AHostAuthoritativeDifferenceGetsBothLines()
    {
        BehaviourAnnouncer announcer = new();
        List<string> lines = [];
        BehaviourComparisonResult result = new([HostAuthoritative("effect_sync", hostValue: "1", yourValue: "0")]);

        announcer.Observe(result, lines.Add);

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("co-op: Your co-op setting 'effect_sync' differs", lines[0]);
        Assert.Equal("co-op: adopted host's effect_sync (on; local setting was off)", lines[1]);
    }

    [Fact]
    public void TheAdoptedLineReadsTheValuesTheRightWayRound()
    {
        BehaviourAnnouncer announcer = new();
        List<string> lines = [];
        BehaviourComparisonResult result = new([HostAuthoritative("trigger_relay", hostValue: "0", yourValue: "1")]);

        announcer.Observe(result, lines.Add);

        Assert.Equal("co-op: adopted host's trigger_relay (off; local setting was on)", lines[1]);
    }

    [Fact]
    public void EachKeyAnnouncesOnlyOnce()
    {
        BehaviourAnnouncer announcer = new();
        List<string> lines = [];
        BehaviourComparisonResult result = new([HostAuthoritative("effect_sync")]);

        announcer.Observe(result, lines.Add);
        announcer.Observe(result, lines.Add);

        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void ResetAllowsTheSameKeyToAnnounceAgain()
    {
        BehaviourAnnouncer announcer = new();
        List<string> lines = [];
        BehaviourComparisonResult result = new([HostAuthoritative("effect_sync")]);

        announcer.Observe(result, lines.Add);
        announcer.Reset();
        announcer.Observe(result, lines.Add);

        Assert.Equal(4, lines.Count);
    }

    [Fact]
    public void MultipleDifferencesEachGetTheirOwnLines()
    {
        BehaviourAnnouncer announcer = new();
        List<string> lines = [];
        BehaviourComparisonResult result = new([
            WarnOnly("local_combat"),
            HostAuthoritative("effect_sync"),
            HostAuthoritative("trigger_relay"),
        ]);

        announcer.Observe(result, lines.Add);

        // 1 line for the warn-only key, 2 each for the two host-authoritative keys.
        Assert.Equal(5, lines.Count);
        Assert.Contains(lines, l => l == "co-op: adopted host's effect_sync (on; local setting was off)");
        Assert.Contains(lines, l => l == "co-op: adopted host's trigger_relay (on; local setting was off)");
    }
}
