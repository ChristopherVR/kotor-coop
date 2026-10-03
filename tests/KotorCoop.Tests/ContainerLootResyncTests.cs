using System.Reflection;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ContainerLootResyncTests
{
    [Theory]
    [InlineData(1102)]
    [InlineData(2500)]
    public void RecoveryLargerThanTheSendQueueEventuallyOffersEveryRemoval(int count)
    {
        using CoopHost host = new(new SessionSettings { Port = 0 });
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        ReliableLootSender sender = (ReliableLootSender)typeof(CoopHost)
            .GetField("_lootSender", hidden)!.GetValue(host)!;
        MethodInfo resync = typeof(CoopHost).GetMethod("ResyncContainerLoot", hidden)!;
        host.SetLootModule("end_m01aa");
        // A disconnected player's missing history exceeds the bounded queue. Drain/ack
        // each recovery batch exactly as the send loop would on a repaired link.
        for (uint item = 1; item <= count; item++)
            host.Loot.Add(new ContainerLootRemoval(0xC6, item));
        HashSet<uint> offered = [];
        for (int batch = 0; batch < 4; batch++)
        {
            resync.Invoke(host, [1L, "end_m01aa"]);
            foreach (ContainerLootPacket packet in sender.CollectDue([2u], 1))
            {
                offered.Add(packet.ItemId);
                sender.Acknowledge(2, packet.OpSequence, packet.Module);
            }
            sender.CollectDue([2u], 2);
        }
        Assert.Equal(count, offered.Count);
        Assert.Contains((uint)count, offered);
    }
}
