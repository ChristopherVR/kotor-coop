using System.Diagnostics;
using System.Runtime.InteropServices;
using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Agent;

/// <summary>
/// Unattended offset discovery: focuses the game, walks the character itself, and scores
/// candidates by how they respond, instead of asking a human to walk and then pick from a
/// list by eye.
///
/// Automation is not just convenience here, it measurably improves the result. The manual
/// loop leaves seconds of uncontrolled game time between each snapshot and each diff,
/// during which a large fraction of the process changes for reasons unrelated to walking.
/// Driving the input directly makes the walk and stand intervals short and known, so
/// "changed while walking" and "still while standing" actually mean something.
/// </summary>
public sealed class AutoTrackConsole(ProcessMemory memory, Process game)
{
    private const float WorldMin = -5000f;
    private const float WorldMax = 5000f;

    private const int WalkMs = 1400;
    private const int SettleMs = 350;
    private const int StandMs = 900;

    // A second and a bit of walking moves a character metres, not millimetres and not
    // hundreds of units. Both bounds do real filtering work.
    private const float MinWalkDelta = 0.35f;
    private const float MaxWalkDelta = 400f;

    private readonly ValueScanner _scanner = new(memory);

    public int Run()
    {
        Console.WriteLine();
        Console.WriteLine($"attached to pid {memory.ProcessId} ({memory.ProcessName}), {(memory.Is32Bit ? "32-bit" : "64-bit")}");

        if (!GameInput.FocusGameWindow(game, out string focusDiagnostic))
        {
            Console.Error.WriteLine($"error: {focusDiagnostic}");
            Console.Error.WriteLine("Click the game window once, then run this again.");
            return 1;
        }

        Console.WriteLine(focusDiagnostic);
        Console.WriteLine("driving the character; do not touch the mouse or keyboard until this finishes.");
        Console.WriteLine();

        try
        {
            return Discover();
        }
        finally
        {
            GameInput.ReleaseAll();
        }
    }

    private int Discover()
    {
        // Round 1: snapshot, walk, keep what moved by a walking-sized amount.
        Console.WriteLine("round 1: baseline snapshot");
        MemorySnapshot baseline = _scanner.TakeSnapshot();
        Console.WriteLine($"  captured {baseline.TotalBytes / (1024.0 * 1024.0):F1} MB in {baseline.Regions.Count} regions");

        Walk("  walking forward");
        FloatCandidateSet candidates = _scanner.DiffFloats(baseline, keepChanged: true, WorldMin, WorldMax);
        Console.WriteLine($"  {candidates.Count:N0} values changed");

        if (candidates.Count == 0)
        {
            return Fail("Nothing changed while walking. Is the character actually able to move?");
        }

        candidates = _scanner.RefineByDelta(SnapshotValues(candidates), MinWalkDelta, MaxWalkDelta);
        Console.WriteLine($"  {candidates.Count:N0} moved by a walking-sized amount");

        // Round 2: stand still. A position must hold exactly.
        candidates = StandStill(candidates, "round 2");
        if (candidates.Count == 0)
        {
            return Fail("Everything moved while standing still.");
        }

        // Round 3 and 4: walk again, stand again.
        candidates = WalkAndRefine(candidates, "round 3");
        if (candidates.Count == 0)
        {
            return Fail("Nothing survived the second walk.");
        }

        candidates = StandStill(candidates, "round 4");
        if (candidates.Count == 0)
        {
            return Fail("Nothing survived the second stand.");
        }

        int beforeShape = candidates.Count;
        candidates = _scanner.RefinePositionShape(candidates);
        Console.WriteLine($"  {beforeShape:N0} -> {candidates.Count:N0} position-shaped (rotation rows rejected)");

        if (candidates.Count == 0)
        {
            return Fail("No position-shaped candidates. The character may have hit a wall.");
        }

        // Every survivor is verified. Taking the first N by address is exactly the silent
        // truncation that makes a scanner lie: the player's own position has no reason to
        // sort early, and KOTOR moves the whole party when you walk, so several addresses
        // legitimately look position-shaped.
        return Verify([.. candidates.Addresses.Order()]);
    }

