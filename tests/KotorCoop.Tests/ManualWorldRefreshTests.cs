using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ManualWorldRefreshTests
{
    private static PlayerState Standing(uint flags = 0, string module = "end_m01aa") =>
        new(Vector3f.Zero, 0, module, 12, 0, Flags: flags);

    [Theory]
    [InlineData("reconnect")]
    [InlineData("rc")]
    public void OrdinaryReconnectKeepsItsExistingMeaning(string line)
    {
        CoopCommand command = ConsoleCommand.Parse(line);
        Assert.Equal(CoopCommandKind.Reconnect, command.Kind);
        Assert.Null(command.Address);
    }

    [Theory]
    [InlineData("reconnect world")]
    [InlineData("rc WORLD")]
    public void WorldRefreshMustBeRequestedExplicitly(string line)
    {
        CoopCommand command = ConsoleCommand.Parse(line);
        Assert.Equal(CoopCommandKind.Reconnect, command.Kind);
        Assert.Equal("world", command.Address);
    }

    [Theory]
    [InlineData("reconnect worlds")]
    [InlineData("reconnect world now")]
    public void TyposDoNotSilentlyReconnect(string line) =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);

    [Theory]
    [InlineData(PlayerState.FlagAway)]
    [InlineData(PlayerState.FlagInCombat)]
    [InlineData(PlayerState.FlagInDialogue)]
    [InlineData(PlayerState.FlagInMinigame)]
    [InlineData(PlayerState.FlagDowned)]
    [InlineData(PlayerState.FlagPartyWiped)]
    [InlineData(PlayerState.FlagMenuGuard)]
    public void BusyOrAbsentPlayerCannotBeCaptured(uint flags) =>
        Assert.False(ManualWorldRefreshRule.IsSafe(Standing(flags)));

    [Fact]
    public void ALoadInTheSameModuleInvalidatesTheCapture()
    {
        Assert.True(ManualWorldRefreshRule.IsSameWorld("END_M01AA", 7, Standing(), 7));
        Assert.False(ManualWorldRefreshRule.IsSameWorld("end_m01aa", 7, Standing(), 8));
        Assert.False(ManualWorldRefreshRule.IsSameWorld("end_m01aa", 7, Standing(module: "end_m01ab"), 7));
        Assert.False(ManualWorldRefreshRule.IsSafe(Standing(module: "")));
    }

    [Fact]
    public void AStaleMissingOrFutureArchiveIsNeverTheCurrentCharacter()
    {
        DateTime now = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(ManualWorldRefreshRule.IsFreshSave(now.AddSeconds(-30), now));
        Assert.False(ManualWorldRefreshRule.IsFreshSave(now.AddSeconds(-31), now));
        Assert.False(ManualWorldRefreshRule.IsFreshSave(DateTime.MinValue, now));
        Assert.False(ManualWorldRefreshRule.IsFreshSave(now.AddSeconds(1), now));
    }

    [Fact]
    public void HostCannotAccidentallyReloadItsWorldWithTheJoinerCommand()
    {
        using NullBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings());
        manager.Connect(true, null, new SessionSettings { Port = 0, PlayerName = "host" });
        ICoopSession? original = manager.Current;
        Assert.IsType<CoopHost>(original);
        List<string> messages = [];
        CoopCommandDispatcher dispatcher = new(manager, bridge, 7777, _ => new SessionSettings())
        { Log = messages.Add };
        dispatcher.Dispatch(ConsoleCommand.Parse("reconnect world"));
        Assert.Same(original, manager.Current);
        Assert.Contains(messages, m => m.Contains("needs a connected joiner"));
    }

    private sealed class NullBridge : IGameBridge
    {
        public string Description => "world-refresh-test";
        public bool IsAttached => false;
        public bool CanWriteRemotes => false;
        public bool TryReadLocalState(out PlayerState state) { state = default; return false; }
        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => false;
        public void Dispose() { }
    }

}

