using System.Runtime.InteropServices;
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>A set of addresses and the float value each held when the set was taken.</summary>
public sealed class FloatCandidateSet(Dictionary<nint, float> values)
{
    public Dictionary<nint, float> Values { get; } = values;

    public int Count => Values.Count;

    public IEnumerable<nint> Addresses => Values.Keys;

    public bool Truncated { get; init; }
}

/// <summary>
/// A raw copy of the target's readable memory at one instant.
///
/// This exists because an enumerate-everything-first scan does not scale: KOTOR keeps
/// around 100 MB of private committed memory, which is ~26 million floats, and a
/// candidate dictionary that large costs about a gigabyte. Capping the candidate list
/// instead is worse than useless, because the cap silently biases the survivors towards
/// low addresses and can exclude the player object entirely.
///
/// Snapshotting the bytes and diffing two snapshots gives a complete first pass at a
/// fixed, predictable cost.
/// </summary>
public sealed class MemorySnapshot(List<(nint Base, byte[] Data)> regions)
{
    public IReadOnlyList<(nint Base, byte[] Data)> Regions => regions;

    public long TotalBytes => regions.Sum(r => (long)r.Data.Length);

    public bool Truncated { get; init; }
}

/// <summary>
/// Cheat Engine's core loop, in-process. This is how the KOTOR profile gets filled in:
/// take a wide scan of plausible coordinate values, move in game, keep the addresses
/// that changed, stand still, keep the ones that did not, and repeat until a handful
/// remain. Nothing here is KOTOR-specific.
/// </summary>
public sealed class ValueScanner(ProcessMemory memory)
{
    private const int ChunkSize = 1 << 20;
    private const long MaxRegionSize = 256L * 1024 * 1024;

    private readonly ProcessMemory _memory = memory;

    /// <summary>
    /// Sweeps for 4-byte-aligned floats inside a value range. World coordinates in KOTOR
    /// are metres-scale, so a range like [-5000, 5000] excluding near-zero rejects the
    /// overwhelming majority of incidental float-looking bytes.
    /// </summary>
    public FloatCandidateSet InitialScan(
        float min,
        float max,
        ScanScope scope = ScanScope.PrivateCommitted,
        int limit = 2_000_000,
        float excludeMagnitudeBelow = 0.01f,
        CancellationToken cancellationToken = default)
    {
        Dictionary<nint, float> found = [];
        byte[] buffer = new byte[ChunkSize];
        bool truncated = false;

        foreach (MemoryRegion region in _memory.EnumerateRegions(scope))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (region.Size > MaxRegionSize)
            {
                continue;
            }

            long offset = 0;
            while (offset < region.Size)
            {
                int toRead = (int)Math.Min(ChunkSize, region.Size - offset);
                toRead -= toRead % 4;
                if (toRead <= 0)
                {
                    break;
                }

                Span<byte> window = buffer.AsSpan(0, toRead);
                if (!_memory.TryRead(region.BaseAddress + (nint)offset, window))
                {
                    offset += ChunkSize;
                    continue;
                }

                ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(window);
                for (int i = 0; i < floats.Length; i++)
                {
                    float value = floats[i];

                    if (!float.IsFinite(value)
                        || value < min
                        || value > max
                        || MathF.Abs(value) < excludeMagnitudeBelow)
                    {
                        continue;
                    }

                    found[region.BaseAddress + (nint)(offset + (i * 4))] = value;

                    if (found.Count >= limit)
                    {
                        return new FloatCandidateSet(found) { Truncated = true };
                    }
                }

                offset += toRead;
            }
        }

