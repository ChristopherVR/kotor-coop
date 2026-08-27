using System.Net;
using System.Net.Sockets;
using System.Text;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Roadmap item 24 ("UPnP IGD port mapping"): exercises <see cref="UpnpClient"/> against a
/// fake IGD on loopback — a hand-rolled HTTP/SOAP responder (deliberately not
/// <c>HttpListener</c>, which needs a URL ACL reservation or admin rights to bind a prefix on
/// Windows and would make this suite flaky outside an elevated shell) plus a fake SSDP
/// responder. <see cref="UpnpClient.DiscoverAsync"/> takes its SSDP target as a parameter
/// specifically so a test can point it at a loopback endpoint instead of the real multicast
/// group, which needs no multicast join and works the same in any sandboxed CI network.
/// </summary>
public class UpnpClientTests
{
    [Fact]
    public async Task FetchControlPointAsyncFindsTheWanIpConnectionServiceInADeviceDescription()
    {
        using FakeIgdHttpServer http = await FakeIgdHttpServer.StartAsync(DeviceDescriptionXml("/control"));

        UpnpClient.ControlPoint? point = await UpnpClient.FetchControlPointAsync(
            new Uri($"http://127.0.0.1:{http.Port}/desc.xml"), CancellationToken.None);

        Assert.NotNull(point);
        Assert.Equal("urn:schemas-upnp-org:service:WANIPConnection:1", point!.Value.ServiceType);
        Assert.Equal($"http://127.0.0.1:{http.Port}/control", point.Value.ControlUrl.ToString());
    }

    [Fact]
    public async Task FetchControlPointAsyncResolvesARelativeControlUrlAgainstTheDescriptionLocation()
    {
        using FakeIgdHttpServer http = await FakeIgdHttpServer.StartAsync(DeviceDescriptionXml("control"));

        UpnpClient.ControlPoint? point = await UpnpClient.FetchControlPointAsync(
            new Uri($"http://127.0.0.1:{http.Port}/desc.xml"), CancellationToken.None);

        Assert.NotNull(point);
        Assert.Equal($"http://127.0.0.1:{http.Port}/control", point!.Value.ControlUrl.ToString());
    }

