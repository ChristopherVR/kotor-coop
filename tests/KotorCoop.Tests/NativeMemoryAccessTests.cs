using System.Runtime.InteropServices;
using KotorCoop.Memory;
using Xunit;
using Native = KotorCoop.Plugin.NativeMethods;

namespace KotorCoop.Tests;

public sealed unsafe class NativeMemoryAccessTests : IDisposable
{
    private readonly nint _pages;
    private readonly int _pageSize = Environment.SystemPageSize;

    public NativeMemoryAccessTests()
    {
        // Preserve the plugin's x86 address ceiling while running in the x64 test host.
        for (nint candidate = 0x1000_0000; candidate < 0x7000_0000; candidate += 0x100_0000)
        {
            _pages = Native.VirtualAlloc(candidate, (nuint)(_pageSize * 2),
                Native.MemCommit | Native.MemReserve, Native.PageReadWrite);
            if (_pages != 0)
            {
                return;
            }
        }

        throw new InvalidOperationException("Could not allocate dedicated low-address test pages.");
    }

    [Fact]
    public void RangeChecksRejectReadOnlyAndInaccessiblePageTails()
    {
        nint crossing = _pages + _pageSize - 2;
        Assert.True(Native.IsReadable(crossing, 4));
        Assert.True(Native.IsWritable(crossing, 4));
        Assert.True(Native.VirtualProtect(_pages + _pageSize, (nuint)_pageSize, 0x02, out _));
        Assert.True(Native.IsReadable(crossing, 4));
        Assert.False(Native.IsWritable(crossing, 4));
        Assert.True(Native.VirtualProtect(_pages + _pageSize, (nuint)_pageSize, 0x01, out _));
        Assert.False(Native.IsReadable(crossing));
        Assert.True(Native.IsReadable(crossing, 2));
        Assert.False(Native.IsWritable(crossing, 4));
    }

    [Fact]
    public void GuardPagesAndOverflowingRangesAreRejected()
    {
        Assert.True(Native.VirtualProtect(_pages + _pageSize, (nuint)_pageSize, 0x104, out _));
        Assert.False(Native.IsReadable(_pages + _pageSize));
        Assert.False(Native.IsWritable(_pages + _pageSize));
        Assert.False(Native.IsReadable(_pages, nuint.MaxValue));
        Assert.False(Native.IsReadable(_pages, 0));
        Assert.False(Native.IsReadable(1));
        Assert.False(Native.IsReadable(0x7FFE_FFFF, 4));
    }

    [Fact]
    public void NativeStringsRequireAccessibleTerminator()
    {
        nint tail = _pages + _pageSize - 4;
        new Span<byte>((void*)tail, 4).Fill((byte)'A');
        Assert.True(Native.VirtualProtect(_pages + _pageSize, (nuint)_pageSize, 0x01, out _));
        Assert.False(Native.IsReadableTerminatedString(tail, 4));
        Assert.False(Native.IsReadableTerminatedString(tail, 5));
        *(byte*)(tail + 3) = 0;
        Assert.True(Native.IsReadableTerminatedString(tail, 4));
        Assert.False(Native.IsReadableTerminatedString(tail, 0));
        Assert.False(Native.IsReadableTerminatedString(tail, 65));
    }

    [Fact]
    public void JournalGuardValidatesLaterEntriesAndFullCapacityBeforeMutation()
    {
        nint entries = _pages + 64;
        nint tag = _pages + 512;
        *(uint*)_pages = (uint)entries;
        *(int*)(_pages + 4) = 2;
        *(int*)(_pages + 8) = 2;
        *(byte*)tag = (byte)'a';
        *(uint*)(entries + 0x18) = (uint)tag;
        *(uint*)(entries + 0x1C) = 2;
        *(uint*)(entries + 0x3C + 0x18) = (uint)tag;
        *(uint*)(entries + 0x3C + 0x1C) = 2;
        Assert.True(KotorCoop.Plugin.JournalMemoryGuard.TryValidate(_pages, out nint data, out int count, out _));
        Assert.Equal(entries, data);
        Assert.Equal(2, count);

        // A valid first entry must not hide a malformed later entry from remove-and-add.
        *(uint*)(entries + 0x3C + 0x1C) = 1;
        Assert.False(KotorCoop.Plugin.JournalMemoryGuard.TryValidate(_pages, out _, out _, out int failure));
        Assert.Equal(5, failure);
        *(uint*)(entries + 0x3C + 0x1C) = 2;
        *(int*)(_pages + 8) = 4097;
        Assert.False(KotorCoop.Plugin.JournalMemoryGuard.TryValidate(_pages, out _, out _, out failure));
        Assert.Equal(4, failure);

        // An array starting in accessible memory can still run into a protected page.
        *(uint*)_pages = (uint)(_pages + _pageSize - 0x3C);
        *(int*)(_pages + 8) = 2;
        Assert.True(Native.VirtualProtect(_pages + _pageSize, (nuint)_pageSize, 0x01, out _));
        Assert.False(KotorCoop.Plugin.JournalMemoryGuard.TryValidate(_pages, out _, out _, out failure));
        Assert.Equal(4, failure);
    }

    [Fact]
    public void BooleanAtPageEndReadsAndWritesOnlyOneByte()
    {
        nint tail = _pages + _pageSize - 1;
        Assert.True(Native.VirtualProtect(_pages + _pageSize, (nuint)_pageSize, 0x01, out _));
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        Assert.True(memory.TryWrite(tail, true));
        Assert.True(memory.TryRead(tail, out bool value));
        Assert.True(value);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);

    public void Dispose() => VirtualFree(_pages, 0, 0x8000);
}