        return new FloatCandidateSet(found) { Truncated = truncated };
    }

    /// <summary>
    /// Copies every readable region in scope so it can be diffed later.
    ///
    /// Capture is chunked rather than one read per region, for two reasons. A region can
    /// be partially unreadable or can change underneath the enumeration, and a whole-region
    /// read fails outright in that case, silently discarding megabytes of perfectly good
    /// memory (including, non-deterministically, the object being hunted). Chunking also
    /// keeps every allocation small instead of putting multi-hundred-megabyte arrays on
    /// the large object heap.
    /// </summary>
    /// <param name="minAddress">Lower bound, inclusive. Zero means unbounded.</param>
    /// <param name="maxAddress">
    /// Upper bound, exclusive. Zero means unbounded. Bounding the sweep matters when the
    /// scanner is attached to its own process, because its snapshot buffers are then
    /// inside the address space being swept and each capture would include the previous
    /// one. Against a separate process (the normal case) that cannot happen.
    /// </param>
    public MemorySnapshot TakeSnapshot(
        ScanScope scope = ScanScope.PrivateCommitted,
        long maxTotalBytes = 768L * 1024 * 1024,
        nint minAddress = 0,
        nint maxAddress = 0,
        CancellationToken cancellationToken = default)
    {
        List<(nint Base, byte[] Data)> captured = [];
        long total = 0;
        bool truncated = false;

        foreach (MemoryRegion region in _memory.EnumerateRegions(scope))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (region.Size > MaxRegionSize || region.Size < 4)
            {
                continue;
            }

            if (minAddress != 0 && region.EndAddress <= minAddress)
            {
                continue;
            }

            if (maxAddress != 0 && region.BaseAddress >= maxAddress)
            {
                continue;
            }

            // Clip to the requested window, so scoping bounds what is captured rather
            // than merely which regions are considered.
            long offset = 0;
            long limit = region.Size;

            if (minAddress != 0 && region.BaseAddress < minAddress)
            {
                offset = minAddress - region.BaseAddress;
                offset -= offset % 4;
            }

            if (maxAddress != 0 && region.EndAddress > maxAddress)
            {
                limit = maxAddress - region.BaseAddress;
            }

            while (offset < limit)
            {
                // Chunks stay 4-aligned, so no float straddles a boundary.
                int length = (int)Math.Min(ChunkSize, limit - offset);
                length -= length % 4;

                if (length <= 0)
                {
                    break;
                }

                if (total + length > maxTotalBytes)
                {
                    truncated = true;
                    break;
                }

                byte[] buffer = new byte[length];
                nint address = region.BaseAddress + (nint)offset;

                if (_memory.TryRead(address, buffer))
                {
                    captured.Add((address, buffer));
                    total += length;
                }

                offset += length;
            }

            if (truncated)
            {
                break;
            }
        }

        return new MemorySnapshot(captured) { Truncated = truncated };
    }

    /// <summary>
    /// Compares a snapshot against the target's current contents and returns every
    /// 4-byte-aligned float that changed (or did not) and looks like a world coordinate.
    /// This is the complete, uncapped first pass.
    /// </summary>
    public FloatCandidateSet DiffFloats(
        MemorySnapshot before,
        bool keepChanged,
        float min,
        float max,
        float excludeMagnitudeBelow = 0.01f,
        float epsilon = 0.0001f,
        CancellationToken cancellationToken = default)
    {
        Dictionary<nint, float> found = [];

        foreach ((nint baseAddress, byte[] old) in before.Regions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] current = new byte[old.Length];
            if (!_memory.TryRead(baseAddress, current))
            {
                continue;
            }

            ReadOnlySpan<float> oldFloats = MemoryMarshal.Cast<byte, float>(old);
            ReadOnlySpan<float> newFloats = MemoryMarshal.Cast<byte, float>(current);
            int length = Math.Min(oldFloats.Length, newFloats.Length);

            for (int i = 0; i < length; i++)
            {
                float now = newFloats[i];

                if (!float.IsFinite(now)
                    || now < min
                    || now > max
                    || MathF.Abs(now) < excludeMagnitudeBelow)
                {
                    continue;
                }

                float previous = oldFloats[i];
                if (!float.IsFinite(previous))
                {
                    continue;
                }

                bool changed = MathF.Abs(now - previous) > epsilon;
                if (changed == keepChanged)
                {
                    found[baseAddress + (nint)(i * 4)] = now;
                }
            }
        }

        return new FloatCandidateSet(found);
    }

    /// <summary>Re-reads every candidate and keeps those whose value changed (or did not).</summary>
    public FloatCandidateSet Refine(FloatCandidateSet previous, bool keepChanged, float epsilon = 0.0001f)
    {
        Dictionary<nint, float> kept = [];

        foreach ((nint address, float before) in previous.Values)
        {
            if (!_memory.TryReadSingle(address, out float now) || !float.IsFinite(now))
            {
                continue;
            }

            bool changed = MathF.Abs(now - before) > epsilon;
            if (changed == keepChanged)
            {
                kept[address] = now;
            }
        }

        return new FloatCandidateSet(kept);
    }

    /// <summary>
    /// Keeps candidates whose value moved by a plausible walking distance.
    ///
    /// Stronger than a plain "did it change" test: a position moves by metres over a
    /// second of walking, whereas timers, blend weights and accumulators move by amounts
    /// that are either tiny or enormous. Bounding the delta on both sides discards both.
    /// </summary>
    public FloatCandidateSet RefineByDelta(FloatCandidateSet previous, float minDelta, float maxDelta)
    {
        Dictionary<nint, float> kept = [];

        foreach ((nint address, float before) in previous.Values)
        {
            if (!_memory.TryReadSingle(address, out float now) || !float.IsFinite(now))
            {
                continue;
            }

            float delta = MathF.Abs(now - before);
            if (delta >= minDelta && delta <= maxDelta)
            {
                kept[address] = now;
            }
        }

        return new FloatCandidateSet(kept);
    }

    /// <summary>
    /// Keeps triples that look like a world position rather than a rotation.
    ///
    /// This exists because the plain vector3 filter happily accepts the rows of a
    /// transform matrix: three consecutive floats, all finite, all in world range. In a
    /// real KOTOR scan those matrix rows dominate the survivors. A rotation row is a unit
    /// vector, so every component is within [-1, 1]; a world position has at least one
    /// component well outside that.
    /// </summary>
    public FloatCandidateSet RefinePositionShape(FloatCandidateSet previous, float minMagnitude = 2f)
    {
        Dictionary<nint, float> kept = [];
        Span<byte> buffer = stackalloc byte[12];

        foreach (nint address in previous.Addresses)
        {
            if (address % 4 != 0 || !_memory.TryRead(address, buffer))
            {
                continue;
            }

            float x = MemoryMarshal.Read<float>(buffer[..4]);
            float y = MemoryMarshal.Read<float>(buffer.Slice(4, 4));
            float z = MemoryMarshal.Read<float>(buffer.Slice(8, 4));

            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            {
                continue;
            }

            float largest = MathF.Max(MathF.Abs(x), MathF.Max(MathF.Abs(y), MathF.Abs(z)));
            if (largest < minMagnitude)
            {
                continue;
            }

            // Reject unit-length triples: a normalised direction or a matrix row.
            float length = MathF.Sqrt((x * x) + (y * y) + (z * z));
            if (MathF.Abs(length - 1f) < 0.02f)
            {
                continue;
            }

            // KOTOR modules are broadly flat, so x and y both carry real magnitude while
            // z is a height. Requiring two substantial components kills scalar runs.
            int substantial = 0;
            if (MathF.Abs(x) >= minMagnitude)
            {
                substantial++;
            }

            if (MathF.Abs(y) >= minMagnitude)
            {
                substantial++;
            }

            if (substantial < 2)
            {
                continue;
            }

            kept[address] = x;
        }

        return new FloatCandidateSet(kept);
    }

    /// <summary>Keeps candidates whose current value falls inside a range.</summary>
    public FloatCandidateSet RefineRange(FloatCandidateSet previous, float min, float max)
    {
        Dictionary<nint, float> kept = [];

        foreach (nint address in previous.Addresses)
        {
            if (_memory.TryReadSingle(address, out float now) && float.IsFinite(now) && now >= min && now <= max)
            {
                kept[address] = now;
            }
        }

        return new FloatCandidateSet(kept);
    }

    /// <summary>
    /// Keeps only candidates that look like the first component of an (x, y, z) triple:
    /// the two following floats must also be finite and in world range. Position is stored
    /// as three consecutive floats in Odyssey, so this collapses a noisy candidate list
    /// dramatically in one step.
    /// </summary>
    public FloatCandidateSet RefineVector3Head(FloatCandidateSet previous, float worldMin = -5000f, float worldMax = 5000f)
    {
        Dictionary<nint, float> kept = [];
        Span<byte> buffer = stackalloc byte[12];

        foreach (nint address in previous.Addresses)
        {
            if (address % 4 != 0 || !_memory.TryRead(address, buffer))
            {
                continue;
            }

            float x = MemoryMarshal.Read<float>(buffer[..4]);
            float y = MemoryMarshal.Read<float>(buffer.Slice(4, 4));
            float z = MemoryMarshal.Read<float>(buffer.Slice(8, 4));

            bool plausible = float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
                && x >= worldMin && x <= worldMax
                && y >= worldMin && y <= worldMax
                && z >= worldMin && z <= worldMax;

            if (plausible)
            {
                kept[address] = x;
            }
        }

        return new FloatCandidateSet(kept);
    }

    /// <summary>
    /// Finds every 4-byte-aligned int32 equal to an exact value.
    ///
    /// This is the reliable way into KOTOR, and differential float scanning is not. Around
    /// 200 MB of the process is graphics memory: vertex buffers, normals and per-frame
    /// transform matrices, all of which are float triples in world-coordinate ranges that
    /// change smoothly while the character walks. They swamp any position-shaped filter.
    ///
    /// An exact integer taken off the UI (credits, current hit points, a skill rank) has
    /// no such population competing with it, so two passes usually leave a handful of
    /// addresses, and those sit inside the player's real structure. Position is then a
    /// short walk away at a fixed offset.
    /// </summary>
    public List<nint> ScanInt32(
        int value,
        ScanScope scope = ScanScope.PrivateCommitted,
        int limit = 200_000,
        CancellationToken cancellationToken = default)
    {
        List<nint> found = [];
        byte[] buffer = new byte[ChunkSize];

        foreach (MemoryRegion region in _memory.EnumerateRegions(scope))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (region.Size > MaxRegionSize)
            {
                continue;
            }

            long offset = 0;
            while (offset < region.Size && found.Count < limit)
            {
                int toRead = (int)Math.Min(ChunkSize, region.Size - offset);
                toRead -= toRead % 4;
                if (toRead <= 0)
                {
                    break;
                }

                Span<byte> window = buffer.AsSpan(0, toRead);
                if (!_memory.TryRead(region.BaseAddress + (nint)offset, window))
                {
                    offset += toRead;
                    continue;
                }

                ReadOnlySpan<int> values = MemoryMarshal.Cast<byte, int>(window);
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] == value)
                    {
                        found.Add(region.BaseAddress + (nint)(offset + (i * 4)));

                        if (found.Count >= limit)
                        {
                            break;
                        }
                    }
                }

                offset += toRead;
            }
        }

        return found;
    }

    /// <summary>Keeps candidates that now hold a different exact int32 value.</summary>
    public List<nint> RefineInt32(IEnumerable<nint> candidates, int value)
    {
        List<nint> kept = [];

        foreach (nint address in candidates)
        {
            if (_memory.TryReadInt32(address, out int now) && now == value)
            {
                kept.Add(address);
            }
        }

        return kept;
    }

    /// <summary>
    /// Reports float triples near a known address. Once an exact-value scan has landed
    /// inside the player's structure, the position is a nearby run of three floats.
    /// </summary>
    public List<(int Offset, Vector3f Value)> FindNearbyVectors(
        nint structureAddress,
        int searchBefore = 0x200,
        int searchAfter = 0x400,
        float worldMin = -5000f,
        float worldMax = 5000f)
    {
        List<(int, Vector3f)> results = [];
        Span<byte> buffer = stackalloc byte[12];

        for (int offset = -searchBefore; offset <= searchAfter; offset += 4)
        {
            if (!_memory.TryRead(structureAddress + offset, buffer))
            {
                continue;
            }

            float x = MemoryMarshal.Read<float>(buffer[..4]);
            float y = MemoryMarshal.Read<float>(buffer.Slice(4, 4));
            float z = MemoryMarshal.Read<float>(buffer.Slice(8, 4));

            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            {
                continue;
            }

            bool inRange = x >= worldMin && x <= worldMax
                && y >= worldMin && y <= worldMax
                && z >= worldMin && z <= worldMax;

            if (inRange && (MathF.Abs(x) > 1f || MathF.Abs(y) > 1f))
            {
                results.Add((offset, new Vector3f(x, y, z)));
            }
        }

        return results;
    }

    /// <summary>
    /// Walks backwards from a known field to find pointers elsewhere in the process that
    /// point at the containing structure, which is how a stable pointer path is built out
    /// of an address that changes every load.
    /// </summary>
    public List<(nint Pointer, int Offset)> FindPointersTo(
        nint target,
        int maxOffset = 0x1000,
        ScanScope scope = ScanScope.PrivateCommitted,
        int limit = 256,
        nint minAddress = 0,
        nint maxAddress = 0,
        CancellationToken cancellationToken = default)
    {
        List<(nint, int)> results = [];
        int pointerSize = _memory.PointerSize;
        byte[] buffer = new byte[ChunkSize];

        foreach (MemoryRegion region in _memory.EnumerateRegions(scope))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (region.Size > MaxRegionSize)
            {
                continue;
            }

            if (minAddress != 0 && region.EndAddress <= minAddress)
            {
                continue;
            }

            if (maxAddress != 0 && region.BaseAddress >= maxAddress)
            {
                continue;
            }

            long offset = 0;
            while (offset < region.Size && results.Count < limit)
            {
                int toRead = (int)Math.Min(ChunkSize, region.Size - offset);
                toRead -= toRead % pointerSize;
                if (toRead <= 0)
                {
                    break;
                }

                Span<byte> window = buffer.AsSpan(0, toRead);
                if (!_memory.TryRead(region.BaseAddress + (nint)offset, window))
                {
                    offset += ChunkSize;
                    continue;
                }

                for (int i = 0; i + pointerSize <= toRead; i += pointerSize)
                {
                    nint value = pointerSize == 4
                        ? (nint)MemoryMarshal.Read<uint>(window.Slice(i, 4))
                        : (nint)MemoryMarshal.Read<long>(window.Slice(i, 8));

                    long delta = target - value;
                    if (value != 0 && delta >= 0 && delta <= maxOffset)
                    {
                        results.Add((region.BaseAddress + (nint)(offset + i), (int)delta));

                        if (results.Count >= limit)
                        {
                            return results;
                        }
                    }
                }

                offset += toRead;
            }
        }

        return results;
    }
}
