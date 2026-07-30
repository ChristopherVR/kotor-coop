using System.Globalization;
using System.Runtime.InteropServices;
using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Agent;

/// <summary>
/// An interactive value scanner. This is the tool that turns profiles/kotor1.json from a
/// template into a working profile, and the loop is the same one anyone doing this by
/// hand in Cheat Engine would run:
///
///   scan -5000 5000   take every plausible coordinate in the process
///   (walk in game)
///   changed           keep the ones that moved
///   (stand still)
///   same              keep the ones that did not
///   vec3              keep only those that look like the x of an (x,y,z) triple
///   ... repeat until a handful remain, then 'watch' to confirm and 'ptr' to find a path
/// </summary>
public sealed class ScanConsole(ProcessMemory memory)
{
    private readonly ValueScanner _scanner = new(memory);
    private FloatCandidateSet? _candidates;
    private MemorySnapshot? _snapshot;
    private List<nint>? _intCandidates;

    public void Run()
    {
        Console.WriteLine();
        Console.WriteLine($"attached to pid {memory.ProcessId} ({memory.ProcessName}), {(memory.Is32Bit ? "32-bit" : "64-bit")}");
        Console.WriteLine("type 'help' for commands, 'quit' to leave");
        Console.WriteLine();

        while (true)
        {
            Console.Write($"scan[{_candidates?.Count.ToString(CultureInfo.InvariantCulture) ?? "-"}]> ");
            string? line = Console.ReadLine();

            if (line is null or "quit" or "exit")
            {
                return;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            if (!memory.IsAlive)
            {
                Console.WriteLine("target process has exited.");
                return;
            }

            try
            {
                if (!Execute(parts))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"error: {ex.Message}");
            }
        }
    }

    private bool Execute(string[] parts)
    {
        switch (parts[0].ToLowerInvariant())
        {
            case "help":
                PrintHelp();
                break;

            case "scan":
                InitialScan(parts);
                break;

            case "int":
                ScanInt(parts);
                break;

            case "near":
                ShowNearbyVectors(parts);
                break;

            case "find":
                FindPattern(parts);
                break;

            case "creatures":
                ListCreatures(parts);
                break;

            case "dump":
                Dump(parts);
                break;

            case "snap":
                TakeSnapshot();
                break;

            case "diff":
                DiffSnapshot(parts);
                break;

            case "changed":
                Refine(keepChanged: true);
                break;

            case "same":
                Refine(keepChanged: false);
                break;

            case "range":
                RefineRange(parts);
                break;

            case "vec3":
                RefineVector3();
                break;

            case "list":
                ListCandidates(parts);
                break;

            case "watch":
                Watch(parts);
                break;

            case "ptr":
                FindPointers(parts);
                break;

            case "reset":
                _candidates = null;
                _intCandidates = null;
                _snapshot = null;
                Console.WriteLine("  cleared.");
                break;

            case "modules":
                foreach (ModuleRegion module in memory.GetModules())
                {
                    Console.WriteLine($"  {module}");
                }

                break;

            default:
                Console.WriteLine($"unknown command '{parts[0]}'. Try 'help'.");
                break;
        }

        return true;
    }

    private static void PrintHelp() => Console.WriteLine(
        """
        EXACT VALUE (recommended for KOTOR)
          int <value>        find every int32 equal to a value; run again after changing
                             it in game to narrow. Use a number visible in the UI, such as
                             your credits or current hit points.
          near <hex addr>    show world-range float triples around an address, for finding
                             the position field once you are inside the player structure.
          reset              clear all candidate sets

        DIFFERENTIAL FLOAT (noisy on KOTOR: its graphics heap is full of float triples)
          snap               copy the target's memory as a baseline (preferred first pass)
          diff changed|same  compare against the snapshot; complete and uncapped
          scan <min> <max>   enumerate every 4-byte-aligned float in [min,max]
                             (capped at 2,000,000; prefer snap/diff on a large target)
          changed            keep candidates whose value changed since the last pass
          same               keep candidates whose value did not change
          range <min> <max>  keep candidates currently inside a range
          vec3               keep candidates that look like the x of an (x,y,z) triple
          list [n]           show up to n candidates (default 20)
          watch <hex addr>   print the three floats at an address once a second
          ptr <hex addr>     find pointers in the process that point at or near an address
          modules            list loaded modules
          quit               leave

        Typical run: scan -5000 5000 / walk / changed / stand still / same / walk / changed / vec3
        """);

