using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The arithmetic that carries a companion_local_combat-released companion's damage TAKEN back
/// to the host — the companion sibling of <see cref="PeerDamageLedgerTests"/>. See
/// <see cref="CompanionDamageLedger"/>'s own class comment for why the same three rules apply
/// (bank the rise, ignore a decrease unless the generation says otherwise, no per-module
/// concept because a roster slot is stable for the whole session).
/// </summary>
public class CompanionDamageLedgerTests
{
    [Fact]
    public void OnePeersRisesAreBanked()
    {
        CompanionDamageLedger ledger = new();

        ledger.Merge([Report(peer: 2, rosterSlotPlusOne: 1, total: 7)]);
        Assert.Equal(7u, ledger.OwedFor(1));

        ledger.Merge([Report(peer: 2, rosterSlotPlusOne: 1, total: 19)]);
        Assert.Equal(19u, ledger.OwedFor(1));
    }

    [Fact]
    public void ARepeatedReportAddsNothing()
    {
        // Unreliable and re-sent every tick, so applying the same level twice is the easiest
        // way this channel could turn into a cheat.
        CompanionDamageLedger ledger = new();

        ledger.Merge([Report(2, 1, 7)]);
        ledger.Merge([Report(2, 1, 7)]);
        ledger.Merge([Report(2, 1, 7)]);

        Assert.Equal(7u, ledger.OwedFor(1));
    }

    [Fact]
    public void AnOutOfOrderReportIsIgnoredRatherThanSubtracted()
    {
        CompanionDamageLedger ledger = new();

        ledger.Merge([Report(2, 1, 20)]);
        ledger.Merge([Report(2, 1, 12)]);

        Assert.Equal(20u, ledger.OwedFor(1));
    }

    [Fact]
    public void TwoDifferentCompanionsAreBankedIndependently()
    {
        // A slot's own total must never leak into a sibling's — two companions can be
        // released to the same client's fight at once (party-reconciliation.md's
        // two-companion release entry).
        CompanionDamageLedger ledger = new();

        ledger.Merge([Report(2, rosterSlotPlusOne: 1, total: 10), Report(2, rosterSlotPlusOne: 3, total: 4)]);

        Assert.Equal(10u, ledger.OwedFor(1));
        Assert.Equal(4u, ledger.OwedFor(3));

        ledger.Merge([Report(2, 1, 15)]);
        Assert.Equal(15u, ledger.OwedFor(1));
        Assert.Equal(4u, ledger.OwedFor(3));
    }

    [Fact]
    public void TwoPeersDamageToTheSameCompanionIsSummed()
    {
        // Two different clients could both have the SAME roster slot released at once only if
        // companion_local_combat allowed it on two clients simultaneously, which it does not
        // today — but the ledger's own arithmetic (keyed by peer AND slot) must not assume
        // that and silently let one peer's report overwrite another's.
        CompanionDamageLedger ledger = new();

        ledger.Merge([Report(2, 1, 10), Report(3, 1, 4)]);
        Assert.Equal(14u, ledger.OwedFor(1));
    }

    [Fact]
    public void APeerGoingSilentDoesNotUnHurtTheCompanion()
    {
        CompanionDamageLedger ledger = new();

        ledger.Merge([Report(2, 1, 10), Report(3, 1, 4)]);
        ledger.Merge([Report(3, 1, 4)]);

        Assert.Equal(14u, ledger.OwedFor(1));
    }

    [Fact]
    public void APeerThatRestartsItsCountIsBelievedAgain()
    {
        // The generation is the sender's own reset marker: a companion reclaimed and later
        // released again restarts the plugin's watch at zero, and without this rule every
        // total it sends afterward would read as permanently stale.
        CompanionDamageLedger ledger = new();

        ledger.Merge([Report(2, 1, 30, generation: 1)]);
        Assert.Equal(30u, ledger.OwedFor(1));

        ledger.Merge([Report(2, 1, 6, generation: 2)]);
        Assert.Equal(36u, ledger.OwedFor(1));

        ledger.Merge([Report(2, 1, 11, generation: 2)]);
        Assert.Equal(41u, ledger.OwedFor(1));
    }

