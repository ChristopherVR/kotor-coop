// This peer's own identity and the read-only view of every remote peer's avatar state.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IPeerSession
{
    uint LocalPeerId { get; }

    bool IsConnected { get; }

    string LocalName { get; }

    /// <summary>Publishes the local avatar. Sent on the next tick.</summary>
    void PublishLocalState(in PlayerState state);

    /// <summary>Every remote peer, interpolated to the current render time.</summary>
    IReadOnlyList<RemotePeer> GetRemotePeers();
}
