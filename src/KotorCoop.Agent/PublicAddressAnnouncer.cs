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
    private (IPEndPoint? PublicEndPoint, UpnpMappingStatus Upnp)? _last;

    /// <summary>
    /// The pure part: what to say for a given (public endpoint, UPnP status) pair. Kept
    /// separate from <see cref="Observe"/> so the decision is testable with no tick loop and no
    /// live session.
    /// </summary>
    public static string Line(int port, IPEndPoint? publicEndPoint, UpnpMappingStatus upnp)
    {
        string upnpNote = upnp switch
        {
            UpnpMappingStatus.Mapped => "UPnP mapped",
            UpnpMappingStatus.Unavailable => $"UPnP unavailable - forward UDP+TCP {port} manually",
            UpnpMappingStatus.Pending => "checking UPnP...",
            _ => $"forward UDP+TCP {port} manually",
        };

        return publicEndPoint is { } endpoint
            ? $"co-op: hosting on port {port} - public address {endpoint} ({upnpNote})"
            : $"co-op: hosting on port {port} - public address unknown ({upnpNote})";
    }

    /// <summary>Announces only when the pair actually changes — including the "unknown" -&gt;
    /// "discovered" transition and a UPnP verdict landing after the fact, since either one on
    /// its own is worth telling the player about.</summary>
    public void Observe(int port, IPEndPoint? publicEndPoint, UpnpMappingStatus upnp, Action<string> announce)
    {
        ArgumentNullException.ThrowIfNull(announce);

        (IPEndPoint?, UpnpMappingStatus) current = (publicEndPoint, upnp);
        if (_last == current)
        {
            return;
        }

        _last = current;
        announce(Line(port, publicEndPoint, upnp));
    }

    /// <summary>Hosting stopped (or was never started): forget the last state, so hosting again
    /// announces fresh instead of staying quiet because it happens to start the same way the
    /// previous session ended.</summary>
    public void Reset() => _last = null;
}
