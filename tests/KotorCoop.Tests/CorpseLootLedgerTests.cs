using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>Shared corpse loot: the two engines' body bags for one creature are paired by position
/// and blueprint (live 2026-10-09: host bag 0x135 and joiner bag 0x149 at the same spot, and both
/// players could empty theirs).</summary>
public sealed class CorpseLootLedgerTests
{
    private static BodyBagState Bag(uint id, float x, float y, params (uint Id, string Resref)[] items) =>
        new(id, x, y, [.. items.Select(i => new BodyBagItem(i.Id, i.Resref, 1))]);

    private static readonly IReadOnlyList<(uint, IReadOnlyList<CorpseTakeEntry>)> NoRemote = [];

    [Fact]
    public void AnItemLeavingABagIsThisPlayersTake()
    {
        CorpseLootLedger ledger = new();
        ledger.Update([Bag(0x149, 15.3f, 82.4f, (0x146, "end_1damblast"), (0x148, "g_i_medeqpmnt01"))], NoRemote);
        ledger.Update([Bag(0x149, 15.3f, 82.4f, (0x146, "end_1damblast"))], NoRemote);

        CorpseTakeEntry take = Assert.Single(ledger.OwnTakes);
        Assert.Equal("g_i_medeqpmnt01", take.Resref);
        Assert.Equal(1, take.Count);
    }

    [Fact]
    public void AnEmptiedBagThatTheEngineRemovedCountsEveryItem()
    {
        CorpseLootLedger ledger = new();
        ledger.Update([Bag(0x149, 15.3f, 82.4f, (0x146, "end_1damblast"), (0x148, "g_i_medeqpmnt01"))], NoRemote);
        ledger.Update([], NoRemote);

        Assert.Equal(2, ledger.OwnTakes.Count);
    }

    [Fact]
    public void ABagSeenForTheFirstTimeIsABaselineNotATake()
    {
        CorpseLootLedger ledger = new();
        ledger.Update([Bag(0x135, 15.3f, 82.4f, (0x12B, "g_i_medeqpmnt01"))], NoRemote);

        Assert.Empty(ledger.OwnTakes);
    }

    [Fact]
    public void AnotherPlayersTakeRemovesTheSameBlueprintFromTheBagAtThatSpot()
    {
        CorpseLootLedger ledger = new();
        BodyBagState hostBag = Bag(0x135, 15.4f, 82.1f, (0x12B, "g_i_medeqpmnt01"), (0x136, "g_i_credits001"));
        IReadOnlyList<(uint, IReadOnlyList<CorpseTakeEntry>)> joiner = [(7u, [new CorpseTakeEntry(15.3f, 82.4f, "g_i_medeqpmnt01", 1)])];

        List<ContainerLootRemoval> removals = ledger.Update([hostBag], joiner);

        Assert.Equal(new ContainerLootRemoval(0x135, 0x12B), Assert.Single(removals));
    }

    [Fact]
    public void AnAppliedTakeIsNotAppliedTwiceAndItsRemovalIsNotAnEcho()
    {
        CorpseLootLedger ledger = new();
        IReadOnlyList<(uint, IReadOnlyList<CorpseTakeEntry>)> joiner = [(7u, [new CorpseTakeEntry(15.3f, 82.4f, "g_i_medeqpmnt01", 1)])];
        ledger.Update([Bag(0x135, 15.3f, 82.4f, (0x12B, "g_i_medeqpmnt01"), (0x12C, "g_i_medeqpmnt01"))], joiner);

        // The list arrives again (a level), and the requested item has gone.
        List<ContainerLootRemoval> again = ledger.Update([Bag(0x135, 15.3f, 82.4f, (0x12C, "g_i_medeqpmnt01"))], joiner);

        Assert.Empty(again);
        Assert.Empty(ledger.OwnTakes); // our own removal is not reported back as a take
    }

    [Fact]
    public void ATakeWaitsForTheBagToExistHere()
    {
        CorpseLootLedger ledger = new();
        IReadOnlyList<(uint, IReadOnlyList<CorpseTakeEntry>)> host = [(0u, [new CorpseTakeEntry(15.3f, 82.4f, "end_1damblast", 1)])];

        Assert.Empty(ledger.Update([], host));
        Assert.Single(ledger.Update([Bag(0x149, 15.6f, 82.0f, (0x146, "end_1damblast"))], host));
    }

