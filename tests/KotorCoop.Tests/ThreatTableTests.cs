using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="ThreatTable"/> is the pure accumulation half of THREAT_RETARGET
/// (docs/research/peer-damage-ledger.md §9) — per-creature, per-attacker damage with linear
/// decay to zero over a fixed window. No engine involvement at all; every test below drives
/// the clock explicitly rather than relying on wall time.
/// </summary>
public sealed class ThreatTableTests
{
    [Fact]
    public void AccumulatesPerAttackerWithinTheSameInstant()
    {
        ThreatTable table = new(windowSeconds: 10);

        table.AddThreat(0x10, 0x100, 4, nowSeconds: 0);
        table.AddThreat(0x10, 0x100, 6, nowSeconds: 0);
        table.AddThreat(0x10, 0x200, 3, nowSeconds: 0);

        Assert.Equal(10, table.GetThreat(0x10, 0x100, nowSeconds: 0));
        Assert.Equal(3, table.GetThreat(0x10, 0x200, nowSeconds: 0));
    }

    [Fact]
    public void KeepsSeparateCreaturesSeparate()
    {
        ThreatTable table = new(windowSeconds: 10);

        table.AddThreat(0x10, 0x100, 20, nowSeconds: 0);
        table.AddThreat(0x20, 0x100, 5, nowSeconds: 0);

        Assert.Equal(20, table.GetThreat(0x10, 0x100, nowSeconds: 0));
        Assert.Equal(5, table.GetThreat(0x20, 0x100, nowSeconds: 0));
    }

    [Theory]
    [InlineData(0u, 0x100u, 5)]
    [InlineData(0x10u, 0u, 5)]
    [InlineData(0x10u, 0x100u, 0)]
    [InlineData(0x10u, 0x100u, -5)]
    public void ZeroIdOrNonPositiveAmountIsIgnored(uint creatureId, uint attackerId, double amount)
    {
        ThreatTable table = new(windowSeconds: 10);

        table.AddThreat(creatureId, attackerId, amount, nowSeconds: 0);

        Assert.Equal(0, table.GetThreat(creatureId, attackerId, nowSeconds: 0));
    }

    [Fact]
    public void ThreatDecaysLinearlyOverTheWindow()
    {
        ThreatTable table = new(windowSeconds: 10);

        table.AddThreat(0x10, 0x100, 100, nowSeconds: 0);

        // Halfway through a 10s window, half the threat should remain.
        Assert.Equal(50, table.GetThreat(0x10, 0x100, nowSeconds: 5));
    }

    [Fact]
    public void ThreatIsFullyGoneOncePastTheWindow()
    {
        ThreatTable table = new(windowSeconds: 10);

        table.AddThreat(0x10, 0x100, 100, nowSeconds: 0);

        Assert.Equal(0, table.GetThreat(0x10, 0x100, nowSeconds: 10));
        Assert.Equal(0, table.GetThreat(0x10, 0x100, nowSeconds: 999));
    }

    [Fact]
    public void ANewHitResetsTheDecayClockOnTopOfWhatRemains()
    {
        ThreatTable table = new(windowSeconds: 10);

        table.AddThreat(0x10, 0x100, 100, nowSeconds: 0);
        // 5s later, 50 remains; a fresh 20 lands on top of it, and the clock restarts from
        // this new instant.
        table.AddThreat(0x10, 0x100, 20, nowSeconds: 5);

        Assert.Equal(70, table.GetThreat(0x10, 0x100, nowSeconds: 5));
        // 5s further on (10s after the first hit, but only 5s after the second), the 70
        // should have decayed by half again.
        Assert.Equal(35, table.GetThreat(0x10, 0x100, nowSeconds: 10));
    }

    [Fact]
    public void TryGetTopAttackerReturnsTheHighestCurrentThreat()
    {
        ThreatTable table = new(windowSeconds: 10);

        table.AddThreat(0x10, 0x100, 30, nowSeconds: 0);
        table.AddThreat(0x10, 0x200, 90, nowSeconds: 0);

        Assert.True(table.TryGetTopAttacker(0x10, nowSeconds: 0, out uint topId, out double topThreat));
        Assert.Equal(0x200u, topId);
        Assert.Equal(90, topThreat);
    }

    [Fact]
    public void TryGetTopAttackerAccountsForDifferentDecayAges()
    {
        ThreatTable table = new(windowSeconds: 10);

        // Attacker A hit hard a while ago; attacker B hit lighter but very recently. By the
        // time both are sampled, A's older hit should have decayed below B's fresher one.
        table.AddThreat(0x10, 0xA, 100, nowSeconds: 0);
        table.AddThreat(0x10, 0xB, 40, nowSeconds: 8);

        Assert.True(table.TryGetTopAttacker(0x10, nowSeconds: 8, out uint topId, out double topThreat));
        Assert.Equal(0xBu, topId); // A: 100 * (1 - 8/10) = 20; B: 40 fresh
        Assert.Equal(40, topThreat);
    }

    [Fact]
    public void TryGetTopAttackerIsFalseForAnUntrackedCreature()
    {
        ThreatTable table = new(windowSeconds: 10);

        Assert.False(table.TryGetTopAttacker(0x10, nowSeconds: 0, out uint id, out double threat));
        Assert.Equal(0u, id);
        Assert.Equal(0, threat);
    }

    [Fact]
    public void SecondsSinceLastRetargetIsInfiniteBeforeAnyRetarget()
    {
        ThreatTable table = new(windowSeconds: 10);

        Assert.Equal(double.PositiveInfinity, table.SecondsSinceLastRetarget(0x10, nowSeconds: 100));
    }

    [Fact]
    public void MarkRetargetedArmsTheCooldownClock()
    {
        ThreatTable table = new(windowSeconds: 10);

        table.MarkRetargeted(0x10, nowSeconds: 5);

        Assert.Equal(3, table.SecondsSinceLastRetarget(0x10, nowSeconds: 8));
    }

    [Fact]
    public void ForgetDropsOnlyTheNamedCreature()
    {
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(0x10, 0x100, 50, nowSeconds: 0);
        table.AddThreat(0x20, 0x100, 50, nowSeconds: 0);
        table.MarkRetargeted(0x10, nowSeconds: 0);

        table.Forget(0x10);

        Assert.Equal(0, table.GetThreat(0x10, 0x100, nowSeconds: 0));
        Assert.Equal(50, table.GetThreat(0x20, 0x100, nowSeconds: 0));
        Assert.Equal(double.PositiveInfinity, table.SecondsSinceLastRetarget(0x10, nowSeconds: 0));
    }

    [Fact]
    public void ClearDropsEverything()
    {
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(0x10, 0x100, 50, nowSeconds: 0);
        table.MarkRetargeted(0x10, nowSeconds: 0);

        table.Clear();

        Assert.Equal(0, table.GetThreat(0x10, 0x100, nowSeconds: 0));
        Assert.Equal(double.PositiveInfinity, table.SecondsSinceLastRetarget(0x10, nowSeconds: 0));
    }
}
