using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class AttributedThreatIntegrationTests
{
    [Fact]
    public void ThreePlayersBankIndependentThreatAndRepeatedReportsDoNotHitTwice()
    {
        PeerDamageLedger merged = new();
        AttributedDamageOwedLedger applied = new(128);
        ThreatTable threat = new();
        PeerEnemyDamage[] reports = [new(2, 1, 100, 20), new(3, 1, 100, 40)];
        var owed = merged.Merge("end_m01aa", reports);
        applied.AdoptEpoch(merged.Epoch);
        threat.AddThreat(100, 1, 10, 0); // Host's own engine hit.
        foreach (var row in merged.AttributedFor(owed))
        {
            int hit = applied.Pending(row.PeerId, row.ObjectId, row.CumulativeDamage);
            applied.Commit(row.PeerId, row.ObjectId, hit);
            threat.AddThreat(row.ObjectId, row.PeerId, hit, 0);
        }
        Assert.Equal(60u, merged.OwedFor(100));
        Assert.True(threat.TryGetTopAttacker(100, 0, out uint top, out double amount));
        Assert.Equal(3u, top);
        Assert.Equal(40, amount);
        foreach (var row in merged.AttributedFor(merged.Merge("end_m01aa", reports.Reverse().ToArray())))
            Assert.Equal(0, applied.Pending(row.PeerId, row.ObjectId, row.CumulativeDamage));
        Assert.True(threat.TryGetTopAttacker(100, 0, out top, out amount, id => id != 3));
        Assert.Equal(2u, top); // Downed/disconnected highest contributor does not block next player.
    }

    [Fact]
    public void GenerationRestartAndUnorderedReportsPreserveAcceptedPerPeerDamage()
    {
        PeerDamageLedger merged = new();
        merged.Merge("end_m01aa", [new(2, 1, 100, 20), new(3, 1, 100, 40)]);
        merged.Merge("end_m01aa", [new(2, 1, 100, 10)]); // older datagram
        merged.Merge("end_m01aa", [new(2, 1, 100, 20)]); // current repeated
        var owed = merged.Merge("end_m01aa", [new(2, 2, 100, 5)]);
        Assert.Equal(65u, merged.OwedFor(100));
        Assert.Equal(25u, merged.AttributedFor(owed).Single(row => row.PeerId == 2).CumulativeDamage);
        Assert.Equal(40u, merged.AttributedFor(owed).Single(row => row.PeerId == 3).CumulativeDamage);
        merged.Merge("end_m01aa", [new(2, 2, 100, 30_000)]); // rejected corruption
        Assert.Equal(65u, merged.OwedFor(100));
    }

    [Fact]
    public void FailedApplyIsRetriedAndModuleChangeResetsEveryPeer()
    {
        AttributedDamageOwedLedger applied = new(128);
        applied.AdoptEpoch(1);
        Assert.Equal(30, applied.Pending(2, 100, 30));
        Assert.Equal(30, applied.Pending(2, 100, 30)); // no successful commit
        applied.Commit(2, 100, 30);
        Assert.Equal(0, applied.Pending(2, 100, 30));
        Assert.Equal(20, applied.Pending(3, 100, 20));
        applied.AdoptEpoch(2);
        Assert.Equal(5, applied.Pending(2, 100, 5));
    }

    [Fact]
    public void ActiveEnemyRowsFitForEightPeersEvenAfterLongModules()
    {
        PeerDamageLedger merged = new();
        List<PeerEnemyDamage> all = [];
        for (uint enemy = 1; enemy <= 200; enemy++)
            for (uint peer = 1; peer <= 8; peer++) all.Add(new(peer, 1, enemy, 1));
        merged.Merge("end_m01aa", all);
        var owed = merged.Merge("end_m01aa", [new(8, 1, 200, 2)]);
        var rows = merged.AttributedFor(owed);
        Assert.Equal(1024, rows.Count);
        Assert.Contains(rows, row => row.PeerId == 8 && row.ObjectId == 200 && row.CumulativeDamage == 2);
    }

    [Fact]
    public unsafe void ResolvesEachPeersCurrentBodyAfterSlotsAreReordered()
    {
        byte[] memory = new byte[SharedBlock.TotalSize];
        fixed (byte* address = memory)
        {
            var block = new SharedBlockView(address);
            RemoteSlot* slots = (RemoteSlot*)(address + SharedBlock.SlotsOffset);
            slots[0].Active = 1; slots[0].PeerId = 3;
            slots[1].Active = 1; slots[1].PeerId = 2;
            block.SetPhantomId(0, 300);
            block.SetPhantomId(1, 200);
            Assert.Equal(200u, block.RemotePhantomIdFor(2));
            Assert.Equal(300u, block.RemotePhantomIdFor(3));
            slots[1].Active = 0;
            Assert.Equal(0u, block.RemotePhantomIdFor(2));
            slots[0].PeerId = 2;
            block.SetPhantomId(0, 201); // Reconnected body in a different slot.
            Assert.Equal(201u, block.RemotePhantomIdFor(2));
        }
    }

    [Fact]
    public unsafe void AttributedPublicationRejectsInProgressAndReadsAllPeerRows()
    {
        byte[] memory = new byte[SharedBlock.TotalSize];
        fixed (byte* address = memory)
        {
            var block = new SharedBlockView(address);
            AttributedDamageRow[] rows = new AttributedDamageRow[SharedBlock.AttributedDamageCapacity];
            *(uint*)(address + SharedBlock.OffsetAttributedDamageSequence) = 1;
            Assert.False(block.TryReadAttributedDamage(rows, out _, out _));
            *(uint*)(address + SharedBlock.OffsetLivenessNativeEpoch) = 1;
            *(uint*)(address + SharedBlock.OffsetAttributedDamageNativeEpoch) = 1;
            *(uint*)(address + SharedBlock.OffsetAttributedDamageEpoch) = 7;
            *(uint*)(address + SharedBlock.OffsetAttributedDamageCount) = 2;
            uint* slot = (uint*)(address + SharedBlock.OffsetAttributedDamageSlots);
            slot[0] = 2; slot[1] = 100; slot[2] = 20;
            slot[3] = 3; slot[4] = 100; slot[5] = 40;
            *(uint*)(address + SharedBlock.OffsetAttributedDamageSequence) = 2;
            Assert.True(block.TryReadAttributedDamage(rows, out uint epoch, out int count));
            Assert.Equal(7u, epoch);
            Assert.Equal(2, count);
            Assert.Equal(new AttributedDamageRow(3, 100, 40), rows[1]);
            *(uint*)(address + SharedBlock.OffsetWorldLoadSerial) = 1;
            Assert.False(block.TryReadAttributedDamage(rows, out _, out _)); // Writer from before teardown finished late.
            block.InvalidateAttributedDamage();
            Assert.False(block.TryReadAttributedDamage(rows, out _, out _));
            Assert.True(SharedBlock.OffsetAttributedDamageSlots + SharedBlock.AttributedDamageCapacity * SharedBlock.AttributedDamageStride <= SharedBlock.TotalSize);
        }
    }
}
