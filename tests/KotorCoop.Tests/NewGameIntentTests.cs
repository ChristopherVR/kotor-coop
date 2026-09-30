using System.Runtime.InteropServices;
using KotorCoop.Memory;
using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class NewGameIntentTests
{
    [Fact]
    public void AcceptedStockSplicePreservesRegistersFlagsAndOriginalAbsoluteRead()
    {
        byte[] bytes=new byte[NewGameIntentStub.Length];
        NewGameIntentStub.Write(bytes,0x10000000,0x20000000,0x67B13B);
        Assert.Equal(new byte[]{0x9C,0x60,0xE8},bytes[..3]);
        Assert.Equal(0x20000000u,unchecked(0x10000007u+BitConverter.ToUInt32(bytes,3)));
        Assert.Equal(new byte[]{0x61,0x9D},bytes[7..9]);
        Assert.Equal(NewGameIntentStub.Original.ToArray(),bytes[9..15]);
        Assert.Equal(0xE9,bytes[15]);
        Assert.Equal(0x67B13Bu,unchecked(0x10000014u+BitConverter.ToUInt32(bytes,16)));
        Assert.Throws<ArgumentException>(()=>NewGameIntentStub.Write(new byte[19],0,0,0));
    }

    [Fact]
    public unsafe void IntentSurvivesWorldChangesAndCannotBeReadMidPublication()
    {
        nint block=Marshal.AllocHGlobal(SharedBlock.TotalSize);
        using ProcessMemory memory=ProcessMemory.Attach(Environment.ProcessId);
        using KotorGameBridge bridge=new(memory,ownsMemory:false);
        try
        {
            new Span<byte>((void*)block,SharedBlock.TotalSize).Clear();
            typeof(KotorGameBridge).GetField("_pluginBlock",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(bridge,block);
            SharedBlockView view=new((byte*)block);
            Assert.True(bridge.TryReadNewGameIntent(out var intent)); Assert.Null(intent);
            Marshal.WriteInt32(block+SharedBlock.OffsetWorldLoadSerial,5);
            Marshal.WriteInt32(block+SharedBlock.OffsetLivenessNativeEpoch,8);
            view.PublishNewGameIntent(1234);
            Assert.True(bridge.TryReadNewGameIntent(out intent));
            Assert.Equal(new NewGameIntent(2,5,8,1234),intent);
            Marshal.WriteInt32(block+SharedBlock.OffsetWorldLoadSerial,6);
            Assert.True(bridge.TryReadNewGameIntent(out intent)); Assert.Equal(5u,intent!.WorldLoadSerialAtRequest);
            Marshal.WriteInt32(block+SharedBlock.OffsetNewGameIntent,3);
            Assert.False(bridge.TryReadNewGameIntent(out _));
            Marshal.WriteInt32(block+SharedBlock.OffsetNewGameIntent,unchecked((int)0xFFFFFFFEu));
            view.PublishNewGameIntent(5678);
            Assert.True(bridge.TryReadNewGameIntent(out intent)); Assert.Equal(2u,intent!.Generation);
            Assert.Equal(5678,intent.RequestedAtTick);
            Assert.True(SharedBlock.OffsetNativePartyLoadProof+SharedBlock.NativePartyLoadProofSize<=SharedBlock.OffsetNewGameIntent);
            Assert.True(SharedBlock.OffsetNewGameIntent+SharedBlock.NewGameIntentSize<=SharedBlock.TotalSize);
            Assert.Equal(0,SharedBlock.OffsetNewGameIntent%8);
        }
        finally { Marshal.FreeHGlobal(block); }
    }
}
