using System.Runtime.InteropServices;
using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

public sealed unsafe class MemorySafetyTests
{
    [Fact]
    public void GenericWritesUseActualValueSizeAndPreserveAdjacentBytes()
    {
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        byte* bytes = (byte*)NativeMemory.Alloc(16);
        try
        {
            new Span<byte>(bytes, 16).Fill(0xCC);
            Assert.True(memory.TryWrite((nint)bytes, true));
            Assert.Equal(1, bytes[0]);
            Assert.Equal(0xCC, bytes[1]);
            Assert.True(memory.TryRead((nint)bytes, out bool flag));
            Assert.True(flag);

            Assert.True(memory.TryWrite((nint)(bytes + 2), '\u1234'));
            Assert.Equal(0x34, bytes[2]);
            Assert.Equal(0x12, bytes[3]);
            Assert.Equal(0xCC, bytes[4]);
            Assert.True(memory.TryRead((nint)(bytes + 2), out char character));
            Assert.Equal('\u1234', character);
        }
        finally
        {
            NativeMemory.Free(bytes);
        }
    }

    [Fact]
    public void StringWritesClearPreviousContentWithoutTouchingAdjacentBytes()
    {
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        byte* bytes = (byte*)NativeMemory.Alloc(17);
        try
        {
            new Span<byte>(bytes, 17).Fill(0xCC);
            Assert.True(memory.TryWriteAsciiString((nint)bytes, "abcdefghijklmnop", 16));
            Assert.True(memory.TryReadAsciiString((nint)bytes, 16, out string full));
            Assert.Equal("abcdefghijklmnop", full);
            Assert.True(memory.TryWriteAsciiString((nint)bytes, "abc", 16));
            Assert.All(new ReadOnlySpan<byte>(bytes + 3, 13).ToArray(), b => Assert.Equal(0, b));
            Assert.Equal(0xCC, bytes[16]);
            Assert.False(memory.TryWriteAsciiString((nint)bytes, "x", -1));
            Assert.False(memory.TryRead((nint)1, out int missing));
            Assert.Equal(0, missing);
        }
        finally
        {
            NativeMemory.Free(bytes);
        }
    }

    [Fact]
    public void ScannerFindsBoundaryMatchesOnceAndHonorsLimits()
    {
        const int chunk = 1 << 20;
        byte* bytes = (byte*)NativeMemory.AllocZeroed(2 * chunk + 32);
        try
        {
            byte[] signature = [0xA1, 0xB2, 0xC3, 0xD4];
            signature.CopyTo(new Span<byte>(bytes + chunk - 2, 4));
            signature.CopyTo(new Span<byte>(bytes + 2 * chunk, 4));
            using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
            PatternScanner scanner = new(memory);
            List<nint> hits = [];
            scanner.ScanRegion((nint)bytes, 2 * chunk + 32, BytePattern.FromBytes(signature), hits, 10);
            Assert.Equal([(nint)(bytes + chunk - 2), (nint)(bytes + 2 * chunk)], hits);
            hits.Clear();
            scanner.ScanRegion((nint)bytes, 2 * chunk + 32, BytePattern.FromBytes(signature), hits, 1);
            Assert.Equal((nint)(bytes + chunk - 2), Assert.Single(hits));
            Assert.Throws<OperationCanceledException>(() => scanner.ScanRegion(
                (nint)bytes, 2 * chunk + 32, BytePattern.FromBytes(signature), [], 10, new CancellationToken(true)));
        }
        finally
        {
            NativeMemory.Free(bytes);
        }
    }
}