    /// <summary>
    /// Confirms a candidate by driving the character and measuring the response, then
    /// ranks by how position-like the behaviour was.
    /// </summary>
    private int Verify(List<nint> shortlist)
    {
        Console.WriteLine();
        Console.WriteLine($"verifying {shortlist.Count} candidate(s) by walking and measuring");

        List<(nint Address, Vector3f Before, Vector3f After, float Moved, float Drift)> scored = [];

        Dictionary<nint, Vector3f> before = ReadTriples(shortlist);
        Walk("  walking");
        Dictionary<nint, Vector3f> after = ReadTriples(shortlist);

        // Then stand still and measure drift, so a value that merely wanders is caught.
        Thread.Sleep(StandMs);
        Dictionary<nint, Vector3f> settled = ReadTriples(shortlist);

        foreach (nint address in shortlist)
        {
            if (!before.TryGetValue(address, out Vector3f a)
                || !after.TryGetValue(address, out Vector3f b)
                || !settled.TryGetValue(address, out Vector3f c))
            {
                continue;
            }

            if (!a.IsFinite || !b.IsFinite || !c.IsFinite)
            {
                continue;
            }

            float moved = a.DistanceTo(b);
            float drift = b.DistanceTo(c);

            if (moved is < MinWalkDelta or > MaxWalkDelta || drift > 0.05f)
            {
                continue;
            }

            scored.Add((address, a, b, moved, drift));
        }

        if (scored.Count == 0)
        {
            return Fail("No candidate responded to walking the way a position does.");
        }

        Console.WriteLine($"  {scored.Count} responded to walking; measuring motion smoothness");

        List<MotionProfile> profiles = MeasureSmoothness([.. scored.Select(s => s.Address)]);

        // What separates the player from everything else that also changes while walking:
        // it moves in MOST samples, by SIMILAR amounts. A companion catching up teleports,
        // a recycled graphics buffer snaps between constants like 128 and 256, and a
        // destination waypoint sits still then jumps once.
        //
        // Requiring a high moving fraction is the part that matters. Ranking on
        // "largest step / median step" alone is degenerate: a value that holds still for
        // 22 samples and then jumps once has exactly one non-zero step, so that ratio is
        // 1.0 and it looks perfectly smooth.
        List<MotionProfile> ranked =
        [
            .. profiles
                .Where(p => p.MovingFraction >= 0.4f)
                .Where(p => p.MedianStep is > 0.02f and < 25f)
                .Where(p => p.PathLength > MinWalkDelta)
                .OrderBy(p => p.StepVariation)
                .ThenByDescending(p => p.PathLength),
        ];

        if (ranked.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Nothing moved continuously. Closest behaviours seen:");

            foreach (MotionProfile profile in profiles
                .OrderByDescending(p => p.MovingFraction)
                .ThenByDescending(p => p.PathLength)
                .Take(8))
            {
                Console.WriteLine(
                    $"  0x{profile.Address:X}  {profile.Last}  moved in {profile.MovingSteps}/{profile.Samples} samples  path {profile.PathLength:F2}");
            }

            return Fail("No candidate moved continuously enough to be a character position.");
        }

        Console.WriteLine();
        Console.WriteLine("confirmed candidates, most position-like first:");
        Console.WriteLine("  moving = samples in which the value changed; cv = step-size consistency (lower is steadier)");
        Console.WriteLine();

        foreach (MotionProfile profile in ranked.Take(8))
        {
            Console.WriteLine(
                $"  0x{profile.Address:X}  {profile.Last}  moving {profile.MovingSteps,2}/{profile.Samples}  "
                + $"path {profile.PathLength,7:F2}  step {profile.MedianStep,6:F3}  cv {profile.StepVariation,6:F2}");
        }

        // Print the actual traces of the top few, because a summary statistic can be
        // satisfied by something that is obviously not a character once you see the path.
        Console.WriteLine();
        Console.WriteLine("traces of the top candidates (first 10 samples of a forward walk):");

        foreach (MotionProfile profile in ranked.Take(3))
        {
            Console.WriteLine($"  0x{profile.Address:X}");
            foreach (Vector3f point in profile.Trace.Take(10))
            {
                Console.WriteLine($"      {point}");
            }
        }

        List<MotionProfile> persistent = FilterPersistent(ranked);

        if (persistent.Count == 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Every candidate was transient render memory, not a game object.");
            Console.Error.WriteLine("The values above are rows of per-frame transform matrices: note the");
            Console.Error.WriteLine("trailing 1.00 (a homogeneous w) and that the memory is recycled seconds later.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Try again from a spot with more room to walk, or use 'scan' manually and");
            Console.Error.WriteLine("look for a candidate that still holds a sane position a minute later.");
            return 1;
        }

        List<MotionProfile> ranked2 = persistent;
        nint best = ranked2[0].Address;
        Console.WriteLine();
        Console.WriteLine($"best candidate: 0x{best:X}");

        LiveReadout(best);
        FindPointerPath(best);
        return 0;
    }

