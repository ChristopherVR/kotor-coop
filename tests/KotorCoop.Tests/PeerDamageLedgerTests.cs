using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The arithmetic that carries a peer's contribution to a fight.
///
/// This channel replaced one that reported enemy HEALTH and merged it with min(), and the
/// reason it had to be replaced is the shape of the numbers rather than a bug in the code:
/// health is a level that has to be sampled, and the owner's own mirror writes its value
/// back onto the peer's copy several times a second, so a hit had to win a race against the
/// next mirror pass to be reported at all. Measured live — a client attacking one Sith
/// soldier for ninety seconds moved it from 15 hit points to 11 and then never again.
///
/// So everything below is about one property: A PEER'S DAMAGE MUST BE APPLIED EXACTLY ONCE.
/// Not lost to a dropped datagram, not doubled by a repeated one, not re-applied when the
/// order shuffles, not silently ignored after either side restarts its counting. Three
/// pieces have to agree for that — the hook's table, the owner agent's merge, and the
/// owner plugin's spend — so all three are tested, plus the round trip through them.
/// </summary>
public class PeerDamageLedgerTests
{
    private const string Here = "end_m01aa";
    private const string Elsewhere = "tar_m02aa";

    // ---- the hook's table: what one instance has dealt --------------------------------

    [Fact]
    public void DamageDealtAccumulatesPerTarget()
    {
        DamageDealtTable table = new(8);

        table.Add(0xE, 4);
        table.Add(0xE, 6);
        table.Add(0x14, 3);

        Assert.Equal(10u, TotalFor(table, 0xE));
        Assert.Equal(3u, TotalFor(table, 0x14));
    }

    [Fact]
    public void ZeroAmountsAndZeroIdsAreIgnoredWithoutClaimingASlot()
    {
        // The hook filters before it gets here, but a slot burned on a no-op is a slot a
        // real enemy cannot have, and the table is the bounded resource in this design.
        DamageDealtTable table = new(2);

        table.Add(0, 5);
        table.Add(0xE, 0);
        table.Add(0x14, 1);
        table.Add(0x15, 1);

        Assert.Equal(1u, TotalFor(table, 0x14));
        Assert.Equal(1u, TotalFor(table, 0x15));
        Assert.Equal(0u, table.Dropped);
    }

    [Fact]
    public void AFullTableDropsLoudlyRatherThanQuietly()
    {
        DamageDealtTable table = new(2);

        table.Add(0xE, 1);
        table.Add(0xF, 1);

        Assert.False(table.Add(0x10, 1));
        Assert.Equal(1u, table.Dropped);

        // ...but an id already in the table is still counted, so a full table degrades to
        // "the fight you are already in keeps working" rather than to nothing at all.
        Assert.True(table.Add(0xE, 5));
        Assert.Equal(6u, TotalFor(table, 0xE));
    }

    [Fact]
    public void ForgettingADeadEnemyFreesItsSlot()
    {
        DamageDealtTable table = new(2);
        table.Add(0xE, 9);
        table.Add(0xF, 1);
        Assert.False(table.Add(0x10, 1));

        table.Forget(0xE);

        Assert.True(table.Add(0x10, 1));
        Assert.Equal(0u, TotalFor(table, 0xE));
    }

    [Fact]
    public void AModuleChangeStartsANewGeneration()
    {
        // Ids are assigned per module load, so carrying a total across would credit it
        // against a completely different creature — and the generation is what stops the
        // owner reading the restart as a stale packet.
        DamageDealtTable table = new(4);
        table.Add(0xE, 12);
        uint before = table.Generation;

        table.Clear();

        Assert.Equal(0u, TotalFor(table, 0xE));
        Assert.NotEqual(before, table.Generation);
    }

    [Fact]
    public void ATotalSaturatesRatherThanWrapping()
    {
        // A wrap reads as a reset on the owner's side, which would hand it the whole fight
        // to apply a second time. Impossible in play; cheap to make impossible in code.
        DamageDealtTable table = new(2);
        table.Add(0xE, uint.MaxValue - 1);
        table.Add(0xE, 10);

        Assert.Equal(uint.MaxValue, TotalFor(table, 0xE));
    }

