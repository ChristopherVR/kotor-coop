namespace KotorCoop.Plugin;

// Only coherent, accepted occupant proofs may replace an equipment binding.
internal sealed class PartyPresenceGearBindingCache(int capacity)
{
    private readonly Binding[] _bindings = new Binding[capacity];
    private readonly record struct Binding(bool Known, uint Session, uint Generation, uint ObjectId);

    public bool Observe(int slot, uint session, uint generation, uint objectId)
    {
        if ((uint)slot >= (uint)_bindings.Length || objectId is 0 or 0x7F000000) return false;
        Binding next = new(true, session, generation, objectId);
        if (_bindings[slot] == next) return false;
        _bindings[slot] = next;
        return true;
    }

    public void Forget(int slot)
    {
        if ((uint)slot < (uint)_bindings.Length) _bindings[slot] = default;
    }
}
