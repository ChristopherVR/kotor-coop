using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// UdpChannel used to bind with no try/catch at all, so a second co-op agent (or a leftover
/// one from a crashed previous session) holding the port made the whole session construction
/// throw a bare, unlabelled SocketException. Pinning that it comes back as the typed
/// PortInUseException, carrying the port, is what lets SessionManager turn it into a specific
/// in-game line instead of a generic failure.
/// </summary>
public class UdpChannelTests
{
    [Fact]
    public void BindingAPortAlreadyHeldThrowsPortInUseException()
    {
        using UdpChannel first = new(0); // ephemeral port, actually bound
        int port = first.LocalEndPoint.Port;

        PortInUseException ex = Assert.Throws<PortInUseException>(() => new UdpChannel(port));
        Assert.Equal(port, ex.Port);
    }

    [Fact]
    public void AnEphemeralPortBindsWithoutThrowing()
    {
        // The common case must keep working: port 0 asks the OS to pick a free one.
        using UdpChannel channel = new(0);
        Assert.True(channel.LocalEndPoint.Port > 0);
    }
}
