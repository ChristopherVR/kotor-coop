using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="PhantomAttackerMerge"/> combines several senders' independently-reported
/// per-attacker breakdowns for one target — the session-layer half of per-attacker
/// attribution (CoopHost/CoopClient's own <c>GetIncomingPhantomAttackers</c>).
/// </summary>
public class PhantomAttackerMergeTests
{
    [Fact]
    public void SumsTheSameAttackerAcrossSenders()
    {
        IReadOnlyList<PhantomAttackerEntry>[] reports =
        [
            [new PhantomAttackerEntry(0x10, 30)],
            [new PhantomAttackerEntry(0x10, 20)],
        ];

        IReadOnlyList<PhantomAttackerEntry> merged = PhantomAttackerMerge.Merge(reports, capacity: 4);

        Assert.Single(merged);
        Assert.Equal(0x10u, merged[0].AttackerId);
        Assert.Equal(50u, merged[0].CumulativeDamage);
    }

    [Fact]
    public void KeepsDistinctAttackersSeparate()
    {
        IReadOnlyList<PhantomAttackerEntry>[] reports =
        [
            [new PhantomAttackerEntry(0x10, 30), new PhantomAttackerEntry(0x20, 5)],
            [new PhantomAttackerEntry(0x30, 8)],
        ];

        IReadOnlyList<PhantomAttackerEntry> merged = PhantomAttackerMerge.Merge(reports, capacity: 4);

        Dictionary<uint, uint> byId = merged.ToDictionary(e => e.AttackerId, e => e.CumulativeDamage);
        Assert.Equal(30u, byId[0x10]);
        Assert.Equal(5u, byId[0x20]);
        Assert.Equal(8u, byId[0x30]);
    }

    [Fact]
    public void ZeroIdEntriesAreIgnored()
    {
        IReadOnlyList<PhantomAttackerEntry>[] reports = [[new PhantomAttackerEntry(0, 999)]];

        IReadOnlyList<PhantomAttackerEntry> merged = PhantomAttackerMerge.Merge(reports, capacity: 4);

        Assert.Empty(merged);
    }

    [Fact]
    public void CapsAtCapacityByTakingTheBiggestTotals()
    {
        IReadOnlyList<PhantomAttackerEntry>[] reports =
        [
            [
                new PhantomAttackerEntry(0x10, 10),
                new PhantomAttackerEntry(0x20, 40),
                new PhantomAttackerEntry(0x30, 5),
                new PhantomAttackerEntry(0x40, 30),
                new PhantomAttackerEntry(0x50, 20),
            ],
        ];

        IReadOnlyList<PhantomAttackerEntry> merged = PhantomAttackerMerge.Merge(reports, capacity: 4);

        Assert.Equal(4, merged.Count);
        Assert.DoesNotContain(merged, e => e.AttackerId == 0x30); // smallest — dropped
    }

    [Fact]
    public void EmptyInputProducesEmptyOutput()
    {
        IReadOnlyList<PhantomAttackerEntry> merged =
            PhantomAttackerMerge.Merge([], capacity: 4);

        Assert.Empty(merged);
    }
}