    [Fact]
    public async Task FetchControlPointAsyncReturnsNullWhenNoMatchingServiceIsAdvertised()
    {
        const string xml = """
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:SomeOtherService:1</serviceType>
                    <controlURL>/control</controlURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;

        using FakeIgdHttpServer http = await FakeIgdHttpServer.StartAsync(xml);

        UpnpClient.ControlPoint? point = await UpnpClient.FetchControlPointAsync(
            new Uri($"http://127.0.0.1:{http.Port}/desc.xml"), CancellationToken.None);

        Assert.Null(point);
    }

    [Fact]
    public async Task AddPortMappingAsyncReturnsTrueOnASuccessfulSoapResponse()
    {
        using FakeIgdHttpServer http = await FakeIgdHttpServer.StartAsync(
            DeviceDescriptionXml("/control"), soapResponse: SuccessfulAddPortMappingSoap());

        UpnpClient.ControlPoint point = new(
            new Uri($"http://127.0.0.1:{http.Port}/control"), "urn:schemas-upnp-org:service:WANIPConnection:1");

        bool ok = await UpnpClient.AddPortMappingAsync(
            point, "UDP", 27015, 27015, "192.168.1.50", "KotorCoop", 7200, CancellationToken.None);

        Assert.True(ok);
        Assert.Contains("AddPortMapping", http.LastSoapAction);
        Assert.Contains("27015", http.LastRequestBody);
        Assert.Contains("192.168.1.50", http.LastRequestBody);
    }

    [Fact]
    public async Task AddPortMappingAsyncReturnsFalseOnASoapFault()
    {
        using FakeIgdHttpServer http = await FakeIgdHttpServer.StartAsync(
            DeviceDescriptionXml("/control"), soapResponse: SoapFault("ConflictInMappingEntry"));

        UpnpClient.ControlPoint point = new(
            new Uri($"http://127.0.0.1:{http.Port}/control"), "urn:schemas-upnp-org:service:WANIPConnection:1");

        bool ok = await UpnpClient.AddPortMappingAsync(
            point, "UDP", 27015, 27015, "192.168.1.50", "KotorCoop", 7200, CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task DeletePortMappingAsyncReturnsTrueOnASuccessfulSoapResponse()
    {
        using FakeIgdHttpServer http = await FakeIgdHttpServer.StartAsync(
            DeviceDescriptionXml("/control"), soapResponse: SuccessfulDeletePortMappingSoap());

        UpnpClient.ControlPoint point = new(
            new Uri($"http://127.0.0.1:{http.Port}/control"), "urn:schemas-upnp-org:service:WANIPConnection:1");

        bool ok = await UpnpClient.DeletePortMappingAsync(point, "UDP", 27015, CancellationToken.None);

        Assert.True(ok);
        Assert.Contains("DeletePortMapping", http.LastSoapAction);
    }

    [Fact]
    public async Task DiscoverAsyncFindsTheControlPointThroughAFakeSsdpResponderOnLoopback()
    {
        using FakeIgdHttpServer http = await FakeIgdHttpServer.StartAsync(DeviceDescriptionXml("/control"));
        using FakeSsdpResponder ssdp = FakeSsdpResponder.Start($"http://127.0.0.1:{http.Port}/desc.xml");

        UpnpClient.ControlPoint? point = await UpnpClient.DiscoverAsync(
            new IPEndPoint(IPAddress.Loopback, ssdp.Port), TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.NotNull(point);
        Assert.Equal($"http://127.0.0.1:{http.Port}/control", point!.Value.ControlUrl.ToString());
    }

    [Fact]
    public async Task DiscoverAsyncReturnsNullWhenNothingAnswers()
    {
        // Nobody is listening on this loopback port: DiscoverAsync must give up cleanly
        // within its timeout rather than hang or throw - the router-absent case, which is
        // the common one on a network with no UPnP support at all.
        using Socket claimPort = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        claimPort.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int unusedPort = ((IPEndPoint)claimPort.LocalEndPoint!).Port;
        claimPort.Dispose(); // release it immediately; nothing will bind here again during the test

        UpnpClient.ControlPoint? point = await UpnpClient.DiscoverAsync(
            new IPEndPoint(IPAddress.Loopback, unusedPort), TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.Null(point);
    }

    [Fact]
    public void GetLocalIPv4AddressReturnsNullOrAParsableAddress()
    {
        // Best-effort by design (see the method's own doc comment): this only asserts it never
        // throws and, when it does return something, that something is a real IPv4 address.
        string? address = UpnpClient.GetLocalIPv4Address();
        if (address is not null)
        {
            Assert.True(IPAddress.TryParse(address, out IPAddress? parsed));
            Assert.Equal(AddressFamily.InterNetwork, parsed!.AddressFamily);
        }
    }

    private static string DeviceDescriptionXml(string controlUrl) => $"""
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <device>
            <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                <controlURL>{controlUrl}</controlURL>
              </service>
            </serviceList>
          </device>
        </root>
        """;

    private static string SuccessfulAddPortMappingSoap() =>
        """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body>
            <u:AddPortMappingResponse xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1" />
          </s:Body>
        </s:Envelope>
        """;

    private static string SuccessfulDeletePortMappingSoap() =>
        """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body>
            <u:DeletePortMappingResponse xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1" />
          </s:Body>
        </s:Envelope>
        """;

    private static string SoapFault(string upnpErrorDescription) => $"""
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body>
            <s:Fault>
              <faultcode>s:Client</faultcode>
              <faultstring>UPnPError</faultstring>
              <detail>
                <UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                  <errorDescription>{upnpErrorDescription}</errorDescription>
                </UPnPError>
              </detail>
            </s:Fault>
          </s:Body>
        </s:Envelope>
        """;

    /// <summary>
    /// A minimal hand-rolled HTTP server on loopback (raw <see cref="TcpListener"/>, not
    /// <c>HttpListener</c> — see this file's own header comment) that serves one fixed GET
    /// response (the device description) and one fixed POST response (a SOAP reply) for every
    /// request, recording the last SOAPACTION header and request body it saw so a test can
    /// assert on what <see cref="UpnpClient"/> actually sent.
    /// </summary>
    private sealed class FakeIgdHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _descriptionXml;
        private readonly string _soapResponse;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public int Port { get; }

        public string LastSoapAction { get; private set; } = string.Empty;

        public string LastRequestBody { get; private set; } = string.Empty;

        private FakeIgdHttpServer(TcpListener listener, string descriptionXml, string soapResponse)
        {
            _listener = listener;
            _descriptionXml = descriptionXml;
            _soapResponse = soapResponse;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public static Task<FakeIgdHttpServer> StartAsync(string descriptionXml, string? soapResponse = null)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FakeIgdHttpServer(listener, descriptionXml, soapResponse ?? "<ok/>"));
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    await ServeOneAsync(client, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task ServeOneAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using NetworkStream stream = client.GetStream();
            (string requestLine, Dictionary<string, string> headers, string body) =
                await ReadHttpRequestAsync(stream, cancellationToken).ConfigureAwait(false);

            bool isPost = requestLine.StartsWith("POST", StringComparison.Ordinal);
            if (isPost)
            {
                LastSoapAction = headers.GetValueOrDefault("SOAPACTION", string.Empty);
                LastRequestBody = body;
            }

            string responseBody = isPost ? _soapResponse : _descriptionXml;
            byte[] bodyBytes = Encoding.UTF8.GetBytes(responseBody);
            string statusLine = isPost && responseBody.Contains("Fault", StringComparison.OrdinalIgnoreCase)
                ? "HTTP/1.1 500 Internal Server Error"
                : "HTTP/1.1 200 OK";

            string headerText =
                $"{statusLine}\r\n" +
                "Content-Type: text/xml; charset=\"utf-8\"\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(headerText), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<(string RequestLine, Dictionary<string, string> Headers, string Body)> ReadHttpRequestAsync(
            NetworkStream stream, CancellationToken cancellationToken)
        {
            List<byte> buffer = [];
            byte[] one = new byte[1];

            // Read until the blank line that ends the headers ("\r\n\r\n"). A fixed small
            // request from our own client makes this simple byte-at-a-time read acceptable.
            while (buffer.Count < 4
                || buffer[^4] != (byte)'\r' || buffer[^3] != (byte)'\n'
                || buffer[^2] != (byte)'\r' || buffer[^1] != (byte)'\n')
            {
                int n = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                buffer.Add(one[0]);
            }

            string headerText = Encoding.ASCII.GetString([.. buffer]);
            string[] lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            string requestLine = lines.Length > 0 ? lines[0] : string.Empty;

            Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            string body = string.Empty;
            if (headers.TryGetValue("Content-Length", out string? lengthText)
                && int.TryParse(lengthText, out int length) && length > 0)
            {
                byte[] bodyBuffer = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = await stream.ReadAsync(bodyBuffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }

                body = Encoding.UTF8.GetString(bodyBuffer, 0, read);
            }

            return (requestLine, headers, body);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            _cts.Dispose();
        }
    }

    /// <summary>A fake router's SSDP half: answers any UDP datagram it receives (an M-SEARCH,
    /// in production) with a canned "HTTP/1.1 200 OK" response naming <paramref name="location"/>
    /// via a LOCATION header, unicast back to the sender.</summary>
    private sealed class FakeSsdpResponder : IDisposable
    {
        private readonly Socket _socket;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public int Port { get; }

        private FakeSsdpResponder(Socket socket, string location)
        {
            _socket = socket;
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            _loop = Task.Run(() => RespondLoopAsync(location, _cts.Token));
        }

        public static FakeSsdpResponder Start(string location)
        {
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return new FakeSsdpResponder(socket, location);
        }

        private async Task RespondLoopAsync(string location, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[2048];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    SocketReceiveFromResult result;
                    try
                    {
                        result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        continue;
                    }

                    string response =
                        "HTTP/1.1 200 OK\r\n" +
                        "ST: urn:schemas-upnp-org:service:WANIPConnection:1\r\n" +
                        $"LOCATION: {location}\r\n\r\n";

                    await _socket.SendToAsync(Encoding.ASCII.GetBytes(response), result.RemoteEndPoint, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            _cts.Dispose();
            _socket.Dispose();
        }
    }
}
