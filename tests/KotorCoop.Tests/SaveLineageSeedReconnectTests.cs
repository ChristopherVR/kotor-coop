using System.Net;
using System.Net.Sockets;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The live 2026-09-24 defect: a joiner running <c>disconnect</c> then <c>reconnect</c> with
/// nothing loaded got a FRESH <see cref="SaveLineage"/> (per-connection by design — see that
/// class's own doc) that had never seen the two saves agree, so the agent's own
/// pre-disconnect character-capture quicksave's identity change read as a genuinely different
/// save: <c>W_SAVE</c>, every id-keyed channel dropped, for a save that never actually
/// diverged. Protocol 67's lineage seed (docs/research/session-save.md) fixes it — these tests
/// exercise it end-to-end over real <see cref="CoopHost"/>/<see cref="CoopClient"/>/
/// <see cref="SessionManager"/> objects on loopback, the same style
/// <c>SessionManagerTests</c>/<c>PeerIdentityByNonceTests</c> already use.
/// </summary>
[Trait("Category", "Slow")]
public class SaveLineageSeedReconnectTests
{
    private const string Module = "end_m01aa";

    /// <summary>
    /// ⚠ MUTABLE, DELIBERATELY. A test in this class drives a real <see cref="CoopAgent"/>
    /// (through <see cref="SessionManager"/>), which republishes ITS OWN <see cref="WorldDigest"/>
    /// off this bridge every heavy-pump tick (~1 Hz) regardless of anything a test calls
    /// directly on the <see cref="CoopClient"/> — a one-shot <c>client.PublishWorldDigest(...)</c>
    /// races that republish and loses within a second or two, which is exactly what made the
    /// first draft of these tests flaky. Setting the fields here instead makes the agent's own
    /// republish the single, stable source of truth, the same way a real game bridge would be.
    /// </summary>
    private sealed class LineageTestBridge : IGameBridge
    {
        public uint SaveIdentity = 1;
        public uint GlobalsCatalogue = 1;
        public uint ModuleGit = 7;
        public uint LoadSerial;

        public string Description => "lineage-seed-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 100, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public void ClearCoopState()
        {
        }

        public void Dispose()
        {
        }

        public uint GlobalsCatalogueDigest => GlobalsCatalogue;

        public uint SaveIdentityDigest => SaveIdentity;

        public uint ModuleGitDigest(string module) => ModuleGit;

        public uint WorldLoadSerial => LoadSerial;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private static async Task<bool> WaitForPhase(SessionManager manager, SessionPhase phase, TimeSpan timeout) =>
        await WaitForAsync(() =>
        {
            manager.Poll();
            return manager.Phase == phase;
        }, timeout);

    /// <summary>
    /// The core fix, end to end: agree once, disconnect, reconnect with NO load edge either
    /// side, and a save-only mismatch on the fresh connection (the pre-disconnect capture's
    /// identity change) is absorbed exactly as it would be on a connection that had lived
    /// through the door crossing itself.
    /// </summary>
    [Fact]
    public async Task ABareReconnectAbsorbsAPostDisconnectSaveWithNoLoadEitherSide()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);

