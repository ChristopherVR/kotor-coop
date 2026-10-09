using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — shared corpse loot (corpse_loot = shared): this player's body-bag takes go to
/// the host as a level each second and on change; every other player's, relayed by the host,
/// are kept for CorpseLootPump to apply to this game's own bags.
/// </summary>
public sealed partial class CoopClient
{
    private const int CorpseTakesIntervalMs = 1000;

    private string _ownCorpseModule = string.Empty;
    private IReadOnlyList<CorpseTakeEntry> _ownCorpseTakes = [];
    private bool _ownCorpseTakesChanged;
    private long _lastCorpseTakesSentMs;
    private readonly Dictionary<uint, (string Module, IReadOnlyList<CorpseTakeEntry> Entries)> _remoteCorpseTakes = [];

    public void PublishCorpseTakes(string module, IReadOnlyList<CorpseTakeEntry> entries)
    {
        lock (_gate)
        {
            if (string.Equals(module, _ownCorpseModule, StringComparison.OrdinalIgnoreCase)
                && entries.SequenceEqual(_ownCorpseTakes))
            {
                return;
            }

            _ownCorpseModule = module;
            _ownCorpseTakes = [.. entries];
            _ownCorpseTakesChanged = true;
        }
    }

    public IReadOnlyList<(uint Origin, IReadOnlyList<CorpseTakeEntry> Entries)> GetRemoteCorpseTakes(string module)
    {
        lock (_gate)
        {
            return [.. _remoteCorpseTakes
                .Where(p => string.Equals(p.Value.Module, module, StringComparison.OrdinalIgnoreCase))
                .Select(p => (p.Key, p.Value.Entries))];
        }
    }

    private void FlushCorpseTakes(long now)
    {
        CorpseTakesPacket packet;
        lock (_gate)
        {
            if (_localPeerId == 0 || _ownCorpseModule.Length == 0 || _ownCorpseTakes.Count == 0
                || (!_ownCorpseTakesChanged && now - _lastCorpseTakesSentMs < CorpseTakesIntervalMs))
            {
                return;
            }

            _ownCorpseTakesChanged = false;
            _lastCorpseTakesSentMs = now;
            packet = new CorpseTakesPacket(_hostSessionId, _localPeerId, _ownCorpseModule, _ownCorpseTakes);
        }

        int length = PacketCodec.Write(_sendBuffer, packet);
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    /// <summary>A host bag older than this is gone on the host (emptied and removed).</summary>
    private const long HostBagFreshMs = 3000;

    private readonly Dictionary<(int X, int Y), (HostBodyBagPacket Bag, long AtMs)> _hostBags = [];

    /// <summary>The host's body bags in <paramref name="module"/> heard about recently.</summary>
    public IReadOnlyList<HostBodyBagPacket> GetHostBodyBags(string module)
    {
        long now = Environment.TickCount64;
        lock (_gate)
        {
            return [.. _hostBags.Values
                .Where(b => now - b.AtMs < HostBagFreshMs && string.Equals(b.Bag.Module, module, StringComparison.OrdinalIgnoreCase))
                .Select(b => b.Bag)];
        }
    }

    private void HandleHostBodyBag(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadHostBodyBag(span) is { } bag)
        {
            ApplyHostBodyBag(bag);
        }
    }

    internal void ApplyHostBodyBag(HostBodyBagPacket bag)
    {
        lock (_gate)
        {
            if (bag.HostSessionId != _hostSessionId)
            {
                return;
            }

            if (_hostBags.Count > 256)
            {
                _hostBags.Clear();
            }

            _hostBags[((int)MathF.Round(bag.X * 10), (int)MathF.Round(bag.Y * 10))] = (bag, Environment.TickCount64);
        }
    }

    private void HandleCorpseTakes(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadCorpseTakes(span) is not { } packet)
        {
            return;
        }

        ApplyCorpseTakes(packet);
    }

    internal void ApplyCorpseTakes(CorpseTakesPacket packet)
    {
        lock (_gate)
        {
            if (packet.HostSessionId != _hostSessionId || packet.OriginPeerId == _localPeerId)
            {
                return;
            }

            _remoteCorpseTakes[packet.OriginPeerId] = (packet.Module, packet.Entries);
        }
    }
}