    // ---- the owner agent's merge: many peers into one total ---------------------------

    [Fact]
    public void OnePeersRisesAreBanked()
    {
        PeerDamageLedger ledger = new();

        ledger.Merge(Here, [Report(peer: 2, id: 0xE, total: 7)]);
        Assert.Equal(7u, ledger.OwedFor(0xE));

        ledger.Merge(Here, [Report(peer: 2, id: 0xE, total: 19)]);
        Assert.Equal(19u, ledger.OwedFor(0xE));
    }

    [Fact]
    public void ARepeatedReportAddsNothing()
    {
        // The wire is unreliable and every tick re-sends the same running total. Applying
        // the level twice is the single easiest way to turn this channel into a cheat.
        PeerDamageLedger ledger = new();

        ledger.Merge(Here, [Report(2, 0xE, 7)]);
        ledger.Merge(Here, [Report(2, 0xE, 7)]);
        ledger.Merge(Here, [Report(2, 0xE, 7)]);

        Assert.Equal(7u, ledger.OwedFor(0xE));
    }

    [Fact]
    public void AnOutOfOrderReportIsIgnoredRatherThanSubtracted()
    {
        // Datagrams arrive out of order. A smaller total is an older total, not a heal.
        PeerDamageLedger ledger = new();

        ledger.Merge(Here, [Report(2, 0xE, 20)]);
        ledger.Merge(Here, [Report(2, 0xE, 12)]);

        Assert.Equal(20u, ledger.OwedFor(0xE));
    }

    [Fact]
    public void ALostReportCostsNothingOnceTheNextOneArrives()
    {
        // The whole reason the wire carries a LEVEL and not an event: the tick that went
        // missing is subsumed by the one after it.
        PeerDamageLedger ledger = new();

        ledger.Merge(Here, [Report(2, 0xE, 5)]);
        // ...totals of 9, 13 and 18 are all lost...
        ledger.Merge(Here, [Report(2, 0xE, 22)]);

        Assert.Equal(22u, ledger.OwedFor(0xE));
    }

    [Fact]
    public void TwoPeersDamageIsSummed()
    {
        PeerDamageLedger ledger = new();

        ledger.Merge(Here, [Report(2, 0xE, 10), Report(3, 0xE, 4)]);
        Assert.Equal(14u, ledger.OwedFor(0xE));

        ledger.Merge(Here, [Report(2, 0xE, 10), Report(3, 0xE, 9)]);
        Assert.Equal(19u, ledger.OwedFor(0xE));
    }

    [Fact]
    public void APeerGoingSilentDoesNotUnHurtTheEnemy()
    {
        // ⚠ This is why the ledger banks each peer's RISES instead of summing their current
        // totals. A peer that disconnects, changes module or simply has a stale report is
        // dropped from GetEnemyDamage — and a sum over what is left would instantly give an
        // enemy back everything that peer had done to it.
        PeerDamageLedger ledger = new();

        ledger.Merge(Here, [Report(2, 0xE, 10), Report(3, 0xE, 4)]);
        ledger.Merge(Here, [Report(3, 0xE, 4)]);

        Assert.Equal(14u, ledger.OwedFor(0xE));
    }

    [Fact]
    public void APeerThatRestartsItsCountIsBelievedAgain()
    {
        // ⚠ The quietest possible failure, and the reason the generation exists. A peer
        // walks out of the module and back in; its own ledger restarts at zero. Without the
        // generation, every total it ever sends again is smaller than the one we remember,
        // reads as stale, and its fight simply stops counting for the rest of the session.
        PeerDamageLedger ledger = new();

        ledger.Merge(Here, [Report(2, 0xE, 30, generation: 1)]);
        Assert.Equal(30u, ledger.OwedFor(0xE));

        ledger.Merge(Here, [Report(2, 0xE, 6, generation: 2)]);
        Assert.Equal(36u, ledger.OwedFor(0xE));

        ledger.Merge(Here, [Report(2, 0xE, 11, generation: 2)]);
        Assert.Equal(41u, ledger.OwedFor(0xE));
    }

