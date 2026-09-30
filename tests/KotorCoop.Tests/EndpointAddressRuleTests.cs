using System.Net;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class EndpointAddressRuleTests
{
    [Fact]
    public void Ipv6FirstDnsAnswerStillConnectsToIpv4SessionSocket()
        => Assert.Equal(IPAddress.Loopback,
            EndpointAddressRule.PreferIpv4([IPAddress.IPv6Loopback, IPAddress.Loopback]));

    [Fact]
    public void KeepsDnsOrderAmongIpv4Answers()
        => Assert.Equal(IPAddress.Parse("192.0.2.2"), EndpointAddressRule.PreferIpv4(
            [IPAddress.IPv6Loopback, IPAddress.Parse("192.0.2.2"), IPAddress.Parse("192.0.2.3")]));

    [Fact]
    public void EmptyAnswerHasNoEndpoint()
        => Assert.Null(EndpointAddressRule.PreferIpv4([]));

    [Fact]
    public void Ipv6OnlyAnswerRemainsAvailableForExplicitCallerPolicy()
        => Assert.Equal(IPAddress.IPv6Loopback, EndpointAddressRule.PreferIpv4([IPAddress.IPv6Loopback]));
}
