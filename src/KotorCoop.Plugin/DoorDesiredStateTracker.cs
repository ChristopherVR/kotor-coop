namespace KotorCoop.Plugin;

/// <summary>Apply a received state once, allowing local script changes to reach the scanner.</summary>
internal sealed class DoorDesiredStateTracker
{
    private readonly Dictionary<uint, byte> _applied = [];

    public bool IsNew(uint id, byte open, bool unlocked) =>
        !_applied.TryGetValue(id, out byte previous) || previous != Encode(open, unlocked);

    public void Applied(uint id, byte open, bool unlocked) => _applied[id] = Encode(open, unlocked);
    public void Clear() => _applied.Clear();
    private static byte Encode(byte open, bool unlocked) => (byte)(open | (unlocked ? 4 : 0));
}
