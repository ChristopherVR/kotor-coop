using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure eligibility decision behind acting on an incoming
/// <see cref="KotorCoop.Protocol.PartySelectRequestPacket"/>: same-module only, one at a time,
/// host only. See <see cref="PartySelectHostRequestRule"/>'s own doc comment.
/// </summary>
public class PartySelectHostRequestRuleTests
{
    [Fact]
    public void AcceptsAHostSeeingASameModuleRequestWithNothingElsePending()
    {
        Assert.True(PartySelectHostRequestRule.ShouldAccept(isHost: true, sameModule: true, alreadyPending: false));
    }

    [Fact]
    public void AClientNeverActsOnIt()
    {
        Assert.False(PartySelectHostRequestRule.ShouldAccept(isHost: false, sameModule: true, alreadyPending: false));
    }

    [Fact]
    public void IgnoresACrossModuleOrStaleRequest()
    {
        Assert.False(PartySelectHostRequestRule.ShouldAccept(isHost: true, sameModule: false, alreadyPending: false));
    }

    [Fact]
    public void DebouncesWhileAnotherRequestIsAlreadyPending()
    {
        Assert.False(PartySelectHostRequestRule.ShouldAccept(isHost: true, sameModule: true, alreadyPending: true));
    }

    [Fact]
    public void EveryConditionMustHold()
    {
        Assert.False(PartySelectHostRequestRule.ShouldAccept(isHost: false, sameModule: false, alreadyPending: true));
    }
}
