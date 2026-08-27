using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>Roadmap item 24: confirms <see cref="CoopHost"/> wires
/// <see cref="SessionSettings.StunServers"/>/<see cref="SessionSettings.UpnpEnabled"/> into
/// <see cref="PublicAddressDiscovery"/>/<see cref="UpnpMappingService"/> rather than starting
/// them unconditionally — no network access needed, since an empty server list and a disabled
/// flag are both no-ops by construction.</summary>
public class CoopHostPublicAddressTests
{
    [Fact]
    public void NoStunServersAndUpnpDisabledLeavesBothAtTheirOffState()
    {
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan",
            Port = 0,
            TickRate = 30,
            StunServers = [],
            UpnpEnabled = false,
        });

        Assert.Null(host.PublicEndPoint);
        Assert.Equal(UpnpMappingStatus.Disabled, host.UpnpStatus);
    }

    [Fact]
    public void SessionSettingsDefaultsToBothDiscoveryMechanismsOff()
    {
        // Every OTHER test in this suite builds a CoopHost from bare SessionSettings and must
        // not spontaneously start a background STUN loop or an SSDP multicast search - both
        // are opt-in AT THIS LAYER (real hosting turns UPnP on by default via coop.ini,
        // applied explicitly in AgentComposition; see UpnpEnabled's own doc comment).
        Assert.Empty(new SessionSettings().StunServers);
        Assert.False(new SessionSettings().UpnpEnabled);
    }
}
