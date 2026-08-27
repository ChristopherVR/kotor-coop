using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Tier 2 item 18 task 2: whether a client's galaxy-map confirm should be refused. See
/// <see cref="GalaxyMapConfirmRefusalRule"/>'s own doc comment for why this is a simpler gate
/// than <see cref="ClientDoorRefusalRule"/>'s door case (no host-busy check).
/// </summary>
public class GalaxyMapConfirmRefusalRuleTests
{
    [Fact]
    public void RefusesAClientUnderHostLeadMode()
    {
        Assert.True(GalaxyMapConfirmRefusalRule.ShouldRefuse(isClient: true, FollowLeadMode.Host));
    }

    [Fact]
    public void LeavesAClientAloneUnderAnyLeadMode()
    {
        Assert.False(GalaxyMapConfirmRefusalRule.ShouldRefuse(isClient: true, FollowLeadMode.Any));
    }

    [Fact]
    public void NeverRefusesTheHostUnderEitherMode()
    {
        Assert.False(GalaxyMapConfirmRefusalRule.ShouldRefuse(isClient: false, FollowLeadMode.Host));
        Assert.False(GalaxyMapConfirmRefusalRule.ShouldRefuse(isClient: false, FollowLeadMode.Any));
    }
}
