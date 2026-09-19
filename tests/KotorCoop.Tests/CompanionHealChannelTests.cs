using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The two pure halves of the companion HEAL channel (docs/research/companion-state-sync.md
/// §13.6, 2026-09-18) that are not the ledger: the client's hit-point sample
/// (<see cref="CompanionHealSampleRule"/>) and the host's rotating owed window
/// (<see cref="CompanionDamageOwedWindow"/>). <see cref="CompanionDamageLedgerTests"/> covers
/// the banking in between.
/// </summary>
public class CompanionHealChannelTests
{
    private static CompanionHealSampleRule.Sample S(uint id, int hp, uint serial) => new(id, hp, serial);

    [Fact]
    public void ARiseWithNoHitInTheWindowIsAHeal()
    {
        CompanionHealSampleRule.Verdict verdict =
            CompanionHealSampleRule.Observe(S(0x46, 20, 3), S(0x46, 45, 3), out int heal);

        Assert.Equal(CompanionHealSampleRule.Verdict.Healed, verdict);
        Assert.Equal(25, heal);
    }

    [Fact]
    public void TheFirstSampleOfABodyIsOnlyABaseline()
    {
        Assert.Equal(
            CompanionHealSampleRule.Verdict.Baseline,
            CompanionHealSampleRule.Observe(default, S(0x46, 45, 0), out int heal));
        Assert.Equal(0, heal);
    }

    [Fact]
    public void ADifferentBodyIsANewBaselineNotAHeal()
    {
        // The slot was reclaimed and re-released with another companion (or the same one
        // resolved fresh after a transition): its hit points are nobody's medpac.
        Assert.Equal(
            CompanionHealSampleRule.Verdict.Baseline,
            CompanionHealSampleRule.Observe(S(0x46, 5, 0), S(0x47, 80, 0), out int heal));
        Assert.Equal(0, heal);
    }

    [Fact]
    public void AWindowWithAHitInItIsNeverCredited()
    {
        // Hit points went 30 -> 50 but the damage serial moved: a hit and a heal in one
        // window cannot be split, and overkill on a low body would read as a heal.
        Assert.Equal(
            CompanionHealSampleRule.Verdict.Baseline,
            CompanionHealSampleRule.Observe(S(0x46, 30, 3), S(0x46, 50, 4), out int heal));
        Assert.Equal(0, heal);
    }

    [Fact]
    public void ComingBackFromZeroIsTheEnginesRecoveryNotAHeal()
    {
        Assert.Equal(
            CompanionHealSampleRule.Verdict.Baseline,
            CompanionHealSampleRule.Observe(S(0x46, -3, 3), S(0x46, 1, 3), out _));
        Assert.Equal(
            CompanionHealSampleRule.Verdict.Baseline,
            CompanionHealSampleRule.Observe(S(0x46, 0, 3), S(0x46, 40, 3), out _));
    }

    [Fact]
    public void AFallOrNoChangeIsNothing()
    {
        Assert.Equal(
            CompanionHealSampleRule.Verdict.Baseline,
            CompanionHealSampleRule.Observe(S(0x46, 40, 3), S(0x46, 40, 3), out _));
        Assert.Equal(
            CompanionHealSampleRule.Verdict.Baseline,
            CompanionHealSampleRule.Observe(S(0x46, 40, 3), S(0x46, 12, 3), out _));
    }

    [Fact]
    public void ARiseNoHealCouldProduceIsRefused()
    {
        Assert.Equal(
            CompanionHealSampleRule.Verdict.Refused,
            CompanionHealSampleRule.Observe(S(0x46, 10, 3), S(0x46, 10 + CompanionHealSampleRule.MaxPlausibleHeal + 1, 3), out int heal));
        Assert.Equal(0, heal);

        Assert.Equal(
            CompanionHealSampleRule.Verdict.Healed,
            CompanionHealSampleRule.Observe(S(0x46, 10, 3), S(0x46, 10 + CompanionHealSampleRule.MaxPlausibleHeal, 3), out heal));
        Assert.Equal(CompanionHealSampleRule.MaxPlausibleHeal, heal);
    }

    // ---- the host's owed window ------------------------------------------------------

    private static List<CompanionDamageEntry> Table(int rows)
    {
        List<CompanionDamageEntry> table = [];
        for (int i = 0; i < rows; i++)
        {
            table.Add(new CompanionDamageEntry((uint)(i + 1), (uint)(10 * (i + 1))));
        }

        return table;
    }

    [Fact]
    public void ATableThatFitsIsPublishedWholeAndTheCursorStaysAtZero()
    {
        int start = 0;
        IReadOnlyList<CompanionDamageEntry> table = Table(9);

        Assert.Same(table, CompanionDamageOwedWindow.Slice(table, ProtocolConstants.PartyRosterSlots, ref start));
        Assert.Equal(0, start);
    }

    [Fact]
    public void ATableTooBigForTheBlockIsSeenWholeAcrossPasses()
    {
        // Nine damage rows and nine heal rows: every row is published within two passes.
        int start = 0;
        List<CompanionDamageEntry> table = Table(9);
        for (int i = 0; i < 9; i++)
        {
            table.Add(new CompanionDamageEntry(CompanionDamageSlots.HealSlot((uint)(i + 1)), 5));
        }

        HashSet<uint> seen = [];
        for (int pass = 0; pass < 2; pass++)
        {
            IReadOnlyList<CompanionDamageEntry> window = CompanionDamageOwedWindow.Slice(table, 9, ref start);
            Assert.Equal(9, window.Count);
            foreach (CompanionDamageEntry entry in window)
            {
                seen.Add(entry.RosterSlotPlusOne);
            }
        }

        Assert.Equal(18, seen.Count);
        Assert.Equal(0, start);
    }

    [Fact]
    public void AnOddSizedTableWrapsRatherThanStalling()
    {
        int start = 0;
        List<CompanionDamageEntry> table = Table(11);

        HashSet<uint> seen = [];
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (CompanionDamageEntry entry in CompanionDamageOwedWindow.Slice(table, 9, ref start))
            {
                seen.Add(entry.RosterSlotPlusOne);
            }
        }

        Assert.Equal(11, seen.Count);
    }

    [Fact]
    public void AnEmptyTableIsEmpty()
    {
        int start = 4;
        Assert.Empty(CompanionDamageOwedWindow.Slice([], 9, ref start));
        Assert.Equal(0, start);
    }
}
