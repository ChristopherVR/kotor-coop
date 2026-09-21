using System.Threading;
using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Exercises the actual <see cref="SharedBlockView.TryGetPartyPresence"/> accessor over the
/// production slot layout. The writer loop deliberately exposes an odd marker while changing
/// correlated position/facing values; an accepted snapshot must never combine generations.
/// </summary>
public sealed class PartyPresenceSeqlockTests
{
    private static unsafe SharedBlockView NewScratchBlock(out byte[] storage)
    {
        storage = GC.AllocateArray<byte>(SharedBlock.TotalSize, pinned: true);
        return new SharedBlockView((byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref storage[0]));
    }

    [Fact]
    public unsafe void StableEvenSlotReturnsTheCompletePayload()
    {
        SharedBlockView block = NewScratchBlock(out byte[] storage);
        byte* slot = Slot(storage, 0);
        WritePayload(slot, generation: 2, value: 17);

        Assert.True(block.TryGetPartyPresence(0, out PartyPresenceView member));
        Assert.Equal(1u, member.RosterSlotPlusOne);
        Assert.Equal(17f, member.Position.X);
        Assert.Equal(17.25f, member.Facing);
        Assert.Equal(170, member.Health);
        GC.KeepAlive(storage);
    }

    [Fact]
    public unsafe void OddMarkerIsNotExposedAsAStaleOrPartialPayload()
    {
        SharedBlockView block = NewScratchBlock(out byte[] storage);
        byte* slot = Slot(storage, 0);
        WritePayload(slot, generation: 3, value: 22);

        Assert.False(block.TryGetPartyPresence(0, out _));
        GC.KeepAlive(storage);
    }
    [Fact]
    public async Task BoundedWriterInterleavingNeverReturnsMixedPositionAndFacing()
    {
        SharedBlockView block = NewScratchBlock(out byte[] storage);
        nint slotAddress = InitialiseConcurrentSlot(storage);
        Task writer = Task.Run(() => WriteInterleaved(slotAddress));

        int attempts = 0;
        try
        {
            while ((!writer.IsCompleted || attempts < 256) && attempts++ < 100_000)
            {
                if (!block.TryGetPartyPresence(0, out PartyPresenceView member))
                {
                    Thread.Yield();
                    continue;
                }


                Assert.Equal(member.Position.X + 0.25f, member.Facing);
                Assert.Equal(member.Position.X + 0.5f, member.Position.Y);
                Assert.Equal(member.Position.X + 1f, member.Position.Z);
                Assert.Equal((int)member.Position.X * 10, member.Health);
            }

            await writer.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            // Never leave a background writer touching pinned test memory if an assertion fails.
            await writer;
            GC.KeepAlive(storage);
        }

        Assert.True(block.TryGetPartyPresence(0, out PartyPresenceView final));
        Assert.Equal(4000f, final.Position.X);
        Assert.Equal(4000.25f, final.Facing);
        GC.KeepAlive(storage);
    }

    private static unsafe nint InitialiseConcurrentSlot(byte[] storage)
    {
        byte* slot = Slot(storage, 0);
        WritePayload(slot, generation: 2, value: 0);
        return (nint)slot;
    }

    private static unsafe void WriteInterleaved(nint slotAddress)
    {
        byte* slot = (byte*)slotAddress;
        for (uint i = 1; i <= 4000; i++)
        {
            uint even = (i + 1) * 2; // Continue after the initial generation; never reuse it.
            Volatile.Write(ref *(uint*)(slot + 0x24), even - 1);
            Thread.MemoryBarrier();
            *(uint*)(slot + 0x00) = 1;
            *(float*)(slot + 0x04) = i;
            Thread.Yield();
            *(float*)(slot + 0x08) = i + 0.5f;
            *(float*)(slot + 0x0C) = i + 1f;
            *(float*)(slot + 0x10) = i + 0.25f;
            *(uint*)(slot + 0x14) = i;
            *(int*)(slot + 0x18) = (int)i * 10;
            *(uint*)(slot + 0x1C) = i;
            *(uint*)(slot + 0x20) = i + 100;
            Thread.MemoryBarrier();
            Volatile.Write(ref *(uint*)(slot + 0x24), even);
        }
    }
    private static unsafe byte* Slot(byte[] storage, int index) =>
        (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref storage[0])
        + SharedBlock.OffsetPartyPresenceSlots
        + (index * SharedBlock.PartyPresenceStride);

    private static unsafe void WritePayload(byte* slot, uint generation, float value)
    {
        *(uint*)(slot + 0x00) = 1;
        *(float*)(slot + 0x04) = value;
        *(float*)(slot + 0x08) = value + 0.5f;
        *(float*)(slot + 0x0C) = value + 1f;
        *(float*)(slot + 0x10) = value + 0.25f;
        *(uint*)(slot + 0x14) = (uint)value;
        *(int*)(slot + 0x18) = (int)value * 10;
        *(uint*)(slot + 0x1C) = (uint)value;
        *(uint*)(slot + 0x20) = (uint)value + 100;
        Thread.MemoryBarrier();
        *(uint*)(slot + 0x24) = generation;
    }
}
