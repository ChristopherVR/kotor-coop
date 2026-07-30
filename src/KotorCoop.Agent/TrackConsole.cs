using System.Globalization;
using KotorCoop.Memory;

namespace KotorCoop.Agent;

/// <summary>
/// Guided discovery of the player's coordinates, followed by a live readout.
///
/// This is the command that answers "is any of this real". It automates the differential
/// scan that would otherwise be done by hand in Cheat Engine: take every float in the
/// process that could be a world coordinate, then alternately walk and stand still,
/// keeping the addresses that behave the way a position does and discarding everything
/// else. A few rounds of that reduces millions of candidates to a handful.
/// </summary>
public sealed class TrackConsole(ProcessMemory memory)
{
    private const float WorldMin = -5000f;
    private const float WorldMax = 5000f;

    private readonly ValueScanner _scanner = new(memory);

    public int Run()
    {
        Console.WriteLine();
        Console.WriteLine($"attached to pid {memory.ProcessId} ({memory.ProcessName}), {(memory.Is32Bit ? "32-bit" : "64-bit")}");
        Console.WriteLine();
        Console.WriteLine("Before starting: load a save and take control of your character.");
        Console.WriteLine("You need to be able to alt-tab between the game and this window, so run");
        Console.WriteLine("the game windowed (agent launch --windowed does this for you).");
        Console.WriteLine();

        if (!Prompt("Standing still and in control of your character?"))
        {
            return 1;
        }

        // First pass is a snapshot diff rather than an enumerate-everything scan. The
        // target has roughly 100 MB of private committed memory, which is ~26 million
        // floats; listing them all would cost about a gigabyte, and capping the list
        // biases it towards low addresses and can drop the player object outright.
        Console.WriteLine("taking a baseline snapshot of the game's memory ...");
        DateTime started = DateTime.UtcNow;
        MemorySnapshot baseline = _scanner.TakeSnapshot();
        Console.WriteLine(
            $"  captured {baseline.TotalBytes / (1024.0 * 1024.0):F1} MB across {baseline.Regions.Count} regions "
            + $"({(DateTime.UtcNow - started).TotalSeconds:F1}s)"
            + (baseline.Truncated ? " [truncated]" : string.Empty));

        Console.WriteLine();
        Console.WriteLine("Walk in a straight line for a couple of seconds, then STOP.");

        if (!Prompt("Done?"))
        {
            return 1;
        }

        started = DateTime.UtcNow;
        FloatCandidateSet candidates = _scanner.DiffFloats(baseline, keepChanged: true, WorldMin, WorldMax);
        Console.WriteLine($"  {candidates.Count:N0} values changed ({(DateTime.UtcNow - started).TotalSeconds:F1}s)");

        if (candidates.Count == 0)
        {
            Console.Error.WriteLine("Nothing changed at all. Is the game actually in-world rather than at a menu?");
            return 1;
        }

        // Alternating walk/stand rounds. Walking must change the value; standing must not.
        // Anything that fails either test is not a position.
        (string Prompt, bool KeepChanged)[] rounds =
        [
            ("Now stand completely still (do not touch the controls).", false),
            ("Walk somewhere else, then STOP.", true),
            ("Stand completely still again.", false),
        ];

        foreach ((string prompt, bool keepChanged) in rounds)
        {
            Console.WriteLine();
            Console.WriteLine(prompt);

            if (!Prompt("Done?"))
            {
                return 1;
            }

            int before = candidates.Count;
            candidates = _scanner.Refine(candidates, keepChanged);
            Console.WriteLine($"  {before:N0} -> {candidates.Count:N0} candidates ({(keepChanged ? "changed" : "held still")})");

            if (candidates.Count == 0)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Everything was eliminated. Usual causes:");
                Console.Error.WriteLine("  - you moved during a 'stand still' round (the camera drifting is fine, the character moving is not)");
                Console.Error.WriteLine("  - you did not actually move during a 'walk' round");
                Console.Error.WriteLine("Run track again.");
                return 1;
            }

            if (candidates.Count <= 8)
            {
                break;
            }
        }

        int beforeVector = candidates.Count;
        candidates = _scanner.RefineVector3Head(candidates, WorldMin, WorldMax);
        Console.WriteLine($"  {beforeVector:N0} -> {candidates.Count:N0} that look like the x of an (x, y, z) triple");

        // The vector3 test alone keeps the rows of transform matrices, which are also
        // three consecutive in-range floats. On a real scan they dominate the survivors.
        int beforeShape = candidates.Count;
        candidates = _scanner.RefinePositionShape(candidates);
        Console.WriteLine($"  {beforeShape:N0} -> {candidates.Count:N0} position-shaped (rotation rows rejected)");

