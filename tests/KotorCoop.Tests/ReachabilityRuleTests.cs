using System.Net;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>Local probe evidence and additional-NAT hints; no WAN connectivity guarantee.</summary>
public sealed class ReachabilityRuleTests
{
    private static readonly IPAddress Public = IPAddress.Parse("203.0.113.5");
    private static readonly IPAddress OtherPublic = IPAddress.Parse("198.51.100.7");

    [Fact]
    public void AReceivedProbeWithMatchingAddressesConfirmsOnlyHairpinning()
    {
        Assert.Equal(ReachabilityVerdict.HairpinConfirmed, ReachabilityRule.Decide(Public, Public, hairpinReceived: true));
    }

    [Fact]
    public void AReceivedProbeWithNoRouterAddressConfirmsOnlyHairpinning()
    {
        // UPnP off or refused, STUN on, the router hairpins: nothing contradicts the probe.
        Assert.Equal(ReachabilityVerdict.HairpinConfirmed, ReachabilityRule.Decide(null, Public, hairpinReceived: true));
    }

    [Fact]
    public void RouterAndStunDisagreeingSuggestsAdditionalNatEvenIfTheProbeCameBack()
    {
        // Address disagreement suggests another NAT or egress route; its cause is unknown.
        Assert.Equal(ReachabilityVerdict.AdditionalNatSuspected, ReachabilityRule.Decide(OtherPublic, Public, hairpinReceived: true));
        Assert.Equal(ReachabilityVerdict.AdditionalNatSuspected, ReachabilityRule.Decide(OtherPublic, Public, hairpinReceived: false));
    }

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.1")]
    public void ARouterWanAddressInPrivateOrSharedSpaceSuggestsAdditionalNatWhateverElseIsKnown(string wan)
    {
        IPAddress router = IPAddress.Parse(wan);
        Assert.Equal(ReachabilityVerdict.AdditionalNatSuspected, ReachabilityRule.Decide(router, null, null));
        Assert.Equal(ReachabilityVerdict.AdditionalNatSuspected, ReachabilityRule.Decide(router, Public, hairpinReceived: true));
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
