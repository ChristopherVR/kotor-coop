using System.Net;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// Announces a hosting player's internet reachability once it is known — the public IP:port
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
    /// </summary>
    public static string Line(int port, IPEndPoint? publicEndPoint, UpnpMappingStatus upnp, ReachabilityVerdict reachability)
    {
        string upnpNote = UpnpNote(upnp, port, reachability);

        return publicEndPoint is { } endpoint
            ? $"co-op: hosting on port {port} - public address {endpoint} ({upnpNote})"
            : $"co-op: hosting on port {port} - public address unknown ({upnpNote})";
    }

    /// <summary>
    /// The parenthetical every status surface shares (this announcer, <c>status</c>,
    /// <c>public_address</c>). "UPnP mapped" alone over-promised — docs/research/network-topology.md
    /// §16 saw it reported while the port was unreachable — so a mapping is always qualified by
    /// the self-probe's verdict (<see cref="ReachabilityVerdict"/>), and the two verdicts that
    /// change what a player should DO (carrier NAT: a manual forward cannot help either; a
    /// manual forward that already answers) are named under an unavailable mapping too.
    /// </summary>
    public static string UpnpNote(UpnpMappingStatus upnp, int port, ReachabilityVerdict reachability) => upnp switch
    {
        UpnpMappingStatus.Mapped => reachability switch
        {
            ReachabilityVerdict.Reachable => "UPnP mapped - reachable from the internet",
            ReachabilityVerdict.CarrierNat =>
                "UPnP mapped but NOT reachable (your ISP shares your address - use a tunnel such as playit.gg)",
            ReachabilityVerdict.Unconfirmed =>
                "UPnP mapped - could not confirm it is reachable; if a friend cannot connect, use a tunnel such as playit.gg",
            _ => "UPnP mapped - checking reachability...",
        },
        UpnpMappingStatus.Unavailable => reachability switch
        {
            ReachabilityVerdict.CarrierNat =>
                "UPnP unavailable and your ISP shares your address - a manual port forward will not help; use a tunnel such as playit.gg",
            ReachabilityVerdict.Reachable => $"UPnP unavailable but port {port} is reachable from the internet",
            _ => $"UPnP unavailable - forward UDP+TCP {port} manually",
        },
        UpnpMappingStatus.Pending => "checking UPnP...",
        _ => reachability switch
        {
            ReachabilityVerdict.CarrierNat =>
                "your ISP shares your address - a manual port forward will not help; use a tunnel such as playit.gg",
            ReachabilityVerdict.Reachable => $"port {port} is reachable from the internet",
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
