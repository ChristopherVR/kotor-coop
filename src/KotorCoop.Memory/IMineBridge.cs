// Floor-mine sync: collecting mines that were disarmed or detonated locally. Applying a peer's
// removal rides the placeable slot list (KotorGameBridge.TryWritePlaceableStates).
namespace KotorCoop.Memory;

public interface IMineBridge
{
    /// <summary>False when this bridge cannot see trap-trigger state.</summary>
    bool SupportsMineSync => false;

    /// <summary>
    /// GIT-placed floor mines that vanished locally since the last call (disarmed, recovered,
    /// key-disarmed or one-shot detonated), as mine-typed (<see cref="MineFlags"/>) world flags
    /// ready for the reliable flag channel. Each mine is reported at most once per module.
    /// </summary>
    List<KeyValuePair<ulong, sbyte>> CollectChangedMineState() => [];
}
