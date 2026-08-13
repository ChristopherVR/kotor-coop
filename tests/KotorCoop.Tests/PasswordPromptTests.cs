using System.Net;
using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// What happens when you press Connect on a server that wants a password.
///
/// Both halves of this were broken live and both are covered here: a refused join used to load
/// the world anyway (so the player stood in single-player believing they were in co-op), and
/// nothing ever asked for a password, because the browser's own Connect went straight to
/// <see cref="SessionManager"/> and skipped the only code that knew a prompt was owed.
/// </summary>
public class PasswordPromptTests
{
    private sealed class StubBridge : IGameBridge
    {
        public string Description => "password-prompt-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public void ClearCoopState()
        {
        }

        public void Dispose()
        {
        }
    }

    private static async Task<bool> WaitForPhase(
        SessionManager manager, SessionPhase phase, ICoopSession? pump = null)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);

        while (DateTime.UtcNow < deadline)
        {
            pump?.PublishLocalState(state);
            manager.Poll();
            if (manager.Phase == phase)
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    [Fact]
    public async Task AWrongPasswordFailsAndTheTypedOneGetsIn()
    {
        // The live scenario end to end: coop.ini holds the wrong password, the host refuses,
        // and the player answers the prompt with `password <right>` — which must retry the SAME
        // server without them retyping an address they never saw.
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, TickRate = 30, Password = "hoth",
        });

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(40));
        Task hostTask = host.RunAsync(cts.Token);
        int port = host.ListenEndPoint.Port;

        using StubBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        SessionSettings SettingsFor(string password) => new()
        {
            PlayerName = "Malak", TickRate = 30, Password = password,
        };

        CoopCommandDispatcher dispatcher = new(manager, bridge, port, SettingsFor);

        dispatcher.Dispatch(new CoopCommand(
            CoopCommandKind.Connect, Address: $"127.0.0.1:{port}", Password: "wrong"));

        Assert.True(await WaitForPhase(manager, SessionPhase.Failed, host),
            "the host admitted a client with the wrong password");
        Assert.Contains("password", manager.Detail, StringComparison.OrdinalIgnoreCase);

        // The answer carries the secret and nothing else.
        dispatcher.Dispatch(ConsoleCommand.Parse("password hoth"));

        Assert.True(await WaitForPhase(manager, SessionPhase.Connected, host),
            "answering the prompt did not retry the join that asked");

        manager.Disconnect();
        await cts.CancelAsync();
        await hostTask;
    }

    [Fact]
    public void AnAnswerWithNothingWaitingConnectsToNothing()
    {
        // `password x` with no attempt waiting must not dial whatever was last in mind. It is
        // no longer an error though: the in-browser prompt sends the password BEFORE the row
        // confirmation, so an unsolicited answer is an OFFER — held for the join about to
        // start, dialling nothing by itself.
        using StubBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, 27140, _ => new SessionSettings { PlayerName = "Malak" });

        dispatcher.Dispatch(ConsoleCommand.Parse("password hoth"));

        Assert.Equal(SessionPhase.Idle, manager.Phase);
        Assert.Null(manager.Current);
    }

    [Fact]
    public async Task AnUnsolicitedAnswerIsSavedAndUsedByTheNextJoin()
    {
        // The in-browser prompt's exact ordering, agent-side: the `password <pw>` console line
        // lands FIRST (nothing waiting — an offer), the join starts a moment later carrying no
        // password of its own. The offered one must get it in, with no prompt round-trip.
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, TickRate = 30, Password = "hoth",
        });

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(40));
        Task hostTask = host.RunAsync(cts.Token);
        int port = host.ListenEndPoint.Port;

        using StubBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        CoopCommandDispatcher dispatcher = new(manager, bridge, port,
            password => new SessionSettings { PlayerName = "Malak", TickRate = 30, Password = password });

        // The offer: nothing is waiting, so nothing is dialled.
        dispatcher.Dispatch(ConsoleCommand.Parse("password hoth"));
        Assert.Equal(SessionPhase.Idle, manager.Phase);
        Assert.Null(manager.Current);

        // The join that follows names no password; the offered one rides along.
        dispatcher.Dispatch(new CoopCommand(CoopCommandKind.Connect, Address: $"127.0.0.1:{port}"));

        Assert.True(await WaitForPhase(manager, SessionPhase.Connected, host),
            "the join did not reuse the password offered ahead of it");

        manager.Disconnect();
        await cts.CancelAsync();
        await hostTask;
    }

    [Fact]
    public async Task AWrongOfferStillGetsARetryPrompt()
    {
        // An offered password that turns out wrong must degrade into the existing retry flow:
        // the join fails with E_PASSWORD, the retry answer dials the SAME server again.
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, TickRate = 30, Password = "hoth",
        });

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(40));
        Task hostTask = host.RunAsync(cts.Token);
        int port = host.ListenEndPoint.Port;

        using StubBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        CoopCommandDispatcher dispatcher = new(manager, bridge, port,
            password => new SessionSettings { PlayerName = "Malak", TickRate = 30, Password = password });

        dispatcher.Dispatch(ConsoleCommand.Parse("password wrong"));
        dispatcher.Dispatch(new CoopCommand(CoopCommandKind.Connect, Address: $"127.0.0.1:{port}"));

        Assert.True(await WaitForPhase(manager, SessionPhase.Failed, host),
            "the host admitted a client with the wrong offered password");
        Assert.Contains("password", manager.Detail, StringComparison.OrdinalIgnoreCase);

        // Now a join IS waiting: the same verb is a retry, not an offer.
        dispatcher.Dispatch(ConsoleCommand.Parse("password hoth"));

        Assert.True(await WaitForPhase(manager, SessionPhase.Connected, host),
            "answering the retry prompt did not redial the join that asked");

        manager.Disconnect();
        await cts.CancelAsync();
        await hostTask;
    }

    [Fact]
    public async Task AJoinerLoadsTheWorldOnlyOnceItIsAdmitted()
    {
        // The bug that put a rejected player on the Endar Spire: the load ran in parallel with
        // a handshake that had not happened yet.
        using StubBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        using CancellationTokenSource cts = new();

        int loads = 0;
        Task waiter = MenuAutoLoad.RunAsync(
            manager, host: false, () => Interlocked.Increment(ref loads), cts.Token, pollMs: 10);

        // Nothing connected: nothing loaded, however long we wait.
        await Task.Delay(300);
        Assert.Equal(0, loads);
        Assert.False(waiter.IsCompleted);

        // Now admit it for real.
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource hostCts = new(TimeSpan.FromSeconds(30));
        Task hostTask = host.RunAsync(hostCts.Token);

        manager.Connect(
            host: false,
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port),
            new SessionSettings { PlayerName = "Malak", TickRate = 30 });

        Assert.True(await WaitForPhase(manager, SessionPhase.Connected, host));
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, loads);

        manager.Disconnect();
        await cts.CancelAsync();
        await hostCts.CancelAsync();
        await hostTask;
    }

    [Fact]
    public async Task ARefusedJoinNeverLoadsTheWorld()
    {
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, TickRate = 30, Password = "hoth",
        });

        using CancellationTokenSource hostCts = new(TimeSpan.FromSeconds(30));
        Task hostTask = host.RunAsync(hostCts.Token);

        using StubBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        using CancellationTokenSource cts = new();

        int loads = 0;
        string? announced = null;
        Task waiter = MenuAutoLoad.RunAsync(
            manager, host: false, () => Interlocked.Increment(ref loads), cts.Token,
            announce: m => announced = m, pollMs: 10);

        manager.Connect(
            host: false,
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port),
            new SessionSettings { PlayerName = "Malak", TickRate = 30, Password = "wrong" });

        Assert.True(await WaitForPhase(manager, SessionPhase.Failed, host));
        await Task.Delay(300);

        Assert.Equal(0, loads);
        Assert.False(waiter.IsCompleted);
        Assert.NotNull(announced);

        await cts.CancelAsync();
        await waiter;
        await hostCts.CancelAsync();
        await hostTask;
    }

    [Fact]
    public async Task AHostLoadsWithoutWaitingForAnybody()
    {
        // A host has nobody to be admitted by, and has to be in the world for a client to have
        // anything to join — so the gate must not apply to it.
        using StubBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        using CancellationTokenSource cts = new();

        int loads = 0;
        await MenuAutoLoad.RunAsync(
            manager, host: true, () => Interlocked.Increment(ref loads), cts.Token, pollMs: 10);

        Assert.Equal(1, loads);
    }
}