    [Fact]
    public void ABagTooFarAwayIsAnotherCreaturesBag()
    {
        CorpseLootLedger ledger = new();
        IReadOnlyList<(uint, IReadOnlyList<CorpseTakeEntry>)> host = [(0u, [new CorpseTakeEntry(15.3f, 82.4f, "end_1damblast", 1)])];

        Assert.Empty(ledger.Update([Bag(0x150, 30f, 82.4f, (0x151, "end_1damblast"))], host));
    }

    [Fact]
    public void TakesOfOneBlueprintAtOneBagAccumulate()
    {
        CorpseLootLedger ledger = new();
        ledger.Update([Bag(0x149, 1f, 1f, (0x1, "g_i_medeqpmnt01"), (0x2, "g_i_medeqpmnt01"))], NoRemote);
        ledger.Update([], NoRemote);

        CorpseTakeEntry take = Assert.Single(ledger.OwnTakes);
        Assert.Equal(2, take.Count);
    }

    private static HostBodyBagPacket HostBag(float x, float y, params (string Resref, ushort Stack)[] items) =>
        new(123, "end_m01aa", x, y, [.. items.Select(i => new BodyBagItemEntry(i.Resref, i.Stack))]);

    [Fact]
    public void AJoinersBagIsMadeToHoldWhatTheHostsBagHoldsAndExtrasGoOnlyAfterTheGivesLand()
    {
        // Live 2026-10-09: host bag credits x11 + medpac; joiner bag blaster, blade, medpac.
        CorpseLootLedger ledger = new();
        BodyBagState mine = Bag(0x14A, 15.3f, 82.4f, (0x147, "end_1damblast"), (0x148, "g_w_vbroshort002"), (0x149, "g_i_medeqpmnt01"));
        HostBodyBagPacket host = HostBag(15.3f, 82.4f, ("g_i_credits001", 11), ("g_i_medeqpmnt01", 1));
        ledger.Update([mine], []);

        var gives = ledger.Reconcile([mine], [host], nowMs: 0);
        Assert.Equal((0x14Au, new BodyBagItemEntry("g_i_credits001", 11)), Assert.Single(gives));
        Assert.Empty(ledger.Removals); // not yet: emptying the bag first could let the engine remove it

        BodyBagState refilled = Bag(0x14A, 15.3f, 82.4f, (0x147, "end_1damblast"), (0x148, "g_w_vbroshort002"),
            (0x149, "g_i_medeqpmnt01"), (0x150, "g_i_credits001"));
        Assert.Empty(ledger.Reconcile([refilled], [host], nowMs: 500));
        Assert.Equal([new ContainerLootRemoval(0x14A, 0x147), new ContainerLootRemoval(0x14A, 0x148)], ledger.Removals);
    }

    [Fact]
    public void ExtrasGoAfterTheWaitEvenIfAGiveNeverLands()
    {
        CorpseLootLedger ledger = new();
        BodyBagState mine = Bag(0x14A, 1f, 1f, (0x147, "g_i_credits001"));
        ledger.Reconcile([mine], [HostBag(1f, 1f, ("g_i_medeqpmnt01", 1))], nowMs: 0);

        ledger.Reconcile([mine], [HostBag(1f, 1f, ("g_i_medeqpmnt01", 1))], nowMs: CorpseLootLedger.GiveWaitMs);

        Assert.Single(ledger.Removals);
    }

    [Fact]
    public void ReconcilingOnceMeansLaterDifferencesAreTakesNotRefills()
    {
        CorpseLootLedger ledger = new();
        BodyBagState mine = Bag(0x14A, 1f, 1f, (0x149, "g_i_medeqpmnt01"));
        ledger.Update([mine], []);
        ledger.Reconcile([mine], [HostBag(1f, 1f, ("g_i_medeqpmnt01", 1))], 0);

        // This player takes the medpac; the host's level still lists it until our take lands.
        ledger.Update([Bag(0x14A, 1f, 1f)], []);
        Assert.Empty(ledger.Reconcile([Bag(0x14A, 1f, 1f)], [HostBag(1f, 1f, ("g_i_medeqpmnt01", 1))], 1000));
        Assert.Single(ledger.OwnTakes);
    }

