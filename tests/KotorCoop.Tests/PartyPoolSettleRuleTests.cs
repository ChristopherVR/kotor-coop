using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins <see cref="PartyPoolSettleRule"/>: after a world replacement the pool's first sweep is
/// held until a second sweep agrees with it. The live round this pins is in the rule's own doc
/// comment (docs/research/shared-inventory-v2.md §17 — the host reload whose first sweep
/// carried two grenade stacks that were gone a second later).
/// </summary>
public class PartyPoolSettleRuleTests
{
    private static readonly PartyPoolStackKey Meds = new("g_i_medeqpmnt01", 0, 0, 0, 1);
    private static readonly PartyPoolStackKey Rifle = new("g_w_blstrrfl001", 0, 0, 0, 1);
    private static readonly PartyPoolStackKey IonGrenade = new("g_w_iongren01", 0, 0, 0, 1);

    private static Dictionary<PartyPoolStackKey, int> Pool(params (PartyPoolStackKey Key, int Count)[] stacks)
    {
        Dictionary<PartyPoolStackKey, int> pool = [];
        foreach ((PartyPoolStackKey key, int count) in stacks)
        {
            pool[key] = count;
        }

        return pool;
    }

    [Fact]
    public void BeforeAnyResetEverySweepIsAnObservation()
    {
        PartyPoolSettleRule rule = new();

        Assert.True(rule.Accept(Pool((Meds, 2)), out string note));
        Assert.Equal(string.Empty, note);
        Assert.False(rule.Settling);
    }

    /// <summary>The live shape: the transient first sweep is held, the steady sweep that
    /// differs from it is held too (it is now the one awaiting confirmation), and the sweep
    /// after that — identical — is the first observation of the new world.</summary>
    [Fact]
    public void ATransientFirstSweepIsNeverPublished()
    {
        PartyPoolSettleRule rule = new();
        rule.Reset();

        Assert.False(rule.Accept(Pool((Meds, 2), (Rifle, 1), (IonGrenade, 1)), out string first));
        Assert.Contains("first sweep", first);
        Assert.True(rule.Holding);

        Assert.False(rule.Accept(Pool((Meds, 2), (Rifle, 1)), out string second));
        Assert.Contains("still changing", second);

        Assert.True(rule.Accept(Pool((Meds, 2), (Rifle, 1)), out string third));
        Assert.Equal(string.Empty, third);
        Assert.False(rule.Settling);
        Assert.False(rule.Holding);
    }

    [Fact]
    public void ASteadyPoolIsAnObservationOnTheSecondSweep()
    {
        PartyPoolSettleRule rule = new();
        rule.Reset();

        Assert.False(rule.Accept(Pool((Meds, 2), (Rifle, 1)), out _));
        Assert.True(rule.Accept(Pool((Meds, 2), (Rifle, 1)), out _));
    }

    /// <summary>A changed COUNT on a key that was there before is movement, not agreement.</summary>
    [Fact]
    public void ADifferentCountForTheSameKeyIsNotAgreement()
    {
        PartyPoolSettleRule rule = new();
        rule.Reset();

        Assert.False(rule.Accept(Pool((Meds, 4)), out _));
        Assert.False(rule.Accept(Pool((Meds, 2)), out _));
        Assert.True(rule.Accept(Pool((Meds, 2)), out _));
    }

    /// <summary>An empty new world (nobody has used the stash) still needs two looks — the
    /// between-worlds "0 of everything" is exactly the reading that must not be believed on
    /// sight.</summary>
    [Fact]
    public void AnEmptyPoolAlsoSettlesInTwoSweeps()
    {
        PartyPoolSettleRule rule = new();
        rule.Reset();

        Assert.False(rule.Accept(Pool(), out _));
        Assert.True(rule.Accept(Pool(), out _));
    }

    [Fact]
    public void AResetMidSettleForgetsTheHeldSweep()
    {
        PartyPoolSettleRule rule = new();
        rule.Reset();
        Assert.False(rule.Accept(Pool((Meds, 2)), out _));

        rule.Reset();
        Assert.False(rule.Holding);
        Assert.False(rule.Accept(Pool((Meds, 2)), out string note));
        Assert.Contains("first sweep", note);
    }

    /// <summary>Once settled, a later change is ordinary movement and publishes at once.</summary>
    [Fact]
    public void OnceSettledLaterMovementPublishesImmediately()
    {
        PartyPoolSettleRule rule = new();
        rule.Reset();
        rule.Accept(Pool((Meds, 2)), out _);
        rule.Accept(Pool((Meds, 2)), out _);

        Assert.True(rule.Accept(Pool((Meds, 3)), out _));
    }
}
