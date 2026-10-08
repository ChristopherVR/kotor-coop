using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class HostRestartWatchTests
{
    [Fact]
    public void ADifferentHostSessionOnTheSameConnectionIsARestart()
    {
        object connection = new();
        HostRestartWatch.State state = default;
        Assert.False(HostRestartWatch.Observe(ref state, connection, 0));       // not welcomed yet
        Assert.False(HostRestartWatch.Observe(ref state, connection, 0x1111));  // first welcome: baseline
        Assert.False(HostRestartWatch.Observe(ref state, connection, 0x1111));
        Assert.False(HostRestartWatch.Observe(ref state, connection, 0));       // host lost, reconnecting
        Assert.True(HostRestartWatch.Observe(ref state, connection, 0x2222));   // relaunched host
        Assert.False(HostRestartWatch.Observe(ref state, connection, 0x2222));  // once only
    }

    [Fact]
    public void ANewConnectionOnlyTakesABaseline()
    {
        // A fresh join adopts the host's world by itself; it must not trigger a second adoption.
        HostRestartWatch.State state = default;
        Assert.False(HostRestartWatch.Observe(ref state, new object(), 0x1111));
        Assert.False(HostRestartWatch.Observe(ref state, new object(), 0x2222));
    }
}