    [Fact]
    public void RemovedExtrasAreNotThisPlayersTakes()
    {
        CorpseLootLedger ledger = new();
        BodyBagState mine = Bag(0x14A, 1f, 1f, (0x147, "end_1damblast"));
        ledger.Update([mine], []);
        ledger.Reconcile([mine], [HostBag(1f, 1f, ("g_i_credits001", 11))], 0);
        ledger.Reconcile([mine], [HostBag(1f, 1f, ("g_i_credits001", 11))], CorpseLootLedger.GiveWaitMs);
        ledger.Update([Bag(0x14A, 1f, 1f)], []);

        Assert.Empty(ledger.OwnTakes);
    }

    [Fact]
    public void ABagWithNoHostBagNearWaitsThenIsEmptied()
    {
        // The host's corpse left nothing; the joiner's bag only holds the stand-in's placeholder.
        CorpseLootLedger ledger = new();
        BodyBagState mine = Bag(0x14A, 1f, 1f, (0x147, "g_i_credits001"));
        Assert.Empty(ledger.Reconcile([mine], [HostBag(30f, 1f, ("g_i_credits001", 11))], 0));
        Assert.Empty(ledger.Removals);

        ledger.Reconcile([mine], [], CorpseLootLedger.OrphanMs);
        Assert.Equal(new ContainerLootRemoval(0x14A, 0x147), Assert.Single(ledger.Removals));
    }

    [Fact]
    public void ThePlaceholderIsOneCredit()
    {
        Assert.Equal("g_i_credits001", CorpseLootLedger.Placeholder.Resref);
        Assert.Equal(1, CorpseLootLedger.Placeholder.Identity.StackSize);
    }

    [Fact]
    public void ThePacketRoundTrips()
    {
        CorpseTakesPacket packet = new(123, 7, "end_m01aa", [new CorpseTakeEntry(15.3f, 82.4f, "g_w_vbroshort002", 2)]);
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, packet);
        CorpseTakesPacket? read = PacketCodec.ReadCorpseTakes(buffer.AsSpan(0, length));

        Assert.NotNull(read);
        Assert.Equal(packet.Entries, read.Entries);
        Assert.Equal((123u, 7u, "end_m01aa"), (read.HostSessionId, read.OriginPeerId, read.Module));
    }

    [Fact]
    public void AFullListFitsOneDatagram()
    {
        List<CorpseTakeEntry> entries = [.. Enumerable.Range(0, CorpseTakesPacket.MaxEntries)
            .Select(i => new CorpseTakeEntry(i, i, "abcdefghijklmnop", 9))];
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];

        Assert.True(PacketCodec.Write(buffer, new CorpseTakesPacket(1, 2, "end_m01aa", entries)) > 0);
    }

    [Fact]
    public void CorpseLootIsSharedUnlessCoopIniSaysPersonal()
    {
        Assert.False(BehaviourDigestFlag(corpseLootPersonal: false));
        Assert.True(BehaviourDigestFlag(corpseLootPersonal: true));
    }

    private static bool BehaviourDigestFlag(bool corpseLootPersonal) =>
        (BehaviourDigest.Compute(
            localCombat: false, companionLocalCombat: false, partyFastPump: false, partySync: true,
            clientDoorRefusal: true, transitionAnnounce: true, clientLoadRejoin: true, hostLoadReadopt: true,
            effectSync: true, triggerRelay: true, clientEncounters: true, dialogueItemSync: true,
            dialogueRecruitSync: true, scriptItemSync: true, movieRelay: true, livenessSync: true,
            mapRevealSync: true, requireIdenticalSave: false, interpolationMs: 100, followLead: "any",
            corpseLootPersonal: corpseLootPersonal).Flags & (uint)BehaviourFlags.CorpseLootPersonal) != 0;
}
