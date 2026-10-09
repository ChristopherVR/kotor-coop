using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// Host spawns only (ScriptCreateObjectHook): on the host, publishes the blueprints of the
/// creatures this game's scripts created in its current world; on a joiner, hands the host's
/// list for the current module to the plugin, which builds those creatures from their real
/// template instead of a generic stand-in. Runs on the combat pump.
/// </summary>
internal sealed class SpawnTemplatePump(IGameBridge bridge, ICoopSession session)
{
    private IReadOnlyList<SpawnTemplateEntry> _written = [];

    public void Pump(in PlayerState local)
    {
        if (!bridge.TryReadCurrentModule(out string module) || module.Length == 0
            || ModuleNameGuard.IsFallbackModuleName(module)
            || !string.Equals(module, local.Module, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (session.IsEnemyAuthority)
        {
            if (bridge.TryReadLivenessEpoch(out uint epoch) && epoch != 0)
            {
                session.PublishSpawnTemplates(module, epoch, bridge.ReadHostScriptSpawns(epoch));
            }

            return;
        }

        IReadOnlyList<SpawnTemplateEntry> templates = session.GetSpawnTemplates(module);
        if (templates.SequenceEqual(_written))
        {
            return;
        }

        if (bridge.TryWriteSpawnTemplates(templates))
        {
            _written = templates;
        }
    }
}
