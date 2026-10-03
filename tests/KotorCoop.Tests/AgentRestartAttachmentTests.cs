using System.Reflection;
using System.Runtime.InteropServices;
using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

public sealed class AgentRestartAttachmentTests
{
    [Fact]
    public void ReplacementAttachmentWithdrawsOldSessionWithoutReplayingTheLastCommand()
    {
        nint block = Marshal.AllocHGlobal(0x10000);
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        using KotorGameBridge bridge = new(memory, ownsMemory: false);
        try
        {
            Marshal.Copy(new byte[0x10000], 0, block, 0x10000);
            foreach ((_, int offset) in KotorGameBridge.SessionScopedBlockLevels)
                Marshal.WriteInt32(block + offset, 1);
            const BindingFlags hidden = BindingFlags.Static | BindingFlags.NonPublic;
            int serialOffset = (int)typeof(KotorGameBridge).GetField("BlockConsoleSerial", hidden)!.GetRawConstantValue()!;
            Marshal.WriteInt32(block + serialOffset, 77);

            bridge.InitializeAttachedPlugin(block);

            foreach ((string name, int offset) in KotorGameBridge.SessionScopedBlockLevels)
                Assert.True(Marshal.ReadInt32(block + offset) == 0, $"Old {name} survived attachment");
            Assert.Equal(77, Marshal.ReadInt32(block + serialOffset));
            Assert.Equal(77u, typeof(KotorGameBridge).GetField("_lastConsoleSerial",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge));
        }
        finally { Marshal.FreeHGlobal(block); }
    }
}
