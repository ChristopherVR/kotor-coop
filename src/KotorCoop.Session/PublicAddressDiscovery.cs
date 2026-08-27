using System.Net;
using System.Net.Sockets;

namespace KotorCoop.Session;

/// <summary>
/// Keeps a host's own public IP:port current by asking a small, configurable list of public
/// STUN servers (RFC 5389, via <see cref="StunClient"/>) from the SAME socket the session
/// listens on, refreshed periodically so a player who has been hosting a while does not show a
/// stale address. Roadmap item 24's "public address discovery" piece — see
/// docs/research/network-topology.md's 2026-08-27 section.
///
/// Off entirely when no STUN server is configured (coop.ini <c>stun_servers =</c> empty):
/// <see cref="Start"/> is then a no-op and <see cref="PublicEndPoint"/> stays null forever,
/// which the status line already treats as "not discovered". This is the deliberate opt-out —
/// see the privacy note below.
///
/// ⚠ PRIVACY. Every STUN query is a UDP datagram to a third party (by default one of Google's
/// public STUN servers) that gets to see this host's socket address talk to it — nothing else
/// is sent (no player names, no session content), but it IS third-party contact a fully
/// offline/LAN-only host would not otherwise make. Documented in
/// docs/research/network-topology.md and docs/CONFIG.md; the empty-list opt-out exists
/// specifically so a player who cares can turn it off without losing anything else co-op does.
/// </summary>
public sealed class PublicAddressDiscovery : IDisposable
{
    /// <summary>
    /// coop.ini's default <c>stun_servers</c> value when the key is absent: two independently
    /// hosted, well-known public STUN servers (Google's; run unauthenticated for years, at no
    /// cost to the querying host), so one being briefly unreachable does not read as "STUN
    /// doesn't work". A blank value (present, but empty) is what actually disables discovery —
    /// see <see cref="ParseServers"/>.
    /// </summary>
    public const string DefaultServers = "stun.l.google.com:19302,stun1.l.google.com:19302";

    /// <summary>How often the mapping is re-queried once one has been found — "a few minutes",
    /// long enough this is not chatty, short enough a NAT rebind (a router reboot, an ISP
    /// re-lease) is noticed well within a normal play session.</summary>
    public const int RefreshIntervalMs = 5 * 60_000;

    private const int PerServerTimeoutMs = 2500;

    private readonly UdpChannel _channel;
    private readonly IReadOnlyList<string> _servers;
    private readonly CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;

    private volatile IPEndPoint? _publicEndPoint;

    public PublicAddressDiscovery(UdpChannel channel, IReadOnlyList<string> stunServers)
    {
        _channel = channel;
        _servers = stunServers;
    }

    /// <summary>
    /// Splits coop.ini's <c>stun_servers</c> value ("host:port,host:port") into a server list.
    /// An input that is null, empty or whitespace — which is what an explicitly-blank
    /// <c>stun_servers =</c> line parses to — returns an empty list, and an empty list is what
    /// actually disables discovery (see <see cref="Start"/>). Resolving "the key was absent"
    /// to <see cref="DefaultServers"/> is the caller's job (<c>CoopConfig</c>), because only
    /// the config reader can tell "absent" apart from "present and blank".
    /// </summary>
    public static IReadOnlyList<string> ParseServers(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The last discovered public endpoint, or null before the first successful query
    /// (or when discovery is disabled, or every configured server is currently unreachable —
    /// in which case this keeps the last known-good value rather than clearing it).</summary>
    public IPEndPoint? PublicEndPoint => _publicEndPoint;

    /// <summary>Starts the background refresh loop. A no-op when no server is configured.</summary>
    public void Start()
    {
        if (_servers.Count == 0)
        {
            return;
        }

        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await QueryOnceAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(RefreshIntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Tries every configured server in order and keeps the first answer. Public so a caller
    /// that wants an immediate, non-cached read (a `public_address` console verb pressed right
    /// after hosting, before the loop's first cycle) can force one round rather than wait for
    /// <see cref="RefreshIntervalMs"/>.
    /// </summary>
    public async Task QueryOnceAsync(CancellationToken cancellationToken)
    {
        foreach (string server in _servers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!TryResolveServer(server, out IPEndPoint? endpoint))
            {
                continue;
            }

            byte[] request = StunClient.BuildBindingRequest(out byte[] transactionId);

            byte[]? response;
            try
            {
                response = await _channel
                    .SendStunRequestAsync(endpoint, request, transactionId, PerServerTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (response is null)
            {
                continue; // this server did not answer in time; try the next one
            }

            if (StunClient.TryParseBindingResponse(response, transactionId) is { } mapped)
            {
                _publicEndPoint = mapped;
                return;
            }
        }

        // Every configured server failed (or none resolved). _publicEndPoint deliberately
        // keeps its last value — see this property's own doc comment.
    }

    private static bool TryResolveServer(string server, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.None, 0);
        int colon = server.LastIndexOf(':');
        string host = colon > 0 ? server[..colon] : server;
        int port = colon > 0 && int.TryParse(server[(colon + 1)..], out int parsedPort) ? parsedPort : 3478;

        try
        {
            if (IPAddress.TryParse(host, out IPAddress? ip))
            {
                endpoint = new IPEndPoint(ip, port);
                return true;
            }

            IPAddress[] resolved = Dns.GetHostAddresses(host);
            if (resolved.Length == 0)
            {
                return false;
            }

            // Prefer an IPv4 address: StunClient's XOR-MAPPED-ADDRESS parser only decodes the
            // IPv4 form, matching this project's IPv4-only game socket.
            IPAddress chosen = Array.Find(resolved, a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? resolved[0];
            endpoint = new IPEndPoint(chosen, port);
            return true;
        }
        catch
        {
            return false; // unresolvable host; try the next configured server
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
            // Best-effort shutdown, matching ServerDiscovery.Dispose's own rule.
        }

        _cts.Dispose();
    }
}
