using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — the party-select relay: receives a peer's request and queues it for the pump to
/// drive through the plugin (opening a real, native GUI takes a frame — unlike
/// <c>InteractionLockStore</c>'s pure in-memory arbitration, this cannot be answered
/// synchronously in-process). See IPartySelectSession's own remarks and
/// docs/research/galaxy-map-and-party-select.md's 2026-09-25 relay section.
/// </summary>
public sealed partial class CoopHost
{
    private readonly List<PartySelectRequestPacket> _pendingPartySelectRequests = [];

    /// <inheritdoc />
    /// <remarks>The host's own version of this call — reachable in principle (symmetry with
    /// <see cref="CoopClient.RequestPartySelect"/>), though in practice the host's own
    /// party-select screen is never suppressed/captured, so nothing calls it today. Queues
    /// through the SAME path a peer's packet would, so the pump does not need a host/client
    /// branch when it comes to acting on a request. NOT fully symmetric, by the way: a request
    /// filed this way carries <see cref="HostPeerId"/> as its <c>PeerId</c>, and
    /// <see cref="SendPartySelectResponse"/> can only reach an entry in <c>_peers</c> — a
    /// remote connection, never the host itself — so a self-filed request's eventual response
    /// is silently dropped rather than looped back. Left this way because the call path is
    /// unreachable today; wiring a real loopback is future work if this ever needs to be
    /// exercised.</remarks>
    public void RequestPartySelect(
        string module, string exitScript, int forceNpc1, int forceNpc2, uint requestSerial)
    {
        if (requestSerial == 0)
        {
            return;
        }

        lock (_gate)
        {
            _pendingPartySelectRequests.Add(
                new PartySelectRequestPacket(HostPeerId, requestSerial, module, exitScript, forceNpc1, forceNpc2));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PartySelectRequestPacket> DrainPartySelectRequests()
    {
        lock (_gate)
        {
            if (_pendingPartySelectRequests.Count == 0)
            {
                return [];
            }

            PartySelectRequestPacket[] drained = [.. _pendingPartySelectRequests];
            _pendingPartySelectRequests.Clear();
            return drained;
        }
    }

    /// <inheritdoc />
    public void SendPartySelectResponse(uint peerId, PartySelectResponsePacket response)
    {
        PeerConnection? peer;
        lock (_gate)
        {
            if (!_peers.TryGetValue(peerId, out peer))
            {
                return;
            }
        }

        SendFromCaller(peer.EndPoint, response, static (buffer, packet) => PacketCodec.Write(buffer, packet));
    }

    private void HandlePartySelectRequest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPartySelectRequest(span) is not { } request)
        {
            _channel.NoteMalformed();
            return;
        }

        // Only a known peer, for its own peer id — the same rule HandleInteractionClaim uses.
        lock (_gate)
        {
            if (!_peers.ContainsKey(request.PeerId))
            {
                return;
            }

            _pendingPartySelectRequests.Add(request);
        }
    }
}