    private void InitialScan(string[] parts)
    {
        float min = parts.Length > 1 && float.TryParse(parts[1], CultureInfo.InvariantCulture, out float parsedMin) ? parsedMin : -5000f;
        float max = parts.Length > 2 && float.TryParse(parts[2], CultureInfo.InvariantCulture, out float parsedMax) ? parsedMax : 5000f;

        Console.WriteLine($"scanning private committed memory for floats in [{min}, {max}]...");
        DateTime started = DateTime.UtcNow;

        _candidates = _scanner.InitialScan(min, max);

        Console.WriteLine(
            $"{_candidates.Count:N0} candidates in {(DateTime.UtcNow - started).TotalSeconds:F1}s"
            + (_candidates.Truncated ? " (truncated: narrow the range)" : string.Empty));
        Console.WriteLine("now move in game, then type 'changed'.");
    }

    private void ScanInt(string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out int value))
        {
            Console.WriteLine("usage: int <exact value>   e.g. your credits or current hit points");
            return;
        }

        DateTime started = DateTime.UtcNow;

        if (_intCandidates is null)
        {
            _intCandidates = _scanner.ScanInt32(value);
            Console.WriteLine(
                $"  {_intCandidates.Count:N0} addresses hold {value} ({(DateTime.UtcNow - started).TotalSeconds:F1}s)");
            Console.WriteLine("  now change that value in game, then run 'int <new value>' again.");
        }
        else
        {
            int before = _intCandidates.Count;
            _intCandidates = _scanner.RefineInt32(_intCandidates, value);
            Console.WriteLine($"  {before:N0} -> {_intCandidates.Count:N0} now hold {value}");
        }

        if (_intCandidates.Count is > 0 and <= 20)
        {
            foreach (nint address in _intCandidates)
            {
                Console.WriteLine($"    0x{address:X}");
            }

            Console.WriteLine("  inspect one with: near <addr>");
        }

        if (_intCandidates.Count == 0)
        {
            Console.WriteLine("  nothing left; run 'reset' and start again with a fresh value.");
        }
    }

    /// <summary>
    /// Byte-signature search. A creature's attribute block is a short run of small
    /// integers (strength, dexterity, constitution, intelligence, wisdom, charisma), which
    /// together make a far more selective fingerprint than any single value.
    /// </summary>
    private void FindPattern(string[] parts)
    {
        if (parts.Length < 2)
        {
            Console.WriteLine("usage: find <aob>   e.g. find 10 0E 0E 0A 0C 0A");
            return;
        }

        string text = string.Join(' ', parts[1..]);

        if (!BytePattern.TryParse(text, out BytePattern pattern, out string? error))
        {
            Console.WriteLine($"  bad pattern: {error}");
            return;
        }

        PatternScanner scanner = new(memory);
        DateTime started = DateTime.UtcNow;
        List<nint> hits = scanner.ScanAll(pattern, ScanScope.PrivateCommitted, limit: 400);

        Console.WriteLine($"  {hits.Count} hit(s) for '{pattern}' ({(DateTime.UtcNow - started).TotalSeconds:F1}s)");

        foreach (nint hit in hits.Take(40))
        {
            Console.WriteLine($"    0x{hit:X}");
        }
    }

    /// <summary>
    /// Enumerates live creature objects in the loaded area.
    ///
    /// Every creature carries a portrait resource reference ("po_..."), and in the player's
    /// object that string sits a fixed distance after the position field. So: sweep for the
    /// literal "po_", and keep only the hits that have a plausible world position exactly
    /// that far earlier. Neither test alone is selective (the string appears throughout the
    /// resource cache, and world-range float triples are everywhere in the graphics heap),
    /// but together they pin down real objects and nothing else.
    /// </summary>
    private void ListCreatures(string[] parts)
    {
        int delta = 0x174;

        if (parts.Length > 1)
        {
            string text = parts[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[1][2..] : parts[1];
            if (int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int parsed))
            {
                delta = parsed;
            }
        }

        Console.WriteLine($"scanning for creature objects (portrait at position + 0x{delta:X}) ...");

        PatternScanner scanner = new(memory);
        DateTime started = DateTime.UtcNow;
        List<nint> hits = scanner.ScanAll(BytePattern.Parse("70 6F 5F"), ScanScope.PrivateCommitted, limit: 500_000);

        Console.WriteLine($"  {hits.Count:N0} occurrences of \"po_\" ({(DateTime.UtcNow - started).TotalSeconds:F1}s)");

        List<(nint Position, string Portrait, Vector3f Value)> creatures = [];
        Span<byte> buffer = stackalloc byte[12];

        foreach (nint hit in hits)
        {
            nint position = hit - delta;

            if (!memory.TryRead(position, buffer))
            {
                continue;
            }

            float x = MemoryMarshal.Read<float>(buffer[..4]);
            float y = MemoryMarshal.Read<float>(buffer.Slice(4, 4));
            float z = MemoryMarshal.Read<float>(buffer.Slice(8, 4));

            bool plausible = float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
                && MathF.Abs(x) is > 0.5f and < 5000f
                && MathF.Abs(y) is > 0.5f and < 5000f
                && MathF.Abs(z) < 500f;

            if (!plausible || !memory.TryReadAsciiString(hit, 24, out string portrait) || portrait.Length < 4)
            {
                continue;
            }

            creatures.Add((position, portrait, new Vector3f(x, y, z)));
        }

        Console.WriteLine($"  {creatures.Count} creature object(s):");

        foreach ((nint position, string portrait, Vector3f value) in creatures.OrderBy(c => c.Portrait))
        {
            Console.WriteLine($"    position 0x{position:X}  {value}   {portrait}");
        }

        if (creatures.Count == 0)
        {
            Console.WriteLine("  none. Try a different delta, e.g. 'creatures 178'.");
        }
    }

    private void Dump(string[] parts)
    {
        if (parts.Length < 2 || !TryParseAddress(parts[1], out nint address))
        {
            Console.WriteLine("usage: dump <hex addr> [byteCount]");
            return;
        }

        int count = parts.Length > 2 && int.TryParse(parts[2], out int parsed) ? Math.Clamp(parsed, 16, 4096) : 128;

        byte[] buffer = new byte[count];
        if (!memory.TryRead(address, buffer))
        {
            Console.WriteLine("  read failed.");
            return;
        }

        for (int offset = 0; offset < count; offset += 16)
        {
            int length = Math.Min(16, count - offset);
            string hex = string.Join(' ', buffer.Skip(offset).Take(length).Select(b => b.ToString("X2")));
            string ascii = new([.. buffer.Skip(offset).Take(length)
                .Select(b => b is >= 0x20 and <= 0x7E ? (char)b : '.')]);
            Console.WriteLine($"    +0x{offset:X3}  {hex,-47}  {ascii}");
        }
    }

    private void ShowNearbyVectors(string[] parts)
    {
        if (parts.Length < 2 || !TryParseAddress(parts[1], out nint address))
        {
            Console.WriteLine("usage: near <hex addr>");
            return;
        }

        List<(int Offset, Vector3f Value)> vectors = _scanner.FindNearbyVectors(address);

        if (vectors.Count == 0)
        {
            Console.WriteLine("  no world-range float triples nearby.");
            return;
        }

        Console.WriteLine($"  float triples near 0x{address:X}:");

        foreach ((int offset, Vector3f value) in vectors.Take(40))
        {
            string sign = offset < 0 ? "-" : "+";
            Console.WriteLine($"    {sign}0x{Math.Abs(offset):X4}  {value}   (address 0x{address + offset:X})");
        }

        Console.WriteLine("  walk in game, then run 'near' again: the one that changed is the position.");
    }

    private void TakeSnapshot()
    {
        Console.WriteLine("snapshotting private committed memory ...");
        DateTime started = DateTime.UtcNow;

        _snapshot = _scanner.TakeSnapshot();

        Console.WriteLine(
            $"  captured {_snapshot.TotalBytes / (1024.0 * 1024.0):F1} MB across {_snapshot.Regions.Count} regions "
            + $"({(DateTime.UtcNow - started).TotalSeconds:F1}s)"
            + (_snapshot.Truncated ? " [truncated]" : string.Empty));
        Console.WriteLine("now move in game, then type 'diff changed'.");
    }

    private void DiffSnapshot(string[] parts)
    {
        if (_snapshot is null)
        {
            Console.WriteLine("no snapshot. Run 'snap' first.");
            return;
        }

        bool keepChanged = parts.Length < 2 || !parts[1].Equals("same", StringComparison.OrdinalIgnoreCase);

        DateTime started = DateTime.UtcNow;
        _candidates = _scanner.DiffFloats(_snapshot, keepChanged, -5000f, 5000f);

        Console.WriteLine(
            $"  {_candidates.Count:N0} values {(keepChanged ? "changed" : "held still")} "
            + $"({(DateTime.UtcNow - started).TotalSeconds:F1}s)");

        if (_candidates.Count is > 0 and <= 20)
        {
            ListCandidates(["list"]);
        }
    }

    private void Refine(bool keepChanged)
    {
        if (_candidates is null)
        {
            Console.WriteLine("no candidate set. Run 'scan <min> <max>' first.");
            return;
        }

        int before = _candidates.Count;
        _candidates = _scanner.Refine(_candidates, keepChanged);
        Console.WriteLine($"{before:N0} -> {_candidates.Count:N0} candidates ({(keepChanged ? "changed" : "unchanged")})");

        if (_candidates.Count is > 0 and <= 20)
        {
            ListCandidates(["list"]);
        }
    }

    private void RefineRange(string[] parts)
    {
        if (_candidates is null)
        {
            Console.WriteLine("no candidate set. Run 'scan <min> <max>' first.");
            return;
        }

        if (parts.Length < 3
            || !float.TryParse(parts[1], CultureInfo.InvariantCulture, out float min)
            || !float.TryParse(parts[2], CultureInfo.InvariantCulture, out float max))
        {
            Console.WriteLine("usage: range <min> <max>");
            return;
        }

        int before = _candidates.Count;
        _candidates = _scanner.RefineRange(_candidates, min, max);
        Console.WriteLine($"{before:N0} -> {_candidates.Count:N0} candidates");
    }

    private void RefineVector3()
    {
        if (_candidates is null)
        {
            Console.WriteLine("no candidate set. Run 'scan <min> <max>' first.");
            return;
        }

        int before = _candidates.Count;
        _candidates = _scanner.RefineVector3Head(_candidates);
        Console.WriteLine($"{before:N0} -> {_candidates.Count:N0} candidates that look like a vector3 head");

        if (_candidates.Count is > 0 and <= 20)
        {
            ListCandidates(["list"]);
        }
    }

    private void ListCandidates(string[] parts)
    {
        if (_candidates is null || _candidates.Count == 0)
        {
            Console.WriteLine("no candidates.");
            return;
        }

        int take = parts.Length > 1 && int.TryParse(parts[1], out int parsed) ? parsed : 20;

        foreach (nint address in _candidates.Addresses.Order().Take(take))
        {
            memory.TryReadSingle(address, out float x);
            memory.TryReadSingle(address + 4, out float y);
            memory.TryReadSingle(address + 8, out float z);
            Console.WriteLine($"  0x{address:X}  x={x,12:F3}  y={y,12:F3}  z={z,12:F3}");
        }

        if (_candidates.Count > take)
        {
            Console.WriteLine($"  ... and {_candidates.Count - take:N0} more");
        }
    }

    private void Watch(string[] parts)
    {
        if (parts.Length < 2 || !TryParseAddress(parts[1], out nint address))
        {
            Console.WriteLine("usage: watch <hex addr>");
            return;
        }

        Console.WriteLine("watching; press any key to stop.");

        while (!Console.KeyAvailable)
        {
            memory.TryReadSingle(address, out float x);
            memory.TryReadSingle(address + 4, out float y);
            memory.TryReadSingle(address + 8, out float z);
            Console.WriteLine($"  0x{address:X}  ({x:F3}, {y:F3}, {z:F3})");
            Thread.Sleep(500);
        }

        Console.ReadKey(intercept: true);
    }

    private void FindPointers(string[] parts)
    {
        if (parts.Length < 2 || !TryParseAddress(parts[1], out nint address))
        {
            Console.WriteLine("usage: ptr <hex addr>");
            return;
        }

        Console.WriteLine($"searching for pointers to within 0x1000 of 0x{address:X}...");
        List<(nint Pointer, int Offset)> hits = _scanner.FindPointersTo(address);

        foreach ((nint pointer, int offset) in hits.Take(30))
        {
            Console.WriteLine($"  0x{pointer:X} -> base + 0x{offset:X}");
        }

        Console.WriteLine($"{hits.Count} hit(s). A pointer inside a module's static data is the one you want.");
    }

    private static bool TryParseAddress(string text, out nint address)
    {
        string trimmed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;

        if (long.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long parsed))
        {
            address = (nint)parsed;
            return true;
        }

        address = 0;
        return false;
    }
}
