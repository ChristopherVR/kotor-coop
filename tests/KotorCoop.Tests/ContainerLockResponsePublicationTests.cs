using System.Reflection;
using System.Runtime.InteropServices;
using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ContainerLockResponsePublicationTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(nint address, nuint size, uint type);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedGrantBitWriteCannotPublishTheNewResponseSerial(bool granted)
    {
        nint allocation = VirtualAlloc(0, 0x6000, 0x3000, 0x04);
        Assert.NotEqual(0, allocation);
        // Put the payload on a read-only page but leave its preceding serial writable.
        // Publishing the new serial first would expose the previous, opposite verdict.
        nint block = allocation + 0x5000 - 0x40DC;
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        using KotorGameBridge bridge = new(memory, ownsMemory: false);
        try
        {
            typeof(KotorGameBridge).GetField("_pluginBlock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(bridge, block);
            Marshal.WriteInt32(block + 0x40D8, 10);
            Marshal.WriteInt32(block + 0x40DC, granted ? 0 : 1);
            Assert.True(VirtualProtect(allocation + 0x5000, 0x1000, 0x02, out _));
            Assert.False(bridge.WriteContainerLockResponse(11, granted));
            Assert.Equal(10, Marshal.ReadInt32(block + 0x40D8));
            Assert.Equal(granted ? 0 : 1, Marshal.ReadInt32(block + 0x40DC));

            Assert.True(VirtualProtect(allocation + 0x5000, 0x1000, 0x04, out _));
            Assert.True(bridge.WriteContainerLockResponse(11, granted));
            Assert.Equal(11, Marshal.ReadInt32(block + 0x40D8));
            Assert.Equal(granted ? 1 : 0, Marshal.ReadInt32(block + 0x40DC));
        }
        finally { VirtualFree(allocation, 0, 0x8000); }
    }
}
