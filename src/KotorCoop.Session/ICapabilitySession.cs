// Content-fingerprint announcement, the negotiated channel capabilities it drives, the
// compatibility verdict for the dashboard, and the transport fault injector for live testing.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface ICapabilitySession
{
    /// <summary>
    /// Announces this instance's in-world content fingerprint (globals catalogue, save
    /// identity, current module + its GIT digest; each field 0/empty until resolved). Sent
    /// to the peer so a mismatch only knowable in-world refines the negotiated
    /// capabilities — and, because the module fields track wherever the player is standing,
    /// re-judged on every module change. Cheap to call every pump; the session only puts
    /// it on the wire when it changes or periodically.
    /// </summary>
    void PublishWorldDigest(in WorldDigest digest);

    /// <summary>
    /// The replication channels this session is currently allowed to use, after content
    /// compatibility with the peer(s) has been judged. Starts at <see
    /// cref="SessionCapabilities.All"/> and narrows if a mod mismatch is detected. The
    /// agent's pump consults this before each channel so a differently-modded pair still
    /// plays, minus whatever their difference makes unsafe.
    /// </summary>
    SessionCapabilities Capabilities { get; }

    /// <summary>The current compatibility verdict, for surfacing on the dashboard. Null
    /// until a peer has been evaluated.</summary>
    CompatibilityVerdict? Compatibility { get; }

    /// <summary>
    /// Roadmap item 14 phase 2: the raw <see cref="WorldDigest"/> most recently received from
    /// "the" remote peer — meaningful on a <c>CoopClient</c> (the host), where it drives
    /// <c>host_load_readopt</c>'s classifier; a host has many peers and no single "the remote",
    /// so it is left at the default (<see cref="WorldDigest.Empty"/>) there. See
    /// <c>CoopClient.HostWorldDigest</c>'s own doc comment for why the raw fields, not the
    /// already-compared <see cref="Compatibility"/> verdict, are what this needs.
    /// </summary>
    WorldDigest RemoteWorldDigest => WorldDigest.Empty;

    /// <summary>
    /// This session's transport fault injector, or null for a session with no real socket
    /// (the test stubs). Off by default. Driving it is how a drop is reproduced on the live
    /// rig without touching a cable — see <c>coop netsim</c> in <see cref="ConsoleCommand"/>.
    /// </summary>
    NetworkSim? Network => null;
}