    private readonly record struct MotionProfile(
        nint Address,
        int Samples,
        int MovingSteps,
        float PathLength,
        float MedianStep,
        float LargestStep,
        float StepVariation,
        Vector3f Last,
        IReadOnlyList<Vector3f> Trace)
    {
        /// <summary>Fraction of samples in which the value actually moved.</summary>
        public float MovingFraction => Samples == 0 ? 0f : (float)MovingSteps / Samples;
    }

    /// <summary>
    /// Walks forward continuously while sampling every candidate, and summarises how each
    /// one moved. A real character position produces a run of small, similar steps; a
    /// teleport, a snapped destination or a recycled slot produces one enormous step.
    /// </summary>
    private List<MotionProfile> MeasureSmoothness(List<nint> addresses)
    {
        const int sampleCount = 24;
        const int sampleIntervalMs = 90;

        Dictionary<nint, List<Vector3f>> traces = addresses.ToDictionary(a => a, _ => new List<Vector3f>());

        GameInput.KeyDown(GameInput.ScanW);

        try
        {
            for (int i = 0; i < sampleCount; i++)
            {
                Thread.Sleep(sampleIntervalMs);

                foreach (nint address in addresses)
                {
                    traces[address].Add(ReadTriple(address));
                }
            }
        }
        finally
        {
            GameInput.KeyUp(GameInput.ScanW);
        }

        List<MotionProfile> profiles = [];

        foreach ((nint address, List<Vector3f> trace) in traces)
        {
            List<float> steps = [];
            float path = 0f;
            bool usable = true;

            for (int i = 1; i < trace.Count; i++)
            {
                if (!trace[i].IsFinite || !trace[i - 1].IsFinite)
                {
                    usable = false;
                    break;
                }

                float step = trace[i - 1].DistanceTo(trace[i]);
                steps.Add(step);
                path += step;
            }

            if (!usable || steps.Count == 0)
            {
                continue;
            }

            // Median over mean: a single teleport would drag a mean upwards and mask the
            // fact that every other step was small.
            List<float> moving = [.. steps.Where(s => s > 0.0001f).Order()];
            float median = moving.Count > 0 ? moving[moving.Count / 2] : 0f;

            // Coefficient of variation across the moving steps. A character walking at a
            // constant speed produces steps of near-identical size, so this is small.
            // Anything that sits still and then jumps once produces a huge value, and
            // unlike max/median it cannot be gamed by having only a single moving step.
            float variation = 0f;
            if (moving.Count > 1 && median > 0f)
            {
                float mean = moving.Average();
                float sumSquares = moving.Sum(s => (s - mean) * (s - mean));
                variation = MathF.Sqrt(sumSquares / moving.Count) / MathF.Max(mean, 0.0001f);
            }
            else
            {
                // One moving step is not evidence of walking, it is evidence of a jump.
                variation = float.MaxValue;
            }

            profiles.Add(new MotionProfile(
                address,
                steps.Count,
                moving.Count,
                path,
                median,
                steps.Max(),
                variation,
                trace[^1],
                trace));
        }

        return profiles;
    }

