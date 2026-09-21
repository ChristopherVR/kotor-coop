using System.Net;

namespace KotorCoop.Session;

/// <summary>
/// The peer-free self-probe behind a host's local-test and possible additional-NAT status
/// wording. It cannot prove internet reachability — docs/research/network-topology.md
/// §17 (2026-09-18), the follow-up to §16's finding that "UPnP mapped" was reported while the
/// port was unreachable.
///
/// Runs once, in the background, after hosting starts: waits for <see cref="UpnpMappingService"/>
/// to reach a verdict and <see cref="PublicAddressDiscovery"/> to learn the public endpoint
/// (up to <see cref="SettleTimeoutMs"/>), then gathers the three facts
/// <see cref="ReachabilityRule.Decide"/> needs — the router's WAN address, STUN's observed
/// address, and whether a probe from a second socket to that public endpoint hairpins back in
/// through the session's own listener (<see cref="UdpChannel.SendReachabilityProbeAsync"/>).
/// The probe is retried once, because a single lost datagram must not read as "unreachable".
///
/// Everything here is best-effort and additive: a host with STUN disabled (no public endpoint
/// to probe) and UPnP off simply lands on <see cref="ReachabilityVerdict.Unconfirmed"/>, which
/// is the honest pre-existing state of affairs, and nothing here can make hosting fail.
/// </summary>
public sealed class ReachabilityCheck : IDisposable
{
    /// <summary>How long one hairpin probe waits for itself. A LAN round trip through the
    /// router is milliseconds; two seconds is generous for a router that will hairpin at all.</summary>
    public const int ProbeTimeoutMs = 2000;

    /// <summary>How long to wait for STUN and UPnP to settle before judging with whatever is
    /// known. Both ordinarily answer within a few seconds; a STUN server that never answers
    /// (every configured one unreachable) is what this bound is for.</summary>
    public const int SettleTimeoutMs = 30_000;

    private const int SettlePollMs = 250;

    private readonly UdpChannel _channel;
    private readonly PublicAddressDiscovery? _stun;
    private readonly UpnpMappingService? _upnp;
    private readonly CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;

    // volatile for the same cross-thread reason UpnpMappingService._status documents: written
    // by the background task, read from whatever thread builds the status line.
    private volatile ReachabilityVerdict _verdict = ReachabilityVerdict.Pending;
    private volatile string _detail = "checking";

    public ReachabilityCheck(UdpChannel channel, PublicAddressDiscovery? stun, UpnpMappingService? upnp)
    {
        _channel = channel;
        _stun = stun;
        _upnp = upnp;
    }

    public ReachabilityVerdict Verdict => _verdict;

    /// <summary>The evidence behind <see cref="Verdict"/>, for the <c>public_address</c> verb
    /// and the log: which addresses were compared and what the probe did.</summary>
    public string Detail => _detail;

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            long deadline = SessionClock.NowMs + SettleTimeoutMs;
            while (SessionClock.NowMs < deadline && !Settled())
            {
                await Task.Delay(SettlePollMs, cancellationToken).ConfigureAwait(false);
            }

            IPEndPoint? publicEndPoint = _stun?.PublicEndPoint;
            IPAddress? routerExternal = _upnp?.ExternalAddress;

            bool? hairpin = null;
            if (publicEndPoint is not null)
            {
                hairpin = await _channel.SendReachabilityProbeAsync(publicEndPoint, ProbeTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (hairpin == false)
                {
                    hairpin = await _channel.SendReachabilityProbeAsync(publicEndPoint, ProbeTimeoutMs, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            _detail = Describe(routerExternal, publicEndPoint, hairpin);
            _verdict = ReachabilityRule.Decide(routerExternal, publicEndPoint?.Address, hairpin);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // A probe is diagnostics, never a reason to take a session down.
            _detail = $"probe failed: {ex.Message}";
            _verdict = ReachabilityVerdict.Unconfirmed;
        }
    }

    private bool Settled() =>
        (_upnp is null || _upnp.Status != UpnpMappingStatus.Pending)
        && (_stun is null || _stun.PublicEndPoint is not null);

    /// <summary>The evidence line, pure so its wording is unit-tested.</summary>
    public static string Describe(IPAddress? routerExternal, IPEndPoint? publicEndPoint, bool? hairpin)
    {
        string router = routerExternal is null ? "router WAN address unknown" : $"router WAN {routerExternal}";
        string stun = publicEndPoint is null ? "STUN address unknown" : $"internet sees {publicEndPoint}";
        string probe = hairpin switch
        {
            true => "self-probe came back",
            false => "self-probe did not come back (router may not hairpin)",
            null => "self-probe not sent",
        };

        return $"{router}; {stun}; {probe}";
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
            // Best-effort shutdown, matching PublicAddressDiscovery.Dispose's own rule.
        }

        _cts.Dispose();
    }
}
