using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — Per-object interaction locks — this client's own claim, re-sent every
/// tick while held (the lease itself is the release, so there is no
/// separate unclaim message), and the host's broadcast live set applied here.
/// </summary>
public sealed partial class CoopClient
{
    /// <summary>
    /// Sends this client's conversation status every tick, idle or not — same "state, not
    /// events" rule as the avatar update, so a released token is never lost to a dropped
    /// packet. The host's arbiter turns it into token grants/updates/releases.
    /// </summary>
    // ---- per-object interaction locks -------------------------------------------------

    private string _interactionModule = string.Empty;
    private string _interactionTag = string.Empty;
    private InteractionKind _interactionKind;
    private bool _interactionActive;
    private uint _interactionTargetId;

    private IReadOnlyList<InteractionLock> _interactionLocks = [];
    private string _interactionLockModule = string.Empty;

    public void PublishInteractionClaim(
        string module, string tag, InteractionKind kind, bool active, uint targetId = 0)
    {
        lock (_gate)
        {
            _interactionModule = module ?? string.Empty;
            _interactionTag = tag ?? string.Empty;
            _interactionKind = kind;
            _interactionActive = active;
            _interactionTargetId = targetId;
        }
    }

    /// <summary>
    /// The locks the host last broadcast, or empty when they describe a DIFFERENT module.
    ///
    /// The module check is not belt-and-braces: a lock names a TAG, tags are only unique
    /// inside a module, and a client that has walked through a door is briefly holding the
    /// previous area's set. Applying those would hold whatever happens to share a tag here.
    /// </summary>
    public IReadOnlyList<InteractionLock> GetInteractionLocks()
    {
        lock (_gate)
        {
            return string.Equals(_interactionLockModule, _interactionModule, StringComparison.OrdinalIgnoreCase)
                ? _interactionLocks
                : [];
        }
    }

    /// <summary>
    /// Re-sent every tick while a claim is held, never as an edge — the host's store expires
    /// a lease that stops being refreshed, so this IS the release: stop asking and the NPC is
    /// free within one lease. A client that crashes mid-conversation therefore cannot strand
    /// anybody, which no message-based release can promise.
    /// </summary>
    private void SendInteractionClaim()
    {
        string module;
        string tag;
        InteractionKind kind;
        bool active;
        uint targetId;
        lock (_gate)
        {
            module = _interactionModule;
            tag = _interactionTag;
            kind = _interactionKind;
            active = _interactionActive;
            targetId = _interactionTargetId;
        }

        // A Container claim keys on the object id instead of the tag — an empty tag is the
        // NORMAL shape for one, not "nothing to claim" (InteractionLockStore's own remarks).
        if (tag.Length == 0 && !(kind == InteractionKind.Container && targetId != 0))
        {
            return;
        }

        int length = PacketCodec.Write(
            _sendBuffer, new InteractionClaimPacket(_localPeerId, module, tag, kind, active, targetId));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void HandleInteractionLock(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadInteractionLock(span) is { } interaction)
        {
            lock (_gate)
            {
                // A LEVEL, replaced wholesale. The host sends the complete live set
                // every tick, so a lock that has gone simply is not in the next one —
                // there is no release to miss.
                _interactionLocks = interaction.Locks;
                _interactionLockModule = interaction.Module ?? string.Empty;
            }
        }
    }

    // ---- explicit arbitration: closes the container-loot race, docs/roadmap-seamless.md
    // Tier 1 item 4 ----------------------------------------------------------------------
    //
    // See CoopHost.InteractionLocks.cs's identical header remark. This half sends the ONE-SHOT
    // request over the wire (the ordinary per-tick claim above is unaffected — it keeps
    // republishing exactly as before) and remembers the last answer that arrived so
    // TryGetLockResponse can hand it back on the next poll.

    private uint _lastLockResponseSerial;
    private bool _lastLockResponseGranted;

    /// <summary>
    /// Sends one explicit arbitration request to the host — see
    /// <see cref="KotorCoop.Session.IInteractionLockSession.RequestContainerLock"/>'s own remarks
    /// for why this exists alongside the ordinary per-tick claim. Fire-and-forget: no retry, no
    /// ack of the request itself, because the caller's own deferral state machine already has a
    /// timeout that covers a lost request exactly the way it covers a lost reply.
    /// </summary>
    public void RequestContainerLock(string module, uint targetId, uint requestSerial)
    {
        if (targetId == 0 || requestSerial == 0)
        {
            return;
        }

        // NOT _sendBuffer: this is called from the plugin-pump thread while RunAsync's tick
        // loop is encoding its own packets into _sendBuffer on another thread. Sharing it
        // corrupted the request (the host dropped it as malformed), so the first click
        // silently lost the race it was supposed to win. This was the first sighting of the
        // hazard and used to fix itself alone with a freshly allocated buffer; every
        // caller-thread sender now shares one guarded buffer instead — see
        // CoopClient._apiBuffer.
        SendFromCaller(
            new InteractionClaimPacket(
                _localPeerId, module, string.Empty, InteractionKind.Container, true, targetId,
                requestSerial),
            static (buffer, packet) => PacketCodec.Write(buffer, packet));
    }

    /// <summary>The most recent <see cref="LockResponsePacket"/> the host sent us, if its
    /// serial matches <paramref name="requestSerial"/> — false while the round trip is still in
    /// flight (or was lost), which the caller's own timeout is what covers.</summary>
    public bool TryGetLockResponse(uint requestSerial, out bool granted)
    {
        lock (_gate)
        {
            if (requestSerial != 0 && requestSerial == _lastLockResponseSerial)
            {
                granted = _lastLockResponseGranted;
                return true;
            }
        }

        granted = false;
        return false;
    }

    private void HandleLockResponse(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadLockResponse(span) is not { } response)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            _lastLockResponseSerial = response.RequestSerial;
            _lastLockResponseGranted = response.Granted;
        }
    }
}
