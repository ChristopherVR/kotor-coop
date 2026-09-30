using System.Net;
using System.Reflection;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class DamageWorldTests
{
    [Theory]
    [InlineData(4u, 5u)] // explicit same-module load
    [InlineData(3u, 5u)] // announced same-module load, native epoch alone changes
    public void UnresolvedOldDebtCannotLandOnRecycledId(uint newSerial, uint newEpoch)
    {
        PeerDamageLedger ledger = new();
        AttributedDamageOwedLedger applied = new(128);
        var owed = ledger.Merge("end_m01aa", 3, 4, [new(2, 7, 0x12E, 6, 100)]);
        uint oldEpoch = ledger.Epoch;
        applied.AdoptEpoch(oldEpoch);
        Assert.Equal(6, applied.Pending(2, 0x12E, 6)); // old body never resolved; no commit
        owed = ledger.Merge("end_m01aa", newSerial, newEpoch, [new(2, 7, 0x12E, 10, 100)]);
        Assert.NotEqual(oldEpoch, ledger.Epoch);
        applied.AdoptEpoch(ledger.Epoch);
        Assert.Empty(owed);
        Assert.Empty(ledger.AttributedFor(owed));
        Assert.Equal(0u, ledger.OwedFor(0x12E));
        // Genuine adopted local world has a newer native generation and can hurt its body.
        owed = ledger.Merge("end_m01aa", newSerial, newEpoch, [new(2, 8, 0x12E, 3, 100)]);
        var row = Assert.Single(ledger.AttributedFor(owed));
        int hit = applied.Pending(row.PeerId, row.ObjectId, row.CumulativeDamage);
        Assert.Equal(3, hit);
        applied.Commit(row.PeerId, row.ObjectId, hit);
        owed = ledger.Merge("end_m01aa", newSerial, newEpoch, [new(2, 8, 0x12E, 3, 100)]);
        Assert.Equal(0, applied.Pending(2, 0x12E, Assert.Single(ledger.AttributedFor(owed)).CumulativeDamage));
        ledger.Merge("end_m01aa", newSerial, newEpoch, [new(2, 7, 0x12E, 20, 100)]);
        Assert.Equal(3u, ledger.OwedFor(0x12E));
    }

    [Fact]
    public void WorldRetirementSurvivesReportRemovalAndSameProcessReadmission()
    {
        DamageGenerationWatch watch = new();
        Assert.True(watch.Bind(2, 100));
        Assert.True(watch.Observe(2, 100, 7));
        watch.RetireCurrentWorld();
        Assert.True(watch.Bind(2, 100));
        Assert.False(watch.Observe(2, 100, 7));
        Assert.False(watch.Observe(2, 100, 6));
        Assert.True(watch.Observe(2, 100, 8));
        Assert.True(watch.Observe(2, 100, 8));
        watch.RetireCurrentWorld();
        Assert.True(watch.Bind(2, 200)); // actual new authenticated process may reset
        Assert.True(watch.Observe(2, 200, 1));
        Assert.False(watch.Bind(2, 100));
    }

    [Fact]
    public void NativeEpochHashIsStableAcrossAgentRestartsButChangesOnOrdinaryTravel()
    {
        Assert.Equal(PeerDamageLedger.WorldEpochOf("END_M01AA", 3, 4), PeerDamageLedger.WorldEpochOf("end_m01aa", 3, 4));
        Assert.NotEqual(PeerDamageLedger.WorldEpochOf("end_m01aa", 3, 4), PeerDamageLedger.WorldEpochOf("end_m01aa", 3, 5));
    }

    [Fact]
    public void EmptyTickBindsSenderBeforeFirstHitAndNewDigestCannotRetagOldRun()
    {
        DamageReportWorldWatch watch = new();
        Assert.False(watch.Accept(0, "end_m01aa", 3, 4, "end_m01aa", 7));
        Assert.True(watch.Accept(10, "end_m01aa", 3, 4, "end_m01aa", 7));
        Assert.True(watch.Accept(10, "end_m01aa", 3, 4, "end_m01aa", 7)); // lost/duplicate ordinary report
        Assert.False(watch.Accept(10, "end_m01aa", 4, 5, "end_m01aa", 7));
        Assert.False(watch.Accept(10, "end_m01aa", 4, 5, "end_m01aa", 6));
        Assert.True(watch.Accept(10, "end_m01aa", 4, 5, "end_m01aa", 8));
        Assert.False(watch.Accept(10, "end_m01aa", 3, 4, "end_m01aa", 7));
        Assert.True(watch.Accept(10, "end_m01aa", 4, 5, "end_m01aa", 8));
    }

    [Fact]
    public void SameAreaLocalLoadBeforeHostDigestCannotBindNewRunToOldHostWorld()
    {
        using CoopClient client = new(new SessionSettings(), new IPEndPoint(IPAddress.Loopback, 27140));
        void Set(string name, object value) => typeof(CoopClient).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, value);
        Set("_hostSessionId", 10u); Set("_localPeerId", 2u);
        Set("_hostWorld", new WorldDigest(1, 1, "end_m01aa", 1, 3, NativeWorldEpoch: 1));
        client.PublishWorldDigest(new WorldDigest(1, 1, "end_m01aa", 1, 7, NativeWorldEpoch: 1));
        client.ReportEnemyDamage("end_m01aa", 1, [], 1);
        // Native same-area load arrives before either managed digest. Sampled epoch guard
        // must refuse this empty gen2 binding, or a later host epoch2 would be stuck forever.
        client.ReportEnemyDamage("end_m01aa", 2, [], 2);
        var watch = (DamageReportWorldWatch)typeof(CoopClient).GetField("_damageReportWorld",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        Assert.True(watch.Accept(10, "end_m01aa", 3, 2, "end_m01aa", 2));
        // Publishing the own load quarantines until the required fresh saved-world admission.
        client.PublishWorldDigest(new WorldDigest(1, 1, "end_m01aa", 1, 8, NativeWorldEpoch: 2));
        Assert.True((bool)typeof(CoopClient).GetField("_livenessLocalLoadQuarantined",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!);
        // A fresh admitted session has no stale-generation binding and can report gen2
        // in the actual host epoch2, without another game/world load.
        DamageReportWorldWatch freshAdmission = new();
        Assert.True(freshAdmission.Accept(10, "end_m01aa", 3, 2, "end_m01aa", 2));
        Assert.True(freshAdmission.Accept(10, "end_m01aa", 3, 2, "end_m01aa", 2));
    }

    [Fact]
    public void LatestSenderGenerationRetiresEvenWhenEarlierEnemyRowsRemain()
    {
        PeerDamageLedger ledger = new();
        ledger.Merge("end_m01aa", 3, 4, [new(2, 7, 100, 6, 100), new(2, 8, 101, 3, 100)]);
        Assert.Empty(ledger.Merge("end_m01aa", 4, 5, [new(2, 8, 101, 10, 100)]));
        Assert.Empty(ledger.Merge("end_m01aa", 4, 5, [new(2, 7, 100, 10, 100)]));
        Assert.Equal(2u, Assert.Single(ledger.Merge("end_m01aa", 4, 5, [new(2, 9, 101, 2, 100)])).CumulativeDamage);
    }

    [Fact]
    public void SenderGenerationWrapAdmitsOnlyActuallyNewNativeRun()
    {
        DamageReportWorldWatch watch = new();
        Assert.True(watch.Accept(10, "end_m01aa", 3, 4, "end_m01aa", uint.MaxValue));
        Assert.True(watch.Accept(10, "end_m01aa", 4, 5, "end_m01aa", 1));
        Assert.False(watch.Accept(10, "end_m01aa", 3, 4, "end_m01aa", uint.MaxValue));
    }

    private delegate void Handler(IPEndPoint from, ReadOnlySpan<byte> data, long now);
    private static void Deliver(CoopHost host, string method, byte[] bytes, int length, IPEndPoint from) =>
        ((Handler)typeof(CoopHost).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Handler), host))(from, bytes.AsSpan(0, length), SessionClock.NowMs);

    [Fact]
    public void AuthenticatedQueuedOldNamespaceCannotAdvanceOrderingOrReplaceNewReport()
    {
        using CoopHost host = new(new SessionSettings { Port = 0 });
        host.PublishWorldDigest(new WorldDigest(1, 1, "end_m01aa", 1, 3, NativeWorldEpoch: 4));
        IPEndPoint from = new(IPAddress.Loopback, 9400);
        byte[] bytes = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(bytes, new HelloPacket("peer", "end_m01aa", 44, default, 0, DamageIncarnation: 100));
        Deliver(host, "HandleHello", bytes, length, from);
        uint session = (uint)typeof(CoopHost).GetField("_sessionId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        void Damage(uint generation, uint total, uint serial, uint epoch, uint? nonce = null)
        {
            int n = PacketCodec.Write(bytes, new EnemyDamagePacket(2, "end_m01aa", generation,
                [new(0x12E, total)], 100, nonce ?? session, serial, epoch));
            Deliver(host, "HandleEnemyDamage", bytes, n, from);
        }
        Damage(7, 6, 3, 4);
        Assert.Equal(6u, Assert.Single(host.GetEnemyDamage("end_m01aa")).CumulativeDamage);
        host.PublishWorldDigest(new WorldDigest(1, 1, "end_m01aa", 1, 4, NativeWorldEpoch: 5));
        Assert.Empty(host.GetEnemyDamage("end_m01aa"));
        Damage(9, 99, 3, 4); // queued old namespace, even a newer sender generation
        Damage(7, 99, 4, 5); // same native run cannot be relabelled with new host world
        Damage(8, 3, 4, 5, session + 1); // other authenticated host session
        Assert.Empty(host.GetEnemyDamage("end_m01aa"));
        Damage(8, 3, 4, 5); // generation9 rejection above must not poison ordering
        Assert.Equal(3u, Assert.Single(host.GetEnemyDamage("end_m01aa")).CumulativeDamage);
        Damage(9, 99, 3, 4);
        Assert.Equal(8u, Assert.Single(host.GetEnemyDamage("end_m01aa")).Generation);
    }

    [Fact]
    public unsafe void NativeDamageBankRefusesLateWriterAfterAcknowledgedSameModuleReload()
    {
        byte[] memory = new byte[SharedBlock.TotalSize];
        fixed (byte* address = memory)
        {
            SharedBlockView block = new(address);
            *(uint*)(address + SharedBlock.OffsetWorldLoadSerial) = 3;
            *(uint*)(address + SharedBlock.OffsetAttributedDamageWorldSerial) = 3;
            *(uint*)(address + SharedBlock.OffsetLivenessNativeEpoch) = 4;
            *(uint*)(address + SharedBlock.OffsetAttributedDamageNativeEpoch) = 4;
            *(uint*)(address + SharedBlock.OffsetAttributedDamageSequence) = 2;
            *(uint*)(address + SharedBlock.OffsetAttributedDamageCount) = 1;
            uint* row = (uint*)(address + SharedBlock.OffsetAttributedDamageSlots);
            row[0] = 2; row[1] = 0x12E; row[2] = 6;
            AttributedDamageRow[] rows = new AttributedDamageRow[128];
            Assert.True(block.TryReadAttributedDamage(rows, out _, out _));
            *(uint*)(address + SharedBlock.OffsetLivenessNativeEpoch) = 5;
            Assert.False(block.TryReadAttributedDamage(rows, out _, out _));
            Assert.True(SharedBlock.OffsetAttributedDamageNativeEpoch >= SharedBlock.OffsetLivenessNativeEpoch + 4);
            Assert.True(SharedBlock.OffsetAttributedDamageNativeEpoch + 4 <= SharedBlock.OffsetLocalPlaceableChanges);
        }
    }
}
