namespace KotorCoop.Plugin;

/// <summary>Identity observed from successful native recruitment, never inferred from tags.</summary>
internal sealed class PartyOccupantTable
{
    private readonly uint[] _generation = new uint[9];
    private readonly byte[] _templates = new byte[9 * 16];
    private uint _sequence;
    private readonly bool[] _replaced = new bool[9];
    private readonly uint[] _cachedBody = new uint[9], _retiredBody = new uint[9];
    private readonly bool[] _cachedBodyKnown = new bool[9], _retiredBodyKnown = new bool[9];
    private readonly bool[] _loadRestoreEligible = new bool[9];
    public bool RestoreVerifiedTemplate(int slot, uint expectedGeneration, ReadOnlySpan<byte> template, uint currentBody, bool replaced)
    {
        if ((uint)slot >= 9 || !_loadRestoreEligible[slot] || _generation[slot] != expectedGeneration
            || !ValidTemplate(template)) return false;
        if (_generation[slot] == 0) ObserveUnknown(slot);
        _templates.AsSpan(slot * 16, 16).Clear();
        template.CopyTo(_templates.AsSpan(slot * 16, 16));
        _loadRestoreEligible[slot] = false;
        _replaced[slot] |= replaced;
        _cachedBody[slot] = currentBody; _cachedBodyKnown[slot] = true;
        // This source was loaded into a fresh native world; there is no retained prior
        // body's payload to relabel. A later native admission records its own retirement.
        _retiredBody[slot] = 0x7F000000; _retiredBodyKnown[slot] = true;
        return true;
    }
    public void ObserveCachedBody(int slot, uint objectId)
    { if ((uint)slot < 9) { _cachedBody[slot] = objectId; _cachedBodyKnown[slot] = objectId != 0; } }
    public void ObserveSerialization(int slot, uint objectId)
    {
        if ((uint)slot >= 9 || !Replaced(slot) || !TemplateKnown(slot)) return;
        // SaveMember has resolved a real cached body. The old body's serialization must
        // not relabel its UTC as the newly admitted occupant, even on a later save retry.
        if (!_retiredBodyKnown[slot] || objectId == _retiredBody[slot]) ObserveUnknown(slot);
    }
    public bool TemplateKnown(int slot) => (uint)slot < 9 && _templates[slot * 16] != 0;
    public bool FixedStockIdentity(int slot)
    {
        if ((uint)slot >= 9) return false;
        if (_generation[slot] == 0) return true;
        if (_replaced[slot] || !PartyRosterTemplateTable.TryGetTemplate(slot, out string stock)) return false;
        ReadOnlySpan<byte> bytes = _templates.AsSpan(slot * 16, 16);
        for (int i = 0; i < stock.Length; i++)
            if (bytes[i] != (byte)stock[i]) return false;
        return stock.Length == 16 || bytes[stock.Length] == 0;
    }
    public bool LegacySideChannelAllowed(int slot) => (uint)slot < 9
        && (_generation[slot] == 0 || (!_replaced[slot] && TemplateKnown(slot)));
    public void ObserveAdmission(int slot)
    {
        if ((uint)slot < 9 && (TemplateKnown(slot) || Replaced(slot)
            || (_cachedBodyKnown[slot] && _cachedBody[slot] != 0x7F000000)))
        { _replaced[slot] = true; _retiredBody[slot] = _cachedBody[slot]; _retiredBodyKnown[slot] = _cachedBodyKnown[slot]; }
        ObserveUnknown(slot);
    }
    public bool Replaced(int slot) => (uint)slot < 9 && _replaced[slot];
    public void ObserveRemoval(int slot)
    {
        if ((uint)slot < 9) { _replaced[slot] = true; _retiredBody[slot] = _cachedBody[slot]; _retiredBodyKnown[slot] = _cachedBodyKnown[slot]; }
        ObserveUnknown(slot);
    }
    public void HoldUnprovenLoadedSlot(int slot)
    { if ((uint)slot < 9 && _generation[slot] == 0) { _replaced[slot] = true; ObserveUnknown(slot); _loadRestoreEligible[slot] = true; } }

    public uint Generation(int slot) => (uint)slot < 9 ? _generation[slot] : 0;
    public void CopyTemplate(int slot, Span<byte> destination)
    {
        destination.Clear();
        if ((uint)slot < 9) _templates.AsSpan(slot * 16, 16).CopyTo(destination);
    }
    public void ObserveUnknown(int slot)
    {
        if ((uint)slot >= 9) return;
        _loadRestoreEligible[slot] = false;
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
        Array.Clear(_loadRestoreEligible);
        Array.Clear(_cachedBody); Array.Clear(_retiredBody); Array.Clear(_cachedBodyKnown); Array.Clear(_retiredBodyKnown);
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
            else { ObserveUnknown(slot); _loadRestoreEligible[slot] = true; }
        }
    }
}
