using System.Reflection;
using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Guards the CoopCommandDispatcher.Dispatch refactor (the 48-case switch became a
/// <c>Dictionary&lt;CoopCommandKind, Action&lt;CoopCommand&gt;&gt;</c> built once by
/// <c>CoopCommandDispatcher.BuildDispatchTable</c>): every <see cref="CoopCommandKind"/> value
/// must have a row in that table, the same way the old switch handled every kind (a `None`/
/// `Unknown` shared case, an `Invalid` case, and one case per verb). A kind that loses its row
/// would silently become a no-op instead of failing loudly, so this reads the table via
/// reflection and checks it against the full enum rather than trusting the switch is complete.
/// </summary>
public class CoopCommandDispatcherTableCoverageTests
{
    /// <summary>Bare-bones stand-in for the game: no readable player state, no remotes. Only
    /// exists so a dispatcher can be constructed; no test here calls into the game.</summary>
    private sealed class NullBridge : IGameBridge
    {
        public string Description => "null-bridge-test";

        public bool IsAttached => false;

        public bool CanWriteRemotes => false;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = default;
            return false;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => false;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void DispatchTableHasARowForEveryCoopCommandKind()
    {
        NullBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, ProtocolConstants.DefaultPort,
            p => new SessionSettings { PlayerName = "x", Port = 0, TickRate = 30, Password = p });

        PropertyInfo? tableProperty = typeof(CoopCommandDispatcher)
            .GetProperty("DispatchTable", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(tableProperty);

        var table = Assert.IsType<Dictionary<CoopCommandKind, Action<CoopCommand>>>(
            tableProperty!.GetValue(dispatcher));

        foreach (CoopCommandKind kind in Enum.GetValues<CoopCommandKind>())
        {
            Assert.True(table.ContainsKey(kind), $"dispatch table has no row for {kind}");
        }

        // Exact count too, not just "at least" - a stray duplicate key would silently shadow
        // an earlier row without failing the containment check above.
        Assert.Equal(Enum.GetValues<CoopCommandKind>().Length, table.Count);
    }
}
