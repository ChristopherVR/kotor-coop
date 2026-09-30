namespace KotorCoop.Plugin;

/// <summary>Identity observed from successful native recruitment, never inferred from tags.</summary>
internal sealed class PartyOccupantTable
{
    private readonly uint[] _generation = new uint[9];
    private readonly byte[] _templates = new byte[9 * 16];
    private uint _sequence;
    private readonly bool[] _replaced = new bool[9];
    public bool TemplateKnown(int slot) => (uint)slot < 9 && _templates[slot * 16] != 0;
    public bool LegacySideChannelAllowed(int slot) => (uint)slot < 9
        && (_generation[slot] == 0 || (!_replaced[slot] && TemplateKnown(slot)));
    public void ObserveAdmission(int slot)
    {
        if ((uint)slot < 9 && TemplateKnown(slot)) _replaced[slot] = true;
        ObserveUnknown(slot);
    }
    public bool Replaced(int slot) => (uint)slot < 9 && _replaced[slot];
    public void ObserveRemoval(int slot) { if ((uint)slot < 9) _replaced[slot] = true; ObserveUnknown(slot); }

    public uint Generation(int slot) => (uint)slot < 9 ? _generation[slot] : 0;
    public void CopyTemplate(int slot, Span<byte> destination)
    {
        destination.Clear();
        if ((uint)slot < 9) _templates.AsSpan(slot * 16, 16).CopyTo(destination);
    }
    public void ObserveUnknown(int slot)
    {
        if ((uint)slot >= 9) return;
        _templates.AsSpan(slot * 16, 16).Clear();
        if (++_sequence == 0) ++_sequence;
        _generation[slot] = _sequence;
    }
    public bool ObserveTemplate(int slot, ReadOnlySpan<byte> template, bool succeeded)
    {
        if ((uint)slot >= 9 || !succeeded || !ValidTemplate(template)) return false;
        if (TemplateKnown(slot)) _replaced[slot] = true;
        ObserveUnknown(slot);
        template.CopyTo(_templates.AsSpan(slot * 16, 16));
        return true;
    }
    public static bool ValidTemplate(ReadOnlySpan<byte> template)
    {
        if (template.Length is < 1 or > 16) return false;
        foreach (byte c in template)
            if (!(c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'_')) return false;
        return true;
    }
    public void InvalidateWorld()
    {
        for (int slot = 0; slot < 9; slot++)
        {
            if (_generation[slot] == 0) continue;
            // The existing fixed stock-slot contract remains available after loading a
            // save. A replaced or non-stock template never falls back to that contract.
            ReadOnlySpan<byte> bytes = _templates.AsSpan(slot * 16,16);
            int length = bytes.IndexOf((byte)0); if (length < 0) length = 16;
            bool fixedStock = !_replaced[slot] && PartyRosterTemplateTable.TryGetTemplate(slot, out string stock)
                && System.Text.Encoding.ASCII.GetString(bytes[..length]).Equals(stock, StringComparison.OrdinalIgnoreCase);
            if (fixedStock) { _generation[slot] = 0; _templates.AsSpan(slot * 16,16).Clear(); }
            else ObserveUnknown(slot);
        }
    }
}
