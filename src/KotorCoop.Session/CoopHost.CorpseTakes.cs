using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — shared corpse loot (corpse_loot = shared): every player's body-bag takes in the
/// current module. The host keeps its own list and each client's, applies the clients' to its
/// own bags (CorpseLootPump), and relays every list to every client as a level, each second and
/// on change.
/// </summary>
public sealed partial class CoopHost
{
    /// <summary>The origin id the host's own list travels under; no client is ever peer 0.</summary>
    public const uint HostCorpseOrigin = 0;

    private const int CorpseTakesIntervalMs = 1000;

    private readonly Dictionary<uint, (string Module, IReadOnlyList<CorpseTakeEntry> Entries)> _corpseTakes = [];
    private bool _corpseTakesChanged;
    private long _lastCorpseTakesMs;

    public void PublishCorpseTakes(string module, IReadOnlyList<CorpseTakeEntry> entries) =>
        StoreCorpseTakes(HostCorpseOrigin, module, entries);

    public IReadOnlyList<(uint Origin, IReadOnlyList<CorpseTakeEntry> Entries)> GetRemoteCorpseTakes(string module)
    {
        lock (_gate)
        {
            return [.. _corpseTakes
                .Where(p => p.Key != HostCorpseOrigin && string.Equals(p.Value.Module, module, StringComparison.OrdinalIgnoreCase))
                .Select(p => (p.Key, p.Value.Entries))];
        }
    }

    private void StoreCorpseTakes(uint origin, string module, IReadOnlyList<CorpseTakeEntry> entries)
    {
        lock (_gate)
        {
            if (_corpseTakes.TryGetValue(origin, out var known)
                && string.Equals(known.Module, module, StringComparison.OrdinalIgnoreCase)
                && known.Entries.SequenceEqual(entries))
            {
                return;
            }

            _corpseTakes[origin] = (module, [.. entries]);
            _corpseTakesChanged = true;
        }
    }

    private string _hostBagsModule = string.Empty;
    private IReadOnlyList<HostBodyBagPacket> _hostBags = [];
    private long _lastHostBagsMs;

    /// <summary>Host: every one of this game's body bags in <paramref name="module"/>.</summary>
    public void PublishHostBodyBags(string module, IReadOnlyList<(float X, float Y, IReadOnlyList<BodyBagItemEntry> Items)> bags)
    {
        lock (_gate)
        {
            _hostBagsModule = module;
            _hostBags = [.. bags.Select(b => new HostBodyBagPacket(_sessionId, module, b.X, b.Y, b.Items))];
        }
    }

    private void BroadcastHostBodyBags(long now, List<PeerConnection> recipients)
    {
        IReadOnlyList<HostBodyBagPacket> bags;
        lock (_gate)
        {
            if (recipients.Count == 0 || _hostBags.Count == 0 || now - _lastHostBagsMs < CorpseTakesIntervalMs)
            {
                return;
            }

            _lastHostBagsMs = now;
            bags = _hostBags;
        }

        foreach (HostBodyBagPacket bag in bags)
        {
            int length = PacketCodec.Write(_sendBuffer, bag);
            if (length == 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    private void HandleCorpseTakes(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadCorpseTakes(span) is not { } packet)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            // Only a connected peer, speaking for itself.
            if (packet.OriginPeerId == HostCorpseOrigin
                || !_peers.TryGetValue(packet.OriginPeerId, out PeerConnection? peer) || !peer.EndPoint.Equals(from))
            {
                return;
            }
        }

        StoreCorpseTakes(packet.OriginPeerId, packet.Module, packet.Entries);
    }

    private void BroadcastCorpseTakes(long now, List<PeerConnection> recipients)
    {
        List<CorpseTakesPacket> packets;
        lock (_gate)
        {
            if (recipients.Count == 0 || _corpseTakes.Count == 0
                || (!_corpseTakesChanged && now - _lastCorpseTakesMs < CorpseTakesIntervalMs))
            {
                return;
            }

            _corpseTakesChanged = false;
            _lastCorpseTakesMs = now;
            packets = [.. _corpseTakes.Select(p => new CorpseTakesPacket(_sessionId, p.Key, p.Value.Module, p.Value.Entries))];
        }

        foreach (CorpseTakesPacket packet in packets)
        {
            int length = PacketCodec.Write(_sendBuffer, packet);
            if (length == 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                if (peer.Id != packet.OriginPeerId)
                {
                    _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
                }
            }
        }
    }
}