    [Fact]
    public void ARosterSlotOfZeroIsIgnored()
    {
        // 0 is the "unused" sentinel, not a real slot — see CompanionDamageEntry's own doc.
        CompanionDamageLedger ledger = new();

        ledger.Merge([Report(2, rosterSlotPlusOne: 0, total: 99)]);

        Assert.Equal(0u, ledger.OwedFor(0));
    }

    [Fact]
    public void TheMergedTableIsALevelSoRepublishingIsFree()
    {
        CompanionDamageLedger ledger = new();
        ledger.Merge([Report(2, 1, 7)]);

        IReadOnlyList<CompanionDamageEntry> again = ledger.Merge([]);

        Assert.Contains(again, e => e.RosterSlotPlusOne == 1 && e.CumulativeDamage == 7);
    }

    [Fact]
    public void ATotalSaturatesRatherThanWrapping()
    {
        // A single report near uint.MaxValue is refused as implausible since 2026-09-02, so
        // the only way to the ceiling is the honest one: the largest believable rise, tick
        // after tick, from two peers whose totals both count against the same slot.
        CompanionDamageLedger ledger = new();
        uint step = PeerDamageLedger.MaxPlausibleRise;
        uint perPeer = uint.MaxValue / 2 + step;

        for (uint total = step; total <= perPeer; total += step)
        {
            ledger.Merge([Report(2, 1, total), Report(3, 1, total)]);
        }

        Assert.Equal(uint.MaxValue, ledger.OwedFor(1));
        Assert.Equal(0u, ledger.RefusedRises);
    }

    // ---- end to end: one hit is one hit, through the same plugin-side DamageOwedLedger ------

    [Fact]
    public void ALonePlayersCompanionFightArrivesExactlyOnceThroughLossAndDuplication()
    {
        CompanionDamageLedger agent = new();
        DamageOwedLedger plugin = new(SharedBlock.CompanionDamageOwedCapacity);

        uint[] wire = [4, 10, 13, 20, 25];

        // Drop the second report, duplicate the third, deliver the fourth twice more after
        // the fifth. Cumulative totals make all of that a no-op.
        uint[] delivered = [wire[0], wire[2], wire[2], wire[4], wire[3], wire[3]];

        int spent = 0;
        foreach (uint total in delivered)
        {
            IReadOnlyList<CompanionDamageEntry> owedTable = agent.Merge([new PeerCompanionDamage(2, 1, 1, total)]);

            foreach (CompanionDamageEntry entry in owedTable)
            {
                int amount = plugin.Pending(entry.RosterSlotPlusOne, entry.CumulativeDamage);
                plugin.Commit(entry.RosterSlotPlusOne, amount);
                spent += amount;
            }
        }

        Assert.Equal(25, spent);
        Assert.Equal(25u, plugin.AppliedTo(1));
    }

    /// <summary>Same gate as the enemy ledger (PeerDamageLedger.MaxPlausibleRise): a rise no
    /// fight could produce is a misread effect on the peer, and banked against a companion it
    /// would wipe the host's copy. Refused, counted, baseline moved.</summary>
    [Fact]
    public void ARiseNoFightCouldProduceIsRefused()
    {
        CompanionDamageLedger ledger = new();

        ledger.Merge([Report(2, 1, 7)]);
        ledger.Merge([Report(2, 1, 1_634_994_435)]);

        Assert.Equal(7u, ledger.OwedFor(1));
        Assert.Equal(1u, ledger.RefusedRises);
        Assert.Equal((2u, 1u, 1_634_994_428u), ledger.LastRefused);

        ledger.Merge([Report(2, 1, 1_634_994_440)]);
        Assert.Equal(12u, ledger.OwedFor(1));
    }

    // ---- heals (2026-09-18): the same arithmetic under the heal-flagged key -------------

