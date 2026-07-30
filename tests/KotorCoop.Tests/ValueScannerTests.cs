using System.Runtime.InteropServices;
using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Runs the discovery scanner against the test process's own memory, driving a real
/// unmanaged buffer the way the game drives its player structure.
/// </summary>
public sealed class ValueScannerTests : IDisposable
{
    private readonly unsafe float* _values;
    private readonly ProcessMemory _memory;
    private readonly ValueScanner _scanner;

    public unsafe ValueScannerTests()
    {
        // A stand-in for a position triple plus some neighbouring noise.
        _values = (float*)NativeMemory.AllocZeroed(16 * sizeof(float));
        _memory = ProcessMemory.Attach(Environment.ProcessId);
        _scanner = new ValueScanner(_memory);
    }

    private unsafe void SetPosition(float x, float y, float z)
    {
        _values[0] = x;
        _values[1] = y;
        _values[2] = z;
    }

    private unsafe nint PositionAddress => (nint)_values;

    /// <summary>
    /// Snapshots only the neighbourhood of the test buffer.
    ///
    /// Sweeping the whole address space is what the tool does against a real game, but it
    /// cannot be done against the test host itself: the snapshot's own byte arrays live in
    /// the very address space being captured, so each snapshot swallows the previous one
    /// and memory compounds until the runtime falls over. A separate target process, which
    /// is the real deployment, has no such feedback loop.
    /// </summary>
    private MemorySnapshot TakeScopedSnapshot() => _scanner.TakeSnapshot(
        ScanScope.PrivateCommitted,
        maxTotalBytes: 8L * 1024 * 1024,
        minAddress: PositionAddress - 0x10000,
        maxAddress: PositionAddress + 0x10000);

    [Fact]
    public void ScopedSnapshotStaysSmall()
    {
        MemorySnapshot snapshot = TakeScopedSnapshot();

        Assert.True(snapshot.TotalBytes > 0, "the buffer's own region must be captured");
        Assert.True(snapshot.TotalBytes < 8L * 1024 * 1024, "range scoping must bound the capture");
    }

    [Fact]
    public void RangeScopingExcludesAddressesOutsideTheWindow()
    {
        MemorySnapshot snapshot = TakeScopedSnapshot();

        Assert.All(snapshot.Regions, r =>
        {
            Assert.True(r.Base + r.Data.Length > PositionAddress - 0x10000);
            Assert.True(r.Base < PositionAddress + 0x10000);
        });
    }

    [Fact]
    public unsafe void SnapshotDiffFindsAValueThatChanged()
    {
        SetPosition(120.5f, 240.25f, 3f);

        MemorySnapshot baseline = TakeScopedSnapshot();
        Assert.True(baseline.TotalBytes > 0);

        SetPosition(130.5f, 240.25f, 3f);

        FloatCandidateSet changed = _scanner.DiffFloats(baseline, keepChanged: true, -5000f, 5000f);

        Assert.Contains(PositionAddress, changed.Addresses);

        // y did not move, so it must not be in the "changed" set.
        Assert.DoesNotContain(PositionAddress + 4, changed.Addresses);
    }

    [Fact]
    public unsafe void SnapshotDiffFindsValuesThatHeldStill()
    {
        SetPosition(120.5f, 240.25f, 3f);

        MemorySnapshot baseline = TakeScopedSnapshot();

        SetPosition(130.5f, 240.25f, 3f);

        FloatCandidateSet unchanged = _scanner.DiffFloats(baseline, keepChanged: false, -5000f, 5000f);

        Assert.Contains(PositionAddress + 4, unchanged.Addresses);
        Assert.DoesNotContain(PositionAddress, unchanged.Addresses);
    }

    [Fact]
    public unsafe void AlternatingWalkAndStandNarrowsToThePosition()
    {
        // The exact loop the track command runs.
        SetPosition(100f, 200f, 5f);
        MemorySnapshot baseline = TakeScopedSnapshot();

        SetPosition(110f, 200f, 5f);
        FloatCandidateSet candidates = _scanner.DiffFloats(baseline, keepChanged: true, -5000f, 5000f);
        Assert.Contains(PositionAddress, candidates.Addresses);

        // Stand still: our x must survive an unchanged pass.
        candidates = _scanner.Refine(candidates, keepChanged: false);
        Assert.Contains(PositionAddress, candidates.Addresses);

        // Walk again.
        SetPosition(125f, 200f, 5f);
        candidates = _scanner.Refine(candidates, keepChanged: true);
        Assert.Contains(PositionAddress, candidates.Addresses);

        candidates = _scanner.RefineVector3Head(candidates);
        Assert.Contains(PositionAddress, candidates.Addresses);
    }

    [Fact]
    public unsafe void Vector3FilterRejectsAnIsolatedFloat()
    {
        SetPosition(100f, 200f, 5f);

        // A lone float followed by values far outside world range.
        _values[8] = 300f;
        _values[9] = 9e30f;
        _values[10] = 9e30f;

        Dictionary<nint, float> seed = new()
        {
            [PositionAddress] = 100f,
            [PositionAddress + (8 * 4)] = 300f,
        };

        FloatCandidateSet filtered = _scanner.RefineVector3Head(new FloatCandidateSet(seed));

        Assert.Contains(PositionAddress, filtered.Addresses);
        Assert.DoesNotContain(PositionAddress + (8 * 4), filtered.Addresses);
    }

    [Fact]
    public unsafe void FindsAPointerToTheBuffer()
    {
        SetPosition(1f, 2f, 3f);

        nint* holder = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));

        try
        {
            *holder = PositionAddress;

            // Scoped for the same reason the snapshots are: a full sweep of the test
            // host's own address space is not a sound thing to do.
            List<(nint Pointer, int Offset)> hits = _scanner.FindPointersTo(
                PositionAddress,
                maxOffset: 0x100,
                minAddress: (nint)holder - 0x10000,
                maxAddress: (nint)holder + 0x10000);

            Assert.Contains(hits, h => h.Pointer == (nint)holder && h.Offset == 0);
        }
        finally
        {
            NativeMemory.Free(holder);
        }
    }

    [Fact]
    public unsafe void ExcludesNearZeroNoise()
    {
        SetPosition(0.001f, 0.002f, 0.003f);

        MemorySnapshot baseline = TakeScopedSnapshot();
        SetPosition(0.004f, 0.002f, 0.003f);

        FloatCandidateSet changed = _scanner.DiffFloats(baseline, keepChanged: true, -5000f, 5000f);

        // Near-zero values are overwhelmingly noise, so they are filtered out.
        Assert.DoesNotContain(PositionAddress, changed.Addresses);
    }

    public unsafe void Dispose()
    {
        _memory.Dispose();
        NativeMemory.Free(_values);
    }
}
