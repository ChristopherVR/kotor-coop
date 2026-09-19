using System.Net;
using KotorCoop.Agent;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>Roadmap item 24's "surface it": <see cref="PublicAddressAnnouncer"/>'s line
/// wording and its diff-and-repost behaviour, the same shape <see cref="HostSaveAnnouncer"/>
/// already uses — and, since 2026-09-18, the reachability qualifier every status surface
/// shares (<see cref="PublicAddressAnnouncer.UpnpNote"/>): "UPnP mapped" is never said bare.</summary>
public class PublicAddressAnnouncerTests
{
    private static readonly IPEndPoint Endpoint = new(IPAddress.Parse("203.0.113.5"), 27015);

    [Fact]
    public void UnknownAddressAndDisabledUpnpProducesTheForwardHint()
    {
        string line = PublicAddressAnnouncer.Line(27015, null, UpnpMappingStatus.Disabled, ReachabilityVerdict.Unconfirmed);

        Assert.Equal(
            "co-op: hosting on port 27015 - public address unknown (forward UDP+TCP 27015 manually)",
            line);
    }

    [Fact]
    public void MappedAndReachableNamesBothAndSaysReachable()
    {
        string line = PublicAddressAnnouncer.Line(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.Reachable);

        Assert.Equal(
            "co-op: hosting on port 27015 - public address 203.0.113.5:27015 (UPnP mapped - reachable from the internet)",
            line);
    }

    [Fact]
    public void MappedBehindCarrierNatSaysNotReachableAndNamesTheTunnel()
    {
        string note = PublicAddressAnnouncer.UpnpNote(UpnpMappingStatus.Mapped, 27015, ReachabilityVerdict.CarrierNat);

        Assert.Equal(
            "UPnP mapped but NOT reachable (your ISP shares your address - use a tunnel such as playit.gg)",
            note);
    }

    [Fact]
    public void MappedButUnconfirmedSaysSoRatherThanPromising()
    {
        string note = PublicAddressAnnouncer.UpnpNote(UpnpMappingStatus.Mapped, 27015, ReachabilityVerdict.Unconfirmed);

        Assert.StartsWith("UPnP mapped - could not confirm", note);
        Assert.Contains("playit.gg", note);
    }

    [Fact]
    public void MappedWhileTheProbeIsStillRunningSaysChecking()
    {
        Assert.Equal(
            "UPnP mapped - checking reachability...",
            PublicAddressAnnouncer.UpnpNote(UpnpMappingStatus.Mapped, 27015, ReachabilityVerdict.Pending));
    }

    [Fact]
    public void UnavailableUpnpNamesBothPortsToForward()
    {
        string line = PublicAddressAnnouncer.Line(27015, Endpoint, UpnpMappingStatus.Unavailable, ReachabilityVerdict.Unconfirmed);

        Assert.Equal(
            "co-op: hosting on port 27015 - public address 203.0.113.5:27015 (UPnP unavailable - forward UDP+TCP 27015 manually)",
            line);
    }

    [Fact]
    public void UnavailableUpnpBehindCarrierNatSaysAForwardWillNotHelp()
    {
        string note = PublicAddressAnnouncer.UpnpNote(UpnpMappingStatus.Unavailable, 27015, ReachabilityVerdict.CarrierNat);

        Assert.Contains("manual port forward will not help", note);
        Assert.Contains("playit.gg", note);
    }

    [Fact]
    public void UnavailableUpnpWithAWorkingManualForwardSaysSo()
    {
        Assert.Equal(
            "UPnP unavailable but port 27015 is reachable from the internet",
            PublicAddressAnnouncer.UpnpNote(UpnpMappingStatus.Unavailable, 27015, ReachabilityVerdict.Reachable));
    }

    [Fact]
    public void PendingUpnpSaysStillChecking()
    {
        string line = PublicAddressAnnouncer.Line(27015, null, UpnpMappingStatus.Pending, ReachabilityVerdict.Pending);
        Assert.Contains("checking UPnP", line);
    }

    [Fact]
    public void RepeatedIdenticalObservationsAnnounceOnce()
    {
        PublicAddressAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.Reachable, lines.Add);
        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.Reachable, lines.Add);
        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.Reachable, lines.Add);

        Assert.Single(lines);
    }

    [Fact]
    public void EachVerdictLandingAfterTheLastAnnouncesAgain()
    {
        PublicAddressAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Pending, ReachabilityVerdict.Pending, lines.Add);
        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.Pending, lines.Add);
        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.CarrierNat, lines.Add);

        Assert.Equal(3, lines.Count);
        Assert.Contains("checking UPnP", lines[0]);
        Assert.Contains("checking reachability", lines[1]);
        Assert.Contains("NOT reachable", lines[2]);
    }

    [Fact]
    public void ResetForgetsTheLastStateSoHostingAgainAnnouncesFresh()
    {
        PublicAddressAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.Reachable, lines.Add);
        announcer.Reset();
        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.Reachable, lines.Add);

        Assert.Equal(2, lines.Count);
    }
}
