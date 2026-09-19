using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace KotorCoop.Session;

/// <summary>
/// The pieces of UPnP IGD (Internet Gateway Device) this mod needs: find the router's
/// WANIPConnection/WANPPPConnection control point over SSDP, and call AddPortMapping /
/// DeletePortMapping on it. Not a general UPnP client — no eventing, no other service types,
/// and the XML handling is <c>System.Xml.Linq</c> (BCL, not a package) used just enough to
/// pull one URL and one service type out of a device description.
///
/// Fails soft everywhere: every public method returns null/false on any failure rather than
/// throwing, because a router with UPnP off (the default on a lot of modern consumer
/// hardware, and often disabled deliberately) is the ORDINARY case this mod has to work
/// without, not an error condition worth surfacing as one.
/// </summary>
public static class UpnpClient
{
    /// <summary>The two IGD service types worth a port mapping: most home routers expose
    /// WANIPConnection; a minority (older PPPoE-based DSL routers) only WANPPPConnection. The
    /// SOAP calls are identical for both — only the service type (and therefore the SOAPACTION
    /// header/XML namespace) differs.</summary>
    public static readonly string[] ServiceTypes =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    private static readonly XNamespace DeviceNamespace = "urn:schemas-upnp-org:device-1-0";

    public const int DefaultSsdpPort = 1900;

    /// <summary>The real SSDP multicast group, used in production. A test passes a specific
    /// loopback endpoint instead — see <see cref="DiscoverAsync"/> — so exercising this needs
    /// no multicast group join, which is unreliable in a sandboxed CI network namespace.</summary>
    public static readonly IPEndPoint DefaultSsdpTarget = new(IPAddress.Parse("239.255.255.250"), DefaultSsdpPort);

    /// <summary>A discovered control point: where to POST the SOAP action, and which of the
    /// two <see cref="ServiceTypes"/> answered.</summary>
    public readonly record struct ControlPoint(Uri ControlUrl, string ServiceType);

