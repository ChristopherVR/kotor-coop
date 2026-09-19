using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// coop.ini <c>store_stock</c>'s arithmetic (docs/research/player-count-scaling.md §Store):
/// how many MORE units of a finite stack the host tops a store up by on its first open.
/// Infinite stacks never reach here — the store scan excludes them (store-sync.md).
/// </summary>
public static class StoreStockScaleRule
{
    /// <summary>Units to ADD so <paramref name="count"/> becomes <paramref name="count"/> at
    /// <paramref name="percent"/>; 0 when nothing changes (a rate at or below 100 never removes
    /// stock — the shop's own goods are the player's to buy, and a removal would be a second
    /// once-only marker to get right). Capped to a ushort, the apply channel's count.</summary>
    public static ushort ExtraStock(int count, int percent)
    {
        if (count <= 0 || percent <= PlayerCountScalingRule.Vanilla)
        {
            return 0;
        }

        int scaled = PlayerCountScalingRule.Scale(count, percent);
        int extra = scaled - count;
        return extra <= 0 ? (ushort)0 : (ushort)Math.Min(extra, ushort.MaxValue);
    }

    /// <summary>The module every new game starts in. Entering it means a fresh playthrough
    /// with fresh (vanilla) shops, so the once-per-store marker starts over.</summary>
    public const string NewGameModule = "end_m01aa";

    public static bool IsNewGameModule(string? module) =>
        string.Equals(module, NewGameModule, StringComparison.OrdinalIgnoreCase);

    /// <summary>The marker key for one store: module + object id, both stable across loads of
    /// the same playthrough (ids are assigned in GIT order — store-sync.md §1.2).</summary>
    public static string Key(string module, uint storeObjectId) =>
        $"{module.ToLowerInvariant()}/{storeObjectId:X}";
}

/// <summary>
/// Remembers which stores this playthrough has already topped up, ACROSS PROCESSES: the extra
/// stock is written into the host's engine and baked into every save the host writes after,
/// so a marker that lived only in memory would top the same shop up again on every reload
/// and the stock would compound. Backed by one text file, one key per line
/// (<c>saves\kotorcoop-storescale.txt</c>, next to the other mod-owned sidecars). Reset when
/// the host enters the new-game module (<see cref="StoreStockScaleRule.NewGameModule"/>).
///
/// Fails SAFE: if the file cannot be read or written, <see cref="Persistent"/> is false and the
/// caller skips scaling altogether — a shop with vanilla stock is a smaller surprise than one
/// that grows on every load. A null path (tests, an install directory the agent could not
/// resolve) is the same "not persistent" state.
/// </summary>
public sealed class StoreStockScaleLatch
{
    public const string FileName = "kotorcoop-storescale.txt";

    private readonly string? _path;
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public StoreStockScaleLatch(string? savesDirectory)
    {
        _path = string.IsNullOrEmpty(savesDirectory) ? null : Path.Combine(savesDirectory, FileName);
    }

    /// <summary>Whether marks survive the process. False until the first successful load
    /// (an absent file loads as empty and counts as success) and after any failed write.</summary>
    public bool Persistent { get; private set; }

    /// <summary>A one-line reason the last load or write failed, for the caller's log.</summary>
    public string? LastFailure { get; private set; }

    public bool Contains(string key)
    {
        EnsureLoaded();
        return _keys.Contains(key);
    }

    /// <summary>Records a store as topped up and writes the file through. Returns false when
    /// the write failed (the mark is kept in memory for this process regardless).</summary>
    public bool Mark(string key)
    {
        EnsureLoaded();
        if (!_keys.Add(key))
        {
            return Persistent;
        }

        return Save();
    }

    /// <summary>Forgets every store — a new playthrough started.</summary>
    public bool Reset()
    {
        EnsureLoaded();
        if (_keys.Count == 0)
        {
            return Persistent;
        }

        _keys.Clear();
        return Save();
    }

    public int Count
    {
        get
        {
            EnsureLoaded();
            return _keys.Count;
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

            Persistent = true;
        }
        catch (Exception ex)
        {
            LastFailure = ex.Message;
            Persistent = false;
        }
    }

    private bool Save()
    {
        if (_path is null)
        {
            return false;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            List<string> lines = ["# stores this playthrough already topped up for store_stock (kotor-coop); delete to redo"];
            lines.AddRange(_keys.Order(StringComparer.OrdinalIgnoreCase));
            File.WriteAllLines(_path, lines);
            Persistent = true;
            return true;
        }
        catch (Exception ex)
        {
            LastFailure = ex.Message;
            Persistent = false;
            return false;
        }
    }
}