    /// <summary>
    /// Discards candidates that are transient memory rather than a game object.
    ///
    /// The graphics path allocates and recycles per-frame transform matrices constantly,
    /// and their translation rows are indistinguishable from a position over a two second
    /// window: they hold world coordinates and they move smoothly as the character walks.
    /// What they do not do is survive. The player's own object lives for as long as the
    /// area is loaded, so waiting and re-testing separates the two cleanly.
    /// </summary>
    private List<MotionProfile> FilterPersistent(List<MotionProfile> candidates)
    {
        const int settleSeconds = 6;

        List<MotionProfile> shortlist = [.. candidates.Take(32)];

        Console.WriteLine();
        Console.WriteLine($"persistence check: standing idle for {settleSeconds}s, then re-testing {shortlist.Count} candidate(s)");

        Dictionary<nint, Vector3f> before = ReadTriples([.. shortlist.Select(p => p.Address)]);

        for (int i = 0; i < settleSeconds; i++)
        {
            Thread.Sleep(1000);
        }

        Dictionary<nint, Vector3f> after = ReadTriples([.. shortlist.Select(p => p.Address)]);
        List<MotionProfile> survivors = [];

        foreach (MotionProfile profile in shortlist)
        {
            Vector3f a = before[profile.Address];
            Vector3f b = after[profile.Address];

            // Standing idle, a real position is unchanged. Recycled memory has either
            // been zeroed or overwritten with something unrelated.
            bool stillPlausible = b.IsFinite
                && MathF.Abs(b.X) + MathF.Abs(b.Y) > 0.5f
                && b.X is > WorldMin and < WorldMax
                && b.Y is > WorldMin and < WorldMax;

            bool unchanged = a.IsFinite && a.DistanceTo(b) < 0.05f;

            if (stillPlausible && unchanged)
            {
                survivors.Add(profile);
            }
        }

        Console.WriteLine($"  {shortlist.Count} -> {survivors.Count} still hold a stable position after idling");

        if (survivors.Count == 0)
        {
            return survivors;
        }

        // Re-verify that the survivors still respond to walking. Anything that passed the
        // idle test by being permanently frozen is removed here.
        Console.WriteLine("  re-testing response to walking");

        Dictionary<nint, Vector3f> preWalk = ReadTriples([.. survivors.Select(p => p.Address)]);
        Walk("  walking");
        Dictionary<nint, Vector3f> postWalk = ReadTriples([.. survivors.Select(p => p.Address)]);

        List<MotionProfile> confirmed =
        [
            .. survivors.Where(p =>
            {
                float moved = preWalk[p.Address].DistanceTo(postWalk[p.Address]);
                return moved is >= MinWalkDelta and <= MaxWalkDelta;
            }),
        ];

        Console.WriteLine($"  {survivors.Count} -> {confirmed.Count} still respond to walking");

        foreach (MotionProfile profile in confirmed.Take(8))
        {
            Console.WriteLine($"    0x{profile.Address:X}  now {postWalk[profile.Address]}");
        }

        return confirmed;
    }

    /// <summary>Prints the chosen address live while walking the character in a square.</summary>
    private void LiveReadout(nint address)
    {
        Console.WriteLine();
        Console.WriteLine("live readout while walking (each line is one sample):");

        ushort[] pattern = [GameInput.ScanW, GameInput.ScanD, GameInput.ScanW, GameInput.ScanA];

        foreach (ushort key in pattern)
        {
            GameInput.KeyDown(key);

            for (int i = 0; i < 6; i++)
            {
                Thread.Sleep(120);
                Vector3f position = ReadTriple(address);

                // One WriteLine per sample. The previous version rewrote a single line
                // with a carriage return, which interleaved badly with everything else
                // on the console and produced unreadable output.
                Console.WriteLine($"    {position}");
            }

            GameInput.KeyUp(key);
            Thread.Sleep(120);
        }
    }