    /// <summary>
    /// SSDP M-SEARCH for an IGD control point. <paramref name="target"/> is the SSDP endpoint
    /// to search — the real multicast group in production; a test passes a loopback endpoint
    /// standing in for a fake router. Sends one M-SEARCH per <see cref="ServiceTypes"/> entry
    /// and returns the first control point whose device description advertises a matching
    /// service, or null if nothing usable answers within <paramref name="timeout"/>.
    /// </summary>
    public static async Task<ControlPoint?> DiscoverAsync(IPEndPoint target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        foreach (string serviceType in ServiceTypes)
        {
            try
            {
                await socket.SendToAsync(BuildSearchRequest(target, serviceType), target, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // One failed send must not stop the other service type from being tried.
            }
        }

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        byte[] buffer = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket
                        .ReceiveFromAsync(buffer, SocketFlags.None, any, timeoutCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                string text = Encoding.ASCII.GetString(buffer, 0, result.ReceivedBytes);
                if (TryParseLocationHeader(text) is not { } location)
                {
                    continue;
                }

                if (await FetchControlPointAsync(location, cancellationToken).ConfigureAwait(false) is { } found)
                {
                    return found;
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }

        return null;
    }

    private static byte[] BuildSearchRequest(IPEndPoint target, string serviceType)
    {
        string request =
            "M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {target.Address}:{target.Port}\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            $"ST: {serviceType}\r\n\r\n";
        return Encoding.ASCII.GetBytes(request);
    }

    /// <summary>Pulls a LOCATION header out of an SSDP response, line by line. Deliberately
    /// tolerant of formatting — header names compared case-insensitively, either line ending —
    /// so a hand-rolled fake responder in a test does not have to reproduce a real router's
    /// exact byte-for-byte framing.</summary>
    private static Uri? TryParseLocationHeader(string text)
    {
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string name = line[..colon].Trim();
            if (name.Equals("LOCATION", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(line[(colon + 1)..].Trim(), UriKind.Absolute, out Uri? location))
            {
                return location;
            }
        }

        return null;
    }

    /// <summary>Fetches the device description at <paramref name="location"/> and finds the
    /// controlURL of whichever service in <see cref="ServiceTypes"/> it advertises.</summary>
    public static async Task<ControlPoint?> FetchControlPointAsync(Uri location, CancellationToken cancellationToken)
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };

        string xml;
        try
        {
            xml = await http.GetStringAsync(location, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch
        {
            return null;
        }

        foreach (XElement service in doc.Descendants(DeviceNamespace + "service"))
        {
            string? serviceType = service.Element(DeviceNamespace + "serviceType")?.Value;
            string? controlUrl = service.Element(DeviceNamespace + "controlURL")?.Value;

            if (serviceType is null || controlUrl is null || Array.IndexOf(ServiceTypes, serviceType) < 0)
            {
                continue;
            }

            Uri resolved = Uri.TryCreate(controlUrl, UriKind.Absolute, out Uri? absolute)
                ? absolute
                : new Uri(location, controlUrl);

            return new ControlPoint(resolved, serviceType);
        }

        return null;
    }

    /// <summary>Calls AddPortMapping. <paramref name="internalClient"/> is this machine's LAN
    /// IP — the router needs it to know which host on the network to forward to (see
    /// <see cref="GetLocalIPv4Address"/>). Returns false on any SOAP fault or transport
    /// failure; never throws.</summary>
    public static Task<bool> AddPortMappingAsync(
        ControlPoint point, string protocol, ushort externalPort, ushort internalPort,
        string internalClient, string description, uint leaseSeconds, CancellationToken cancellationToken) =>
        InvokeAsync(
            point,
            "AddPortMapping",
            $"""
            <NewRemoteHost></NewRemoteHost>
            <NewExternalPort>{externalPort}</NewExternalPort>
            <NewProtocol>{protocol}</NewProtocol>
            <NewInternalPort>{internalPort}</NewInternalPort>
            <NewInternalClient>{internalClient}</NewInternalClient>
            <NewEnabled>1</NewEnabled>
            <NewPortMappingDescription>{System.Security.SecurityElement.Escape(description)}</NewPortMappingDescription>
            <NewLeaseDuration>{leaseSeconds}</NewLeaseDuration>
            """,
            cancellationToken);

    /// <summary>Calls DeletePortMapping — best-effort cleanup on host stop/disconnect, so a
    /// mapping does not outlive the session that asked for it.</summary>
    public static Task<bool> DeletePortMappingAsync(
        ControlPoint point, string protocol, ushort externalPort, CancellationToken cancellationToken) =>
        InvokeAsync(
            point,
            "DeletePortMapping",
            $"""
            <NewRemoteHost></NewRemoteHost>
            <NewExternalPort>{externalPort}</NewExternalPort>
            <NewProtocol>{protocol}</NewProtocol>
            """,
            cancellationToken);

    private static async Task<bool> InvokeAsync(
        ControlPoint point, string action, string argumentsXml, CancellationToken cancellationToken) =>
        await InvokeForBodyAsync(point, action, argumentsXml, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>One SOAP action; the response body on success, null on a transport failure,
    /// a non-success status or a SOAP Fault.</summary>
    private static async Task<string?> InvokeForBodyAsync(
        ControlPoint point, string action, string argumentsXml, CancellationToken cancellationToken)
    {
        string envelope =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" +
            $"<u:{action} xmlns:u=\"{point.ServiceType}\">{argumentsXml}</u:{action}>" +
            "</s:Body></s:Envelope>";

        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
        using HttpRequestMessage request = new(HttpMethod.Post, point.ControlUrl)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{point.ServiceType}#{action}\"");

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // A router answers a FAILED AddPortMapping with a SOAP Fault, carried in a body it
            // still returns 500 for — so the HTTP status code alone cannot tell success apart
            // from a well-formed refusal (e.g. ConflictInMappingEntry). The body can.
            return response.IsSuccessStatusCode && !body.Contains("Fault", StringComparison.OrdinalIgnoreCase)
                ? body
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Calls GetExternalIPAddress: the address the ROUTER believes it holds on its WAN side.
    /// Compared against what STUN says the internet sees (<see cref="ReachabilityRule"/>): when
    /// the two differ, or this one is itself a private/shared-space address, the router is not
    /// the last NAT on the way out and no mapping it grants can be reached from outside. Null
    /// on any failure, or when the router answers 0.0.0.0 (some do while the WAN link is down).
    /// </summary>
    public static async Task<IPAddress?> GetExternalIPAddressAsync(ControlPoint point, CancellationToken cancellationToken)
    {
        string? body = await InvokeForBodyAsync(point, "GetExternalIPAddress", string.Empty, cancellationToken)
            .ConfigureAwait(false);
        return body is null ? null : ParseExternalIPAddress(body);
    }

    /// <summary>The <c>NewExternalIPAddress</c> element of a GetExternalIPAddress response,
    /// as an address; null when absent, unparsable or the all-zero placeholder. Pure, for tests.</summary>
    public static IPAddress? ParseExternalIPAddress(string responseBody)
    {
        const string open = "<NewExternalIPAddress>";
        const string close = "</NewExternalIPAddress>";
        int start = responseBody.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += open.Length;
        int end = responseBody.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return null;
        }

        string text = responseBody[start..end].Trim();
        return IPAddress.TryParse(text, out IPAddress? address) && !address.Equals(IPAddress.Any) ? address : null;
    }

    /// <summary>
    /// This machine's own LAN-facing IPv4 address — what AddPortMapping needs as
    /// <c>NewInternalClient</c>. Uses the standard trick: "connect" a UDP socket (no packet is
    /// actually sent for a connectionless socket — this only asks the OS to pick a route and a
    /// source address for it) to an arbitrary routable address and reads back the local
    /// endpoint it chose.
    /// </summary>
    public static string? GetLocalIPv4Address()
    {
        try
        {
            using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect("8.8.8.8", 65530);
            return probe.LocalEndPoint is IPEndPoint endpoint ? endpoint.Address.ToString() : null;
        }
        catch
        {
            return null;
        }
    }
}
