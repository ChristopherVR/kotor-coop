using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins the "a load I asked for is still in flight" rule the party-pool between-lives guard
/// added on 2026-09-02 (docs/research/session-save.md, the rejoin hang round): the plugin acks
/// a successful load only once the new world is up and on screen, so request != ack covers the
/// whole window — including the first second, when the OLD world still looks perfectly loaded
/// and neither the WorldLoadSerial nor the engine-player guard has anything to say.
/// </summary>
public class PluginMenuLoaderInFlightTests
{
    [Fact]
    public void RequestAheadOfAckIsInFlight()
    {
        Assert.True(PluginMenuLoader.IsLoadInFlight(requestedSerial: 3, appliedSerial: 2));
    }

    [Fact]
    public void AckedRequestIsIdle()
    {
        Assert.False(PluginMenuLoader.IsLoadInFlight(requestedSerial: 3, appliedSerial: 3));
    }

    [Fact]
    public void NeverAskedIsIdle()
    {
        Assert.False(PluginMenuLoader.IsLoadInFlight(requestedSerial: 0, appliedSerial: 0));
    }

    [Fact]
    public void PreviousAppliedSerialCannotAcknowledgeANewRequest()
    {
        Assert.False(PluginMenuLoader.IsLoadAcknowledged(requestedSerial: 2, appliedSerial: 1));
        Assert.True(PluginMenuLoader.IsLoadAcknowledged(requestedSerial: 2, appliedSerial: 2));
        Assert.False(PluginMenuLoader.IsLoadAcknowledged(requestedSerial: 0, appliedSerial: 0));
        Assert.False(PluginMenuLoader.IsLoadAcknowledged(
            requestedSerialBefore: 2, requestedSerialAfter: 3, appliedSerial: 2));
        Assert.True(PluginMenuLoader.IsLoadAcknowledged(
            requestedSerialBefore: 2, requestedSerialAfter: 2, appliedSerial: 2));
    }
}
