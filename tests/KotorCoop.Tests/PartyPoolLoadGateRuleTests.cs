using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PartyPoolLoadGateRuleTests
{
    [Fact]
    public void ClientWaitsForInitialAlignmentButAllowsLaterOrdinarySaveDivergence()
    {
        (PartyPoolLoadGateState state, bool hold) = PartyPoolLoadGateRule.ObserveClient(
            PartyPoolLoadGateState.Initial, 3, WorldDigest.Empty, localWorldReadable: true);
        Assert.True(hold);

        WorldDigest priorHost = new(10, 20, "danm13", 30, LoadSerial: 8);
        (state, hold) = PartyPoolLoadGateRule.ObserveClient(state, 3, priorHost, localWorldReadable: true);
        Assert.True(hold);

        state = PartyPoolLoadGateRule.ObserveInitialWorldAlignment(state, priorHost, priorHost);
        Assert.True(state.HasInitialAlignedWorld);
        (state, hold) = PartyPoolLoadGateRule.ObserveClient(state, 3, priorHost, localWorldReadable: true);
        Assert.False(hold);

        // Saves are allowed to diverge without a real load edge.
        WorldDigest ordinarySaveDivergence = priorHost with { SaveIdentity = 21 };
        (state, hold) = PartyPoolLoadGateRule.ObserveClient(
            state, 3, ordinarySaveDivergence, localWorldReadable: true);
        Assert.False(hold);
    }

    [Fact]
    public void ClientHoldsAfterEitherRealLoadEdgeUntilItsAgentIsReplaced()
    {
        WorldDigest host = new(10, 20, "danm13", 30, LoadSerial: 8);
        (PartyPoolLoadGateState state, bool hold) = PartyPoolLoadGateRule.ObserveClient(
            PartyPoolLoadGateState.Initial, 3, host, localWorldReadable: true);
        Assert.True(hold);
        state = PartyPoolLoadGateRule.ObserveInitialWorldAlignment(state, host, host);
        (state, hold) = PartyPoolLoadGateRule.ObserveClient(state, 3, host, localWorldReadable: true);
        Assert.False(hold);

        (state, hold) = PartyPoolLoadGateRule.ObserveClient(
            state, 4, host, localWorldReadable: true);
        Assert.True(hold);

        WorldDigest adopted = host with { SaveIdentity = 22, LoadSerial = 9 };
        (state, hold) = PartyPoolLoadGateRule.ObserveClient(
            state, 5, adopted, localWorldReadable: true);
        Assert.True(hold);

        // CompleteRejoin creates a fresh CoopAgent; its initial state adopts current serials,
        // but pool reconciliation waits for its first matching ready digest.
        (PartyPoolLoadGateState fresh, hold) = PartyPoolLoadGateRule.ObserveClient(
            PartyPoolLoadGateState.Initial, 5, adopted, localWorldReadable: true);
        Assert.True(hold);
        fresh = PartyPoolLoadGateRule.ObserveInitialWorldAlignment(fresh, adopted, adopted);
        (_, hold) = PartyPoolLoadGateRule.ObserveClient(fresh, 5, adopted, localWorldReadable: true);
        Assert.False(hold);
    }

    [Fact]
    public void UnavailableAndUnknownStartupWorldsHoldUntilInitialAlignment()
    {
        (_, bool hold) = PartyPoolLoadGateRule.ObserveClient(
            PartyPoolLoadGateState.Initial, 5, WorldDigest.Empty, localWorldReadable: true);
        Assert.True(hold);

        WorldDigest knownUnavailable = new(10, 20, string.Empty, 0, LoadSerial: 9);
        (_, hold) = PartyPoolLoadGateRule.ObserveClient(
            PartyPoolLoadGateState.Initial, 5, knownUnavailable, localWorldReadable: true);
        Assert.True(hold);

        WorldDigest ready = knownUnavailable with { Module = "danm13", ModuleGit = 30 };
        (PartyPoolLoadGateState state, hold) = PartyPoolLoadGateRule.ObserveClient(
            PartyPoolLoadGateState.Initial with { HasLocalSerial = true, LocalSerial = 5,
                HasRemoteSerial = true, RemoteSerial = 9 },
            5, ready, localWorldReadable: true);
        Assert.True(hold);
        state = PartyPoolLoadGateRule.ObserveInitialWorldAlignment(state, ready, ready);
        (_, hold) = PartyPoolLoadGateRule.ObserveClient(state, 5, ready, localWorldReadable: true);
        Assert.False(hold);
    }

    [Fact]
    public void HostLoadHoldNeedsReadyPostEdgeSample()
    {
        (PartyPoolLoadGateState state, bool hold) = PartyPoolLoadGateRule.ObserveHost(
            PartyPoolLoadGateState.Initial, localLoadSerial: 10, postEdgeWorldReady: true);
        Assert.False(hold);

        (state, hold) = PartyPoolLoadGateRule.ObserveHost(
            state, localLoadSerial: 11, postEdgeWorldReady: false);
        Assert.True(hold);

        (state, hold) = PartyPoolLoadGateRule.ObserveHost(
            state, localLoadSerial: 11, postEdgeWorldReady: true);
        Assert.False(hold);
    }

    [Fact]
    public void InitialPoolAlignmentRequiresSameReadySaveAndModule()
    {
        WorldDigest host = new(10, 20, "danm13", 30, LoadSerial: 8);
        WorldDigest differentSave = host with { SaveIdentity = 21 };
        WorldDigest differentModule = host with { Module = "lev_m40aa" };
        Assert.False(PartyPoolLoadGateRule.PeerMatchesReadyWorld(host, differentSave));
        Assert.False(PartyPoolLoadGateRule.PeerMatchesReadyWorld(host, differentModule));
        Assert.False(PartyPoolLoadGateRule.PeerMatchesReadyWorld(
            host with { Module = string.Empty }, host));
        Assert.True(PartyPoolLoadGateRule.PeerMatchesReadyWorld(host, host));

        (PartyPoolLoadGateState state, bool hold) = PartyPoolLoadGateRule.ObserveClient(
            PartyPoolLoadGateState.Initial, 3, host, localWorldReadable: true);
        (state, hold) = PartyPoolLoadGateRule.ObserveClient(
            state, 3, host, localWorldReadable: true);
        Assert.True(hold);
        state = PartyPoolLoadGateRule.ObserveInitialWorldAlignment(state, differentSave, host);
        Assert.False(state.HasInitialAlignedWorld);
        state = PartyPoolLoadGateRule.ObserveInitialWorldAlignment(state, host, host);
        Assert.True(state.HasInitialAlignedWorld);
    }
}
