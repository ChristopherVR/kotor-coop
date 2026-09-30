using System.Reflection;
using System.Runtime.InteropServices;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;
namespace KotorCoop.Tests;
public sealed class WorldRequestContinuityTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(nint address, nuint size, uint type);

    [Fact]
    public void FailedAmountWriteDoesNotPublishReviveRequest()
    {
        nint allocation = VirtualAlloc(0, 0x3000, 0x3000, 0x04);
        Assert.NotEqual(0, allocation);
        // Put the amount on a read-only page while leaving the preceding serial writable.
        nint block = allocation + 0x2000 - 0x1040;
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        using KotorGameBridge bridge = new(memory, ownsMemory:false);
        try
        {
            typeof(KotorGameBridge).GetField("_pluginBlock", BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(bridge,block);
            Marshal.WriteInt32(block+0x1034, 10);
            Marshal.WriteInt32(block+0x1038, 9);
            Marshal.WriteInt32(block+0x1040, 3);
            Assert.True(VirtualProtect(allocation+0x2000, 0x1000, 0x02, out _));
            Assert.False(bridge.RequestRevive(15));
            Assert.Equal(10, Marshal.ReadInt32(block+0x1034));
            Assert.Equal(3, Marshal.ReadInt32(block+0x1040));
        }
        finally { VirtualFree(allocation, 0, 0x8000); }
    }
    [Fact]
    public void SamePeerSequenceAfterCheckpointIsANewOpButItsExactReplayRemainsDuplicate()
    {
        EventFlagStore flags = new();
        ulong door = DoorFlags.MakeKey(DoorFlags.HashModule("end_m01aa"), 0x47);
        FlagOpPacket first = new(2, 1, door, 1, 1, 10, 3, 0);
        Assert.True(flags.ApplyOp(first));
        flags.Prune(ObjectKeyedFlags.IsObjectKeyed);
        flags.Set(door, 0);
        FlagOpPacket nextWorld = first with { Serial = 3, HostLoadSerial = 4, WorldRevision = 1 };
        Assert.True(flags.ApplyOp(nextWorld)); // newly admitted client starts sequence1 again
        Assert.False(flags.ApplyOp(nextWorld));
        Assert.True(flags.TryGet(door, out sbyte value)); Assert.Equal(1, value);
        Assert.True(flags.ApplyOp(nextWorld with { HostSessionId = 11, Serial = 4, Value = 0 }));
        Assert.True(flags.TryGet(door, out value)); Assert.Equal(0, value);
    }
    [Fact]
    public void FreshSenderReusingSequenceInSameWorldCanPublishNewLamportIntent()
    {
        EventFlagStore flags = new();
        FlagOpPacket first = new(2, 1, 100, 1, 8, 10, 3, 0);
        Assert.True(flags.ApplyOp(first));
        Assert.False(flags.ApplyOp(first));
        // A restarted sender learned the snapshot clock then minted a fresh decrease.
        FlagOpPacket next = first with { Value = 0, Serial = 9 };
        Assert.True(flags.ApplyOp(next));
        Assert.False(flags.ApplyOp(next));
        Assert.True(flags.TryGet(100, out sbyte value)); Assert.Equal(0, value);
        Assert.True(flags.ApplyOp(first with { FlagId = 101 }));
        Assert.False(flags.ApplyOp(first)); // still cannot replay the old advance
        Assert.True(flags.TryGet(100, out value)); Assert.Equal(0, value);
    }

    [Theory]
    [InlineData(1u,10u,11u,12u)]
    [InlineData(20u,10u,11u,21u)]
    [InlineData(uint.MaxValue,1u,1u,2u)]
    [InlineData(uint.MaxValue,uint.MaxValue,uint.MaxValue,1u)]
    public void ReviveRequestUsesActualMailboxHighWaterIncludingWrap(uint cached,uint requested,uint applied,uint expected)
    {
        nint block = Marshal.AllocHGlobal(0x1050);
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        using KotorGameBridge bridge = new(memory, ownsMemory:false);
        const BindingFlags scope = BindingFlags.Instance|BindingFlags.NonPublic;
        try
        {
            Marshal.Copy(new byte[0x1050],0,block,0x1050);
            typeof(KotorGameBridge).GetField("_pluginBlock",scope)!.SetValue(bridge,block);
            typeof(KotorGameBridge).GetField("_reviveSerial",scope)!.SetValue(bridge,cached);
            Marshal.WriteInt32(block+0x1034,unchecked((int)requested));
            Marshal.WriteInt32(block+0x1038,unchecked((int)applied));
            Assert.True(bridge.RequestRevive(15));
            Assert.Equal(expected,unchecked((uint)Marshal.ReadInt32(block+0x1034)));
            Assert.Equal(15,Marshal.ReadInt32(block+0x1040));
            Assert.True(bridge.RequestRevive(6));
            Assert.Equal(expected+1,unchecked((uint)Marshal.ReadInt32(block+0x1034)));
            Assert.Equal(6,Marshal.ReadInt32(block+0x1040));
        }
        finally { Marshal.FreeHGlobal(block); }
    }
}
