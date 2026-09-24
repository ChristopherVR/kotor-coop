using KotorCoop.Session;

namespace KotorCoop.Agent;

// Exact enemy-health baseline persistence (shared block version 76,
// docs/research/enemy-health-persistence.md). Pumped from the command loop, not from a
// session: the world lineage has to survive connects and disconnects, and a solo save made
// between two sessions needs its sidecar as much as a co-op one.
public sealed partial class CoopCommandDispatcher
{
    private EnemyScalingPersistence? _enemyBaseline;

    private void PumpEnemyScalingPersistence()
    {
        if (_kotor is null || string.IsNullOrEmpty(gameDirectory))
        {
            return;
        }

        _enemyBaseline ??= new EnemyScalingPersistence(
            _kotor, Path.Combine(gameDirectory, "saves"), line => Log?.Invoke(line));
        _enemyBaseline.Tick(Environment.TickCount64);
    }
}
