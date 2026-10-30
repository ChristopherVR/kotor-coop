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

    [Theory]
    [InlineData(UpnpMappingStatus.Mapped)]
    [InlineData(UpnpMappingStatus.Unavailable)]
    [InlineData(UpnpMappingStatus.Disabled)]
    public void LocalProbeEvidenceNeverPromisesWanIngressOrDiagnosesTheIsp(UpnpMappingStatus mapping)
    {
        string hairpin = PublicAddressAnnouncer.UpnpNote(mapping, 27015, ReachabilityVerdict.HairpinConfirmed);
        Assert.Contains("internet access unconfirmed", hairpin);
        string nat = PublicAddressAnnouncer.UpnpNote(mapping, 27015, ReachabilityVerdict.AdditionalNatSuspected);
        Assert.Contains("possible additional NAT", nat);
        Assert.Contains("upstream routers", nat);
        Assert.DoesNotContain("ISP", nat);
        Assert.DoesNotContain("NOT reachable", nat);
    }

    [Fact]
    public void TheUnrequestedLineNeverShowsTheAddress()
    {
        // The announcement lands on the game screen; a stream or screenshot must not carry the
        // host's IP. Only public_address prints it.
        foreach (UpnpMappingStatus upnp in Enum.GetValues<UpnpMappingStatus>())
        {
            foreach (ReachabilityVerdict reachability in Enum.GetValues<ReachabilityVerdict>())
            {
                string line = PublicAddressAnnouncer.Line(27015, Endpoint, upnp, reachability);
                Assert.DoesNotContain("203.0.113.5", line);
                Assert.Contains("F9 > Copy connection address", line);
                Assert.InRange(line.Length, 1, PlayerHelpText.DefaultWidth);
            }
        }
    }

    [Fact]
    public void UnknownAddressPointsToVisibleConnectionHelp()
    {
        string line = PublicAddressAnnouncer.Line(27015, null, UpnpMappingStatus.Disabled, ReachabilityVerdict.Unconfirmed);

        Assert.Equal(
            "Hosting. No internet address yet. F9 > Connection help.",
            line);
    }

    [Fact]
    public void DiscoveredAddressOffersCopyWithoutPromisingInternetAccess()
    {
        string line = PublicAddressAnnouncer.Line(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.HairpinConfirmed);

        Assert.Equal(
            "Hosting. F9 > Copy connection address to invite a friend.",
            line);
    }

    [Fact]
    public void MappedWithAdditionalNatSuggestsUpstreamChecksOrTunnel()
    {
        string note = PublicAddressAnnouncer.UpnpNote(UpnpMappingStatus.Mapped, 27015, ReachabilityVerdict.AdditionalNatSuspected);

        Assert.Equal(
            "UPnP mapped - possible additional NAT; check upstream routers or use a tunnel such as playit.gg",
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
    public void UnavailableUpnpStillOffersTheExplicitCopyAction()
    {
        string line = PublicAddressAnnouncer.Line(27015, Endpoint, UpnpMappingStatus.Unavailable, ReachabilityVerdict.Unconfirmed);

        Assert.Equal(
            "Hosting. F9 > Copy connection address to invite a friend.",
            line);
    }

    [Fact]
    public void UnavailableUpnpWithAdditionalNatDoesNotRuleOutForwarding()
    {
        string note = PublicAddressAnnouncer.UpnpNote(UpnpMappingStatus.Unavailable, 27015, ReachabilityVerdict.AdditionalNatSuspected);

        Assert.Contains("possible additional NAT", note);
        Assert.Contains("playit.gg", note);
    }

    [Fact]
    public void UnavailableUpnpWithHairpinSuccessDoesNotPromiseInternetAccess()
    {
        Assert.Equal(
            "UPnP unavailable - local self-test passed on port 27015; internet access unconfirmed",
            PublicAddressAnnouncer.UpnpNote(UpnpMappingStatus.Unavailable, 27015, ReachabilityVerdict.HairpinConfirmed));
    }

    [Fact]
    public void PendingNetworkCheckFitsTheOverlay()
    {
        string line = PublicAddressAnnouncer.Line(27015, null, UpnpMappingStatus.Pending, ReachabilityVerdict.Pending);
        Assert.Contains("Checking network", line);
        Assert.InRange(line.Length, 1, PlayerHelpText.DefaultWidth);
    }

    [Fact]
    public void RepeatedIdenticalObservationsAnnounceOnce()
    {
        PublicAddressAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.HairpinConfirmed, lines.Add);
        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.HairpinConfirmed, lines.Add);
        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.HairpinConfirmed, lines.Add);

        Assert.Single(lines);
    }

    [Fact]
    public void EachVerdictLandingAfterTheLastAnnouncesAgain()
    {
        PublicAddressAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Pending, ReachabilityVerdict.Pending, lines.Add);
        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.Pending, lines.Add);
        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.AdditionalNatSuspected, lines.Add);

        Assert.Equal(3, lines.Count);
        Assert.All(lines, line => Assert.Contains("F9 > Copy connection address", line));
        Assert.All(lines, line => Assert.InRange(line.Length, 1, PlayerHelpText.DefaultWidth));
    }

    [Fact]
    public void ResetForgetsTheLastStateSoHostingAgainAnnouncesFresh()
    {
        PublicAddressAnnouncer announcer = new();
        List<string> lines = [];

        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.HairpinConfirmed, lines.Add);
        announcer.Reset();
        announcer.Observe(27015, Endpoint, UpnpMappingStatus.Mapped, ReachabilityVerdict.HairpinConfirmed, lines.Add);

        Assert.Equal(2, lines.Count);
    }
}
