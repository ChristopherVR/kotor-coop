using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Which engine floor gets the blame when a player who should be dead is still walking.
///
/// The reason travels to the agent as a number and ends up in front of a human, so the two
/// things that must never be wrong are the ORDER (report the floor furthest upstream, the
/// one that would have to go first) and the WIRE VALUES (renumbering them silently
/// mistranslates every future log). Both are pinned here.
///
/// The engine half — reading +0x200, +0xF8 and the difficulty table — is only measurable
/// live; see docs/research/min-one-hitpoint.md.
/// </summary>
public class DownBlockTests
{
    [Fact]
    public void NothingSetMeansNothingIsStoppingIt()
    {
        Assert.Equal(DownBlockReason.None, DownBlock.Classify(false, false, false));
    }

    [Fact]
    public void MinOneHitPointIsTheEndarSpireCase()
    {
        // The measured symptom: hit points fall to exactly 1 and the downed flag never sets.
        Assert.Equal(
            DownBlockReason.MinOneHitPointFlag,
            DownBlock.Classify(plot: false, difficultyMinHitPoint1: false, minOneHitPoint: true));
    }

    [Fact]
    public void PlotOutranksTheOthers()
    {
        // Plot zeroes the damage before anything downstream sees it, so it is the floor a
        // player would have to remove first — and it looks different in play (hit points do
        // not move at all), which is why blaming min-1-hp here would send the next session
        // hunting the wrong flag.
        Assert.Equal(
            DownBlockReason.PlotFlag,
            DownBlock.Classify(plot: true, difficultyMinHitPoint1: true, minOneHitPoint: true));
    }

    [Fact]
    public void DifficultyRuleOutranksTheCreatureFlag()
    {
        Assert.Equal(
            DownBlockReason.DifficultyMinHitPoint1,
            DownBlock.Classify(plot: false, difficultyMinHitPoint1: true, minOneHitPoint: true));
    }

    [Fact]
    public void WireValuesAreFixed()
    {
        Assert.Equal(0u, (uint)DownBlockReason.None);
        Assert.Equal(1u, (uint)DownBlockReason.MinOneHitPointFlag);
        Assert.Equal(2u, (uint)DownBlockReason.PlotFlag);
        Assert.Equal(3u, (uint)DownBlockReason.DifficultyMinHitPoint1);
    }

    [Fact]
    public void EveryReasonExplainsItself()
    {
        foreach (DownBlockReason reason in Enum.GetValues<DownBlockReason>())
        {
            Assert.False(string.IsNullOrWhiteSpace(DownBlock.Describe(reason)));
        }

        // The one a session will actually hit should say where it comes from, or the log
        // line is just as unhelpful as the silence it replaced.
        Assert.Contains("Endar Spire", DownBlock.Describe(DownBlockReason.MinOneHitPointFlag));
    }
}
