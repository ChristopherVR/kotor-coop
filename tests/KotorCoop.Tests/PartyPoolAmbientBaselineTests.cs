using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins <see cref="PartyPoolAmbientBaseline"/> — how a suppressed ambient item is kept out of the
/// totals <c>KotorGameBridge.TryScanPartyPoolTotals</c> reports.
///
/// <see cref="PartyPoolAmbientSuppressionTests"/> pins the DECISION (is this item ambient?); this
/// pins what is done with the answer, which is where three live rig rounds' worth of phantom
/// party-pool movement actually came from. The bridge used to SKIP a suppressed item, which is
/// only sound while an item id keeps its own separate stack forever — and the engine merges
/// same-key stacks, destroying either id. Every test below is a merge the old skip-based
/// aggregation would have reported as engine movement that never happened.
/// See docs/research/shared-inventory-v2.md §12.
/// </summary>
public class PartyPoolAmbientBaselineTests
{
    private static readonly PartyPoolStackKey Medpac =
        new("g_i_medeqpmnt01", 0, 0, 0, ItemIdentity.FlagKnown);

    private static readonly PartyPoolStackKey Blaster =
        new("g_w_blstrpstl001", 0, 0, 0, ItemIdentity.FlagKnown);

    private static Dictionary<PartyPoolStackKey, int> Raw(params (PartyPoolStackKey Key, int Count)[] items)
    {
        Dictionary<PartyPoolStackKey, int> raw = [];
        foreach ((PartyPoolStackKey key, int count) in items)
        {
            raw[key] = count;
        }

        return raw;
    }

    [Fact]
    public void AnUnsuppressedKeyIsReportedExactlyAsScanned()
    {
        PartyPoolAmbientBaseline baseline = new();

        Assert.Equal(4, baseline.Net(Raw((Medpac, 4))).GetValueOrDefault(Medpac));
    }

    [Fact]
    public void AKeyThatIsEntirelyAmbientIsAbsentFromTheTotals()
    {
        PartyPoolAmbientBaseline baseline = new();
        baseline.NoteSuppressed(0x401, Medpac, 2);

        Assert.False(baseline.Net(Raw((Medpac, 2))).ContainsKey(Medpac));
    }

    /// <summary>The live-observed "local add of 6x nobody asked for" (§10.1) and §6.2's own
    /// doubling ramp on <c>g_w_blstrpstl001</c>/<c>g_a_clothes01</c>. A suppressed ambient stack
    /// merges INTO a counted one — one object id remains, holding both — and the skip-based
    /// aggregation reported the counted total jumping by the whole ambient amount.</summary>
    [Fact]
    public void AnAmbientStackMergingIntoACountedOneIsNotReportedAsAJump()
    {
        PartyPoolAmbientBaseline baseline = new();

        // Tick 1: two separate stacks of the same key — 6 ambient, 3 genuinely the player's.
        baseline.NoteSuppressed(0x401, Medpac, 6);
        Assert.Equal(3, baseline.Net(Raw((Medpac, 9))).GetValueOrDefault(Medpac));

        // Tick 2: the engine merged them. One id now holds all 9; the ambient id is gone, so a
        // skip-based aggregation counts the whole 9 and reports +6 of movement.
        Assert.Equal(3, baseline.Net(Raw((Medpac, 9))).GetValueOrDefault(Medpac));
    }

    /// <summary>The mirror image (§10.1's "remove of 4x ... no matching stack found"): the
    /// COUNTED stack merges into the suppressed one, so a skip-based aggregation loses the
    /// player's own units entirely and reports a phantom removal.</summary>
    [Fact]
    public void ACountedStackMergingIntoAnAmbientOneIsNotReportedAsALoss()
    {
        PartyPoolAmbientBaseline baseline = new();
        baseline.NoteSuppressed(0x401, Medpac, 4);

        Assert.Equal(2, baseline.Net(Raw((Medpac, 6))).GetValueOrDefault(Medpac));
        Assert.Equal(2, baseline.Net(Raw((Medpac, 6))).GetValueOrDefault(Medpac));
    }

    [Fact]
    public void AnAmbientItemContributesToTheBaselineOnceHoweverManyScansSeeIt()
    {
        PartyPoolAmbientBaseline baseline = new();

        Assert.True(baseline.NoteSuppressed(0x401, Medpac, 2));
        Assert.False(baseline.NoteSuppressed(0x401, Medpac, 2));
        Assert.False(baseline.NoteSuppressed(0x401, Medpac, 2));

        Assert.Equal(3, baseline.Net(Raw((Medpac, 5))).GetValueOrDefault(Medpac));
    }

    /// <summary>Consuming ambient content must shrink the offset with it — otherwise a used-up
    /// ambient medpac silently eats a genuine deposit of the same resref later in the
    /// session.</summary>
    [Fact]
    public void ConsumingAmbientContentShrinksTheOffsetRatherThanEatingALaterDeposit()
    {
        PartyPoolAmbientBaseline baseline = new();
        baseline.NoteSuppressed(0x401, Medpac, 3);

        Assert.False(baseline.Net(Raw((Medpac, 3))).ContainsKey(Medpac));

        // The player uses two of the three ambient medpacs.
        Assert.False(baseline.Net(Raw((Medpac, 1))).ContainsKey(Medpac));

        // ...and later genuinely deposits four of their own.
        Assert.Equal(4, baseline.Net(Raw((Medpac, 5))).GetValueOrDefault(Medpac));
    }

    [Fact]
    public void AKeyGoneFromThePoolEntirelyDropsItsOffsetOutright()
    {
        PartyPoolAmbientBaseline baseline = new();
        baseline.NoteSuppressed(0x401, Medpac, 3);

        Assert.Empty(baseline.Net(Raw()));

        Assert.Equal(2, baseline.Net(Raw((Medpac, 2))).GetValueOrDefault(Medpac));
    }

    [Fact]
    public void OffsetsAreIndependentPerKey()
    {
        PartyPoolAmbientBaseline baseline = new();
        baseline.NoteSuppressed(0x401, Medpac, 2);
        baseline.NoteSuppressed(0x402, Blaster, 1);

        Dictionary<PartyPoolStackKey, int> net = baseline.Net(Raw((Medpac, 5), (Blaster, 1)));

        Assert.Equal(3, net.GetValueOrDefault(Medpac));
        Assert.False(net.ContainsKey(Blaster));
    }

    [Fact]
    public void ClearDropsEveryOffset()
    {
        PartyPoolAmbientBaseline baseline = new();
        baseline.NoteSuppressed(0x401, Medpac, 2);
        baseline.Clear();

        Assert.Equal(5, baseline.Net(Raw((Medpac, 5))).GetValueOrDefault(Medpac));
    }
}
