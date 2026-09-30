using System.Net;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ItemReconnectContinuityTests
{
    [Fact]
    public void VisitingAnotherHostDoesNotEraseTheFirstHostsNamespace()
    {
        string path = Path.GetTempFileName();
        File.Delete(path);
        try
        {
            ReliableItemSender sender = new();
            sender.Enqueue(2, ItemOpKind.Drop, new GroundItem(0x20001, "medpac", "end_m01aa", Vector3f.Zero), 0);
            ItemContinuationFile.Write(path, new(11, 2, 33, 2, sender));
            ItemContinuationFile.Write(path, new(22, 2, 33, 1, new ReliableItemSender()));
            ItemContinuation restored = ItemContinuationFile.ReadFor(path, 11, 2, 33)!;
            Assert.Equal(2u, restored.NextItemSequence);
            Assert.Single(restored.Sender.Capture().Pending);
            Assert.Null(ItemContinuationFile.ReadFor(path, 11, 2, 44));
        }
        finally
        {
            File.Delete(path);
            foreach (string ownedFile in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*")) File.Delete(ownedFile);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Version\":1,\"HostSessionId\":1,\"PeerId\":2,\"Nonce\":3,\"NextItemSequence\":1,\"Sender\":null}")]
    [InlineData("{\"Version\":1,\"HostSessionId\":1,\"PeerId\":2,\"Nonce\":3,\"NextItemSequence\":1,\"Sender\":{\"NextSequence\":1,\"Pending\":null}}")]
    public void CorruptJournalCannotMintFreshCounters(string json)
    {
        string path = Path.GetTempFileName();
        try { File.WriteAllText(path, json); Assert.Throws<InvalidDataException>(() => ItemContinuationFile.Read(path)); }
        finally { File.Delete(path); }
    }

    private sealed class Bridge : IGameBridge
    {
        public string Description => "item-reconnect";
        public bool IsAttached => true;
        public bool CanWriteRemotes => true;
        public bool TryReadLocalState(out PlayerState state)
        {
            state = new(Vector3f.Zero, 0, "end_m01aa", 100, 0);
            return true;
        }
        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;
        public void ClearCoopState() { }
        public void Dispose() { }
    }

    private static async Task Until(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task RecreatedClientDoesNotReuseRemovedItemOrAcknowledgedOperationIds()
    {
        using CoopHost host = new(new SessionSettings { Port = 0, TickRate = 30 });
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(40));
        Task hostTask = host.RunAsync(cts.Token);
        using SessionManager manager = new(new Bridge(), new AgentSettings { PollRate = 30 });
        manager.Connect(false, new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port),
            new SessionSettings { PeerIdentity = 0xCAFE, TickRate = 30 });
        await Until(() => { manager.Poll(); return manager.Phase == SessionPhase.Connected; });
        CoopClient first = Assert.IsType<CoopClient>(manager.Current);
        uint initial = first.DropItem("g_i_medeqpmnt01", "end_m01aa", Vector3f.Zero);
        Assert.NotEqual(0u, initial);
        await Until(() => host.Items.TryGet(initial, out _));
        first.RemoveItem(initial);
        await Until(() => !host.Items.TryGet(initial, out _));
        ItemContinuation continuation = Assert.IsType<ItemContinuation>(first.CaptureItemContinuation());

        // The host still remembers both operation IDs after the ground item is removed.
        manager.Disconnect();
        manager.Reconnect();
        await Until(() => { manager.Poll(); return manager.Phase == SessionPhase.Connected; });
        CoopClient second = Assert.IsType<CoopClient>(manager.Current);
        Assert.NotSame(first, second);
        Assert.Same(continuation.Sender, second.CaptureItemContinuation()!.Sender);
        uint next = second.DropItem("g_i_medeqpmnt01", "end_m01aa", Vector3f.Zero);
        Assert.Equal(initial >> 16, next >> 16); // retained nonce reclaimed the same peer
        Assert.NotEqual(initial, next);
        await Until(() => host.Items.TryGet(next, out _)); // old op sequence would be ACKed but ignored
        Assert.False(host.Items.TryGet(initial, out _));
        // A full resync requested at Welcome may have been captured before this drop.
        // Force that timing deterministically: the pickup must still publish its removal.
        second.Items.ReplaceAll([], second.Items.Version);
        second.RemoveItem(next);
        await Until(() => !host.Items.TryGet(next, out _));
        manager.Disconnect();

        // A different host has a different operation namespace, even if it assigns peer 2.
        using CoopHost replacement = new(new SessionSettings { Port = 0, TickRate = 30 });
        Task replacementTask = replacement.RunAsync(cts.Token);
        manager.Connect(false, new IPEndPoint(IPAddress.Loopback, replacement.ListenEndPoint.Port),
            new SessionSettings { PeerIdentity = 0xCAFE, TickRate = 30 });
        await Until(() => { manager.Poll(); return manager.Phase == SessionPhase.Connected; });
        CoopClient third = Assert.IsType<CoopClient>(manager.Current);
        Assert.NotSame(continuation.Sender, third.CaptureItemContinuation()!.Sender);
        uint fresh = third.DropItem("g_i_medeqpmnt01", "end_m01aa", Vector3f.Zero);
        Assert.Equal(1u, fresh & 0xFFFF);
        await Until(() => replacement.Items.TryGet(fresh, out _));
        manager.Disconnect();
        cts.Cancel();
        await hostTask;
        await replacementTask;
    }

    [Fact]
    public async Task NewManagerRestoresDurableIdsAndAnUnacknowledgedDrop()
    {
        string path = Path.GetTempFileName();
        File.Delete(path);
        try
        {
            using CoopHost host = new(new SessionSettings { Port = 0, TickRate = 30 });
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(40));
            Task hostTask = host.RunAsync(cts.Token);
            IPEndPoint endpoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);
            SessionSettings settings = new() { PeerIdentity = 0xCAFE, TickRate = 30, ItemContinuationPath = path };
            uint initial;
            ItemContinuation checkpoint;
            using (SessionManager firstManager = new(new Bridge(), new AgentSettings { PollRate = 30 }))
            {
                firstManager.Connect(false, endpoint, settings);
                await Until(() => { firstManager.Poll(); return firstManager.Phase == SessionPhase.Connected; });
                CoopClient client = Assert.IsType<CoopClient>(firstManager.Current);
                initial = client.DropItem("g_i_medeqpmnt01", "end_m01aa", Vector3f.Zero);
                await Until(() => host.Items.TryGet(initial, out _));
                client.RemoveItem(initial);
                await Until(() => !host.Items.TryGet(initial, out _));
                firstManager.Disconnect();
                checkpoint = ItemContinuationFile.Read(path)!;
            }
            // Model a crash after the write-ahead drop commit but before its first datagram.
            uint queued = (checkpoint.PeerId << 16) | checkpoint.NextItemSequence;
            checkpoint.Sender.Enqueue(checkpoint.PeerId, ItemOpKind.Drop,
                new GroundItem(queued, "g_i_medeqpmnt01", "end_m01aa", Vector3f.Zero), 0);
            ItemContinuationFile.Write(path, checkpoint with { NextItemSequence = checkpoint.NextItemSequence + 1 });
            Assert.False(host.Items.TryGet(queued, out _));
            using SessionManager nextManager = new(new Bridge(), new AgentSettings { PollRate = 30 });
            nextManager.Connect(false, endpoint, settings);
            await Until(() => { nextManager.Poll(); return nextManager.Phase == SessionPhase.Connected; });
            CoopClient restored = Assert.IsType<CoopClient>(nextManager.Current);
            await Until(() => host.Items.TryGet(queued, out _));
            uint next = restored.DropItem("g_i_medeqpmnt01", "end_m01aa", Vector3f.Zero);
            Assert.NotEqual(initial, next);
            Assert.NotEqual(queued, next);
            await Until(() => host.Items.TryGet(next, out _));
            Assert.False(host.Items.TryGet(initial, out _));
            ItemContinuation exhausted = restored.CaptureItemContinuation()! with { NextItemSequence = 0x10000 };
            nextManager.Disconnect();
            ItemContinuationFile.Write(path, exhausted);
            using SessionManager exhaustedManager = new(new Bridge(), new AgentSettings { PollRate = 30 });
            exhaustedManager.Connect(false, endpoint, settings);
            await Until(() => { exhaustedManager.Poll(); return exhaustedManager.Phase == SessionPhase.Connected; });
            CoopClient atLimit = Assert.IsType<CoopClient>(exhaustedManager.Current);
            Assert.Throws<InvalidOperationException>(() => atLimit.DropItem("g_i_medeqpmnt01", "end_m01aa", Vector3f.Zero));
            exhaustedManager.Disconnect();
            cts.Cancel();
            await hostTask;
        }
        finally
        {
            File.Delete(path);
            foreach (string ownedFile in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*"))
                File.Delete(ownedFile);
        }
    }
}
