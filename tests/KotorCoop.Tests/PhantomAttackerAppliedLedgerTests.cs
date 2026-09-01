using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="PhantomAttackerAppliedLedger"/> is the fix for the phantom-damage attribution
/// bug: with one attacker id per delta, a pump window mixing a locally-fought attacker with a
/// mirrored one had to be billed wholesale to one or the other. These tests exercise the four
/// properties docs/research/peer-damage-ledger.md §7 names: a mixed-attacker window splits
/// correctly, absorbed vs applied stays independent per attacker, reordering/duplication is
/// harmless, and the eviction rule holds once more than 4 distinct attackers appear.
/// </summary>
public class PhantomAttackerAppliedLedgerTests
{
    [Fact]
    public void MixedAttackerWindowSplitsCorrectly()
    {
        PhantomAttackerAppliedLedger ledger = new();
        Span<uint> deltas = stackalloc uint[PhantomAttackerAppliedLedger.Capacity];

        PhantomAttackerEntry[] owed =
        [
            new PhantomAttackerEntry(0x10, 40), // locally-fought attacker
            new PhantomAttackerEntry(0x20, 15), // mirrored attacker
        ];

        ledger.Reconcile(owed, deltas);

        Assert.Equal(40u, deltas[0]);
        Assert.Equal(15u, deltas[1]);
    }

    [Fact]
    public void AbsorbedAndAppliedAreTrackedIndependentlyPerAttacker()
    {
        PhantomAttackerAppliedLedger ledger = new();
        Span<uint> deltas = stackalloc uint[PhantomAttackerAppliedLedger.Capacity];

        PhantomAttackerEntry[] owed =
        [
            new PhantomAttackerEntry(0x10, 40),
            new PhantomAttackerEntry(0x20, 15),
        ];

        ledger.Reconcile(owed, deltas);

        // 0x10 is absorbed (locally engaged); 0x20 is applied for real.
        ledger.MarkAccounted(0x10, 40);
        ledger.MarkAccounted(0x20, 15);

        Assert.Equal(40u, ledger.AppliedFor(0x10));
        Assert.Equal(15u, ledger.AppliedFor(0x20));

        // Next pass: 0x10 gained 10 more (still absorbed), 0x20 unchanged (nothing new owed).
        owed[0] = new PhantomAttackerEntry(0x10, 50);

        ledger.Reconcile(owed, deltas);

        Assert.Equal(10u, deltas[0]);
        Assert.Equal(0u, deltas[1]);
    }

    [Fact]
    public void ReorderingAndDuplicationAreHarmless()
    {
        PhantomAttackerAppliedLedger ledger = new();
        Span<uint> deltas = stackalloc uint[PhantomAttackerAppliedLedger.Capacity];

        PhantomAttackerEntry[] owed = [new PhantomAttackerEntry(0x10, 40)];

        ledger.Reconcile(owed, deltas);
        ledger.MarkAccounted(0x10, 40);

        // A duplicate/reordered report carrying the SAME cumulative total again.
        ledger.Reconcile(owed, deltas);
        Assert.Equal(0u, deltas[0]);

        // A lower total (an out-of-order datagram, not a real restart) still produces no
        // delta — Reconcile treats it as a reset rather than negative damage, but the applied
        // figure never regresses below what growth already proved.
        owed[0] = new PhantomAttackerEntry(0x10, 30);
        ledger.Reconcile(owed, deltas);
        Assert.Equal(0u, deltas[0]);
        Assert.Equal(30u, ledger.AppliedFor(0x10));

        // Growth resumes from the adopted (lower) baseline.
        owed[0] = new PhantomAttackerEntry(0x10, 35);
        ledger.Reconcile(owed, deltas);
        Assert.Equal(5u, deltas[0]);
    }

    [Fact]
    public void ARestartedSenderIsAdoptedRatherThanRefusedForever()
    {
        PhantomAttackerAppliedLedger ledger = new();
        Span<uint> deltas = stackalloc uint[PhantomAttackerAppliedLedger.Capacity];

        PhantomAttackerEntry[] owed = [new PhantomAttackerEntry(0x10, 200)];
        ledger.Reconcile(owed, deltas);
        ledger.MarkAccounted(0x10, 200);

        // The sender's own ledger restarted (a module change): total drops back to 0.
        owed[0] = new PhantomAttackerEntry(0x10, 0);
        ledger.Reconcile(owed, deltas);
        Assert.Equal(0u, deltas[0]);
        Assert.Equal(0u, ledger.AppliedFor(0x10));

        // Ordinary growth from the new baseline counts immediately, not after climbing back
        // past the old (now irrelevant) applied figure of 200.
        owed[0] = new PhantomAttackerEntry(0x10, 6);
        ledger.Reconcile(owed, deltas);
        Assert.Equal(6u, deltas[0]);
    }

    [Fact]
    public void EvictionDropsTheSmallestTotalWhenAFifthAttackerArrives()
    {
        PhantomAttackerAppliedLedger ledger = new();

        // Establish four attackers with distinct applied totals.
        ledger.MarkAccounted(0x10, 100);
        ledger.MarkAccounted(0x20, 50);
        ledger.MarkAccounted(0x30, 10); // smallest — expected to be evicted
        ledger.MarkAccounted(0x40, 75);

        // A fifth, genuinely new attacker needs a slot.
        ledger.MarkAccounted(0x50, 5);

        Assert.Equal(100u, ledger.AppliedFor(0x10));
        Assert.Equal(50u, ledger.AppliedFor(0x20));
        Assert.Equal(0u, ledger.AppliedFor(0x30)); // evicted
        Assert.Equal(75u, ledger.AppliedFor(0x40));
        Assert.Equal(5u, ledger.AppliedFor(0x50));
    }

    [Fact]
    public void ZeroIdEntriesProduceNoDeltaAndAreNeverTracked()
    {
        PhantomAttackerAppliedLedger ledger = new();
        Span<uint> deltas = stackalloc uint[PhantomAttackerAppliedLedger.Capacity];

        PhantomAttackerEntry[] owed = [new PhantomAttackerEntry(0, 999)];
        ledger.Reconcile(owed, deltas);

        Assert.Equal(0u, deltas[0]);
        Assert.Equal(0u, ledger.AppliedFor(0));

        ledger.MarkAccounted(0, 999);
        Assert.Equal(0u, ledger.AppliedFor(0));
    }

    [Fact]
    public void ClearDropsEveryTrackedAttacker()
    {
        PhantomAttackerAppliedLedger ledger = new();
        ledger.MarkAccounted(0x10, 40);
        ledger.MarkAccounted(0x20, 15);

        ledger.Clear();

        Assert.Equal(0u, ledger.AppliedFor(0x10));
        Assert.Equal(0u, ledger.AppliedFor(0x20));
    }
}
