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

    // ---- explicit arbitration: closes the container-loot race, docs/roadmap-seamless.md
    // Tier 1 item 4 ----------------------------------------------------------------------
    //
    // The per-tick claim/level pair above is EVENTUALLY consistent by design: two clicks
    // inside one RTT both read "not held yet" locally and both proceed before either claim's
    // effect has crossed the wire. RequestContainerLock/TryGetLockResponse add a SYNCHRONOUS
    // question on top of the exact same InteractionLockStore.Claim decision, so the loser
    // finds out before its own OpenInventory ever runs — see ContainerUseHook's deferral and
    // docs/research/container-sync.md's container_lock_arbitration section.

    /// <summary>The host's answer to its OWN most recent local request — resolved
    /// synchronously in-process, the moment <see cref="RequestContainerLock"/> is called, so
    /// the host's own plugin never has to wait for a network round trip it is not making. Only
    /// one slot: the plugin's own deferral state machine only ever has one request in flight
    /// at a time (a single local player can only be clicking one container).</summary>
    private uint _lastLocalLockResponseSerial;
    private bool _lastLocalLockResponseGranted;

    /// <summary>
    /// Requests explicit arbitration for a <see cref="InteractionKind.Container"/> claim right
    /// now, instead of leaving it to the periodic level to eventually converge.
    ///
    /// Symmetric by construction with a client's own <see cref="CoopClient.RequestContainerLock"/>:
    /// this runs the identical <c>InteractionLockStore.Claim</c> call
    /// <see cref="PublishInteractionClaim"/> already uses for the host's own ordinary claim — the
    /// SAME rule, SAME store, just asked for and answered immediately rather than left for the
    /// next broadcast. No network hop, because the host IS the arbiter: this is the whole
    /// "the host's own clicks go through the same arbiter in-process" half of the design.
    /// </summary>
    public void RequestContainerLock(string module, uint targetId, uint requestSerial)
    {
        if (targetId == 0 || requestSerial == 0)
        {
            return;
        }

        _interactionModule = module ?? string.Empty;

        bool granted = _interactionLocks.Claim(
            new InteractionClaimPacket(
                HostPeerId, _interactionModule, string.Empty, InteractionKind.Container, true,
                targetId, requestSerial),
            SessionClock.NowMs);

        _lastLocalLockResponseSerial = requestSerial;
        _lastLocalLockResponseGranted = granted;
    }

    /// <summary>The answer to the host's own most recent <see cref="RequestContainerLock"/>,
    /// if <paramref name="requestSerial"/> matches it — always immediately true on the same
    /// call that requested it, since arbitration never leaves this process.</summary>
    public bool TryGetLockResponse(uint requestSerial, out bool granted)
    {
        if (requestSerial != 0 && requestSerial == _lastLocalLockResponseSerial)
        {
            granted = _lastLocalLockResponseGranted;
            return true;
        }

        granted = false;
        return false;
    }

    private void HandleInteractionClaim(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadInteractionClaim(span) is not { } claim)
        {
            _channel.NoteMalformed();
            return;
        }

        // Only a known peer may claim, and only for its own peer id — the same rule the
        // dialogue token uses, and for the same reason.
        PeerConnection? peer;
        lock (_gate)
        {
            if (!_peers.TryGetValue(claim.PeerId, out peer))
            {
                return;
            }
        }

        bool granted = _interactionLocks.Claim(claim, SessionClock.NowMs);

        // An explicit arbitration request (RequestSerial != 0, protocol version 44) gets an
        // immediate, UNICAST answer instead of waiting for the requester to notice its claim
        // did or did not land on the next InteractionLockPacket broadcast — see
        // docs/research/container-sync.md's container_lock_arbitration section. The ordinary
        // per-tick republish (RequestSerial == 0) is unaffected: no response is ever sent for
        // it, exactly as before this feature existed.
        if (claim.RequestSerial != 0)
        {
            // Private buffer: this runs on the receive loop while Tick() is encoding broadcasts
            // into _sendBuffer on the tick thread — see CoopClient.RequestContainerLock.
            byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
            int length = PacketCodec.Write(
                buffer,
                new LockResponsePacket(claim.PeerId, claim.Kind, claim.TargetId, claim.RequestSerial, granted));
            if (length > 0)
            {
                _channel.Send(peer.EndPoint, buffer.AsSpan(0, length));
            }
        }
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
