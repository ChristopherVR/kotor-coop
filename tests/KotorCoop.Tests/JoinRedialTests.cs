using KotorCoop.Agent;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>A command-line join keeps dialing a host that is still loading (2026-10-10).</summary>
public sealed class JoinRedialTests
{
    [Fact]
    public void AnUnansweredDialIsRetriedAfterThePause()
    {
        JoinRedial rule = new();
        Assert.False(rule.ShouldRedial(SessionPhase.Idle, 0));        // first dial not picked up yet
        Assert.False(rule.ShouldRedial(SessionPhase.Connecting, 100));
        Assert.False(rule.ShouldRedial(SessionPhase.Idle, 15_100));   // "no reply"
        Assert.False(rule.ShouldRedial(SessionPhase.Idle, 15_100 + JoinRedial.PauseMs - 1));
        Assert.True(rule.ShouldRedial(SessionPhase.Idle, 15_100 + JoinRedial.PauseMs));
        Assert.Equal(1, rule.Redials);
    }

    [Fact]
    public void ItStopsOnceConnected()
    {
        JoinRedial rule = new();
        rule.ShouldRedial(SessionPhase.Connecting, 0);
        rule.ShouldRedial(SessionPhase.Connected, 1000);
        Assert.False(rule.ShouldRedial(SessionPhase.Idle, 60_000));
    }

    [Theory]
    [InlineData(SessionPhase.Failed)]
    [InlineData(SessionPhase.Disconnected)]
    public void ARefusalOrADisconnectEndsIt(SessionPhase phase)
    {
        JoinRedial rule = new();
        rule.ShouldRedial(SessionPhase.Connecting, 0);
        rule.ShouldRedial(phase, 1000);
        Assert.False(rule.ShouldRedial(SessionPhase.Idle, 60_000));
    }

    [Fact]
    public void ItGivesUpAfterTheLastRedial()
    {
        JoinRedial rule = new();
        long now = 0;
        int dials = 0;
        for (int i = 0; i < JoinRedial.MaxRedials + 5; i++)
        {
            rule.ShouldRedial(SessionPhase.Connecting, now);
            now += 15_000;
            rule.ShouldRedial(SessionPhase.Idle, now);
            now += JoinRedial.PauseMs;
            if (rule.ShouldRedial(SessionPhase.Idle, now))
            {
                dials++;
            }
        }

        Assert.Equal(JoinRedial.MaxRedials, dials);
    }
}
