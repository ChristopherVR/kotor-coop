using System.Net;
using KotorCoop.Agent;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>Roadmap item 24's "surface it": <see cref="PublicAddressAnnouncer"/>'s line
/// wording and its diff-and-repost behaviour, the same shape <see cref="HostSaveAnnouncer"/>
/// already uses.</summary>
public class PublicAddressAnnouncerTests
{
    [Fact]
    public void UnknownAddressAndDisabledUpnpProducesTheForwardHint()
    {
        string line = PublicAddressAnnouncer.Line(27015, null, UpnpMappingStatus.Disabled);

        Assert.Equal(
            "co-op: hosting on port 27015 - public address unknown (forward UDP+TCP 27015 manually)",
            line);
    }

    [Fact]
    public void MappedUpnpWithAKnownAddressNamesBoth()
    {
        IPEndPoint endpoint = new(IPAddress.Parse("203.0.113.5"), 27015);
        string line = PublicAddressAnnouncer.Line(27015, endpoint, UpnpMappingStatus.Mapped);

        Assert.Equal("co-op: hosting on port 27015 - public address 203.0.113.5:27015 (UPnP mapped)", line);
    }

    [Fact]
    public void UnavailableUpnpNamesBothPortsToForward()
    {
        IPEndPoint endpoint = new(IPAddress.Parse("203.0.113.5"), 27015);
        string line = PublicAddressAnnouncer.Line(27015, endpoint, UpnpMappingStatus.Unavailable);

        Assert.Equal(
            "co-op: hosting on port 27015 - public address 203.0.113.5:27015 (UPnP unavailable - forward UDP+TCP 27015 manually)",
            line);
    }

    [Fact]
    public void PendingUpnpSaysStillChecking()
    {
        string line = PublicAddressAnnouncer.Line(27015, null, UpnpMappingStatus.Pending);
        Assert.Contains("checking UPnP", line);
    }

    [Fact]
    public void RepeatedIdenticalObservationsAnnounceOnce()
    {
        PublicAddressAnnouncer announcer = new();
        List<string> lines = [];
        IPEndPoint endpoint = new(IPAddress.Parse("203.0.113.5"), 27015);

        announcer.Observe(27015, endpoint, UpnpMappingStatus.Mapped, lines.Add);
        announcer.Observe(27015, endpoint, UpnpMappingStatus.Mapped, lines.Add);
        announcer.Observe(27015, endpoint, UpnpMappingStatus.Mapped, lines.Add);

        Assert.Single(lines);
    }

    [Fact]
    public void TheUpnpVerdictLandingAfterTheAddressAnnouncesAgain()
    {
        PublicAddressAnnouncer announcer = new();
        List<string> lines = [];
        IPEndPoint endpoint = new(IPAddress.Parse("203.0.113.5"), 27015);

        announcer.Observe(27015, endpoint, UpnpMappingStatus.Pending, lines.Add);
        announcer.Observe(27015, endpoint, UpnpMappingStatus.Mapped, lines.Add);

        Assert.Equal(2, lines.Count);
        Assert.Contains("checking UPnP", lines[0]);
        Assert.Contains("UPnP mapped", lines[1]);
    }

    [Fact]
    public void ResetForgetsTheLastStateSoHostingAgainAnnouncesFresh()
    {
        PublicAddressAnnouncer announcer = new();
        List<string> lines = [];
        IPEndPoint endpoint = new(IPAddress.Parse("203.0.113.5"), 27015);

        announcer.Observe(27015, endpoint, UpnpMappingStatus.Mapped, lines.Add);
        announcer.Reset();
        announcer.Observe(27015, endpoint, UpnpMappingStatus.Mapped, lines.Add);

        Assert.Equal(2, lines.Count);
    }
}
