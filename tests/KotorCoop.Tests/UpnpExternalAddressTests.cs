using System.Net;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary><see cref="UpnpClient.ParseExternalIPAddress"/>: the one element pulled out of a
/// GetExternalIPAddress response, with the all-zero placeholder some routers answer while
/// their WAN link is down treated as "unknown".</summary>
public sealed class UpnpExternalAddressTests
{
    private const string Envelope =
        "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>"
        + "<u:GetExternalIPAddressResponse xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">"
        + "<NewExternalIPAddress>{0}</NewExternalIPAddress>"
        + "</u:GetExternalIPAddressResponse></s:Body></s:Envelope>";

    [Fact]
    public void ParsesTheAddressElement()
    {
        Assert.Equal(
            IPAddress.Parse("203.0.113.5"),
            UpnpClient.ParseExternalIPAddress(string.Format(Envelope, "203.0.113.5")));
    }

    [Fact]
    public void ParsesACarrierNatAddressAsAnAddress()
    {
        // Classifying it is ReachabilityRule's job; the parser just has to hand it over.
        Assert.Equal(
            IPAddress.Parse("100.72.3.4"),
            UpnpClient.ParseExternalIPAddress(string.Format(Envelope, " 100.72.3.4 ")));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("")]
    [InlineData("not-an-address")]
    public void APlaceholderOrGarbageValueIsUnknown(string value)
    {
        Assert.Null(UpnpClient.ParseExternalIPAddress(string.Format(Envelope, value)));
    }

    [Fact]
    public void AResponseWithoutTheElementIsUnknown()
    {
        Assert.Null(UpnpClient.ParseExternalIPAddress("<s:Envelope><s:Body/></s:Envelope>"));
        Assert.Null(UpnpClient.ParseExternalIPAddress("<NewExternalIPAddress>203.0.113.5"));
    }
}