    [Fact]
    public void OneModuleChangeHereDropsEverything()
    {
        PeerDamageLedger ledger = new();
        ledger.Merge(Here, [Report(2, 0xE, 30)]);

        ledger.Merge(Elsewhere, []);

        Assert.Equal(0u, ledger.OwedFor(0xE));
        Assert.NotEqual(0u, ledger.Epoch);
    }

    [Fact]
    public void TheEpochNamesTheModuleAndSurvivesAnAgentRestart()
    {
        // A hash rather than a counter, so an agent that reattaches mid-module lands on the
        // same epoch and does not needlessly wipe the plugin's applied totals — which would
        // replay the module's whole damage history.
        Assert.Equal(PeerDamageLedger.EpochOf(Here), PeerDamageLedger.EpochOf("END_M01AA"));
        Assert.NotEqual(PeerDamageLedger.EpochOf(Here), PeerDamageLedger.EpochOf(Elsewhere));
        Assert.NotEqual(0u, PeerDamageLedger.EpochOf(string.Empty));
    }

    [Fact]
    public void TheLiveFightIsPublishedAheadOfTheModulesHistory()
    {
        // ⚠ The owed table only grows — an enemy stays in it once it is dead and paid for —
        // while the plugin's side is a fixed 32 slots. Without this ordering, a long session
        // in one module eventually pushes the fight actually happening past the truncation
        // and stops counting it, which is the exact failure this channel exists to remove.
        PeerDamageLedger ledger = new();

        for (uint id = 1; id <= 5; id++)
        {
            ledger.Merge(Here, [Report(2, id, 3)]);
        }

        IReadOnlyList<EnemyDamageEntry> table = ledger.Merge(Here, [Report(2, 4, 8)]);

        Assert.Equal(5, table.Count);
        Assert.Equal(4u, table[0].ObjectId);
    }

    [Fact]
    public void TheMergedTableIsALevelSoRepublishingIsFree()
    {
        PeerDamageLedger ledger = new();
        ledger.Merge(Here, [Report(2, 0xE, 7)]);

        IReadOnlyList<EnemyDamageEntry> again = ledger.Merge(Here, []);

        Assert.Contains(again, e => e.ObjectId == 0xE && e.CumulativeDamage == 7);
    }

    // ---- the owner plugin's spend: total into engine damage ---------------------------

    [Fact]
    public void ThePluginSpendsOnlyTheIncrease()
    {
        DamageOwedLedger owed = new(8);
        owed.AdoptEpoch(1);

        Assert.Equal(7, owed.Pending(0xE, 7));
        owed.Commit(0xE, 7);

        Assert.Equal(0, owed.Pending(0xE, 7));

        Assert.Equal(5, owed.Pending(0xE, 12));
        owed.Commit(0xE, 5);
        Assert.Equal(0, owed.Pending(0xE, 12));
    }

    [Fact]
    public void ARefusedApplyStaysOwed()
    {
        // Commit is deliberately separate from Pending: the engine call can fail (no VM, a
        // conversation, a creature that has left), and advancing on a refusal would lose
        // that damage for good.
        DamageOwedLedger owed = new(8);

        Assert.Equal(9, owed.Pending(0xE, 9));
        // ...the apply is refused, so no Commit...
        Assert.Equal(9, owed.Pending(0xE, 9));
    }

    [Fact]
    public void ALargeDebtArrivesOverSeveralPasses()
    {
        DamageOwedLedger owed = new(8, maxPerApplication: 50);

        Assert.Equal(50, owed.Pending(0xE, 120));
        owed.Commit(0xE, 50);

        Assert.Equal(50, owed.Pending(0xE, 120));
        owed.Commit(0xE, 50);

        Assert.Equal(20, owed.Pending(0xE, 120));
        owed.Commit(0xE, 20);

        Assert.Equal(0, owed.Pending(0xE, 120));
    }

    [Fact]
    public void ATotalBelowWhatWeAppliedIsAdoptedNotTreatedAsNegativeDamage()
    {
        DamageOwedLedger owed = new(8);
        owed.Pending(0xE, 40);
        owed.Commit(0xE, 40);

        Assert.Equal(0, owed.Pending(0xE, 6));
        Assert.Equal(6u, owed.AppliedTo(0xE));

        // ...and from there the peer's new counting is believed.
        Assert.Equal(4, owed.Pending(0xE, 10));
    }

