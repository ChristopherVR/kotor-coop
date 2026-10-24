using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class SteamStartupJoinTests
{
    private sealed class MenuBridge : IGameBridge
    {
        public string Description => "Steam startup test";
        public bool IsAttached => true;
        public bool CanWriteRemotes => true;
        public bool TryReadLocalState(out PlayerState state) { state = default; return false; }
        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;
        public void ClearCoopState() { }
        public void Dispose() { }
    }

    [Theory]
    [InlineData("fixture-password", true)]
    [InlineData("wrong", false)]
    public async Task StartupInviteLoadsOnlyAfterItsPasswordHandshakeIsAccepted(string password, bool accepted)
    {
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Host", Port = 0, TickRate = 30, Password = "fixture-password",
        });
        using MenuBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
        Task hostTask = host.RunAsync(stop.Token);
        int loads = 0;
        CoopCommandDispatcher dispatcher = new(manager, bridge, ProtocolConstants.DefaultPort,
            p => new SessionSettings { PlayerName = "Joiner", Port = 0, TickRate = 30, Password = p },
            menuAutoLoad: () => Interlocked.Increment(ref loads));
        dispatcher.JoinFromSteam($"127.0.0.1:{host.ListenEndPoint.Port}", password);
        Assert.Equal(0, loads);
        Task dispatchTask = dispatcher.RunAsync(stop.Token);
        try
        {
            SessionPhase expected = accepted ? SessionPhase.Connected : SessionPhase.Failed;
            while (manager.Phase != expected || (accepted && Volatile.Read(ref loads) == 0))
            {
                await Task.Delay(20, stop.Token);
            }
            await Task.Delay(300, stop.Token);
            Assert.Equal(accepted ? 1 : 0, Volatile.Read(ref loads));
        }
        finally
        {
            await stop.CancelAsync();
            manager.Disconnect();
            try { await Task.WhenAll(hostTask, dispatchTask); }
            catch (OperationCanceledException) { }
        }
    }
}
