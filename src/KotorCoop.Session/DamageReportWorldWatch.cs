namespace KotorCoop.Session;

/// <summary>A native run can report against its admitted host world only. Hearing a new
/// host digest cannot relabel the old run: actual local world adoption must reset the
/// native generation first. Empty ticks establish the binding before the first hit.</summary>
public sealed class DamageReportWorldWatch
{
    private bool _bound;
    private uint _session, _serial, _epoch, _generation;
    private string _module = string.Empty;

    public bool Accept(uint session, string hostModule, uint serial, uint epoch, string localModule, uint generation)
    {
        if (session == 0 || epoch == 0 || generation == 0 || localModule.Length == 0
            || !string.Equals(hostModule, localModule, StringComparison.OrdinalIgnoreCase)) return false;
        bool changed = _bound && (session != _session || serial != _serial || epoch != _epoch
            || !string.Equals(hostModule, _module, StringComparison.OrdinalIgnoreCase));
        if (changed && unchecked((int)(generation - _generation)) <= 0) return false;
        if (_bound && generation != _generation && unchecked((int)(generation - _generation)) <= 0) return false;
        _bound = true;
        _session = session; _serial = serial; _epoch = epoch; _generation = generation; _module = hostModule;
        return true;
    }
}