    [Fact]
    public void AChangedEpochClearsWhatWeApplied()
    {
        // ⚠ Both halves must restart together. The agent zeroes its owed totals on a module
        // change; a plugin still holding a high applied total would swallow every point of
        // a peer's damage in the new module until it exceeded the old module's running sum.
        DamageOwedLedger owed = new(8);
        owed.AdoptEpoch(0xAAAA);
        owed.Pending(0xE, 40);
        owed.Commit(0xE, 40);

        Assert.True(owed.AdoptEpoch(0xBBBB));
        Assert.Equal(0u, owed.AppliedTo(0xE));
        Assert.Equal(3, owed.Pending(0xE, 3));
    }

    [Fact]
    public void TheSameEpochIsNotAReset()
    {
        DamageOwedLedger owed = new(8);
        owed.AdoptEpoch(0xAAAA);
        owed.Pending(0xE, 40);
        owed.Commit(0xE, 40);

        Assert.False(owed.AdoptEpoch(0xAAAA));
        Assert.Equal(40u, owed.AppliedTo(0xE));
    }

    [Fact]
    public void AnUntrackableEnemyIsRefusedRatherThanAppliedUntracked()
    {
        // Applying without a slot to remember it in would re-apply the same damage on every
        // pass until the creature died of it. The debt survives on the peer, which is still
        // counting, so nothing is lost — it lands once a slot frees up.
        DamageOwedLedger owed = new(1);

        Assert.Equal(4, owed.Pending(0xE, 4));
        owed.Commit(0xE, 4);

        Assert.Equal(0, owed.Pending(0xF, 9));
        Assert.Equal(1u, owed.Dropped);
    }

    [Fact]
    public void AZeroTotalDoesNotClaimASlot()
    {
        DamageOwedLedger owed = new(1);

        Assert.Equal(0, owed.Pending(0xE, 0));
        Assert.Equal(4, owed.Pending(0xF, 4));
        Assert.Equal(0u, owed.Dropped);
    }

    // ---- end to end: one hit is one hit ------------------------------------------------

    [Fact]
    public void ALonePlayersFightArrivesExactlyOnceEvenThroughLossAndDuplication()
    {
        // The scenario this whole channel was built for, run as arithmetic: one player
        // alone against one enemy while the owner is across the module. Every hit must
        // reach the owner's engine once — no more, no less — through a wire that drops,
        // repeats and reorders.
        DamageDealtTable dealt = new(8);
        PeerDamageLedger agent = new();
        DamageOwedLedger plugin = new(8);

        int[] swings = [4, 6, 3, 7, 5];
        List<uint> wire = [];

        foreach (int swing in swings)
        {
            dealt.Add(0xE, (uint)swing);
            wire.Add(TotalFor(dealt, 0xE));
        }

        // Drop the second report, duplicate the third, and deliver the fourth twice more
        // after the fifth. Cumulative totals make all of that a no-op.
        List<uint> delivered = [wire[0], wire[2], wire[2], wire[4], wire[3], wire[3]];

        int spent = 0;
        foreach (uint total in delivered)
        {
            IReadOnlyList<EnemyDamageEntry> owedTable =
                agent.Merge(Here, [new PeerEnemyDamage(2, dealt.Generation, 0xE, total)]);

            plugin.AdoptEpoch(agent.Epoch);

            foreach (EnemyDamageEntry entry in owedTable)
            {
                int amount = plugin.Pending(entry.ObjectId, entry.CumulativeDamage);
                plugin.Commit(entry.ObjectId, amount);
                spent += amount;
            }
        }

        Assert.Equal(swings.Sum(), spent);
        Assert.Equal((uint)swings.Sum(), plugin.AppliedTo(0xE));
    }

