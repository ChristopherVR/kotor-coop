using Xunit;
using KotorCoop.Plugin;

namespace KotorCoop.Tests;

/// <summary>
/// docs/research/shared-inventory-v2.md §15. The party pool's remaining live defect —
/// "<c>pooladd</c> ×1 scanned as <c>raw=-2</c>, published as a REMOVE of 2, applied to the
/// host's canonical engine" — is not a pump bug and never was. It is what a repository looks
/// like after <see cref="PartyPoolDepositRule"/>'s own remarks describe: the same object id
/// linked into the pool's array once per deposit, its stack size overwritten by the LAST
/// deposit's count, so the aggregate a scan reports is <c>links × stack</c> rather than a sum.
///
/// The fixture below is that repository, modelled exactly: an id array that may hold the same
/// id twice, and a stack size per id. Nothing here is a re-implementation of the rule — the
/// rule itself decides, and the two <c>Deposit*</c> methods differ ONLY in whether they consult
/// it, which is precisely the diff shipped in <c>Odyssey.AddItemToPartyPool</c>.
/// </summary>
public sealed class PartyPoolDepositRuleTests
{
    /// <summary>
    /// A <c>CItemRepository</c>'s two observable facts: <c>items_list</c> (a flat array of
    /// object ids, which is what an aggregate scan walks) and each live item's own stack size.
    /// The engine's merge is modelled the way <c>CItemRepository::AddItem(wantMerge=1)</c>
    /// behaves: a spawn of a resref already present returns that stack's id and adds to it,
    /// linking nothing new.
    /// </summary>
    private sealed class FakePool
    {
        private readonly List<uint> _links = [];

        private readonly Dictionary<uint, uint> _stacks = [];

        private uint _nextId = 0x130;

        /// <summary>What a party-pool scan reports: every array slot's item's stack size,
        /// summed — a slot is counted whether or not another slot names the same id, because
        /// the scan has no reason to suspect one would.</summary>
        public int Aggregate
        {
            get
            {
                int total = 0;
                foreach (uint id in _links)
                {
                    total += (int)_stacks[id];
                }

                return total;
            }
        }

        public int LinkCount => _links.Count;

        /// <summary>The id array itself — what <c>SnapshotRepositoryStacks</c> walks.</summary>
        public IReadOnlyList<uint> LinkedIds => _links;

        public void Seed(uint id, uint stack)
        {
            _stacks[id] = stack;
            _links.Add(id);
        }

        /// <summary>The engine half of a deposit: CreateItemOnObject -> AcquireItem ->
        /// AddItem(wantMerge=1) against the party pool. Returns the object id the VM hands
        /// back, which for a merge is the PRE-EXISTING stack's id.</summary>
        public uint Spawn(int count, out bool merged)
        {
            if (_links.Count > 0)
            {
                merged = true;
                uint existing = _links[0];
                _stacks[existing] += (uint)count;
                return existing;
            }

            merged = false;
            uint fresh = _nextId++;
            _stacks[fresh] = (uint)count;
            _links.Add(fresh);
            return fresh;
        }

        public uint StackOf(uint id) => _stacks[id];

        public int LinksOf(uint id) => _links.Count(l => l == id);

        public void SetStack(uint id, uint stack) => _stacks[id] = stack;

        public void Link(uint id) => _links.Add(id);

        public void Unlink(uint id) => _links.Remove(id);
    }

    /// <summary>What shipped before §15: overwrite the stack size with the requested count, and
    /// link the item in unconditionally (the unlink that was supposed to precede it targeted a
    /// repository the item was never in).</summary>
    private static void DepositTheOldWay(FakePool pool, int count)
    {
        uint id = pool.Spawn(count, out _);
        pool.SetStack(id, (uint)count);
        pool.Link(id);
    }

    /// <summary>What ships now: the rule decides.</summary>
    private static void DepositThroughTheRule(FakePool pool, int count)
    {
        // Snapshot before the spawn — membership, not arithmetic, is what identifies a merge.
        Dictionary<uint, uint> before = [];
        foreach (uint id in pool.LinkedIds)
        {
            before[id] = pool.StackOf(id);
        }

        uint spawned = pool.Spawn(count, out _);
        bool wasAlreadyLinked = before.TryGetValue(spawned, out uint stackBefore);

        PartyPoolDepositRule.Deposit deposit = PartyPoolDepositRule.ForCreatedItem(
            wasAlreadyLinked, stackBefore, pool.LinksOf(spawned), count);

        pool.SetStack(spawned, deposit.TargetStackSize);

        if (deposit.NeedsLink)
        {
            pool.Link(spawned);
        }

        for (int i = 0; i < deposit.DuplicateLinksToDrop; i++)
        {
            pool.Unlink(spawned);
        }
    }

