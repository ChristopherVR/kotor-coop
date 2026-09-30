using System.Net;
using System.Net.Sockets;

namespace KotorCoop.Session;

/// <summary>Hostname answers must prefer the IPv4 family used by the session UDP socket.</summary>
public static class EndpointAddressRule
{
    public static IPAddress? PreferIpv4(IReadOnlyList<IPAddress> addresses)
    {
        foreach (IPAddress address in addresses)
            if (address.AddressFamily == AddressFamily.InterNetwork) return address;
        return addresses.Count == 0 ? null : addresses[0];
    }
}