    private void FindPointerPath(nint target)
    {
        Console.WriteLine();
        Console.WriteLine("searching for a static pointer path ...");

        List<(nint Pointer, int Offset)> hits = _scanner.FindPointersTo(target, maxOffset: 0x800);

        ModuleRegion? module = memory.FindModule("swkotor.exe");
        nint moduleBase = module?.BaseAddress ?? 0x400000;
        nint moduleEnd = moduleBase + (nint)(module?.Size ?? 0x4C3000);

        List<(nint Pointer, int Offset)> statics =
            [.. hits.Where(h => h.Pointer >= moduleBase && h.Pointer < moduleEnd)];

        Console.WriteLine($"  {hits.Count} pointer(s) found, {statics.Count} of them inside swkotor.exe's image");

        if (statics.Count > 0)
        {
            (nint pointer, int offset) = statics[0];
            long rva = pointer - moduleBase;

            Console.WriteLine();
            Console.WriteLine("DURABLE profile (survives a restart, because this image has no ASLR):");
            Console.WriteLine();
            Console.WriteLine($$"""
                  "anchor": {
                    "mode": "ModuleOffset",
                    "module": "swkotor.exe",
                    "moduleOffset": {{rva}},
                    "dereferenceAnchor": true,
                    "pointerChain": []
                  },
                  "layout": {
                    "position": {{offset}},
                    "moduleNameLength": 16
                  }
                """);
            Console.WriteLine();
            Console.WriteLine($"  (static global 0x{pointer:X} = swkotor.exe + 0x{rva:X}, position at +0x{offset:X})");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("No static pointer found directly. The player object is probably reached");
        Console.WriteLine("through an intermediate structure, which needs a multi-level chain:");
        Console.WriteLine($"  dotnet run --project src/KotorCoop.Agent -- scan --process {memory.ProcessName}");

        foreach ((nint pointer, int offset) in hits.Take(8))
        {
            Console.WriteLine($"  ptr 0x{pointer:X}      (points at target - 0x{offset:X})");
        }

        Console.WriteLine();
        Console.WriteLine("SESSION-ONLY profile (valid until the game restarts or loads a save):");
        Console.WriteLine();
        Console.WriteLine($$"""
              "anchor": {
                "mode": "Absolute",
                "absoluteAddress": {{(long)target}},
                "dereferenceAnchor": false,
                "pointerChain": []
              },
              "layout": {
                "position": 0,
                "moduleNameLength": 16
              }
            """);
    }

    // ---- helpers --------------------------------------------------------------

    private FloatCandidateSet WalkAndRefine(FloatCandidateSet candidates, string label)
    {
        FloatCandidateSet snapshot = SnapshotValues(candidates);
        Walk($"{label}: walking");

        FloatCandidateSet refined = _scanner.RefineByDelta(snapshot, MinWalkDelta, MaxWalkDelta);
        Console.WriteLine($"  {candidates.Count:N0} -> {refined.Count:N0} moved while walking");
        return refined;
    }

    private FloatCandidateSet StandStill(FloatCandidateSet candidates, string label)
    {
        FloatCandidateSet snapshot = SnapshotValues(candidates);
        Console.WriteLine($"{label}: standing still");
        Thread.Sleep(StandMs);

        FloatCandidateSet refined = _scanner.Refine(snapshot, keepChanged: false);
        Console.WriteLine($"  {candidates.Count:N0} -> {refined.Count:N0} held still");
        return refined;
    }

    private int _walkCount;

    private void Walk(string label)
    {
        Console.WriteLine(label);

        // Turn a little before each walk, alternating direction. Without this the
        // character can end up pinned against a wall for the whole run, every "walk"
        // round produces no movement, and discovery fails for a reason that looks like a
        // bug in the scanner.
        ushort turn = (_walkCount++ % 2) == 0 ? GameInput.ScanD : GameInput.ScanA;
        GameInput.HoldKey(turn, 180);
        Thread.Sleep(80);

        GameInput.HoldKey(GameInput.ScanW, WalkMs);
        Thread.Sleep(SettleMs);
    }

    /// <summary>Re-reads every candidate so the next comparison is against current values.</summary>
    private FloatCandidateSet SnapshotValues(FloatCandidateSet candidates)
    {
        Dictionary<nint, float> current = [];

        foreach (nint address in candidates.Addresses)
        {
            if (memory.TryReadSingle(address, out float value) && float.IsFinite(value))
            {
                current[address] = value;
            }
        }

        return new FloatCandidateSet(current);
    }

    private Vector3f ReadTriple(nint address)
    {
        Span<byte> buffer = stackalloc byte[12];

        if (!memory.TryRead(address, buffer))
        {
            return new Vector3f(float.NaN, float.NaN, float.NaN);
        }

        return new Vector3f(
            MemoryMarshal.Read<float>(buffer[..4]),
            MemoryMarshal.Read<float>(buffer.Slice(4, 4)),
            MemoryMarshal.Read<float>(buffer.Slice(8, 4)));
    }

    private Dictionary<nint, Vector3f> ReadTriples(List<nint> addresses)
    {
        Dictionary<nint, Vector3f> result = [];

        foreach (nint address in addresses)
        {
            result[address] = ReadTriple(address);
        }

        return result;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"discovery failed: {message}");
        Console.Error.WriteLine("Make sure the character is standing in open ground with room to walk forward,");
        Console.Error.WriteLine("is not in dialogue, in combat, or in a menu, then run autotrack again.");
        return 1;
    }
}
