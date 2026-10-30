using System.Net;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// Announces a hosting player's connection evidence — the public IP:port
/// STUN found (if any) and whether UPnP could forward the port automatically — reposted only
/// when either half of the answer changes. Roadmap item 24's "surface it" requirement: the same
/// diff-and-repost shape <see cref="HostSaveAnnouncer"/>/<see cref="ConnectionStatusAnnouncer"/>
/// already use, so a stable host says this once (typically the moment STUN's first query and
/// UPnP's first mapping attempt land) rather than every tick.
/// </summary>
public sealed class PublicAddressAnnouncer
{
    private (IPEndPoint? PublicEndPoint, UpnpMappingStatus Upnp, ReachabilityVerdict Reachability)? _last;

    /// <summary>
    /// The pure part: what to say for a given (public endpoint, UPnP status, reachability)
    /// triple. Kept separate from <see cref="Observe"/> so the decision is testable with no
    /// tick loop and no live session.
    ///
    /// The address itself is never part of this unrequested line: it lands on the game screen,
    /// where a stream or a screenshot would expose the host's IP. The player asks for it with
    /// <c>public_address</c>, which prints it and copies it to the clipboard.
    /// </summary>
    public static string Line(int port, IPEndPoint? publicEndPoint, UpnpMappingStatus upnp, ReachabilityVerdict reachability)
    {
        // This toast must fit the default chat overlay's 60-character width.
        // Detailed reachability evidence remains in Connection help and status.
        return publicEndPoint is not null
            ? "Hosting. F9 > Copy connection address to invite a friend."
            : upnp == UpnpMappingStatus.Pending || reachability == ReachabilityVerdict.Pending
                ? "Hosting. Checking network. F9 > Connection help."
                : "Hosting. No internet address yet. F9 > Connection help.";
    }

    /// <summary>
    /// The parenthetical every status surface shares (this announcer, <c>status</c>,
    /// <c>public_address</c>). "UPnP mapped" alone over-promised — docs/research/network-topology.md
    /// §16 saw it reported while the port was unreachable — so a mapping is always qualified by
    /// the self-probe's verdict (<see cref="ReachabilityVerdict"/>). Hairpin success is only
    /// a local test; mismatched addresses suggest additional NAT but do not establish its
    /// owner or rule out forwarding through upstream routers.
    /// </summary>
    public static string UpnpNote(UpnpMappingStatus upnp, int port, ReachabilityVerdict reachability) => upnp switch
    {
        UpnpMappingStatus.Mapped => reachability switch
        {
            ReachabilityVerdict.HairpinConfirmed => "UPnP mapped - local self-test passed; internet access unconfirmed",
            ReachabilityVerdict.AdditionalNatSuspected =>
                "UPnP mapped - possible additional NAT; check upstream routers or use a tunnel such as playit.gg",
            ReachabilityVerdict.Unconfirmed =>
                "UPnP mapped - could not confirm it is reachable; if a friend cannot connect, use a tunnel such as playit.gg",
            _ => "UPnP mapped - checking reachability...",
        },
        UpnpMappingStatus.Unavailable => reachability switch
        {
            ReachabilityVerdict.AdditionalNatSuspected =>
                "UPnP unavailable - possible additional NAT; check upstream routers or use a tunnel such as playit.gg",
            ReachabilityVerdict.HairpinConfirmed => $"UPnP unavailable - local self-test passed on port {port}; internet access unconfirmed",
            _ => $"UPnP unavailable - forward UDP+TCP {port} manually",
        },
        UpnpMappingStatus.Pending => "checking UPnP...",
        _ => reachability switch
        {
            ReachabilityVerdict.AdditionalNatSuspected =>
                "possible additional NAT; check upstream routers or use a tunnel such as playit.gg",
            ReachabilityVerdict.HairpinConfirmed => $"local self-test passed on port {port}; internet access unconfirmed",
            _ => $"forward UDP+TCP {port} manually",
        },
    };

    /// <summary>Announces only when the triple actually changes — including the "unknown" -&gt;
    /// "discovered" transition, a UPnP verdict landing after the fact, and the reachability
    /// probe's verdict landing after THAT, since each one on its own is worth telling the
    /// player about.</summary>
    public void Observe(
        int port, IPEndPoint? publicEndPoint, UpnpMappingStatus upnp, ReachabilityVerdict reachability, Action<string> announce)
    {
        ArgumentNullException.ThrowIfNull(announce);

        (IPEndPoint?, UpnpMappingStatus, ReachabilityVerdict) current = (publicEndPoint, upnp, reachability);
        if (_last == current)
        {
            return;
        }

        _last = current;
        announce(Line(port, publicEndPoint, upnp, reachability));
    }

    /// <summary>Hosting stopped (or was never started): forget the last state, so hosting again
    /// announces fresh instead of staying quiet because it happens to start the same way the
    /// previous session ended.</summary>
    public void Reset() => _last = null;
}