    [Fact]
    public void TheOldDepositReproducesTheLiveTraceExactly_IncludingTheDepositThatScannedAsMinusTwo()
    {
        // shared-inventory-v2.md §14.3 / the round's own agent log, client side,
        // key 'g_w_blstrpstl001'/c0/m0/u0/f1:
        //   14:51:42  actual=1                       (one blaster in the pool)
        //   14:51:53  actual=2   after an add of 1
        //   15:03:51  actual=6   after an add of 2   <- raw jumped +4 for a deposit of 2
        //   15:06:19  actual=4   after an add of 1   <- raw=-2, published as a REMOVE of 2
        FakePool pool = new();
        pool.Seed(0x132, 1);
        Assert.Equal(1, pool.Aggregate);

        DepositTheOldWay(pool, 1);
        Assert.Equal(2, pool.Aggregate);

        DepositTheOldWay(pool, 2);
        Assert.Equal(6, pool.Aggregate);

        DepositTheOldWay(pool, 1);
        Assert.Equal(4, pool.Aggregate);

        // The defect in one sentence: the reported total is links × stack, not a sum.
        Assert.Equal(4, pool.LinkCount);
        Assert.Equal(1u, pool.StackOf(0x132));
    }

    [Fact]
    public void TheRuleMakesEveryOneOfThoseDepositsMoveTheAggregateByExactlyWhatWasAsked()
    {
        FakePool pool = new();
        pool.Seed(0x132, 1);

        DepositThroughTheRule(pool, 1);
        Assert.Equal(2, pool.Aggregate);

        DepositThroughTheRule(pool, 2);
        Assert.Equal(4, pool.Aggregate);

        DepositThroughTheRule(pool, 1);
        Assert.Equal(5, pool.Aggregate);

        // One id, one slot, all the units on it — never a product.
        Assert.Equal(1, pool.LinkCount);
        Assert.Equal(5u, pool.StackOf(0x132));
    }

    [Fact]
    public void AFirstDepositOntoAnEmptyPoolIsLinkedByTheEngineAndNotAgainByUs()
    {
        FakePool pool = new();

        DepositThroughTheRule(pool, 3);

        Assert.Equal(3, pool.Aggregate);
        Assert.Equal(1, pool.LinkCount);
    }

    [Fact]
    public void ARunOfSingleUnitDepositsLooksIdenticalUnderBothPaths_WhichIsWhyTheDefectSurvivedFiveRounds()
    {
        // §14.1's live pass was host pooladd ×1 three times and read 1 -> 2 -> 3 -> 4 on both
        // sides, which it (reasonably) signed off as correct. links × 1 grows by exactly 1 per
        // deposit, so a count of 1 cannot tell the two apart. Pinned so nobody re-derives the
        // false confidence.
        FakePool old = new();
        FakePool fixedPool = new();
        old.Seed(0x12B, 1);
        fixedPool.Seed(0x12B, 1);

        for (int i = 0; i < 3; i++)
        {
            DepositTheOldWay(old, 1);
            DepositThroughTheRule(fixedPool, 1);
            Assert.Equal(fixedPool.Aggregate, old.Aggregate);
        }

        Assert.Equal(4, fixedPool.Aggregate);

        // They diverge the instant one deposit asks for more than one unit.
        DepositTheOldWay(old, 2);
        DepositThroughTheRule(fixedPool, 2);
        Assert.Equal(6, fixedPool.Aggregate);
        Assert.Equal(10, old.Aggregate);
    }

    [Fact]
    public void AMergeAddsToTheStandingStackAndKeepsItsIdentity()
    {
        PartyPoolDepositRule.Deposit deposit =
            PartyPoolDepositRule.ForCreatedItem(wasAlreadyLinked: true, stackBefore: 4, linksNow: 1, count: 3);

        Assert.Equal(7u, deposit.TargetStackSize);
        Assert.True(deposit.KeepEngineIdentity);
        Assert.False(deposit.NeedsLink);
        Assert.Equal(0, deposit.DuplicateLinksToDrop);
    }

    [Fact]
    public void AFreshBodyTheEngineAlreadyFiledIsNeverLinkedTwice()
    {
        PartyPoolDepositRule.Deposit deposit =
            PartyPoolDepositRule.ForCreatedItem(wasAlreadyLinked: false, stackBefore: 0, linksNow: 1, count: 2);

        Assert.Equal(2u, deposit.TargetStackSize);
        Assert.False(deposit.KeepEngineIdentity);
        Assert.False(deposit.NeedsLink);
        Assert.Equal(0, deposit.DuplicateLinksToDrop);
    }

    [Fact]
    public void ABodyTheEngineFiledSomewhereElseStillHasToBeMovedInByTheCaller()
    {
        PartyPoolDepositRule.Deposit deposit =
            PartyPoolDepositRule.ForCreatedItem(wasAlreadyLinked: false, stackBefore: 0, linksNow: 0, count: 5);

        Assert.Equal(5u, deposit.TargetStackSize);
        Assert.True(deposit.NeedsLink);
        Assert.Equal(0, deposit.DuplicateLinksToDrop);
    }

    [Fact]
    public void APoolAPreFixBuildDoubleLinkedHealsOnTheNextDeposit()
    {
        PartyPoolDepositRule.Deposit deposit =
            PartyPoolDepositRule.ForCreatedItem(wasAlreadyLinked: true, stackBefore: 2, linksNow: 3, count: 1);

        Assert.Equal(3u, deposit.TargetStackSize);
        Assert.Equal(2, deposit.DuplicateLinksToDrop);
        Assert.False(deposit.NeedsLink);
    }

    [Fact]
    public void ACountBelowOneIsStillOneUnit()
    {
        Assert.Equal(
            1u,
            PartyPoolDepositRule.ForCreatedItem(wasAlreadyLinked: false, stackBefore: 0, linksNow: 1, count: 0)
                .TargetStackSize);
    }
}
