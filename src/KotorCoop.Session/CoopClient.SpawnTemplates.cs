using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — the host's script-spawned creature blueprints for its current world (host
/// spawns only). Replaced wholesale by each packet; only the admitted host's session counts.
/// </summary>
public sealed partial class CoopClient
{
    private IReadOnlyList<SpawnTemplateEntry> _spawnTemplates = [];
    private string _spawnTemplatesModule = string.Empty;

    /// <summary>The host's spawn blueprints for <paramref name="module"/>; empty when the host's
    /// list names another module.</summary>
    public IReadOnlyList<SpawnTemplateEntry> GetSpawnTemplates(string module)
    {
        lock (_gate)
        {
            return module.Length > 0 && string.Equals(module, _spawnTemplatesModule, StringComparison.OrdinalIgnoreCase)
                ? _spawnTemplates
                : [];
        }
    }

    private void HandleSpawnTemplates(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadSpawnTemplates(span) is not { } packet)
        {
            return;
        }

        ApplySpawnTemplates(packet);
    }

    internal void ApplySpawnTemplates(SpawnTemplatesPacket packet)
    {
        lock (_gate)
        {
            if (packet.HostSessionId != _hostSessionId)
            {
                return;
            }

            _spawnTemplatesModule = packet.Module;
            _spawnTemplates = packet.Entries;
        }
    }
}
