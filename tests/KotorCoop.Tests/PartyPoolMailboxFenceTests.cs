using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PartyPoolMailboxFenceTests
{
    [Fact]
    public void RequiresExactMarkerAndCurrentSerialIncludingZero()
    {
        Assert.True(PartyPoolMailboxFence.IsCurrent(PartyPoolMailboxFence.Marker, 0, 0));
        Assert.True(PartyPoolMailboxFence.IsCurrent(PartyPoolMailboxFence.Marker, 7, 7));
        Assert.False(PartyPoolMailboxFence.IsCurrent(0f, 7, 7));
        Assert.False(PartyPoolMailboxFence.IsCurrent(0.5f, 7, 7));
        Assert.False(PartyPoolMailboxFence.IsCurrent(float.NaN, 7, 7));
        Assert.False(PartyPoolMailboxFence.IsCurrent(PartyPoolMailboxFence.Marker, 6, 7));
    }
}
