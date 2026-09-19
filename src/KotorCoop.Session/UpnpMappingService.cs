namespace KotorCoop.Session;

/// <summary>What a status line has to say about UPnP for this host, distinguishing "never
/// tried" (the coop.ini opt-out) from "tried and found nothing" from "still trying" from
/// "worked".</summary>
public enum UpnpMappingStatus
{
    Disabled,
    Pending,
    Mapped,
    Unavailable,
}

/// <summary>
/// Owns the lifetime of a UPnP port mapping for one hosted session: discovers the router's IGD
/// control point once, maps the game's UDP port and the world-transfer TCP port (the SAME
/// port number — see <c>WorldTransfer</c>'s own doc comment on why they share one), keeps the
/// lease renewed for as long as the session runs, and deletes both mappings again when it ends.
///
/// Fails soft at every step: a router with UPnP off (or simply absent, which is common) leaves
/// <see cref="Status"/> at <see cref="UpnpMappingStatus.Unavailable"/> rather than throwing,
/// because that is the ORDINARY case for a lot of home networks, not a bug to surface as one —
/// the status line's job is to tell the player to forward the port by hand when this happens,
/// not to make it look like something broke.
/// </summary>
public sealed class UpnpMappingService : IDisposable
{
    /// <summary>How long a mapping is leased for before it would expire on the router's own
    /// side if never renewed.</summary>
    private const uint LeaseSeconds = 7200;

    /// <summary>How often the lease is re-asserted — comfortably inside <see cref="LeaseSeconds"/>
    /// so a slow renewal (or one missed tick) never lets the mapping lapse mid-session.</summary>
    private const int RefreshIntervalMs = 55 * 60_000;

    private readonly int _port;
    private readonly string _description;
    private readonly CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;

    private UpnpClient.ControlPoint? _controlPoint;

    // volatile: written by the background TryMapAsync task, read from whatever thread asks
    // for the status line (CoopHost.UpnpStatus) — the same cross-thread-visibility reasoning
    // PublicAddressDiscovery._publicEndPoint documents for its own identical shape.
    private volatile UpnpMappingStatus _status = UpnpMappingStatus.Pending;

    // Same cross-thread shape as _status: written once by TryMapAsync, read by ReachabilityCheck.
    private volatile System.Net.IPAddress? _externalAddress;

    public UpnpMappingStatus Status => _status;

    /// <summary>The router's own idea of its WAN address (UPnP <c>GetExternalIPAddress</c>),
    /// asked once the control point is found — mapped or not, since a router that refuses the
    /// mapping can still tell us whether it is behind carrier NAT. Null until then, or when the
    /// router would not say. See <see cref="ReachabilityRule"/>.</summary>
    public System.Net.IPAddress? ExternalAddress => _externalAddress;

    public UpnpMappingService(int port, string description = "KotorCoop")
    {
        _port = port;
        _description = description;
    }

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await TryMapAsync(cancellationToken).ConfigureAwait(false);

                if (_status != UpnpMappingStatus.Mapped)
                {
                    return; // nothing mapped, so nothing to keep renewing — one attempt is enough
                }

                await Task.Delay(RefreshIntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TryMapAsync(CancellationToken cancellationToken)
    {
        UpnpClient.ControlPoint? point = _controlPoint
            ?? await UpnpClient.DiscoverAsync(UpnpClient.DefaultSsdpTarget, TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);

        string? localIp = UpnpClient.GetLocalIPv4Address();

        if (point is not { } found || localIp is null)
        {
            _status = UpnpMappingStatus.Unavailable;
            return;
        }

        bool udpMapped = await UpnpClient
            .AddPortMappingAsync(found, "UDP", (ushort)_port, (ushort)_port, localIp, _description, LeaseSeconds, cancellationToken)
            .ConfigureAwait(false);

        // The TCP world-transfer listener shares the UDP session's port number, so it needs
        // its own mapping too. Attempted even if the UDP one failed (and vice versa) — a
        // partial mapping still lets SOMETHING work, and Status only drops to Unavailable when
        // neither protocol went through.
        bool tcpMapped = await UpnpClient
            .AddPortMappingAsync(found, "TCP", (ushort)_port, (ushort)_port, localIp, _description, LeaseSeconds, cancellationToken)
            .ConfigureAwait(false);

        _controlPoint = found;

        // Before the status flips out of Pending, so ReachabilityCheck — which waits on exactly
        // that flip — never reads the verdict without the WAN address that belongs with it.
        _externalAddress ??= await UpnpClient.GetExternalIPAddressAsync(found, cancellationToken).ConfigureAwait(false);

        _status = udpMapped || tcpMapped ? UpnpMappingStatus.Mapped : UpnpMappingStatus.Unavailable;
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
            // Best-effort shutdown; a stuck discovery/SOAP call must not wedge session teardown.
        }

        _cts.Dispose();

        if (_controlPoint is { } point && Status == UpnpMappingStatus.Mapped)
        {
            // Best-effort cleanup: a mapping that fails to delete is a router leftover a
            // player has to notice and clear by hand, not a reason to fail the disconnect.
            try
            {
                UpnpClient.DeletePortMappingAsync(point, "UDP", (ushort)_port, CancellationToken.None)
                    .GetAwaiter().GetResult();
                UpnpClient.DeletePortMappingAsync(point, "TCP", (ushort)_port, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch
            {
            }
        }
    }
}
