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
    /// This install's content fingerprint, announced to the peer so a mod mismatch that
    /// would corrupt shared state is caught at join time. Defaults to
    /// <see cref="ContentDigest.Unknown"/> (agents without game-file access, and the mock
    /// harness), which compares as "unchecked" and never blocks a session.
    /// </summary>
    public ContentDigest Digest { get; init; } = ContentDigest.Unknown;

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
}
