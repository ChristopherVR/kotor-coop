using System.Runtime.InteropServices;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed unsafe class PluginMenuLoaderKnownBlockTests : IDisposable
{
    private readonly nint _block = Marshal.AllocHGlobal(SharedBlock.TotalSize);
    private readonly string _save = Path.Combine(Path.GetTempPath(), "kotor-known-load-" + Guid.NewGuid());
    private readonly ProcessMemory _memory = ProcessMemory.Attach(Environment.ProcessId);

    public PluginMenuLoaderKnownBlockTests()
    {
        new Span<byte>((void*)_block, SharedBlock.TotalSize).Clear();
        byte[] signature = Convert.FromHexString(KotorGameBridge.PluginSignature.Replace(" ", ""));
        Marshal.Copy(signature, 0, _block, signature.Length);
        Marshal.WriteInt32(_block + 0x9A4, 7);
        Directory.CreateDirectory(_save);
        GffWriter nfo = new("NFO ");
        nfo.Root.SetString("LASTMODULE", "end_m01aa");
        File.WriteAllBytes(Path.Combine(_save, "savenfo.res"), nfo.ToBytes());
    }

    [Fact]
    public void ManualLoadPublishesToValidatedAttachmentWithStationaryHeartbeat()
    {
        Marshal.WriteInt32(_block+SharedBlock.OffsetNewGameIntent,2);
        Assert.True(PluginMenuLoader.TryRequestLoad(_memory, out nint block, out string diagnostic,
            out uint serial, inSession: true, manualIntent: true, knownBlock: _block,
            stagedSaveDirectory: _save), diagnostic);

        Assert.Equal(_block, block);
        Assert.Equal(8u, serial);
        Assert.Equal(8, Marshal.ReadInt32(_block + 0x9A4));
        Assert.Equal(1, Marshal.ReadInt32(_block + 0x8634));
        Assert.Equal(1, Marshal.ReadInt32(_block + 0x3F24));
        Assert.Equal(0, Marshal.ReadInt32(_block + 0x3320));
        Assert.Equal("end_m01aa", Marshal.PtrToStringAnsi(_block + 0x9B0));
        Assert.Equal(0, Marshal.ReadInt32(_block + 0x4C));
        Assert.Equal(2,Marshal.ReadInt32(_block+SharedBlock.OffsetMenuLoadNewGameGeneration));
    }

    [Fact]
    public void InProgressIntentPublicationDoesNotPublishAnyLoadRequest()
    {
        Marshal.WriteInt32(_block+SharedBlock.OffsetNewGameIntent,3);
        Assert.False(PluginMenuLoader.TryRequestLoad(_memory,out _,out string diagnostic,
            out uint serial,inSession:true,manualIntent:true,knownBlock:_block,stagedSaveDirectory:_save));
        Assert.Contains("coherent New Game",diagnostic);
        Assert.Equal(0u,serial);
        Assert.Equal(7,Marshal.ReadInt32(_block+0x9A4));
        Assert.Equal(0,Marshal.ReadInt32(_block+0x8634));
    }

    [Theory]
    [InlineData(8, 82)]
    [InlineData(12, 0)]
    [InlineData(0, 0)]
    public void StaleLayoutOrGuardRefusesWithoutPublishing(int offset, int value)
    {
        Marshal.WriteInt32(_block + offset, value);
        Assert.False(PluginMenuLoader.TryRequestLoad(_memory, out _, out string diagnostic,
            out uint serial, inSession: true, manualIntent: true, knownBlock: _block,
            stagedSaveDirectory: _save));
        Assert.Contains("version/guard", diagnostic);
        Assert.Equal(0u, serial);
        Assert.Equal(7, Marshal.ReadInt32(_block + 0x9A4));
        Assert.Equal(0, Marshal.ReadInt32(_block + 0x8634));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void UnreadableKnownAddressFailsValidation(int address)
    {
        Assert.False(PluginMenuLoader.TryValidateKnownBlock(_memory, address, out _));
    }

    public void Dispose()
    {
        _memory.Dispose();
        Marshal.FreeHGlobal(_block);
        Directory.Delete(_save, recursive: true);
    }
}
