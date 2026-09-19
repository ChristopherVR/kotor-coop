using System.Net;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="ReachabilityRule"/> — the decision behind the host's "reachable from the
/// internet" / "NOT reachable (your ISP shares your address)" / "could not confirm" status
/// wording (docs/research/network-topology.md §17). Pins: carrier NAT is judged first and
/// overrides a received probe; a received probe with consistent addresses is the only way to
/// "reachable"; everything else is honestly unconfirmed; and the private/shared address
/// classifier covers exactly the four blocks a home router's WAN side must never be in.
/// </summary>
public sealed class ReachabilityRuleTests
{
    private static readonly IPAddress Public = IPAddress.Parse("203.0.113.5");
    private static readonly IPAddress OtherPublic = IPAddress.Parse("198.51.100.7");

    [Fact]
    public void AReceivedProbeWithMatchingAddressesIsReachable()
    {
        Assert.Equal(ReachabilityVerdict.Reachable, ReachabilityRule.Decide(Public, Public, hairpinReceived: true));
    }

    [Fact]
    public void AReceivedProbeWithNoRouterAddressIsStillReachable()
    {
        // UPnP off or refused, STUN on, the router hairpins: nothing contradicts the probe.
        Assert.Equal(ReachabilityVerdict.Reachable, ReachabilityRule.Decide(null, Public, hairpinReceived: true));
    }

    [Fact]
    public void RouterAndStunDisagreeingIsCarrierNatEvenIfTheProbeCameBack()
    {
        // The 2026-09-10 shape: the home router mapped the port and even hairpinned the probe
        // through its own NAT, but the address the internet sees belongs to the carrier's NAT
        // in front of it, which nothing on the home side can map through.
        Assert.Equal(ReachabilityVerdict.CarrierNat, ReachabilityRule.Decide(OtherPublic, Public, hairpinReceived: true));
        Assert.Equal(ReachabilityVerdict.CarrierNat, ReachabilityRule.Decide(OtherPublic, Public, hairpinReceived: false));
    }

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.1")]
    public void ARouterWanAddressInPrivateOrSharedSpaceIsCarrierNatWhateverElseIsKnown(string wan)
    {
        IPAddress router = IPAddress.Parse(wan);
        Assert.Equal(ReachabilityVerdict.CarrierNat, ReachabilityRule.Decide(router, null, null));
        Assert.Equal(ReachabilityVerdict.CarrierNat, ReachabilityRule.Decide(router, Public, hairpinReceived: true));
    }

    [Fact]
    public void NoProbeAndNoContradictionIsUnconfirmed()
    {
        Assert.Equal(ReachabilityVerdict.Unconfirmed, ReachabilityRule.Decide(null, null, null));
        Assert.Equal(ReachabilityVerdict.Unconfirmed, ReachabilityRule.Decide(Public, Public, null));
        Assert.Equal(ReachabilityVerdict.Unconfirmed, ReachabilityRule.Decide(Public, null, null));
    }

    [Fact]
    public void AProbeThatDidNotComeBackIsUnconfirmedNotUnreachable()
    {
        // Many home routers do not hairpin; that says nothing about whether a friend outside
        // can reach the port, so the rule must not turn silence into a verdict.
        Assert.Equal(ReachabilityVerdict.Unconfirmed, ReachabilityRule.Decide(Public, Public, hairpinReceived: false));
        Assert.Equal(ReachabilityVerdict.Unconfirmed, ReachabilityRule.Decide(null, Public, hairpinReceived: false));
    }

    [Theory]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.0", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("203.0.113.5", false)]
    [InlineData("100.64.0.0", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("192.168.0.0", true)]
    public void PrivateOrSharedSpaceClassifierMatchesTheFourBlocksExactly(string address, bool expected)
    {
        Assert.Equal(expected, ReachabilityRule.IsPrivateOrShared(IPAddress.Parse(address)));
    }

    [Fact]
    public void AnIPv6AddressIsNeverClassifiedAsPrivateOrShared()
    {
        // The game socket is IPv4-only; an IPv6 address here would be a parser surprise, and
        // the honest answer is "not one of the IPv4 blocks", not a verdict.
        Assert.False(ReachabilityRule.IsPrivateOrShared(IPAddress.Parse("fd00::1")));
    }

    [Fact]
    public void TheEvidenceLineNamesEveryFactItHad()
    {
        string line = ReachabilityCheck.Describe(Public, new IPEndPoint(OtherPublic, 27140), hairpin: false);

        Assert.Contains("router WAN 203.0.113.5", line);
        Assert.Contains("internet sees 198.51.100.7:27140", line);
        Assert.Contains("did not come back", line);

        Assert.Equal(
            "router WAN address unknown; STUN address unknown; self-probe not sent",
            ReachabilityCheck.Describe(null, null, null));
    }
}
