using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="IGameBridge"/> is now the composite of sixteen role interfaces
/// (<see cref="IPlayerBridge"/>, <see cref="IPartyBridge"/>, <see cref="IWorldStateBridge"/>,
/// <see cref="IDoorBridge"/>, and so on), split out of one 999-line file. This is the
/// regression the split must never reintroduce: a bridge or test fake that implements only
/// the five genuinely abstract members must still compile against <see cref="IGameBridge"/> as
/// a whole, and every capability flag must still default to false — exactly as it did when
/// they were all declared in one interface. Compiling is most of the proof; the assertions
/// below are the cheap runtime half.
/// </summary>
public class GameBridgeInterfaceShapeTests
{
    /// <summary>The only members <see cref="IGameBridge"/> does not give a default body for.
    /// If this class fails to compile, the split dropped a default implementation somewhere.</summary>
    private sealed class MinimalBridge : IGameBridge
    {
        public string Description => "minimal-bridge";

        public bool IsAttached => false;

        public bool CanWriteRemotes => false;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = PlayerState.Empty;
            return false;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => false;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void MinimalFakeImplementingOnlyTheFiveAbstractMembersCompiles()
    {
        using IGameBridge bridge = new MinimalBridge();

        Assert.False(bridge.IsAttached);
        Assert.False(bridge.CanWriteRemotes);
    }

    [Fact]
    public void EveryCapabilityFlagDefaultsFalse()
    {
        using IGameBridge bridge = new MinimalBridge();

        Assert.False(bridge.SupportsGroundItems);
        Assert.False(bridge.SupportsWorldState);
        Assert.False(bridge.SupportsDoorSync);
        Assert.False(bridge.SupportsPlaceableSync);
        Assert.False(bridge.SupportsContainerLoot);
        Assert.False(bridge.SupportsPauseSync);
        Assert.False(bridge.SupportsCombatSync);
        Assert.False(bridge.SupportsTravel);
        Assert.False(bridge.SupportsEnemySync);
        Assert.False(bridge.SupportsPartySync);
        Assert.False(bridge.SupportsDeathHandling);
        Assert.False(bridge.SupportsProgressionSync);
        Assert.False(bridge.SupportsCheats);
        Assert.False(bridge.SupportsJournalSync);
    }
}
