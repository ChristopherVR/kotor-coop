using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

// World-origin classification and save-request capture for exact enemy-health baseline
// persistence (shared block version 76, docs/research/enemy-health-persistence.md). The
// decisions about what a world IS live in the agent; this file only reports, at the one
// canonical world-up edge, the facts the plugin alone can see: whether the teardown before it
// was announced, whether a native load request was published since the previous world-up, and
// whether that request was made with no world standing.
internal static unsafe partial class PluginHost
{
    /// <summary>Whether the teardown before the next world-up was an announced transition.
    /// False at process start: the first world is a load or a new game, never a transition.</summary>
    private static bool _nextWorldAnnounced;

    /// <summary>The load-provenance generation already attributed to a world-up.</summary>
    private static uint _provenanceAtLastWorldUp;

    /// <summary>True while a world epoch is live. Read by <see cref="LoadProvenanceHook"/> on
    /// the game thread to record whether a load request was made with no world standing.</summary>
    internal static bool WorldStanding => _epoch != 0;

    /// <summary>
    /// Called once per new world epoch, before any controller acts in it. A load request
    /// published since the previous world-up makes this world that load's result (the agent
    /// still requires a successful-load edge and the exact archive before trusting it);
    /// otherwise an announced teardown makes it a transition, and anything else is a new-game
    /// candidate, which the controller accepts only in the retail start module and only
    /// while the load observers are installed (an unobserved load must never look new).
    /// </summary>
    private static void BeginEnemyBaselineWorld()
    {
        if (_enemyScaling is not { } scaling || _view is not { } view)
        {
            return;
        }

        uint provenance = LoadProvenanceHook.PublishedGeneration;
        bool loaded = provenance != 0 && provenance != _provenanceAtLastWorldUp;
        _provenanceAtLastWorldUp = provenance;

        EnemyScalingWorldOrigin origin = loaded
            ? EnemyScalingWorldOrigin.Load
            : _nextWorldAnnounced ? EnemyScalingWorldOrigin.Transition : EnemyScalingWorldOrigin.Unknown;
        bool newGameCandidate = !loaded && !_nextWorldAnnounced && LoadProvenanceHook.Installed;

        uint flags = LoadProvenanceHook.Installed ? EnemyScalingBaselineChannel.OriginFlagProvenanceObserved : 0;
        if (loaded && LoadProvenanceHook.LastPublishedWithoutWorld)
        {
            flags |= EnemyScalingBaselineChannel.OriginFlagRequestWithoutWorld;
        }

        _nextWorldAnnounced = false;
        scaling.BeginWorld(origin, newGameCandidate, provenance, flags, view.WorldLoadSerial);
    }

    /// <summary>Game thread, from <see cref="SaveFlushHook"/>'s callback at the true entry of
    /// <c>CServerExoAppInternal::SaveGame</c>, for every request (held or passed through, the
    /// engine's autosaves too). Copies the name out of the caller's CExoString and publishes
    /// it; no engine call, no logging.</summary>
    internal static void PublishEnemyBaselineSaveRequest(int number, nint name)
    {
        if (_enemyScaling is { } scaling && Odyssey.TryReadExoString(name, SaveFlushHook.NameCapacity, out string text))
        {
            scaling.PublishSaveRequest(number, text);
        }
    }
}
