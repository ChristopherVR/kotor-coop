using System.Buffers.Binary;
using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class SyntheticMouseDeltaQueueTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    public void IsolationDropsPhysicalAxesAndButtonsButDeliversRelativeMotionOnce(int size)
    {
        SyntheticMouseDeltaQueue queue = new();
        byte[] state = Enumerable.Repeat((byte)0x80, size).ToArray();
        Assert.True(queue.Enqueue(20, -30, 100));
        Assert.True(queue.Apply(state, true, true, 101));
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(state));
        Assert.Equal(-30, BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(4)));
        Assert.All(state.Skip(8), b => Assert.Equal(0, b));
        Assert.False(queue.Apply(state, true, true, 102));
        Assert.All(state, b => Assert.Equal(0, b));
    }

    [Fact]
    public void PhysicalInputIsPreservedAndMotionIsAddedWhenIsolationIsOff()
    {
        SyntheticMouseDeltaQueue queue = new();
        byte[] state = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(state, 7);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(4), -3);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(8), 120);
        state[12] = state[19] = 0x80;
        Assert.True(queue.Enqueue(5, 8, 0));
        Assert.True(queue.Apply(state, true, false, 1));
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(state));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(4)));
        Assert.Equal(120, BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(8)));
        Assert.Equal(0x80, state[12]);
        Assert.Equal(0x80, state[19]);
        byte[] expected = state.ToArray();
        Assert.False(queue.Apply(state, true, false, 2));
        Assert.Equal(expected, state);
    }

    [Fact]
    public void UnacquiredDeviceCanDeliverSyntheticMotionWithoutLeakingUninitializedBytes()
    {
        SyntheticMouseDeltaQueue queue = new();
        byte[] state = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        Assert.True(queue.Enqueue(-12, 17, 50));
        Assert.True(queue.Apply(state, false, false, 51));
        Assert.Equal(-12, BinaryPrimitives.ReadInt32LittleEndian(state));
        Assert.Equal(17, BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(4)));
        Assert.All(state.Skip(8), b => Assert.Equal(0, b));
        Assert.False(queue.Apply(state, false, false, 52));
    }

    [Fact]
    public void MovieOrFreezeExpiresMotionAndUnsupportedReadsDoNotDrainTheQueue()
    {
        SyntheticMouseDeltaQueue queue = new();
        Assert.True(queue.Enqueue(5, 6, 100));
        Assert.False(queue.Apply(new byte[256], true, false, 101));
        Assert.Equal(1, queue.Pending);
        byte[] state = new byte[16];
        Assert.False(queue.Apply(state, true, false, 1100));
        Assert.Equal(0, queue.Pending);
        Assert.All(state, b => Assert.Equal(0, b));
    }

    [Fact]
    public void QueueIsBoundedOrderedAndRecoversAfterExpiredEntries()
    {
        SyntheticMouseDeltaQueue queue = new();
        for (int i = 0; i < SyntheticMouseDeltaQueue.Capacity; i++) Assert.True(queue.Enqueue(i, -i, 0));
        Assert.False(queue.Enqueue(1, 1, 1));
        for (int i = 0; i < SyntheticMouseDeltaQueue.Capacity; i++)
        {
            byte[] state = new byte[16];
            Assert.True(queue.Apply(state, true, false, 2));
            Assert.Equal(i, BinaryPrimitives.ReadInt32LittleEndian(state));
        }
        Assert.False(queue.Enqueue(2049, 0, 3));
        Assert.False(queue.Enqueue(0, int.MinValue, 3));
        Assert.True(queue.Enqueue(1, 1, 3));
        Assert.True(queue.Enqueue(2, 2, 1003));
        Assert.Equal(1, queue.Pending);
    }

    [Fact]
    public void AdditiveMotionCannotWrapAnExtremePhysicalAxis()
    {
        SyntheticMouseDeltaQueue queue = new();
        byte[] state = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(state, int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(4), int.MinValue);
        Assert.True(queue.Enqueue(1, -1, 0));
        Assert.True(queue.Apply(state, true, false, 1));
        Assert.Equal(int.MaxValue, BinaryPrimitives.ReadInt32LittleEndian(state));
        Assert.Equal(int.MinValue, BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(4)));
    }
}
