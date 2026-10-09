using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — the blueprints of the creatures this host's scripts created in its current world
/// (host spawns only, SpawnTemplatesPacket). Sent as a level: every second, and on the tick
/// the list changes, so a joiner usually has a creature's template before its first census
/// sighting of it.
/// </summary>
public sealed partial class CoopHost
{
    private const int SpawnTemplatesIntervalMs = 1000;

    private IReadOnlyList<SpawnTemplateEntry> _spawnTemplates = [];
    private string _spawnTemplatesModule = string.Empty;
    private uint _spawnTemplatesEpoch;
    private bool _spawnTemplatesChanged;
    private long _lastSpawnTemplatesMs;

    public void PublishSpawnTemplates(string module, uint nativeEpoch, IReadOnlyList<SpawnTemplateEntry> entries)
    {
        lock (_gate)
        {
            if (string.Equals(module, _spawnTemplatesModule, StringComparison.OrdinalIgnoreCase)
                && nativeEpoch == _spawnTemplatesEpoch && entries.SequenceEqual(_spawnTemplates))
            {
                return;
            }

            _spawnTemplatesModule = module;
            _spawnTemplatesEpoch = nativeEpoch;
            _spawnTemplates = [.. entries];
            _spawnTemplatesChanged = true;
        }
    }

    private void BroadcastSpawnTemplates(long now, List<PeerConnection> recipients)
    {
        SpawnTemplatesPacket packet;
        lock (_gate)
        {
            if (recipients.Count == 0 || _spawnTemplatesModule.Length == 0
                || (!_spawnTemplatesChanged && now - _lastSpawnTemplatesMs < SpawnTemplatesIntervalMs))
            {
                return;
            }

            _spawnTemplatesChanged = false;
            _lastSpawnTemplatesMs = now;
            packet = new SpawnTemplatesPacket(_sessionId, _spawnTemplatesModule, _spawnTemplatesEpoch, _spawnTemplates);
        }

        int length = PacketCodec.Write(_sendBuffer, packet);
        if (length == 0)
        {
            return;
        }

        foreach (PeerConnection peer in recipients)
        {
            _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
        }
    }
}
