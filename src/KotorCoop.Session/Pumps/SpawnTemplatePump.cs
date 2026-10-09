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
    private const long FriendlyRefreshMs = 2_000;

    private IReadOnlyList<SpawnTemplateEntry> _written = [];
    private string _friendlyModule = string.Empty;
    private long _friendlyAtMs;

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

        // Friendly story actors are not in the host's mirror; tell the plugin which of this
        // module's blueprints it may build itself. A level, so rewritten now and then (the block
        // is zeroed with the session).
        long now = SessionClock.NowMs;
        if (!string.Equals(module, _friendlyModule, StringComparison.OrdinalIgnoreCase) || now - _friendlyAtMs >= FriendlyRefreshMs)
        {
            if (bridge.TryWriteFriendlyTemplates(module))
            {
                _friendlyModule = module;
            }

            _friendlyAtMs = now;
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
