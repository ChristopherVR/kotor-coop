// This peer's own identity and the read-only view of every remote peer's avatar state.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IPeerSession
{
    uint LocalPeerId { get; }

    bool IsConnected { get; }

    /// <summary>
    /// True on a CLIENT once it has declared its host lost — sustained silence past
    /// <see cref="SessionSettings.HostLossTimeoutMs"/>, or an explicit
    /// <see cref="HostByePacket"/> — and cleared again the moment a fresh <see
    /// cref="WelcomePacket"/> readmits it. Defaults false so a host (never "lost" to itself)
    /// and every existing test fake need no change. <c>CoopAgent.PumpOnce</c> is the only
    /// consumer: on the rising edge it runs the plugin-side release pass
    /// (<c>KotorGameBridge.ClearCoopState</c>) and stops touching the game for the rest of the
    /// outage — no remote-peer writes, no control-lock re-assert — while the session's own
    /// <c>RunAsync</c> loop keeps re-Helloing in the background regardless. See
    /// <see cref="HostLossRule"/> and docs/research/disconnect-reconnect.md's "host loss"
    /// section.
    /// </summary>
    bool HostLost => false;

    /// <summary>
    /// True while <see cref="HostLost"/> was raised by the host being AWAY (parked at the main
    /// menu or a load screen, still sending) rather than silent or gone — <see cref="HostAwayRule"/>.
    /// The release is identical either way; this only lets the agent word the chat line
    /// truthfully ("the host is at the main menu" rather than "connection lost"). Defaults
    /// false, like <see cref="HostLost"/>, for hosts and test fakes.
    /// </summary>
    bool HostAway => false;

    string LocalName { get; }

    /// <summary>Publishes the local avatar. Sent on the next tick.</summary>
    void PublishLocalState(in PlayerState state);

    /// <summary>Every remote peer, interpolated to the current render time.</summary>
    IReadOnlyList<RemotePeer> GetRemotePeers();
}