        LineageTestBridge bridge = new() { SaveIdentity = 0x1111, LoadSerial = 4 };
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        IPEndPoint endpoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);
        SessionSettings settings = new() { PlayerName = "Malak", TickRate = 30 };

        // Both sides report the SAME save identity/module/GIT, with distinct LoadSerials so a
        // wrong LoadSerial carried through the seed would be caught rather than accidentally
        // matching by both defaulting to 0.
        host.PublishWorldDigest(new WorldDigest(GlobalsCatalogue: 1, SaveIdentity: 0x1111, Module: Module, ModuleGit: 7, LoadSerial: 9));

        manager.Connect(host: false, endpoint, settings);
        Assert.True(await WaitForPhase(manager, SessionPhase.Connected, TimeSpan.FromSeconds(15)));

        CoopClient client = Assert.IsType<CoopClient>(manager.Current);

        // Wait for the mutual WorldDigest exchange (each side broadcasts at ~1 Hz) to record
        // agreement on both ends and for SessionManager to have sampled the client's side of it.
        Assert.True(await WaitForAsync(() =>
        {
            manager.Poll();
            return manager.LineageSeed is { } seed
                && seed.HostLoadAtAgreement == 9 && seed.LocalLoadAtAgreement == 4;
        }, TimeSpan.FromSeconds(15)), "the checkpoint never got seeded from a live agreement");

        uint hostSessionIdAtAgreement = manager.LineageSeed!.Value.HostSessionId;
        Assert.NotEqual(0u, hostSessionIdAtAgreement);

        manager.Disconnect();
        Assert.Equal(SessionPhase.Disconnected, manager.Phase);

        // The seed survives the disconnect — it is process-lifetime, not connection-lifetime.
        Assert.NotNull(manager.LineageSeed);

        // The host never moved (still LoadSerial 9); this player's own agent wrote a
        // pre-disconnect capture quicksave — a NEW SaveIdentity, SAME LoadSerial 4, exactly the
        // live 2026-09-24 shape.
        bridge.SaveIdentity = 0x2222;

        manager.Reconnect();
        Assert.True(await WaitForPhase(manager, SessionPhase.Connected, TimeSpan.FromSeconds(15)));

        CoopClient reconnected = Assert.IsType<CoopClient>(manager.Current);
        Assert.NotSame(client, reconnected);

        Assert.True(await WaitForAsync(() => reconnected.SaveLineageAbsorbing, TimeSpan.FromSeconds(10)),
            "the save-only mismatch after a bare reconnect was not absorbed");

        Assert.True(await WaitForAsync(
            () => reconnected.Compatibility is { } verdict
                && verdict.Notes.All(n => n.Code != CompatibilityCode.Save),
            TimeSpan.FromSeconds(5)),
            "a W_SAVE note was still shown after the seeded lineage absorbed the mismatch");

        await cts.CancelAsync();
        manager.Disconnect();
        try
        {
            await hostTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The other half of the rule: a LOCAL load edge — this player loading a save mid-session
    /// — really can put a different world under the same host session id, so
    /// <see cref="SessionManager.ClearLineageSeed"/> (what <c>CoopCommandDispatcher</c> calls
    /// on that edge) must make the NEXT reconnect judge the same raw mismatch honestly again:
    /// nothing seeds it, so it can never absorb, and <see cref="SessionManager"/> never
    /// re-populates its seed from a mismatch (only from a genuine agreement).
    /// </summary>
    [Fact]
    public async Task ClearingTheSeedMakesTheNextReconnectStartUnseeded()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);

        LineageTestBridge bridge = new() { SaveIdentity = 0x1111, LoadSerial = 4 };
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        IPEndPoint endpoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);
        SessionSettings settings = new() { PlayerName = "Malak", TickRate = 30 };

        host.PublishWorldDigest(new WorldDigest(GlobalsCatalogue: 1, SaveIdentity: 0x1111, Module: Module, ModuleGit: 7, LoadSerial: 9));

        manager.Connect(host: false, endpoint, settings);
        Assert.True(await WaitForPhase(manager, SessionPhase.Connected, TimeSpan.FromSeconds(15)));

        Assert.True(await WaitForAsync(() =>
        {
            manager.Poll();
            return manager.LineageSeed is not null;
        }, TimeSpan.FromSeconds(15)), "the checkpoint never got seeded from a live agreement");

        manager.Disconnect();

        // The local load edge this test stands in for.
        manager.ClearLineageSeed();
        Assert.Null(manager.LineageSeed);

        // The same save-only identity change as the companion test above — the ONLY thing
        // different here is that the seed was cleared first.
        bridge.SaveIdentity = 0x2222;

        manager.Reconnect();
        Assert.True(await WaitForPhase(manager, SessionPhase.Connected, TimeSpan.FromSeconds(15)));
        CoopClient reconnected = Assert.IsType<CoopClient>(manager.Current);

        // Give the agent's heavy pump several cycles to publish the mismatched digest and for
        // the exchange to run both ways, then assert the two guarantees that hold regardless of
        // SaveIdentitySettle's own debounce timing (not what this test is about): a
        // never-agreed SaveLineage can never set Absorbing, and SessionManager never
        // re-populates its seed from anything but a genuine agreement.
        for (int i = 0; i < 15; i++)
        {
            manager.Poll();
            Assert.False(reconnected.SaveLineageAbsorbing, "a cleared seed still let the fresh connection absorb a mismatch");
            Assert.Null(manager.LineageSeed);
            await Task.Delay(200);
        }

        await cts.CancelAsync();
        manager.Disconnect();
        try
        {
            await hostTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// A fresh connection with no prior agreement at all (0/0/0 on the wire — the ordinary
    /// shape of a first join) is judged exactly as before: nothing to seed, so a raw mismatch
    /// is honoured. Raw <see cref="CoopClient"/>/<see cref="CoopHost"/> objects, no
    /// <see cref="SessionManager"/>/<see cref="CoopAgent"/> involved, so nothing races the
    /// published digests and <see cref="SaveIdentitySettle"/>'s ordinary debounce is free to
    /// run to completion.
    /// </summary>
    [Fact]
    public async Task AFreshFirstJoinWithNoRecordBehavesExactlyAsBefore()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);

        using CoopClient client = new(new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));
        Task clientTask = client.RunAsync(cts.Token);

        Assert.True(await WaitForAsync(() => client.IsConnected, TimeSpan.FromSeconds(15)));

        host.PublishWorldDigest(new WorldDigest(GlobalsCatalogue: 1, SaveIdentity: 0x1111, Module: Module, ModuleGit: 7, LoadSerial: 1));
        client.PublishWorldDigest(new WorldDigest(GlobalsCatalogue: 1, SaveIdentity: 0x2222, Module: Module, ModuleGit: 7, LoadSerial: 1));

        // SaveIdentitySettle debounces a raw mismatch for up to its DefaultSettleMs (5 s)
        // before surfacing it, on top of the ~1 Hz digest exchange — both sides' own Tick()
        // loops keep resending the published digest unattended, so this converges cleanly.
        Assert.True(await WaitForAsync(
            () => client.Compatibility is { } verdict && verdict.Notes.Any(n => n.Code == CompatibilityCode.Save),
            TimeSpan.FromSeconds(15)),
            "a genuinely different save on first join must still be reported as a mismatch");
        Assert.False(client.SaveLineageAbsorbing);

        await cts.CancelAsync();
        try
        {
            await Task.WhenAll(hostTask, clientTask);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The host-side gate: a Hello carrying ANOTHER host process's session id (a stale seed
    /// from a previous, different host) must not seed the fresh peer's lineage, even though
    /// the carried LoadSerial happens to match this host's current one.
    /// </summary>
    [Fact]
    public async Task AHelloCarryingAnotherHostsSessionIdIsNotSeeded()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);

        host.PublishWorldDigest(new WorldDigest(GlobalsCatalogue: 1, SaveIdentity: 0x1111, Module: Module, ModuleGit: 7, LoadSerial: 0));

        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint hostEnd = new(IPAddress.Loopback, host.ListenEndPoint.Port);

        // A seed naming a session id this host never minted (session ids are randomised from
        // Random.Shared.Next(1, int.MaxValue), never 0 and vanishingly unlikely to collide),
        // but whose carried LoadSerial (0) DOES match this host's current one — the one field
        // the host-side gate must not treat as sufficient on its own.
        byte[] send = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(
            send,
            new HelloPacket(
                "Malak", Module, ClientNonce: 0xC0FFEE, ContentDigest.Unknown, SessionPassword.None,
                default, LineageHostSessionId: 0x0BAD_5EED, LineageHostLoad: 0, LineageClientLoad: 0));
        Assert.True(length > 0);

        byte[] receive = new byte[ProtocolConstants.MaxPacketSize];
        uint assignedId = 0;
        Assert.True(await WaitForAsync(() =>
        {
            socket.SendTo(send.AsSpan(0, length), hostEnd);
            try
            {
                socket.ReceiveTimeout = 200;
                int received = socket.Receive(receive);
                if (PacketCodec.PeekType(receive.AsSpan(0, received)) == PacketType.Welcome
                    && PacketCodec.ReadWelcome(receive.AsSpan(0, received)) is { } welcome)
                {
                    assignedId = welcome.AssignedPeerId;
                    return true;
                }
            }
            catch (SocketException)
            {
            }

            return false;
        }, TimeSpan.FromSeconds(10)));
        Assert.NotEqual(0u, assignedId);

        // Report a WorldDigest that would be absorbed if (and only if) the bogus session id
        // had been trusted: same module, differing save identity, LoadSerial unchanged. A raw
        // socket has no Tick loop of its own to keep resending it the way a real CoopClient
        // would, and SaveIdentitySettle only surfaces a mismatch after seeing it on repeated
        // comparisons spanning its 5 s debounce — so this resends on the same cadence a real
        // client's Tick() would, for as long as the wait keeps polling.
        int digestLength = PacketCodec.Write(
            send,
            new WorldDigestPacket(assignedId, new WorldDigest(1, 0x2222, Module, 7, LoadSerial: 0)));
        Assert.True(digestLength > 0);

        // ⚠ HandleWorldDigest does NOT refresh LastSeenMs — only Hello and StateUpdate do
        // (HandlePing answers with a Pong but never touches it either). A raw socket that only
        // ever resends the digest looks idle to CoopHost.Tick's own PeerTimeoutMs prune, which
        // would otherwise forget this peer right around the same 5 s mark SaveIdentitySettle's
        // debounce needs to elapse — a real CoopClient's own Tick() sends a StateUpdate every
        // tick regardless; mirror that here too so the peer stays live throughout the wait.
        byte[] stateBuffer = new byte[ProtocolConstants.MaxPacketSize];
        uint stateSequence = 0;
        long lastSendMs = 0;
        long lastStateMs = 0;
        Assert.True(await WaitForAsync(() =>
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - lastSendMs >= 300)
            {
                lastSendMs = nowMs;
                socket.SendTo(send.AsSpan(0, digestLength), hostEnd);
            }

            if (nowMs - lastStateMs >= 200)
            {
                lastStateMs = nowMs;
                int stateLength = PacketCodec.Write(
                    stateBuffer, new StateUpdatePacket(assignedId, ++stateSequence, nowMs, PlayerState.Empty));
                if (stateLength > 0)
                {
                    socket.SendTo(stateBuffer.AsSpan(0, stateLength), hostEnd);
                }
            }

            return host.Compatibility is { } verdict && verdict.Notes.Any(n => n.Code == CompatibilityCode.Save);
        }, TimeSpan.FromSeconds(15)),
            "a Hello naming a foreign host session id seeded the peer's lineage");

        await cts.CancelAsync();
        try
        {
            await hostTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