    [Fact]
    public void AHealRowIsBankedUnderItsOwnKeyAndNeverNetsAgainstDamage()
    {
        // A medpac on a released companion is a rise in the HEAL total for that roster slot;
        // the damage total for the same slot is untouched, and vice versa.
        CompanionDamageLedger ledger = new();

        ledger.Merge([Report(2, 1, 30), Heal(2, 1, 15)]);
        Assert.Equal(30u, ledger.OwedFor(1));
        Assert.Equal(15u, ledger.HealOwedFor(1));

        ledger.Merge([Report(2, 1, 30), Heal(2, 1, 40)]);
        Assert.Equal(30u, ledger.OwedFor(1));
        Assert.Equal(40u, ledger.HealOwedFor(1));

        IReadOnlyList<CompanionDamageEntry> table = ledger.Merge([]);
        Assert.Contains(table, e => e.RosterSlotPlusOne == 1 && e.CumulativeDamage == 30);
        Assert.Contains(table, e => e.RosterSlotPlusOne == CompanionDamageSlots.HealSlot(1) && e.CumulativeDamage == 40);
    }

    [Fact]
    public void AStaleOrRepeatedHealReportAddsNothing()
    {
        CompanionDamageLedger ledger = new();

        ledger.Merge([Heal(2, 1, 20)]);
        ledger.Merge([Heal(2, 1, 20)]);
        ledger.Merge([Heal(2, 1, 12)]);

        Assert.Equal(20u, ledger.HealOwedFor(1));
    }

    [Fact]
    public void AHealThatRestartsItsCountIsBelievedAgain()
    {
        // A module change bumps the sender's generation; its heal total starts at 0 again
        // and everything it now reports is new healing.
        CompanionDamageLedger ledger = new();

        ledger.Merge([Heal(2, 1, 20, generation: 1)]);
        ledger.Merge([Heal(2, 1, 5, generation: 2)]);

        Assert.Equal(25u, ledger.HealOwedFor(1));
    }

    [Fact]
    public void AnImplausibleHealIsRefusedLikeAnImplausibleHit()
    {
        CompanionDamageLedger ledger = new();

        ledger.Merge([Heal(2, 1, 7)]);
        ledger.Merge([Heal(2, 1, 7 + PeerDamageLedger.MaxPlausibleRise + 1)]);

        Assert.Equal(7u, ledger.HealOwedFor(1));
        Assert.Equal(1u, ledger.RefusedRises);
        Assert.Equal((2u, CompanionDamageSlots.HealSlot(1), PeerDamageLedger.MaxPlausibleRise + 1), ledger.LastRefused);
    }

    [Fact]
    public void AHealRowWithNoRosterSlotIsIgnored()
    {
        CompanionDamageLedger ledger = new();

        ledger.Merge([new PeerCompanionDamage(2, 1, CompanionDamageSlots.HealFlag, 50)]);

        Assert.Empty(ledger.Merge([]));
    }

    [Fact]
    public void TwoPeersHealingTheSameCompanionAreSummed()
    {
        CompanionDamageLedger ledger = new();

        ledger.Merge([Heal(2, 3, 10), Heal(3, 3, 25)]);

        Assert.Equal(35u, ledger.HealOwedFor(3));
    }

    [Fact]
    public void TheHostSideOwedLedgerSpendsHealsAndHitsSeparately()
    {
        // The plugin keys its applied totals by the RAW slot field, so a heal row and a damage
        // row for the same companion are two debts, each paid exactly once.
        CompanionDamageLedger ledger = new();
        DamageOwedLedger plugin = new(18, maxPerApplication: 200);

        ledger.Merge([Report(2, 1, 30), Heal(2, 1, 15)]);
        foreach (CompanionDamageEntry entry in ledger.Merge([]))
        {
            int amount = plugin.Pending(entry.RosterSlotPlusOne, entry.CumulativeDamage);
            Assert.True(amount > 0);
            plugin.Commit(entry.RosterSlotPlusOne, amount);
        }

        Assert.Equal(30u, plugin.AppliedTo(1));
        Assert.Equal(15u, plugin.AppliedTo(CompanionDamageSlots.HealSlot(1)));

        foreach (CompanionDamageEntry entry in ledger.Merge([]))
        {
            Assert.Equal(0, plugin.Pending(entry.RosterSlotPlusOne, entry.CumulativeDamage));
        }
    }

    private static PeerCompanionDamage Report(uint peer, uint rosterSlotPlusOne, uint total, uint generation = 1) =>
        new(peer, generation, rosterSlotPlusOne, total);

    private static PeerCompanionDamage Heal(uint peer, uint rosterSlotPlusOne, uint total, uint generation = 1) =>
        new(peer, generation, CompanionDamageSlots.HealSlot(rosterSlotPlusOne), total);
}
