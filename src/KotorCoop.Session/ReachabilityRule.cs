using System.Net;

namespace KotorCoop.Session;

/// <summary>
/// What a host can honestly say about whether a friend on the internet can reach it —
/// distinct from <see cref="UpnpMappingStatus"/>, which only says whether the ROUTER agreed to
/// open a port. The 2026-09-10 internet test (docs/research/network-topology.md §16) showed
/// the two are not the same thing: the router reported the mapping as a success while the
/// address was unreachable from outside. Additional NAT was suspected, not established.
/// </summary>
public enum ReachabilityVerdict
{
    /// <summary>Not decided yet: STUN, UPnP or the probe itself is still in flight.</summary>
    Pending,

    /// <summary>A probe datagram sent to the public address came back in through the
    /// session's own listener. This proves local NAT hairpinning, not WAN ingress or TCP.</summary>
    HairpinConfirmed,

    /// <summary>The address the internet sees (STUN) is not the address the router thinks it
    /// has on its WAN side, or that WAN address is itself a private/shared one. There may be
    /// another router, carrier NAT, a VPN or a different egress route. This does not prove
    /// that inbound forwarding is impossible or that the ISP is responsible.</summary>
    AdditionalNatSuspected,

    /// <summary>Nothing proved it either way. The common reason is a router that does not
    /// "hairpin" (route its own public address back inside), which many home routers do
    /// not — so an unconfirmed port may still be perfectly reachable from outside.</summary>
    Unconfirmed,
}

/// <summary>
/// The pure decision behind <see cref="ReachabilityCheck"/>: three facts in, one verdict out,
/// no sockets, so the rule is unit-tested without a router. The inputs are exactly the three
/// things a host can find out about itself with no help from a peer or a relay:
///
///   1. The router's own idea of its WAN address (UPnP <c>GetExternalIPAddress</c>).
///   2. The address the internet actually sees this host's game socket as (STUN).
///   3. Whether a probe datagram sent from a SECOND local socket to that public address came
///      back in through the session's listener (a "hairpin" through the router's NAT).
///
/// An address mismatch or private WAN address takes precedence as a possible additional-NAT
/// warning. A received probe proves only the local hairpin path. None of these observations
/// establishes whether an external peer can reach the host; that needs an external probe.
/// </summary>
public static class ReachabilityRule
{
    public static ReachabilityVerdict Decide(IPAddress? routerExternal, IPAddress? stunPublic, bool? hairpinReceived)
    {
        if (routerExternal is not null && IsPrivateOrShared(routerExternal))
        {
            return ReachabilityVerdict.AdditionalNatSuspected;
        }

        if (routerExternal is not null && stunPublic is not null && !routerExternal.Equals(stunPublic))
        {
            return ReachabilityVerdict.AdditionalNatSuspected;
        }

        return hairpinReceived == true ? ReachabilityVerdict.HairpinConfirmed : ReachabilityVerdict.Unconfirmed;
    }

    /// <summary>
    /// True for an IPv4 address no home router can legitimately hold as ITS public side:
    /// RFC 1918 private space (10/8, 172.16/12, 192.168/16 — a second router or an ISP modem
    /// in front of it, "double NAT") and RFC 6598 shared address space (100.64/10 — the block
    /// reserved specifically for carrier-grade NAT). A router whose WAN address is any of these
    /// is behind another NAT it cannot map.
    /// </summary>
    public static bool IsPrivateOrShared(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out int written) || written != 4)
        {
            return false;
        }

        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
    }
}
