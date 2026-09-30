namespace KotorCoop.Plugin;

// Retains the latest genuine local interaction edge until native state confirms it.
// Fixed storage is safe to call from the CloseInventory hook without allocating.
internal sealed class PlaceableLocalChangeTracker
{
    private const int Capacity = 32;
    private readonly uint[] _ids = new uint[Capacity];
    private readonly bool[] _open = new bool[Capacity];
    private readonly bool[] _pending = new bool[Capacity];
    private int _cursor;

    public void Record(uint id, bool open)
    {
        if (id is 0 or > 0xFFFF) return;
        int slot = Array.IndexOf(_ids, id);
        if (slot < 0) { slot = _cursor++ % Capacity; _ids[slot] = id; }
        _open[slot] = open;
        _pending[slot] = true;
    }

    public uint PendingId(int slot) => _pending[slot] ? _ids[slot] : 0;
    public bool Confirm(int slot, bool actualOpen)
    {
        if (!_pending[slot] || _open[slot] != actualOpen) return false;
        _pending[slot] = false;
        return true;
    }
    public void Clear() { Array.Clear(_ids); Array.Clear(_pending); _cursor = 0; }
}
