namespace KotorCoop.Memory;

/// <summary>
/// Sweeps a live process for a signature. Scanning the running process (rather than the
/// file on disk) is not optional for swkotor.exe: its entry point sits in a '.bind'
/// section, which is a packer/DRM stub, so the bytes on disk are not necessarily the
/// bytes that execute.
/// </summary>
public sealed class PatternScanner(ProcessMemory memory)
{
    private const int ChunkSize = 1 << 20;

    // A single region can be enormous; cap what is pulled in one read.
    private const long MaxRegionSize = 256L * 1024 * 1024;

    private readonly ProcessMemory _memory = memory;

    /// <summary>Finds every occurrence of the pattern in the given scope.</summary>
    public List<nint> ScanAll(BytePattern pattern, ScanScope scope, int limit = 64, CancellationToken cancellationToken = default)
    {
        List<nint> hits = [];

        foreach (MemoryRegion region in _memory.EnumerateRegions(scope))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (region.Size > MaxRegionSize)
            {
                continue;
            }

            ScanRegion(region.BaseAddress, region.Size, pattern, hits, limit);

            if (hits.Count >= limit)
            {
                break;
            }
        }

        return hits;
    }

    public nint? ScanFirst(BytePattern pattern, ScanScope scope, CancellationToken cancellationToken = default)
    {
        List<nint> hits = ScanAll(pattern, scope, limit: 1, cancellationToken);
        return hits.Count > 0 ? hits[0] : null;
    }

    /// <summary>Scans inside one loaded module, which is the usual way to anchor a code signature.</summary>
    public List<nint> ScanModule(string moduleName, BytePattern pattern, int limit = 64)
    {
        List<nint> hits = [];
        ModuleRegion? module = _memory.FindModule(moduleName);
        if (module is null)
        {
            return hits;
        }

        ScanRegion(module.Value.BaseAddress, module.Value.Size, pattern, hits, limit);
        return hits;
    }

    private void ScanRegion(nint baseAddress, long size, BytePattern pattern, List<nint> hits, int limit)
    {
        // Overlap consecutive chunks so a match straddling a boundary is not missed.
        int overlap = pattern.Length - 1;
        byte[] buffer = new byte[Math.Min(size, ChunkSize) + overlap];

        long offset = 0;
        while (offset < size && hits.Count < limit)
        {
            int toRead = (int)Math.Min(ChunkSize + overlap, size - offset);
            Span<byte> window = buffer.AsSpan(0, toRead);

            if (!_memory.TryRead(baseAddress + (nint)offset, window))
            {
                // Regions can be unmapped underneath us; skip and continue.
                offset += ChunkSize;
                continue;
            }

            int index = 0;
            while (hits.Count < limit)
            {
                int found = pattern.IndexIn(window, index);
                if (found < 0)
                {
                    break;
                }

                hits.Add(baseAddress + (nint)(offset + found));
                index = found + 1;
            }

            offset += ChunkSize;
        }
    }
}
