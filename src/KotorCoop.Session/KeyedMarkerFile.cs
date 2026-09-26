namespace KotorCoop.Session;

/// <summary>
/// A set of string keys persisted as one line each in a small text file under the install's
/// <c>saves\</c> folder: a latch that has to outlive the agent process (the same shape as
/// <see cref="StoreStockScaleLatch"/>'s own file). Every failure degrades to "in memory only"
/// with <see cref="LastFailure"/> saying why; nothing here throws.
/// </summary>
public sealed class KeyedMarkerFile(string? savesDirectory, string fileName, string header)
{
    private readonly string? _path = string.IsNullOrEmpty(savesDirectory) ? null : Path.Combine(savesDirectory, fileName);
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private bool _loaded;

    public string? LastFailure { get; private set; }

    public bool Contains(string key)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _keys.Contains(key);
        }
    }

    /// <summary>Adds <paramref name="key"/>; true when it was new.</summary>
    public bool Mark(string key)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_keys.Add(key))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    public void Unmark(string key)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_keys.Remove(key))
            {
                Save();
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (_path is null)
        {
            LastFailure = "no saves directory";
            return;
        }

        try
        {
            if (File.Exists(_path))
            {
                foreach (string line in File.ReadAllLines(_path))
                {
                    string key = line.Trim();
                    if (key.Length > 0 && key[0] != '#')
                    {
                        _keys.Add(key);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastFailure = ex.Message;
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            List<string> lines = [$"# {header}"];
            lines.AddRange(_keys.Order(StringComparer.OrdinalIgnoreCase));
            File.WriteAllLines(_path, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastFailure = ex.Message;
        }
    }
}
