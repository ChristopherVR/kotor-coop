using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The dispatcher's menu-connect surface: a bare Co-op button press resolved from coop.ini
/// (mode=host hosts, host=&lt;address&gt; joins, neither points at the server browser), and
/// the one-shot auto-load for sessions started from the main menu — fires on the host
/// immediately and on a joiner at Connected, never mid-world, never on a reconnect, never
/// after a refused join.
/// </summary>
public class CoopCommandDispatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Stands in for the game. <see cref="InWorld"/> false models the main menu: no
    /// readable player state, which is exactly the probe the auto-load trigger uses.
    /// </summary>
    private sealed class MenuBridge : IGameBridge
    {
        public volatile bool InWorld;
        public volatile bool HasReadableModule;

        public string Description => "menu-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
            return InWorld;
        }

        public bool TryReadCurrentModule(out string module)
        {
            module = "lev_m40aa";
            return HasReadableModule;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public void ClearCoopState()
        {
        }

        public void Dispose()
        {
        }
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

        return false;
    }

    private static SessionSettings Settings(string password) => new()
    {
        PlayerName = "MenuPlayer",
        Port = 0,
        TickRate = 30,
        Password = password,
    };

    [Fact]
    public void BareButtonPressWithHostModeConfigHostsAndAutoLoadsOnce()
    {
        MenuBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        int loads = 0;
        string? lastPassword = null;
        CoopConfig config = new() { Host = true, Password = "swordfish" };
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort,
            p => { lastPassword = p; return Settings(p); },
            () => config,
            () => loads++);

        Assert.False(manager.CanReconnect);

        dispatcher.OnConnectRequest(string.Empty);

        // mode=host in coop.ini: the press hosts, carries the file's password, and the
        // save auto-loads immediately (a host has nothing to wait for).
        Assert.NotNull(manager.Current);
        Assert.Equal("swordfish", lastPassword);
        Assert.Equal(1, loads);
        Assert.True(manager.CanReconnect);

        // A second bare press now means "reconnect" — and a reconnect must NOT re-fire
        // the auto-load.
        dispatcher.OnConnectRequest(string.Empty);
        Assert.NotNull(manager.Current);
        Assert.Equal(1, loads);

        manager.Disconnect();
    }

    [Fact]
    public void HostMenuKeepsConfiguredPasswordAndDoesNotRestartAnExistingHost()
    {
        MenuBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        string? password = null;
        int loads = 0;
        CoopCommandDispatcher dispatcher = new(manager, bridge, ProtocolConstants.DefaultPort,
            value => { password = value; return Settings(value); },
            () => new CoopConfig { Password = "private-session" }, () => loads++);
        dispatcher.RunTypedLine("host");
        Assert.Equal("private-session", password);
        var session = manager.Current;
        Assert.NotNull(session);
        dispatcher.RunTypedLine("host");
        Assert.Same(session, manager.Current);
        Assert.Equal(1, loads);
    }

    [Fact]
    public void HelpAndRetryWithoutHistoryDoNotStartOrLoadASession()
    {
        MenuBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        int loads = 0;
        CoopCommandDispatcher dispatcher = new(manager, bridge, ProtocolConstants.DefaultPort,
            Settings, menuAutoLoad: () => loads++);
        dispatcher.RunTypedLine("connectionhelp");
        dispatcher.RunTypedLine("reconnect");
        Assert.Null(manager.Current);
        Assert.False(manager.CanReconnect);
        Assert.Equal(0, loads);
    }

    /// <summary>
    /// A host's menu auto-load fires immediately after Dispatch returns, before anything
    /// else looks at whether the connect actually succeeded. A busy port (item 6c) now
    /// fails Connect cleanly instead of throwing, and FireMenuAutoLoadIfArmed must notice
    /// there is no live session rather than load a save into a game nobody is driving.
    /// </summary>
    [Fact]
    public void BareButtonPressHostingOnABusyPortNeverAutoLoads()
    {
        using UdpChannel blocker = new(0);
        int busyPort = blocker.LocalEndPoint.Port;

        MenuBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        int loads = 0;
        CoopConfig config = new() { Host = true };
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort,
            p => new SessionSettings { PlayerName = "MenuPlayer", Port = busyPort, TickRate = 30, Password = p },
            () => config,
            () => loads++);

        dispatcher.OnConnectRequest(string.Empty);

        Assert.Equal(SessionPhase.Idle, manager.Phase);
        Assert.Null(manager.Current);
        Assert.Equal(0, loads);
    }

    [Fact]
    public void BareButtonPressMidWorldHostsButNeverLoadsASave()
    {
        MenuBridge bridge = new() { InWorld = true };
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        int loads = 0;
        CoopConfig config = new() { Host = true };
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort, Settings, () => config, () => loads++);

        dispatcher.OnConnectRequest(string.Empty);

        // The session starts, but the game is already in a world — loading a save out
        // from under the player would be a disaster, so the trigger must hold its fire.
        Assert.NotNull(manager.Current);
        Assert.Equal(0, loads);

        manager.Disconnect();
    }

    [Fact]
    public void BareButtonPressWithNothingConfiguredRepliesAndStartsNothing()
    {
        MenuBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        int loads = 0;
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort, Settings,
            () => null, // no coop.ini
            () => loads++);

        dispatcher.OnConnectRequest(string.Empty);

        // No crash, no session, no load — the player is told to pick a server instead.
        Assert.Equal(SessionPhase.Idle, manager.Phase);
        Assert.Null(manager.Current);
        Assert.False(manager.CanReconnect);
        Assert.Equal(0, loads);
    }

    [Fact]
    public async Task BareButtonPressWithConfiguredAddressJoinsAndLoadsOnConnectedExactlyOnce()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource hostCts = new(Timeout + TimeSpan.FromSeconds(25));
        Task hostTask = host.RunAsync(hostCts.Token);

        MenuBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        int loads = 0;
        CoopConfig config = new()
        {
            HasHostAddress = true,
            HostAddress = "127.0.0.1",
            Port = host.ListenEndPoint.Port,
        };
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort, Settings,
            () => config,
            () => Interlocked.Increment(ref loads));

        // The press dials the configured host; the load waits for Connected.
        dispatcher.OnConnectRequest(string.Empty);
        Assert.Equal(0, loads);

        using (CancellationTokenSource dispatchCts = new())
        {
            Task dispatchTask = dispatcher.RunAsync(dispatchCts.Token);
            Assert.True(
                await WaitForAsync(() => Volatile.Read(ref loads) == 1, Timeout),
                "the menu-initiated join never fired the auto-load");
            Assert.Equal(SessionPhase.Connected, manager.Phase);
            await dispatchCts.CancelAsync();
            await dispatchTask;
        }

        // A bare press with a session behind it is a reconnect, which must not re-fire
        // the load even though the session passes through Connected again.
        dispatcher.OnConnectRequest(string.Empty);
        using (CancellationTokenSource dispatchCts = new())
        {
            Task dispatchTask = dispatcher.RunAsync(dispatchCts.Token);
            Assert.True(
                await WaitForAsync(() => manager.Phase == SessionPhase.Connected, Timeout),
                "the reconnect never reached Connected");
            await Task.Delay(300); // give a wrongful second fire the chance to happen
            Assert.Equal(1, Volatile.Read(ref loads));
            await dispatchCts.CancelAsync();
            await dispatchTask;
        }

        manager.Disconnect();
        await hostCts.CancelAsync();
        try
        {
            await hostTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task RefusedJoinNeverAutoLoads()
    {
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, TickRate = 30, Password = "swordfish",
        });
        using CancellationTokenSource hostCts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(hostCts.Token);

        MenuBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        int loads = 0;
        CoopConfig config = new()
        {
            HasHostAddress = true,
            HostAddress = "127.0.0.1",
            Port = host.ListenEndPoint.Port,
            // No password: the join is rejected with E_PASSWORD.
        };
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort, Settings,
            () => config,
            () => Interlocked.Increment(ref loads));

        dispatcher.OnConnectRequest(string.Empty);

        using CancellationTokenSource dispatchCts = new();
        Task dispatchTask = dispatcher.RunAsync(dispatchCts.Token);
        Assert.True(
            await WaitForAsync(() => manager.Phase == SessionPhase.Failed, Timeout),
            "the passworded host never refused the join");
        await Task.Delay(300); // a late fire would land here
        Assert.Equal(0, Volatile.Read(ref loads));

        await dispatchCts.CancelAsync();
        await dispatchTask;
        manager.Disconnect();
        await hostCts.CancelAsync();
        try
        {
            await hostTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Pins the fix for the round-86 "target machine actively refused it" bug: nothing in a
    /// live host session used to call <see cref="WorldTransfer.ServeAsync(System.Func{(System.Collections.Generic.IReadOnlyList{WorldFile},uint)},int,uint,System.Action{string}?,CancellationToken)"/>
    /// at all, so a client's <c>RejoinAfterLoadAsync</c> fetch was always refused outright —
    /// not raced against a rebuild, refused because there was never a listener on the other
    /// end. With either rejoin gate on, a live host must actually serve its current session
    /// save over <see cref="WorldTransfer"/> on the SAME port its co-op session bound.
    /// </summary>
    [Fact]
    public async Task HostingWithARejoinGateOnServesItsWorldOverWorldTransfer()
    {
        string gameDirectory = Path.Combine(
            Path.GetTempPath(), "kotorcoop-worldserve-" + Guid.NewGuid().ToString("N"));
        string saveDirectory = Path.Combine(gameDirectory, "saves", "000001 - AUTOSAVE");
        Directory.CreateDirectory(saveDirectory);
        byte[] saveBytes = BuildValidWorldSave("lev_m40aa");
        File.WriteAllBytes(Path.Combine(saveDirectory, "SAVEGAME.sav"), saveBytes);

        int port = FreePort();

        MenuBridge bridge = new() { HasReadableModule = true };
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        CoopConfig config = new() { ClientLoadRejoin = true };
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort,
            p => new SessionSettings { PlayerName = "Host", Port = port, TickRate = 30, Password = p },
            () => config,
            gameDirectory: gameDirectory);

        try
        {
            dispatcher.Dispatch(new CoopCommand(CoopCommandKind.Host));
            Assert.IsType<CoopHost>(manager.Current);

            using CancellationTokenSource dispatchCts = new();
            Task dispatchTask = dispatcher.RunAsync(dispatchCts.Token);

            using CancellationTokenSource fetchCts = new(TimeSpan.FromSeconds(10));
            IReadOnlyList<WorldFile> received = (await FetchWithRetryAsync(
                new IPEndPoint(IPAddress.Loopback, port), SessionPassword.None, fetchCts.Token)).Files;

            WorldFile saveFile = Assert.Single(received);
            Assert.Equal("SAVEGAME.sav", saveFile.Name);
            Assert.Equal(saveBytes, saveFile.Data);

            await dispatchCts.CancelAsync();
            await dispatchTask;
        }
        finally
        {
            manager.Disconnect();

            try
            {
                Directory.Delete(gameDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The same listener, reached through the gate a first-time joiner actually depends on.
    ///
    /// ⚠ THIS IS THE ONE THAT MATTERS FOR <c>join_mode = newcharacter</c>. A joiner with no
    /// save has no world of their own and cannot obtain one any other way — if the host is not
    /// serving, there is nothing for them to adopt and the whole flow dead-ends at "could not
    /// fetch the host's world". <c>auto_adopt_world</c> is ON by default precisely so that a
    /// host who has configured nothing still serves.
    /// </summary>
    [Fact]
    public async Task HostingWithOnlyAutoAdoptOnStillServesItsWorld()
    {
        string gameDirectory = Path.Combine(
            Path.GetTempPath(), "kotorcoop-worldserve-adopt-" + Guid.NewGuid().ToString("N"));
        string saveDirectory = Path.Combine(gameDirectory, "saves", "000001 - AUTOSAVE");
        Directory.CreateDirectory(saveDirectory);
        byte[] saveBytes = BuildValidWorldSave("lev_m40aa");
        File.WriteAllBytes(Path.Combine(saveDirectory, "SAVEGAME.sav"), saveBytes);

        int port = FreePort();

        MenuBridge bridge = new() { HasReadableModule = true };
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        CoopConfig config = new()
        {
            ClientLoadRejoin = false,
            HostLoadReadopt = false,
            AutoAdoptWorld = true,
        };

        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort,
            p => new SessionSettings { PlayerName = "Host", Port = port, TickRate = 30, Password = p },
            () => config,
            gameDirectory: gameDirectory);

        try
        {
            dispatcher.Dispatch(new CoopCommand(CoopCommandKind.Host));
            Assert.IsType<CoopHost>(manager.Current);

            using CancellationTokenSource dispatchCts = new();
            Task dispatchTask = dispatcher.RunAsync(dispatchCts.Token);

            using CancellationTokenSource fetchCts = new(TimeSpan.FromSeconds(10));
            IReadOnlyList<WorldFile> received = (await FetchWithRetryAsync(
                new IPEndPoint(IPAddress.Loopback, port), SessionPassword.None, fetchCts.Token)).Files;

            WorldFile saveFile = Assert.Single(received);
            Assert.Equal(saveBytes, saveFile.Data);

            await dispatchCts.CancelAsync();
            await dispatchTask;
        }
        finally
        {
            manager.Disconnect();

            try
            {
                Directory.Delete(gameDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task HostingWithBothRejoinGatesOffServesNothing()
    {
        // All THREE gates default ON (client_load_rejoin/host_load_readopt since their
        // 2026-08-24 live verification, auto_adopt_world since join_mode landed — see
        // CoopConfig's own doc comments); this is the regression check that a host with every
        // one explicitly turned off does not open a new listener at all.
        string gameDirectory = Path.Combine(
            Path.GetTempPath(), "kotorcoop-worldserve-off-" + Guid.NewGuid().ToString("N"));
        string saveDirectory = Path.Combine(gameDirectory, "saves", "000001 - AUTOSAVE");
        Directory.CreateDirectory(saveDirectory);
        File.WriteAllBytes(Path.Combine(saveDirectory, "SAVEGAME.sav"), [1, 2, 3]);

        int port = FreePort();

        MenuBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        CoopConfig config = new()
        {
            ClientLoadRejoin = false,
            HostLoadReadopt = false,
            AutoAdoptWorld = false,
        };
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort,
            p => new SessionSettings { PlayerName = "Host", Port = port, TickRate = 30, Password = p },
            () => config,
            gameDirectory: gameDirectory);

        try
        {
            dispatcher.Dispatch(new CoopCommand(CoopCommandKind.Host));
            Assert.IsType<CoopHost>(manager.Current);

            using CancellationTokenSource dispatchCts = new();
            Task dispatchTask = dispatcher.RunAsync(dispatchCts.Token);

            // Give the tick loop several chances to (wrongly) start serving before asserting
            // it never does.
            await Task.Delay(500);

            using TcpClient probe = new();
            SocketException refused = await Assert.ThrowsAsync<SocketException>(
                () => probe.ConnectAsync(IPAddress.Loopback, port));
            Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);

            await dispatchCts.CancelAsync();
            await dispatchTask;
        }
        finally
        {
            manager.Disconnect();

            try
            {
                Directory.Delete(gameDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A minimal but parseable save, so the host-serving path exercises the same
    /// identity and active-module guards used for a real adopted world.</summary>
    private static byte[] BuildValidWorldSave(string module)
    {
        GffWriter ifo = new("IFO ");
        ifo.Root.SetDword("Mod_NextObjId0", 295);
        ifo.Root.SetDword("Mod_NextObjId1", 0);

        GffWriter git = new("GIT ");
        git.Root.AddList("Creature List")
            .Add(4)
            .SetResRef("TemplateResRef", "n_sith")
            .SetDword("ObjectId", 2);

        byte[] moduleSave = BuildErf("SAV ",
            ("Module", 2014, ifo.ToBytes()),
            (module, 2023, git.ToBytes()));
        return BuildErf("SAV ", (module, 2057, moduleSave));
    }

    /// <summary>Minimal ERF builder matching the archive header/table decoded by
    /// <see cref="ErfArchive"/>.</summary>
    private static byte[] BuildErf(string fileType, params (string Resref, int Type, byte[] Data)[] members)
    {
        int keysOffset = 160;
        int valuesOffset = keysOffset + 24 * members.Length;
        int dataOffset = valuesOffset + 8 * members.Length;
        byte[] file = new byte[dataOffset + members.Sum(member => member.Data.Length)];
        Encoding.ASCII.GetBytes(fileType.PadRight(4)).CopyTo(file, 0);
        Encoding.ASCII.GetBytes("V1.0").CopyTo(file, 4);
        WriteU32(file, 0x10, (uint)members.Length);
        WriteU32(file, 0x18, (uint)keysOffset);
        WriteU32(file, 0x1C, (uint)valuesOffset);

        int nextData = dataOffset;
        for (int i = 0; i < members.Length; i++)
        {
            (string resref, int type, byte[] data) = members[i];
            int key = keysOffset + 24 * i;
            Encoding.ASCII.GetBytes(resref).CopyTo(file, key);
            WriteU32(file, key + 16, (uint)i);
            file[key + 20] = (byte)type;
            file[key + 21] = (byte)(type >> 8);

            int value = valuesOffset + 8 * i;
            WriteU32(file, value, (uint)nextData);
            WriteU32(file, value + 4, (uint)data.Length);
            data.CopyTo(file, nextData);
            nextData += data.Length;
        }

        return file;
    }

    private static void WriteU32(byte[] destination, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset, 4), value);

    /// <summary>
    /// A port the host can open BOTH ways: its game channel is UDP and its world-serve
    /// listener is TCP on the same number. Probing TCP alone (the old helper) handed out ports
    /// inside Windows' UDP exclusion ranges (`netsh int ipv4 show excludedportrange udp` —
    /// Hyper-V/WSL reserve 100- and 1000-port blocks in the ephemeral range, and ephemeral
    /// ports are dealt sequentially), where <c>UdpChannel</c>'s bind fails with WSAEACCES, the
    /// dispatcher's Host lands on "could not start the session", and every Hosting* test in a
    /// row failed with `manager.Current` null until the sequence walked out of the block
    /// (2026-09-03, 3/3 red then 3/3 green with no code change in between).
    ///
    /// Second lesson (2026-09-03, full run after the galaxy-map relay): letting the UDP stack
    /// pick the number is itself the trap — ephemeral ports are dealt SEQUENTIALLY, so when the
    /// sequence sits inside a TCP-excluded block, all 50 probes land in that same block and four
    /// Hosting* tests fail with "no port free … after 50 probes". So this picks RANDOM numbers
    /// in a fixed high range instead and binds BOTH protocols explicitly on each: two adjacent
    /// draws share nothing, and an excluded block costs one attempt, not fifty.
    /// </summary>
    private static int FreePort()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            int port = Random.Shared.Next(20000, 60000);

            try
            {
                using Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(IPAddress.Any, port));

                using TcpListener tcp = new(IPAddress.Loopback, port);
                tcp.Start();
                tcp.Stop();
                return port;
            }
            catch (SocketException)
            {
                // One side taken or excluded on that number; draw another.
            }
        }

        throw new InvalidOperationException("no port free for both UDP and TCP after 50 random probes");
    }

    /// <summary>The world-serve listener starts on the dispatcher's own 150 ms tick, so the
    /// very first fetch attempt can legitimately race it — retry rather than requiring the
    /// caller to guess a safe delay.</summary>
    private static async Task<FetchedWorld> FetchWithRetryAsync(
        IPEndPoint endpoint, uint passwordHash, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await WorldTransfer.FetchAsync(endpoint, passwordHash, cancellationToken);
            }
            catch (SocketException)
            {
                await Task.Delay(20, cancellationToken);
            }
        }
    }
}
