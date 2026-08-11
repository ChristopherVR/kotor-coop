using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The arbitration rules for per-object interaction locks, settled here before anything can
/// hold a body still. These are the rules that decide whether a client can take a
/// conversation off the host, so they are worth being sure about away from the engine.
/// </summary>
public class InteractionLockStoreTests
{
    private const string Module = "end_m01aa";

    private static InteractionClaimPacket Claim(uint peer, string tag = "end_trask", bool active = true) =>
        new(peer, Module, tag, InteractionKind.Conversation, active);

    [Fact]
    public void TheFirstFreshClaimWins()
    {
        InteractionLockStore store = new();

        Assert.True(store.Claim(Claim(2), 1000));
        Assert.False(store.Claim(Claim(3), 1000));
        Assert.Equal(2u, store.HolderOf(Module, "end_trask", 1000));
    }

    [Fact]
    public void TheHolderKeepsItByRefreshing()
    {
        InteractionLockStore store = new();
        store.Claim(Claim(2), 1000);

        Assert.True(store.Claim(Claim(2), 1000 + InteractionLockStore.DefaultLeaseMs));
        Assert.Equal(2u, store.HolderOf(Module, "end_trask", 1000 + InteractionLockStore.DefaultLeaseMs));
    }

    [Fact]
    public void ALapsedLeaseFreesTheObjectForSomebodyElse()
    {
        // ⚠ The failure this whole channel is shaped around: a client that dies
        // mid-conversation must not strand the NPC. Nothing is sent to release it — the
        // refresh simply stops.
        InteractionLockStore store = new();
        store.Claim(Claim(2), 1000);

        long afterLease = 1000 + InteractionLockStore.DefaultLeaseMs + 1;

        Assert.Equal(0u, store.HolderOf(Module, "end_trask", afterLease));
        Assert.True(store.Claim(Claim(3), afterLease));
        Assert.Equal(3u, store.HolderOf(Module, "end_trask", afterLease));
    }

    [Fact]
    public void AnExplicitReleaseFreesItImmediately()
    {
        InteractionLockStore store = new();
        store.Claim(Claim(2), 1000);

        store.Claim(Claim(2, active: false), 1100);

        Assert.Equal(0u, store.HolderOf(Module, "end_trask", 1100));
        Assert.True(store.Claim(Claim(3), 1100));
    }

    [Fact]
    public void ANonHolderCannotReleaseSomebodyElsesLock()
    {
        InteractionLockStore store = new();
        store.Claim(Claim(2), 1000);

        store.Claim(Claim(3, active: false), 1100);

        Assert.Equal(2u, store.HolderOf(Module, "end_trask", 1100));
    }

    [Fact]
    public void ForgettingAPeerDropsEverythingItHeld()
    {
        InteractionLockStore store = new();
        store.Claim(Claim(2, "end_trask"), 1000);
        store.Claim(Claim(2, "end_carth"), 1000);
        store.Claim(Claim(3, "end_bastila"), 1000);

        store.Forget(2);

        Assert.Equal(0u, store.HolderOf(Module, "end_trask", 1000));
        Assert.Equal(0u, store.HolderOf(Module, "end_carth", 1000));
        Assert.Equal(3u, store.HolderOf(Module, "end_bastila", 1000));
    }

    [Fact]
    public void AClaimFromAnotherModuleIsNotCollected()
    {
        // Tags are only meaningful where they were read, so the owner must never act on a
        // claim made by a peer standing somewhere else.
        InteractionLockStore store = new();
        store.Claim(new InteractionClaimPacket(2, "tar_m02aa", "end_trask", InteractionKind.Conversation, true), 1000);

        Assert.Empty(store.Collect(Module, 1000));
        Assert.Equal(0u, store.HolderOf(Module, "end_trask", 1000));
    }

    [Fact]
    public void TagsAreComparedWithoutCase()
    {
        InteractionLockStore store = new();
        store.Claim(Claim(2, "End_Trask"), 1000);

        Assert.False(store.Claim(Claim(3, "end_trask"), 1000));
        Assert.Equal(2u, store.HolderOf(Module, "END_TRASK", 1000));
    }

    [Fact]
    public void CollectIsCappedAndStablyOrdered()
    {
        InteractionLockStore store = new();
        for (uint peer = 9; peer >= 1; peer--)
        {
            store.Claim(Claim(peer, $"npc_{peer}"), 1000);
        }

        IReadOnlyList<InteractionLock> first = store.Collect(Module, 1000);
        IReadOnlyList<InteractionLock> second = store.Collect(Module, 1000);

        Assert.Equal(ProtocolConstants.MaxInteractionLocks, first.Count);
        Assert.Equal(first, second);
        Assert.Equal([1u, 2u, 3u, 4u], first.Select(entry => entry.PeerId));
    }

    [Fact]
    public void ExpiredEntriesAreDroppedByCollect()
    {
        InteractionLockStore store = new();
        store.Claim(Claim(2), 1000);

        Assert.Single(store.Collect(Module, 1000));
        Assert.Empty(store.Collect(Module, 1000 + InteractionLockStore.DefaultLeaseMs + 1));
    }
}
