using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — Per-object interaction locks — "hold this one still, I am using it".
/// The host arbitrates through InteractionLockStore's lease and broadcasts
/// the live set as a level every tick, so a lost datagram costs one tick of
/// staleness rather than an NPC held forever.
/// </summary>
public sealed partial class CoopHost
{
    // ---- per-object interaction locks -------------------------------------------------
    //
    // "Hold this one still, I am using it." The host arbitrates and broadcasts; every peer
    // applies the result to the objects IT owns. See InteractionLockStore for why this is a
    // lease rather than a claim/release handshake.

    private readonly InteractionLockStore _interactionLocks = new();

    /// <summary>The host's own claim goes through the same store as a client's, so ownership
    /// is decided in exactly one place — the same discipline as the dialogue token.
    ///
    /// Called every tick whether or not anything is claimed, so the module the broadcast is
    /// scoped to is always current: a lock names a TAG, and tags only mean anything inside
    /// the module that loaded them.</summary>
    public void PublishInteractionClaim(
        string module, string tag, InteractionKind kind, bool active, uint targetId = 0)
    {
        _interactionModule = module ?? string.Empty;

        // A Container claim keys on the object id instead of the tag (InteractionLockStore's
        // own remarks) — an empty tag is the NORMAL shape for one, not "nothing to claim".
        if (string.IsNullOrEmpty(tag) && !(kind == InteractionKind.Container && targetId != 0))
        {
            return;
        }

        _interactionLocks.Claim(
            new InteractionClaimPacket(HostPeerId, _interactionModule, tag, kind, active, targetId),
            SessionClock.NowMs);
    }

    private string _interactionModule = string.Empty;

    public IReadOnlyList<InteractionLock> GetInteractionLocks()
    {
        lock (_gate)
        {
            return _interactionLocksLatest;
        }
    }

    /// <summary>What was last broadcast, so the host's own plugin sees exactly what the
    /// clients see rather than a separately-computed opinion.</summary>
    private IReadOnlyList<InteractionLock> _interactionLocksLatest = [];

    private void HandleInteractionClaim(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadInteractionClaim(span) is not { } claim)
        {
            _channel.NoteMalformed();
            return;
        }

        // Only a known peer may claim, and only for its own peer id — the same rule the
        // dialogue token uses, and for the same reason.
        lock (_gate)
        {
            if (!_peers.ContainsKey(claim.PeerId))
            {
                return;
            }
        }

        _interactionLocks.Claim(claim, SessionClock.NowMs);
    }

    /// <summary>
    /// Broadcasts the live locks for a module. Sent as a LEVEL every tick, not as edges: a
    /// lost datagram then costs one tick of a stale hold rather than an NPC held forever, and
    /// a peer that joins mid-conversation learns the state without anyone re-announcing it.
    /// </summary>
    private void BroadcastInteractionLocks(List<PeerConnection> recipients)
    {
        IReadOnlyList<InteractionLock> locks =
            _interactionLocks.Collect(_interactionModule, SessionClock.NowMs);

        lock (_gate)
        {
            _interactionLocksLatest = locks;
        }

        if (recipients.Count == 0)
        {
            return;
        }

        int length = PacketCodec.Write(
            _sendBuffer,
            new InteractionLockPacket(SessionClock.NowMs, _interactionModule, locks));
        if (length <= 0)
        {
            return;
        }

        foreach (PeerConnection peer in recipients)
        {
            _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
        }
    }
}
