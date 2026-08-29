// Construction-time settings for a host or client session: identity, networking, and the
// content-fingerprint/compatibility knobs SessionManager reads before starting one.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public sealed record SessionSettings
{
    public string PlayerName { get; init; } = "player";

    /// <summary>
    /// What this host calls itself in other players' server browsers (coop.ini
    /// <c>server_name</c>). Empty falls back to <see cref="PlayerName"/> — the browser must
    /// always have something to show, and the character's name is what it showed before this
    /// existed.
    /// </summary>
    public string ServerName { get; init; } = string.Empty;

    /// <summary>The name this host advertises: its server name when set, else the player's.</summary>
    public string AdvertisedName =>
        ServerName.Length > 0 ? ServerName : PlayerName;

    public int Port { get; init; } = ProtocolConstants.DefaultPort;

    public int TickRate { get; init; } = ProtocolConstants.DefaultTickRate;

    public int InterpolationDelayMs { get; init; } = ProtocolConstants.DefaultInterpolationDelayMs;

    public int PeerTimeoutMs { get; init; } = ProtocolConstants.PeerTimeoutMs;

    /// <summary>
    /// Whether a CLIENT that has lost its host (silence past <see cref="HostLossTimeoutMs"/>,
    /// or an explicit <see cref="HostByePacket"/>) declares itself <c>HostLost</c> — coop.ini
    /// <c>host_loss_release</c>, ON by default. <see cref="CoopClient.HostLost"/> is what
    /// <c>CoopAgent.PumpOnce</c> reads to run every plugin-side lock's release
    /// (<c>KotorGameBridge.ClearCoopState</c>) and stop re-asserting the client-side control
    /// lock, so a solo player is never left holding a lock a host that is not coming back this
    /// tick still owns. OFF restores the pre-existing behaviour exactly: the client still
    /// re-Hellos in the background (nothing about reconnecting itself is gated on this), it
    /// simply never runs the extra release pass while doing so — every lock stays held until a
    /// full manual disconnect, the same as before this existed. Ignored by a host, which is
    /// never "lost" to itself. See docs/research/disconnect-reconnect.md's "host loss" section.
    /// </summary>
    public bool HostLossReleaseEnabled { get; init; } = true;

    /// <summary>
    /// How long a CLIENT'S host may stay silent before <see cref="HostLossReleaseEnabled"/>'s
    /// release pass fires — coop.ini <c>host_loss_timeout_ms</c>. Defaults to the SAME value as
    /// <see cref="PeerTimeoutMs"/> (5000), so out of the box "host lost" is declared in the
    /// exact tick the existing silence-based reconnect logic
    /// (<c>CoopClient.Tick</c>/<c>ForgetSession</c>) already gives up and starts re-Helloing —
    /// no new delay, just a release that used to never happen at all. Kept as an independent
    /// knob (rather than reusing <see cref="PeerTimeoutMs"/> outright) so a player who wants
    /// the release to wait a little longer than the reconnect attempt itself — betting a brief
    /// blip resolves before locks let go — can set this higher without changing how soon
    /// re-Helloing starts.
    /// </summary>
    public int HostLossTimeoutMs { get; init; } = ProtocolConstants.PeerTimeoutMs;

    /// <summary>
    /// This install's content fingerprint, announced to the peer so a mod mismatch that
    /// would corrupt shared state is caught at join time. Defaults to
    /// <see cref="ContentDigest.Unknown"/> (agents without game-file access, and the mock
    /// harness), which compares as "unchecked" and never blocks a session.
    /// </summary>
    public ContentDigest Digest { get; init; } = ContentDigest.Unknown;

    /// <summary>
    /// This install's behaviour-flag fingerprint (coop.ini/CLI settings that gate a wire
    /// channel or a locally-visible behaviour difference — docs/roadmap-seamless.md Tier 1
    /// item 7), announced to the peer so a divergence gets a warning (and, for a
    /// host-authoritative channel, gets adopted by a client) instead of running silently from
    /// local disk. Defaults to <see cref="BehaviourDigest.Default"/> for a caller (test fakes,
    /// the mock harness) that never built a real one.
    /// </summary>
    public BehaviourDigest Behaviour { get; init; } = BehaviourDigest.Default;

    /// <summary>
    /// Whether a peer on a different save is refused outright rather than admitted with the
    /// object-id-keyed channels switched off. Off by default: world adoption makes the saves
    /// match by construction, so a mismatch is a failed adoption to be reported and worked
    /// around, not a session to end. Mirrors
    /// <see cref="ContentCompatibility.RequireIdenticalSave"/>.
    /// </summary>
    public bool RequireIdenticalSave { get; init; }

    /// <summary>
    /// Session password. Empty means an open session. A host with a password admits only
    /// clients that present the same one; a client sends it in its join request. Reduced
    /// to a hash on the wire — a stranger-gate, not a security boundary.
    /// </summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// This player's stable identity, independent of its network address — the value a client
    /// puts in its Hello nonce and the host keys its peer slot on. 0 (the default) means "mint
    /// a fresh one for this session", which is the behaviour every caller had before this
    /// existed; a caller with somewhere to persist one uses <see cref="PeerIdentity"/>.
    ///
    /// Ignored by a host, which is peer 1 by definition and never dials anyone.
    /// </summary>
    public uint PeerIdentity { get; init; }

    /// <summary>
    /// How long a CLIENT'S connect attempt may sit silent before <see cref="SessionManager"/>
    /// gives up and reports "no reply from &lt;endpoint&gt;". Never applies to a host — a host
    /// has nobody to be admitted by, so there is nothing to time out waiting for.
    /// </summary>
    public int ConnectTimeoutMs { get; init; } = ProtocolConstants.DefaultConnectTimeoutMs;

    /// <summary>
    /// STUN servers (<c>host:port</c>, comma-separated) a HOST asks for its own public
    /// IP:port — coop.ini <c>stun_servers</c> (roadmap item 24, "public address discovery").
    /// Empty disables discovery entirely; ignored by a client. See
    /// <see cref="PublicAddressDiscovery"/> for the privacy note.
    /// </summary>
    public IReadOnlyList<string> StunServers { get; init; } = [];

    /// <summary>
    /// Whether a HOST attempts a UPnP IGD port mapping for its session port (and the
    /// world-transfer TCP listener sharing it) — coop.ini <c>upnp</c>. Fails soft when the
    /// router has UPnP off or absent (see <see cref="UpnpMappingService"/>). Ignored by a
    /// client.
    ///
    /// Defaults OFF at THIS layer deliberately — real hosting defaults it ON via coop.ini
    /// (<see cref="CoopConfig.Upnp"/>, applied explicitly in <c>AgentComposition</c>), but a
    /// bare <c>new SessionSettings()</c> (which most of this project's own tests build
    /// directly, never through coop.ini) must not spontaneously start a background SSDP
    /// multicast search — the same reasoning behind <see cref="StunServers"/> defaulting empty.
    /// </summary>
    public bool UpnpEnabled { get; init; }
}