    [Fact]
    public void AModuleRoundTripDoesNotLoseTheSecondFight()
    {
        // The peer fights, leaves, comes back and fights again. The second fight must count
        // in full — this is the combination (peer generation + owner epoch) that the two
        // reset rules exist to survive.
        DamageDealtTable dealt = new(8);
        PeerDamageLedger agent = new();
        DamageOwedLedger plugin = new(8);

        int Run(string module, uint id, params uint[] swings)
        {
            int spent = 0;

            foreach (uint swing in swings)
            {
                dealt.Add(id, swing);

                IReadOnlyList<EnemyDamageEntry> owed = agent.Merge(
                    module, [new PeerEnemyDamage(2, dealt.Generation, id, TotalFor(dealt, id))]);

                plugin.AdoptEpoch(agent.Epoch);

                foreach (EnemyDamageEntry entry in owed)
                {
                    int amount = plugin.Pending(entry.ObjectId, entry.CumulativeDamage);
                    plugin.Commit(entry.ObjectId, amount);
                    spent += amount;
                }
            }

            return spent;
        }

        Assert.Equal(15, Run(Here, 0xE, 5, 10));

        // Both sides transition. The peer's table restarts (new generation); the owner's
        // ledger enters a new module (new epoch) and the plugin drops its applied totals.
        dealt.Clear();

        Assert.Equal(9, Run(Elsewhere, 0xE, 4, 5));

        // And back again — the same id, a third generation, a third epoch.
        dealt.Clear();
        Assert.Equal(6, Run(Here, 0xE, 6));
    }

    private static PeerEnemyDamage Report(uint peer, uint id, uint total, uint generation = 1) =>
        new(peer, generation, id, total);

    // ---- the plausibility gate (2026-09-02): a corrupt rise is refused, not banked --------

    /// <summary>
    /// Live on tar_m10aa a client's row for one Vulkar rose from 3 to 1,634,994,435 between two
    /// ticks. Banked, the owner's 200-per-pass clamp fed it to the creature until it died of a
    /// hit nobody had landed. The rise is refused, the baseline still moves to the reported
    /// total (so the very next real hit is credited from there, not swallowed under a total
    /// the ledger has "already seen"), and the refusal is counted for the pump's one line.
    /// </summary>
    [Fact]
    public void ARiseNoFightCouldProduceIsRefusedAndTheNextRealHitStillCounts()
    {
        PeerDamageLedger agent = new();

        agent.Merge(Here, [new PeerEnemyDamage(2, 1, 0x46, 3)]);
        Assert.Equal(3u, agent.OwedFor(0x46));

        agent.Merge(Here, [new PeerEnemyDamage(2, 1, 0x46, 1_634_994_435)]);

        Assert.Equal(3u, agent.OwedFor(0x46));
        Assert.Equal(1u, agent.RefusedRises);
        Assert.Equal((2u, 0x46u, 1_634_994_432u), agent.LastRefused);

        // A real hit on top of the corrupt total: credited as its own rise.
        agent.Merge(Here, [new PeerEnemyDamage(2, 1, 0x46, 1_634_994_440)]);

        Assert.Equal(8u, agent.OwedFor(0x46));
        Assert.Equal(1u, agent.RefusedRises);
    }

    [Fact]
    public void TheLargestBelievableRiseIsStillBanked()
    {
        PeerDamageLedger agent = new();

        agent.Merge(Here, [new PeerEnemyDamage(2, 1, 0x46, PeerDamageLedger.MaxPlausibleRise)]);

        Assert.Equal(PeerDamageLedger.MaxPlausibleRise, agent.OwedFor(0x46));
        Assert.Equal(0u, agent.RefusedRises);
    }

    /// <summary>A generation change still resets the baseline to zero, so a fresh run's first
    /// report is judged as a rise from nothing — a corrupt FIRST report is refused the same way.</summary>
    [Fact]
    public void ACorruptFirstReportOfANewGenerationIsRefusedToo()
    {
        PeerDamageLedger agent = new();

        agent.Merge(Here, [new PeerEnemyDamage(2, 1, 0x46, 40)]);
        agent.Merge(Here, [new PeerEnemyDamage(2, 2, 0x46, 4_000_000_000)]);

        Assert.Equal(40u, agent.OwedFor(0x46));
        Assert.Equal(1u, agent.RefusedRises);
    }

