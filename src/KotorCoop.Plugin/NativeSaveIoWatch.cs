namespace KotorCoop.Plugin;

/// <summary>One actual stall execution. Only writes to its four required output files count.</summary>
internal sealed class NativeSaveIoWatch
{
    private readonly nint[] _streams = new nint[32];
    private readonly uint[] _files = new uint[32];
    private string _directory = "";
    private uint _thread, _written, _closed;
    private bool _failed;
    public bool Active => _thread != 0;
    internal string CompletionDiagnostic(uint thread)
        => $"active={Active}, thread={thread == _thread}, failed={_failed}, written=0x{_written:X}, closed=0x{_closed:X}";
    public bool Begin(uint thread, string directory)
    {
        bool nested = Active;
        Array.Clear(_streams); Array.Clear(_files);
        _thread = thread; _directory = directory; _written = _closed = 0;
        _failed = nested || thread == 0 || !ValidDirectory(directory);
        return !_failed;
    }
    internal static bool ValidDirectory(string directory) => directory.Length is >= 10 and <= 95
        && directory.AsSpan(0, 6).IndexOfAnyExceptInRange('0', '9') < 0
        && directory.AsSpan(6, 3).SequenceEqual(" - ")
        && directory.AsSpan(9).IndexOfAny('\0', '/', '\\') < 0
        && directory.AsSpan(9).IndexOfAny(":.\r\n") < 0;
    public void Write(uint thread, nint stream, string path, uint actualBytes, uint expectedBytes)
    {
        if (!Active || thread != _thread) return;
        path = path.Replace('\\', '/');
        int slash = path.LastIndexOf('/');
        if (slash < 0) return;
        string parent = path[..slash];
        if (!parent.EndsWith('/' + _directory, StringComparison.OrdinalIgnoreCase)
            && !parent.Equals("SAVES:" + _directory, StringComparison.OrdinalIgnoreCase)
            && !parent.Equals(_directory, StringComparison.OrdinalIgnoreCase)) return;
        uint file = path[(slash + 1)..].ToUpperInvariant() switch
        { "SAVEGAME.SAV" => 1u, "PARTYTABLE.RES" => 2u, "SAVENFO.RES" => 4u, "GLOBALVARS.RES" => 8u, _ => 0u };
        if (file == 0) return;
        if (stream == 0 || actualBytes != expectedBytes) { _failed = true; return; }
        if (expectedBytes == 0) return;
        _written |= file;
        int i = Array.IndexOf(_streams, stream);
        if (i < 0) i = Array.IndexOf(_streams, (nint)0);
        if (i < 0) { _failed = true; return; }
        _streams[i] = stream; _files[i] |= file;
        _closed &= ~file;
    }
    public void Flush(uint thread, nint stream, int result)
    { if (Active && thread == _thread && Array.IndexOf(_streams, stream) >= 0 && result != 0) _failed = true; }
    public void Close(uint thread, nint stream, int result)
    {
        if (!Active || thread != _thread) return;
        int i = Array.IndexOf(_streams, stream);
        if (i < 0 || stream == 0) return;
        if (result != 0) _failed = true;
        else _closed |= _files[i];
        _streams[i] = 0; _files[i] = 0;
    }
    public void Fail(uint thread) { if (Active && thread == _thread) _failed = true; }
    public bool Complete(uint thread)
    {
        if (!Active || thread != _thread) return false;
        bool success = !_failed && _written == 15 && _closed == 15;
        _thread = 0;
        return success;
    }
}