        if (candidates.Count == 0)
        {
            Console.Error.WriteLine("No vector3-shaped candidates left. Try 'scan' for manual control.");
            return 1;
        }

        List<nint> shortlist = [.. candidates.Addresses.Order().Take(16)];
        Console.WriteLine();
        Console.WriteLine("Candidates:");

        for (int i = 0; i < shortlist.Count; i++)
        {
            nint address = shortlist[i];
            memory.TryReadSingle(address, out float x);
            memory.TryReadSingle(address + 4, out float y);
            memory.TryReadSingle(address + 8, out float z);
            Console.WriteLine($"  [{i}] 0x{address:X}  ({x:F3}, {y:F3}, {z:F3})");
        }

        nint chosen = shortlist[0];

        if (shortlist.Count > 1)
        {
            Console.WriteLine();
            Console.Write($"Pick one [0-{shortlist.Count - 1}], or Enter for 0: ");
            string? answer = Console.ReadLine();

            if (!string.IsNullOrWhiteSpace(answer)
                && int.TryParse(answer, out int index)
                && index >= 0
                && index < shortlist.Count)
            {
                chosen = shortlist[index];
            }
        }

        LiveReadout(chosen);
        PrintProfileSnippet(chosen);
        return 0;
    }

    /// <summary>Prints the coordinates continuously so movement can be confirmed by eye.</summary>
    private void LiveReadout(nint address)
    {
        Console.WriteLine();
        Console.WriteLine($"Watching 0x{address:X}. Move around in the game: the numbers should track you.");
        Console.WriteLine("Press any key to stop.");
        Console.WriteLine();

        float lastX = 0f;
        float lastY = 0f;
        float lastZ = 0f;
        double distance = 0;
        bool first = true;

        while (!Console.KeyAvailable)
        {
            if (!memory.IsAlive)
            {
                Console.WriteLine("target process exited.");
                return;
            }

            memory.TryReadSingle(address, out float x);
            memory.TryReadSingle(address + 4, out float y);
            memory.TryReadSingle(address + 8, out float z);

            if (!first)
            {
                double dx = x - lastX;
                double dy = y - lastY;
                double dz = z - lastZ;
                distance += Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            }

            first = false;
            lastX = x;
            lastY = y;
            lastZ = z;

            // One line per sample rather than rewriting a single line with a carriage
            // return: CR-rewriting interleaves badly with anything else on the console
            // and leaves fragments of longer previous lines behind.
            Console.WriteLine($"  x={x,10:F3}  y={y,10:F3}  z={z,10:F3}   travelled {distance,9:F1}");
            Thread.Sleep(250);
        }

        Console.ReadKey(intercept: true);
        Console.WriteLine();
        Console.WriteLine();
    }

    private void PrintProfileSnippet(nint address)
    {
        Console.WriteLine("Profile snippet for profiles/kotor1.json:");
        Console.WriteLine();
        Console.WriteLine($$"""
              "anchor": {
                "mode": "Absolute",
                "absoluteAddress": {{(long)address}},
                "dereferenceAnchor": false,
                "pointerChain": []
              },
              "layout": {
                "position": 0,
                "moduleNameLength": 16
              }
            """);
        Console.WriteLine();
        Console.WriteLine($"(0x{address:X} in decimal is {(long)address})");
        Console.WriteLine();
        Console.WriteLine("IMPORTANT: that address is only valid for THIS run of the game. It is a heap");
        Console.WriteLine("address, so it will be different after a restart or a save load.");
        Console.WriteLine();
        Console.WriteLine("To make it durable, find a static pointer path to it:");
        Console.WriteLine($"  dotnet run --project src/KotorCoop.Agent -- scan --process {memory.ProcessName}");
        Console.WriteLine($"  ptr 0x{address:X}");
        Console.WriteLine();
        Console.WriteLine("A hit that lands inside swkotor.exe's own image (0x400000 - 0x8C3000) is a");
        Console.WriteLine("static global. Because this executable has no ASLR, that address is stable");
        Console.WriteLine("across runs, so you can anchor on it with ModuleOffset and a pointer chain.");
        Console.WriteLine("See docs/FINDING-OFFSETS.md.");
    }

    private static bool Prompt(string question)
    {
        Console.Write($"{question} [Enter to continue, q to quit] ");
        string? answer = Console.ReadLine();
        return !string.Equals(answer?.Trim(), "q", StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatAddress(nint address) =>
        "0x" + address.ToString("X", CultureInfo.InvariantCulture);
}
