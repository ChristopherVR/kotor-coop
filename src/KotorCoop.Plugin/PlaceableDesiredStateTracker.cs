namespace KotorCoop.Plugin;

/// <summary>
/// The placeable twin of <see cref="DoorDesiredStateTracker"/>: apply a received (open,
/// unlocked) tuple once, allowing a local story change (or the closing player's own panel) to
/// reach the scanner before the stale desired value gets re-applied on top of it.
///
/// This is what makes a story re-lock or a real container close STICK on the instance that just
/// produced it, replacing the old <c>ContainerCloseHook.WasEverClosed</c> latch: that latch
/// blocked a re-open FOREVER once seen, which would have broken a genuine later re-open (a
/// second player looting the same chest again, or a script re-opening it); this tracker only
/// blocks re-applying the SAME tuple, so a later, genuinely different value — from either
/// direction — still goes through.
/// </summary>
internal sealed class PlaceableDesiredStateTracker
{
    private readonly Dictionary<uint, byte> _applied = [];

    public bool IsNew(uint id, bool open, bool unlocked) =>
        !_applied.TryGetValue(id, out byte previous) || previous != Encode(open, unlocked);

    public void Applied(uint id, bool open, bool unlocked) => _applied[id] = Encode(open, unlocked);
    public void Clear() => _applied.Clear();
    private static byte Encode(bool open, bool unlocked) => (byte)((open ? 1 : 0) | (unlocked ? 2 : 0));
}
