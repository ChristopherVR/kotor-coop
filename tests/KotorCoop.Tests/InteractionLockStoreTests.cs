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

    // ---- InteractionKind.Container (docs/research/container-sync.md) --------------------

    private static InteractionClaimPacket ContainerClaim(uint peer, uint targetId = 0x131, bool active = true) =>
        new(peer, Module, Tag: string.Empty, InteractionKind.Container, active, targetId);

    [Fact]
    public void AContainerClaimWithNoTargetIdClaimsNothing()
    {
        // The container-kind analogue of "an empty tag claims nothing" — TargetId 0 is never a
        // real placeable id.
        InteractionLockStore store = new();

        Assert.False(store.Claim(ContainerClaim(2, targetId: 0), 1000));
        Assert.Equal(0u, store.HolderOfContainer(Module, 0x131, 1000));
    }

    [Fact]
    public void TheFirstFreshContainerClaimWins()
    {
        InteractionLockStore store = new();

        Assert.True(store.Claim(ContainerClaim(2), 1000));
        Assert.False(store.Claim(ContainerClaim(3), 1000));
        Assert.Equal(2u, store.HolderOfContainer(Module, 0x131, 1000));
    }

    [Fact]
    public void TheOpenerMayReOpenTheirOwnHeldContainer()
    {
        // Re-entrancy: the SAME player using the container they already hold must never be
        // refused by the very lock protecting it from everyone else.
        InteractionLockStore store = new();
        store.Claim(ContainerClaim(2), 1000);

        Assert.True(store.Claim(ContainerClaim(2), 1500));
        Assert.Equal(2u, store.HolderOfContainer(Module, 0x131, 1500));
    }

    [Fact]
    public void ClosingThePanelReleasesTheContainerImmediately()
    {
        InteractionLockStore store = new();
        store.Claim(ContainerClaim(2), 1000);

        store.Claim(ContainerClaim(2, active: false), 1100);

        Assert.Equal(0u, store.HolderOfContainer(Module, 0x131, 1100));
        Assert.True(store.Claim(ContainerClaim(3), 1100));
    }

    [Fact]
    public void TwoContainersSharingATagAreStillIndependentLocks()
    {
        // The reason a container keys on the object id and not the tag at all: the base game
        // routinely places several placeables with the SAME tag (every generic footlocker in a
        // module, typically). Two peers opening two DIFFERENT footlockers that happen to share
        // a tag must not contend with each other.
        InteractionLockStore store = new();

        Assert.True(store.Claim(ContainerClaim(2, targetId: 0x131), 1000));
        Assert.True(store.Claim(ContainerClaim(3, targetId: 0x132), 1000));

        Assert.Equal(2u, store.HolderOfContainer(Module, 0x131, 1000));
        Assert.Equal(3u, store.HolderOfContainer(Module, 0x132, 1000));
    }

    [Fact]
    public void AConversationAndAContainerNeverCollideEvenAtTheSameNumericId()
    {
        // TargetId and a tag live in disjoint keyspaces (InteractionLockStore's own remarks):
        // claiming container id 0x131 must not appear to hold, or be blocked by, an unrelated
        // conversation.
        InteractionLockStore store = new();
        store.Claim(Claim(2, tag: "end_trask"), 1000);

        Assert.True(store.Claim(ContainerClaim(3, targetId: 0x131), 1000));
        Assert.Equal(2u, store.HolderOf(Module, "end_trask", 1000));
        Assert.Equal(3u, store.HolderOfContainer(Module, 0x131, 1000));
    }

    [Fact]
    public void CollectCarriesTheContainerTargetIdThrough()
    {
        InteractionLockStore store = new();
        store.Claim(ContainerClaim(2, targetId: 0x131), 1000);

        InteractionLock entry = Assert.Single(store.Collect(Module, 1000));
        Assert.Equal(InteractionKind.Container, entry.Kind);
        Assert.Equal(0x131u, entry.TargetId);
        Assert.Equal(string.Empty, entry.Tag);
    }

    [Fact]
    public void ALapsedContainerLeaseFreesItForSomebodyElse()
    {
        InteractionLockStore store = new();
        store.Claim(ContainerClaim(2), 1000);

        long afterLease = 1000 + InteractionLockStore.DefaultLeaseMs + 1;

        Assert.Equal(0u, store.HolderOfContainer(Module, 0x131, afterLease));
        Assert.True(store.Claim(ContainerClaim(3), afterLease));
        Assert.Equal(3u, store.HolderOfContainer(Module, 0x131, afterLease));
    }
}