    [Fact]
    public void RetiredGenerationCannotReplayDamageOrAttributedThreat()
    {
        PeerDamageLedger ledger = new();
        ledger.Merge(Here, [new(2, 1, 0xE, 10)]);
        ledger.Merge(Here, [new(2, 2, 0xE, 3)]);
        ledger.Merge(Here, [new(2, 1, 0xE, 10), new(2, 1, 0xF, 9)]);
        ledger.Merge(Here, [new(2, 2, 0xE, 3)]);
        Assert.Equal(13u, ledger.OwedFor(0xE));
        Assert.Equal(0u, ledger.OwedFor(0xF));
        Assert.Equal(13u, Assert.Single(ledger.AttributedFor([new(0xE, 13)])).CumulativeDamage);
    }

    [Fact]
    public void FreshProcessGenerationNeedNotBeNumericallyNewer()
    {
        PeerDamageLedger ledger = new();
        ledger.Merge(Here, [new(2, 9000, 0xE, 10, 100)]);
        ledger.Merge(Here, [new(2, 500, 0xE, 3, 200)]);
        ledger.Merge(Here, [new(2, 9000, 0xE, 10, 100)]);
        ledger.Merge(Here, [new(2, 500, 0xE, 5, 200)]);
        Assert.Equal(15u, ledger.OwedFor(0xE));
    }

    [Fact]
    public void GenerationCounterWrapDoesNotReopenItsRetiredPredecessor()
    {
        PeerDamageLedger ledger = new();
        ledger.Merge(Here, [new(2, uint.MaxValue, 0xE, 10)]);
        ledger.Merge(Here, [new(2, 0, 0xE, 3)]);
        ledger.Merge(Here, [new(2, uint.MaxValue, 0xE, 10)]);
        Assert.Equal(13u, ledger.OwedFor(0xE));
    }

    [Fact]
    public void FreshNativeGameRunDoesNotReuseItsPredecessorsGeneration()
    {
        DamageDealtTable first = new(4), restarted = new(4);
        Assert.NotEqual(0u, first.Generation);
        Assert.NotEqual(0u, restarted.Generation);
        Assert.NotEqual(first.Incarnation, restarted.Incarnation);
        first.Add(0xE, 10);
        restarted.Add(0xE, 3);
        PeerDamageLedger ledger = new();
        ledger.Merge(Here, [new(2, first.Generation, 0xE, 10, first.Incarnation)]);
        // A network reconnect retains the exact native run and cumulative baseline.
        ledger.Merge(Here, [new(2, first.Generation, 0xE, 10, first.Incarnation)]);
        ledger.Merge(Here, [new(2, restarted.Generation, 0xE, 3, restarted.Incarnation)]);
        ledger.Merge(Here, [new(2, first.Generation, 0xE, 10, first.Incarnation)]);
        Assert.Equal(13u, ledger.OwedFor(0xE));
    }

    [Fact]
    public void NeverSeenOlderGenerationCannotBecomeFreshDamage()
    {
        PeerDamageLedger ledger = new();
        ledger.Merge(Here, [new(2, 2, 0xE, 3, 100)]);
        ledger.Merge(Here, [new(2, 1, 0xE, 10, 100), new(2, 1, 0xF, 9, 100)]);
        ledger.Merge(Here, [new(2, 2, 0xE, 5, 100)]);
        Assert.Equal(5u, ledger.OwedFor(0xE));
        Assert.Equal(0u, ledger.OwedFor(0xF));
    }

    [Fact]
    public void DataCannotDeclareANewIncarnationWithoutAdmission()
    {
        DamageGenerationWatch watch = new();
        Assert.True(watch.Bind(2, 100));
        Assert.True(watch.Observe(2, 100, 2));
        Assert.False(watch.Observe(2, 100, 1));
        Assert.False(watch.Observe(2, 200, 1));
        Assert.True(watch.Bind(2, 200));
        Assert.True(watch.Observe(2, 200, 1));
        Assert.False(watch.Bind(2, 100));
    }

    private static uint TotalFor(DamageDealtTable table, uint objectId)
    {
        for (int i = 0; i < table.Capacity; i++)
        {
            if (table.IdAt(i) == objectId)
            {
                return table.TotalAt(i);
            }
        }

        return 0;
    }
}
