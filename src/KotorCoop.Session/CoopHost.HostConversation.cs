using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost - the host-start conversation relay: receives a peer's refused host-only start and
/// queues it for the pump, which hands it to the host plugin (starting a conversation takes
/// engine frames, so it cannot be answered here). See IHostConversationSession.
/// </summary>
public sealed partial class CoopHost
{
    private readonly List<HostConversationRequestPacket> _pendingHostConversationRequests = [];

    /// <inheritdoc />
    public void RequestHostConversation(string module, string tag, string resref, uint requestSerial)
    {
        if (requestSerial == 0)
        {
            return;
        }

        lock (_gate)
        {
            _pendingHostConversationRequests.Add(
                new HostConversationRequestPacket(HostPeerId, requestSerial, module, tag, resref));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HostConversationRequestPacket> DrainHostConversationRequests()
    {
        lock (_gate)
        {
            if (_pendingHostConversationRequests.Count == 0)
            {
                return [];
            }

            HostConversationRequestPacket[] drained = [.. _pendingHostConversationRequests];
            _pendingHostConversationRequests.Clear();
            return drained;
        }
    }

    private void HandleHostConversationRequest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadHostConversationRequest(span) is not { } request)
        {
            _channel.NoteMalformed();
            return;
        }

        // Only a known peer, for its own peer id - the same rule HandlePartySelectRequest uses.
        lock (_gate)
        {
            if (!_peers.ContainsKey(request.PeerId))
            {
                return;
            }

            // Bounded: a flood from one peer must not grow an unread queue forever.
            if (_pendingHostConversationRequests.Count >= 16)
            {
                _pendingHostConversationRequests.RemoveAt(0);
            }

            _pendingHostConversationRequests.Add(request);
        }
    }
}
