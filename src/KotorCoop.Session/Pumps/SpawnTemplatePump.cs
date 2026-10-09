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
        // The host's module rides along: in a follow the host arrives first, and a scene's
        // story actor can be created in the joiner's first second there, before this pump has
        // run in the new module (sta_m45aa's Carth, 2026-10-10).
        string hostModule = HostModule();
        string key = module + "|" + hostModule;
        long now = SessionClock.NowMs;
        if (!string.Equals(key, _friendlyModule, StringComparison.OrdinalIgnoreCase) || now - _friendlyAtMs >= FriendlyRefreshMs)
        {
            string[] modules = hostModule.Length == 0 || string.Equals(hostModule, module, StringComparison.OrdinalIgnoreCase)
                ? [module]
                : [module, hostModule];
            if (bridge.TryWriteFriendlyTemplates(modules))
            {
                _friendlyModule = key;
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

    private string HostModule()
    {
        foreach (RemotePeer peer in session.GetRemotePeers())
        {
            if (peer.PeerId == CoopHost.HostPeerId)
            {
                string module = peer.State.Module ?? string.Empty;
                return ModuleNameGuard.IsFallbackModuleName(module) ? string.Empty : module;
            }
        }

        return string.Empty;
    }
}
